#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C
mkdir -p /test/ca /test/data /etc/nginx/opencredential-tls
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=Isolated CA' -keyout /test/ca/ca.key -out /test/ca/ca.crt >/dev/null 2>&1
openssl req -newkey rsa:2048 -nodes -subj '/CN=127.0.0.1' -keyout /test/ca/server.key -out /test/ca/server.csr >/dev/null 2>&1
printf 'subjectAltName=IP:127.0.0.1\nextendedKeyUsage=serverAuth\n' > /test/ca/server.ext
openssl x509 -req -in /test/ca/server.csr -CA /test/ca/ca.crt -CAkey /test/ca/ca.key -set_serial 10 -days 1 -extfile /test/ca/server.ext -out /test/ca/server.crt >/dev/null 2>&1
install -o postgres -g postgres -m 600 /test/ca/server.key /var/lib/postgresql/server.key
install -o postgres -g postgres -m 644 /test/ca/server.crt /var/lib/postgresql/server.crt
install -m 600 /test/ca/server.key /etc/nginx/opencredential-tls/server.key
install -m 644 /test/ca/server.crt /etc/nginx/opencredential-tls/server.crt
version=$(ls /etc/postgresql | head -n 1)
printf "\nssl=on\nssl_cert_file='/var/lib/postgresql/server.crt'\nssl_key_file='/var/lib/postgresql/server.key'\n" >> "/etc/postgresql/$version/main/postgresql.conf"
pg_ctlcluster "$version" main start
runuser -u postgres -- psql -v ON_ERROR_STOP=1 -c "CREATE ROLE oc_test LOGIN PASSWORD 'synthetic-test-only';" >/dev/null
runuser -u postgres -- psql -v ON_ERROR_STOP=1 -c 'CREATE DATABASE oc_test OWNER oc_test;' >/dev/null
export ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://127.0.0.1:18766 ADMINWEB_DATA_DIR=/test/data
export Database__Mode=Sql Database__Provider=PostgreSql Database__AutoInitialize=true
export Database__ConnectionString='Host=127.0.0.1;Port=5432;Database=oc_test;Username=oc_test;Password=synthetic-test-only;SSL Mode=VerifyFull;Root Certificate=/test/ca/ca.crt'
export AdminAuth__Enabled=true AdminAuth__Username=synthetic_admin AdminAuth__Password=synthetic-admin-password
export ForwardedHeaders__TrustedProxies__0=127.0.0.1
dotnet /app/OpenCredential.AdminWeb.dll > /test/app.log 2>&1 &
nginx -t
nginx
for attempt in $(seq 1 40); do
  if curl --fail --silent --cacert /test/ca/ca.crt https://127.0.0.1:8443/health/ready >/dev/null; then break; fi
  sleep 1
done
curl --fail --silent --show-error --cacert /test/ca/ca.crt https://127.0.0.1:8443/health/ready >/dev/null
status=$(curl --silent --output /dev/null --write-out '%{http_code}' --cacert /test/ca/ca.crt https://127.0.0.1:8443/api/summary)
test "$status" = 401
status=$(curl --silent --output /dev/null --dump-header /test/login.headers --cookie-jar /test/cookies \
  --write-out '%{http_code}' --cacert /test/ca/ca.crt -H 'Content-Type: application/json' \
  -H 'X-OpenCredential-Request: 1' -d '{"username":"synthetic_admin","password":"synthetic-admin-password"}' \
  https://127.0.0.1:8443/api/auth/login)
test "$status" = 200
grep -qi '^set-cookie:.*secure' /test/login.headers
status=$(curl --silent --output /dev/null --write-out '%{http_code}' --cacert /test/ca/ca.crt \
  --cookie /test/cookies 'https://127.0.0.1:8443/api/summary?compact=true')
test "$status" = 200
echo 'PASS HTTPS proxy, PostgreSQL VerifyFull, readiness, login, Secure cookie and anonymous access denied.'
