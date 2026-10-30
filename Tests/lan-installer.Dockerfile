# Runs synthetic installer tests in Linux. It does not access a Docker daemon.
FROM python:3.12-slim
WORKDIR /test
COPY scripts/install-lan.sh scripts/create-lan-config.py scripts/sign-lan-certificate.py scripts/
COPY Tests/test_lan_installer.py Tests/
RUN bash -n scripts/install-lan.sh && python -m unittest discover -s Tests -p test_lan_installer.py
