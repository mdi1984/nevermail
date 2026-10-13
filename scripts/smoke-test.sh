#!/usr/bin/env sh
# Runs the published (NativeAOT) image and exercises every endpoint once.
# Catches serialization metadata that is only missing with reflection disabled, which unit tests (JIT) can't see.
set -eu

IMAGE="${1:?usage: smoke-test.sh <image>}"
PORT="${PORT:-18080}"
SMTP_PORT="${SMTP_PORT:-11025}"
BASE="http://localhost:$PORT"
NAME="nevermail-smoke-$$"

docker run -d --rm --name "$NAME" -p "$PORT:8080" -p "$SMTP_PORT:1025" -e Providers__SendGrid__ApiKey=smoke -e Providers__Brevo__ApiKey=smoke -e Providers__Mandrill__ApiKey=smoke -e Storage__Path=/tmp "$IMAGE" > /dev/null
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

# SMTP (MimeKit parsing, legacy charsets)
printf 'From: smoke@example.com
To: to@example.com
Subject: =?windows-1252?Q?SMTP_=80?=
Content-Type: text/plain

hello via smtp
' > "/tmp/$NAME.eml"
curl -sf "smtp://localhost:$SMTP_PORT" --mail-from smoke@example.com --mail-rcpt to@example.com --upload-file "/tmp/$NAME.eml" || fail "smtp send"
rm -f "/tmp/$NAME.eml"
curl -sf "$BASE/api/mails?provider=smtp" | grep -q '"subject":"SMTP €"' || fail "smtp mail not stored or decoded"

[ "$(status "$BASE/")" = "200" ] || fail "ui"
[ "$(status -X DELETE "$BASE/api/mails")" = "204" ] || fail "clear"

trap 'docker stop "$NAME" > /dev/null' EXIT
echo "Smoke test passed"
