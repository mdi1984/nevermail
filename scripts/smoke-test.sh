#!/usr/bin/env sh
# Runs the published (NativeAOT) image and exercises every endpoint once.
# Catches serialization metadata that is only missing with reflection disabled, which unit tests (JIT) can't see.
set -eu

IMAGE="${1:?usage: smoke-test.sh <image>}"
PORT="${PORT:-18080}"
SMTP_PORT="${SMTP_PORT:-11025}"
BASE="http://localhost:$PORT"
NAME="nevermail-smoke-$$"

docker run -d --rm --name "$NAME" -p "$PORT:8080" -p "$SMTP_PORT:1025" -e Providers__SendGrid__ApiKey=smoke -e Providers__Brevo__ApiKey=smoke -e Providers__Mandrill__ApiKey=smoke -e Providers__AhaSend__ApiKey=smoke -e Storage__Path=/tmp "$IMAGE" > /dev/null
trap 'docker logs "$NAME" 2>&1 | tail -20; docker stop "$NAME" > /dev/null' EXIT

fail() { echo "FAIL: $*"; exit 1; }
status() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

for _ in $(seq 1 30); do
  [ "$(status "$BASE/health")" = "200" ] && break
  sleep 0.5
done

SEND='{"personalizations":[{"to":[{"email":"to@example.com"}]}],"from":{"email":"from@example.com"},"subject":"Smoke","content":[{"type":"text/plain","value":"hi"}],"attachments":[{"filename":"a.txt","content":"aGVsbG8="}]}'

[ "$(status -X POST "$BASE/v3/mail/send" -H 'Content-Type: application/json' -d "$SEND")" = "401" ] || fail "missing auth not rejected"
[ "$(status -X POST "$BASE/v3/mail/send" -H 'Authorization: Bearer smoke' -H 'Content-Type: application/json' -d '{"from":{}}')" = "400" ] || fail "invalid request not rejected"
[ "$(status -X POST "$BASE/v3/mail/send" -H 'Authorization: Bearer smoke' -H 'Content-Type: application/json' -d '{"subject":1}')" = "400" ] || fail "type error not rejected"
[ "$(status -X POST "$BASE/v3/mail/send" -H 'Authorization: Bearer smoke' -H 'Content-Type: application/json' -d "$SEND")" = "202" ] || fail "valid send not accepted"
[ "$(status "$BASE/v3/scopes" -H 'Authorization: Bearer smoke')" = "200" ] || fail "scopes"

ID=$(curl -sf "$BASE/api/mails" | grep -o '"id":"[A-Za-z0-9]*"' | head -1 | cut -d'"' -f4)
[ -n "$ID" ] || fail "mail not listed"
curl -sf "$BASE/api/mails/$ID" | grep -q '"subject":"Smoke"' || fail "mail detail"
[ "$(curl -sf "$BASE/api/mails/$ID/attachments/0")" = "hello" ] || fail "attachment download"
# Brevo, including message versions with params (exercises the hand-written JSON merging)
BREVO='{"sender":{"email":"from@example.com"},"subject":"Hi {{ params.name }}","htmlContent":"<p>x</p>","params":{"name":"you"},"messageVersions":[{"to":[{"email":"a@example.com"}],"params":{"name":"Alice"}},{"to":[{"email":"b@example.com"}]}],"attachment":[{"name":"a.pdf","content":"aGVsbG8="}]}'
[ "$(status -X POST "$BASE/v3/smtp/email" -H 'Content-Type: application/json' -d "$BREVO")" = "401" ] || fail "brevo missing key not rejected"
[ "$(status -X POST "$BASE/v3/smtp/email" -H 'api-key: smoke' -H 'Content-Type: application/json' -d '{"to":[]}')" = "400" ] || fail "brevo invalid request not rejected"
[ "$(status -X POST "$BASE/v3/smtp/email" -H 'api-key: smoke' -H 'Content-Type: application/json' -d "$BREVO")" = "201" ] || fail "brevo send"
curl -sf "$BASE/api/mails?search=Hi%20Alice" | grep -q '"provider":"brevo"' || fail "brevo params not rendered"

# Mandrill, including send-template (builds template data JSON by hand)
MANDRILL='{"key":"smoke","template_name":"welcome","template_content":[{"name":"main","content":"x"}],"message":{"from_email":"from@example.com","subject":"Hi *|NAME|*","to":[{"email":"a@example.com"}],"global_merge_vars":[{"name":"NAME","content":"Bob"}]}}'
[ "$(status -X POST "$BASE/api/1.0/messages/send-template.json" -H 'Content-Type: application/json' -d '{"key":"wrong"}')" = "500" ] || fail "mandrill wrong key not rejected"
curl -sf -X POST "$BASE/api/1.0/messages/send-template.json" -H 'Content-Type: application/json' -d "$MANDRILL" | grep -q '"status":"sent"' || fail "mandrill send"
curl -sf -X POST "$BASE/api/1.3/users/ping" -d '{"key":"smoke"}' | grep -q 'PONG!' || fail "mandrill ping"
curl -sf "$BASE/api/mails?provider=mandrill" | grep -q '"subject":"Hi Bob"' || fail "mandrill merge vars"

# Azure Communication Services (no Operation-Id, so the server generates one; signatures aren't verified)
ACS='{"senderAddress":"from@example.com","recipients":{"to":[{"address":"a@example.com","displayName":"A"}]},"content":{"subject":"ACS smoke","plainText":"hi"},"attachments":[{"name":"a.txt","contentType":"text/plain","contentInBase64":"aGVsbG8="}]}'
ACS_AUTH='Authorization: HMAC-SHA256 SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature=c2ln'
[ "$(status -X POST "$BASE/emails:send?api-version=2025-09-01" -H 'Content-Type: application/json' -d "$ACS")" = "401" ] || fail "acs missing auth not rejected"
[ "$(status -X POST "$BASE/emails:send?api-version=2025-09-01" -H "$ACS_AUTH" -H 'x-ms-date: now' -H 'x-ms-content-sha256: x' -H 'Content-Type: application/json' -d '{"senderAddress":1}')" = "400" ] || fail "acs invalid request not rejected"
OP_LOCATION=$(curl -sf -D - -o /dev/null -X POST "$BASE/emails:send?api-version=2025-09-01" -H "$ACS_AUTH" -H 'x-ms-date: now' -H 'x-ms-content-sha256: x' -H 'Content-Type: application/json' -d "$ACS" | grep -i '^operation-location:' | cut -d' ' -f2 | tr -d '\r')
[ -n "$OP_LOCATION" ] || fail "acs send"
curl -sf "$OP_LOCATION" -H "$ACS_AUTH" -H 'x-ms-date: now' -H 'x-ms-content-sha256: x' | grep -q '"status":"Succeeded"' || fail "acs poll"
curl -sf "$BASE/api/mails?provider=azure" | grep -q '"subject":"ACS smoke"' || fail "acs mail not stored"

# AhaSend, including substitutions (hand-written JSON merging) and an idempotent replay
AHA_URL="$BASE/v2/accounts/4cdd7bc5-1e8d-4b3c-9c1a-6b5f8e2d7a10/messages"
AHA='{"from":{"email":"from@example.com"},"recipients":[{"email":"a@example.com","substitutions":{"name":"Ann"}}],"subject":"Aha {{ name }}","html_content":"<p>x</p>","substitutions":{"name":"you","x":1},"attachments":[{"data":"hello","content_type":"text/plain","file_name":"a.txt"}]}'
[ "$(status -X POST "$AHA_URL" -H 'Content-Type: application/json' -d "$AHA")" = "401" ] || fail "ahasend missing key not rejected"
[ "$(status -X POST "$AHA_URL" -H 'Authorization: Bearer smoke' -H 'Content-Type: application/json' -d '{"recipients":[]}')" = "400" ] || fail "ahasend invalid request not rejected"
curl -sf -X POST "$AHA_URL" -H 'Authorization: Bearer smoke' -H 'Idempotency-Key: smoke' -H 'Content-Type: application/json' -d "$AHA" | grep -q '"status":"queued"' || fail "ahasend send"
curl -sf -D - -o /dev/null -X POST "$AHA_URL" -H 'Authorization: Bearer smoke' -H 'Idempotency-Key: smoke' -H 'Content-Type: application/json' -d "$AHA" | grep -qi '^idempotent-replayed: true' || fail "ahasend idempotent replay"
[ "$(curl -sf "$BASE/api/mails?provider=ahasend" | grep -o '"subject":"Aha Ann"' | wc -l)" = "1" ] || fail "ahasend substitutions or replay"

# SMTP (MimeKit parsing, legacy charsets)
printf 'From: smoke@example.com\r\nTo: to@example.com\r\nSubject: =?windows-1252?Q?SMTP_=80?=\r\nContent-Type: text/plain\r\n\r\nhello via smtp\r\n' > "/tmp/$NAME.eml"
curl -sf "smtp://localhost:$SMTP_PORT" --mail-from smoke@example.com --mail-rcpt to@example.com --upload-file "/tmp/$NAME.eml" || fail "smtp send"
rm -f "/tmp/$NAME.eml"
curl -sf "$BASE/api/mails?provider=smtp" | grep -q '"subject":"SMTP €"' || fail "smtp mail not stored or decoded"

# UI support endpoints
curl -sf "$BASE/api/info" | grep -q '"smtpPort":1025' || fail "info"
EVENTS=$(curl -s -N --max-time 2 "$BASE/api/events" || true)
echo "$EVENTS" | grep -q 'event: ready' || fail "event stream"
SG_ID=$(curl -sf "$BASE/api/mails?provider=sendgrid" | grep -o '"id":"[A-Za-z0-9]*"' | head -1 | cut -d'"' -f4)
curl -sf "$BASE/api/mails/$SG_ID/eml" | grep -q '^Subject: Smoke' || fail "eml export"
curl -sfI "$BASE/app.js" | grep -qi 'cache-control: no-cache' || fail "static file caching headers"

[ "$(status "$BASE/")" = "200" ] || fail "ui"
[ "$(status -X DELETE "$BASE/api/mails")" = "204" ] || fail "clear"

trap 'docker stop "$NAME" > /dev/null' EXIT
echo "Smoke test passed"
