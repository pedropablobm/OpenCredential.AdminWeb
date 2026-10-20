#!/bin/sh
# Run only in a disposable test container with an ephemeral configured account.
set -eu
umask 077
: "${AdminAuth__Accounts__0__Password:?An ephemeral test password is required}"
for attempt in 1 2 3 4 5 6 7 8 9 10; do
    if wget -q -O /dev/null http://127.0.0.1:8080/health; then break; fi
    sleep 1
done
payload="{\"username\":\"container-test\",\"password\":\"${AdminAuth__Accounts__0__Password}\"}"
if ! wget -q --save-cookies /tmp/oc-test-cookies --keep-session-cookies \
    --header='X-OpenCredential-Request: 1' --header='Content-Type: application/json' \
    --post-data="$payload" -O /dev/null http://127.0.0.1:8080/api/auth/login; then
    echo 'FAIL: container login'; exit 1
fi
if ! wget -q --load-cookies /tmp/oc-test-cookies -O /dev/null http://127.0.0.1:8080/api/summary; then
    echo 'FAIL: authenticated summary'; exit 1
fi
if ! wget -q --load-cookies /tmp/oc-test-cookies -O /tmp/oc-test-report.pdf http://127.0.0.1:8080/api/reports/pdf; then
    echo 'FAIL: PDF generation'; exit 1
fi
head -c 4 /tmp/oc-test-report.pdf | grep -q '%PDF'
test ! -f /app/App_Data/adminweb-runtime.json
test ! -d /app/Tests
echo 'PASS Linux container: login, data, PDF and packaging exclusions.'
