#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C
mkdir -p /run/sshd /root/.ssh /root/opencredential-ca
useradd -m -s /bin/bash lanqa
echo 'lanqa ALL=(root) NOPASSWD: ALL' > /etc/sudoers.d/lanqa
chmod 440 /etc/sudoers.d/lanqa
ssh-keygen -q -t ed25519 -N '' -f /root/.ssh/id_ed25519
install -d -o lanqa -g lanqa -m 700 /home/lanqa/.ssh
install -o lanqa -g lanqa -m 600 /root/.ssh/id_ed25519.pub /home/lanqa/.ssh/authorized_keys
ssh-keygen -A
/usr/sbin/sshd
ssh-keyscan -H 127.0.0.1 >> /root/.ssh/known_hosts
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=Isolated Test CA' \
  -keyout /root/opencredential-ca/ca.key -out /root/opencredential-ca/ca.crt >/dev/null 2>&1
printf '192.0.2.80\nlanqa@127.0.0.1\n\nSI\n' | python3 /test/sign-lan-certificate.py
openssl verify -CAfile /root/opencredential-ca/ca.crt -verify_ip 192.0.2.80 /test/proxy-tls/server.crt
test -s /test/proxy-tls/server.key
test "$(stat -c '%a' /test/proxy-tls/server.key)" = 600
test "$(stat -c '%a' /root/opencredential-ca/ca.key)" = 600
