#!/usr/bin/env sh
# Runs the published (NativeAOT) image and exercises every endpoint once.
# Catches serialization metadata that is only missing with reflection disabled, which unit tests (JIT) can't see.
set -eu

IMAGE="${1:?usage: smoke-test.sh <image>}"
PORT="${PORT:-18080}"
BASE="http://localhost:$PORT"
NAME="nevermail-smoke-$$"

docker run -d --rm --name "$NAME" -p "$PORT:8080" -e Providers__SendGrid__ApiKey=smoke -e Storage__Path=/tmp "$IMAGE" > /dev/null
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
[ "$(status "$BASE/")" = "200" ] || fail "ui"
[ "$(status -X DELETE "$BASE/api/mails")" = "204" ] || fail "clear"

trap 'docker stop "$NAME" > /dev/null' EXIT
echo "Smoke test passed"
