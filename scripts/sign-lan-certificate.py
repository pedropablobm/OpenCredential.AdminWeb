#!/usr/bin/env python3
"""Create an HTTPS key locally and request a certificate from a remote CA via SSH."""
import ipaddress
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys


def run(*args, **kwargs):
    return subprocess.run(args, check=True, **kwargs)


def create_output(path, args):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "wb") as output:
        run(*args, stdout=output)


def main():
    name = input("IP o DNS para acceder a AdminWeb: ").strip()
    try:
        san = "IP:" + str(ipaddress.ip_address(name))
    except ValueError:
        if not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?", name):
            raise ValueError("Nombre DNS no valido.")
        san = "DNS:" + name
    target = input("SSH de la CA (usuario@servidor): ").strip()
    if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_-]*@[A-Za-z0-9][A-Za-z0-9.-]*", target):
        raise ValueError("Destino SSH no valido; usa usuario@IP o usuario@DNS.")
    ca_dir = input("Directorio absoluto de la CA en el servidor remoto [/root/opencredential-ca]: ").strip() or "/root/opencredential-ca"
    if not re.fullmatch(r"/[A-Za-z0-9_./-]+", ca_dir) or ".." in ca_dir.split("/"):
        raise ValueError("Directorio de CA no valido.")

    directory = Path("proxy-tls")
    directory.mkdir(mode=0o700, exist_ok=True)
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError("proxy-tls debe ser un directorio privado, no un enlace.")
    os.chmod(directory, 0o700)
    key, csr, cert = (directory / file for file in ("server.key", "server.csr", "server.crt"))
    if cert.exists() or cert.is_symlink() or any(p.is_symlink() for p in (key, csr)):
        raise FileExistsError("Existe un certificado o enlace: no se sobrescribira.")
    if key.exists() != csr.exists():
        raise FileExistsError("La clave y la solicitud deben existir juntas; no se sobrescribira ninguna.")
    reuse = key.is_file() and csr.is_file()
    if reuse:
        csr_key = run("openssl", "req", "-in", str(csr), "-pubkey", "-noout", capture_output=True).stdout
        key_pub = run("openssl", "pkey", "-in", str(key), "-pubout", capture_output=True).stdout
        if csr_key != key_pub:
            raise ValueError("La solicitud existente no corresponde a la clave privada.")
    print("Se reutilizara la clave y solicitud existentes." if reuse else "Se creara una clave local.")
    print("Se solicitara sudo en el servidor de la CA.")
    if input("Escribe SI para continuar: ").strip() != "SI":
        print("Cancelado sin generar archivos.")
        return
    if not reuse:
        create_output(key, ("openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:3072"))
        create_output(csr, ("openssl", "req", "-new", "-key", str(key), "-subj", "/CN=" + name))

    # A private remote directory contains only public material; no remote deletion is performed.
    result = run("ssh", target, "mktemp -d /tmp/oc-lan-cert.XXXXXXXX", text=True, capture_output=True)
    remote = result.stdout.strip()
    if not re.fullmatch(r"/tmp/oc-lan-cert\.[A-Za-z0-9]+", remote):
        raise ValueError("No se pudo verificar el directorio temporal remoto.")
    run("scp", str(csr), f"{target}:{remote}/server.csr")
    extensions = directory / ("server-" + remote.rsplit(".", 1)[-1] + ".ext")
    create_output(extensions, ("printf", "%s\n", "basicConstraints=critical,CA:FALSE", "keyUsage=critical,digitalSignature,keyEncipherment", "extendedKeyUsage=serverAuth", "subjectAltName=" + san))
    run("scp", str(extensions), f"{target}:{remote}/server.ext")
    # Every interpolated value is validated and shell-quoted. The CA private key never leaves the remote host.
    sign = " ".join(shlex.quote(x) for x in (
        "openssl", "x509", "-req", "-in", remote + "/server.csr", "-CA", ca_dir + "/ca.crt",
        "-CAkey", ca_dir + "/ca.key", "-out", remote + "/server.crt", "-days", "825",
        "-sha256", "-extfile", remote + "/server.ext"))
    command = "umask 022; " + sign + ' -set_serial "0x$(openssl rand -hex 16)" && chmod 644 ' + shlex.quote(remote + "/server.crt")
    run("ssh", "-tt", target, "sudo sh -c " + shlex.quote(command))
    fd = os.open(cert, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    os.close(fd)
    run("scp", f"{target}:{remote}/server.crt", str(cert))
    print("Certificado recibido. Antes del despliegue se verificara contra la CA publica.")
    print("Material publico temporal conservado en el servidor CA:", remote)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError, EOFError, KeyboardInterrupt) as error:
        print(f"No se completo la firma: {error}", file=sys.stderr)
        print("Se conservan archivos creados para inspeccion; no se han sobrescrito.", file=sys.stderr)
        sys.exit(1)
