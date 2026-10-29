FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends python3 openssh-client openssh-server openssl sudo ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /test
COPY sign-lan-certificate.py /test/sign-lan-certificate.py
COPY lan-signing-smoke.sh /test/lan-signing-smoke.sh
RUN bash /test/lan-signing-smoke.sh
