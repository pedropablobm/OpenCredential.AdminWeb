#!/usr/bin/env python3
"""Create a private Compose env file once, without displaying credentials."""
import getpass
import os
import sys
from pathlib import Path


def value(prompt, default=None):
    result = input(prompt).strip() or default or ""
    if not result or any(c in result for c in "\r\n\x00"):
        raise ValueError("El valor no puede estar vacio ni contener saltos de linea.")
    return result


def secret(prompt):
    result = getpass.getpass(prompt)
    if not result or any(c in result for c in "\r\n\x00"):
        raise ValueError("La clave no puede estar vacia ni contener saltos de linea.")
    return result


def npgsql(text):
    return '"' + text.replace('"', '""') + '"'


def compose_value(text):
    # Compose env_file single quotes are literal; the only escape is \\'.
    # Backslash immediately before a quote is ambiguous across Compose versions.
    if "\\" in text or "'" in text:
        raise ValueError("Para evitar interpretaciones ambiguas, el valor no puede contener comillas simples ni barras invertidas.")
    return "'" + text + "'"


def main():
    path = Path("adminweb.production.env")
    if path.exists() or path.is_symlink():
        raise FileExistsError("La configuracion ya existe; no se sobrescribira.")
    host = value("Servidor PostgreSQL (IP o DNS): ")
    port = value("Puerto PostgreSQL [5432]: ", "5432")
    if not port.isdecimal() or not 1 <= int(port) <= 65535:
        raise ValueError("Puerto PostgreSQL no valido.")
    database = value("Nombre de base existente: ")
    user = value("Usuario PostgreSQL: ")
    password = secret("Clave PostgreSQL (no se muestra): ")
    admin = value("Usuario administrador web: ")
    admin_password = secret("Clave administrador web (no se muestra): ")
    if admin_password != secret("Repite la clave administrador web: "):
        raise ValueError("Las claves de administrador no coinciden.")
    connection = (f"Host={npgsql(host)};Port={port};Database={npgsql(database)};"
                  f"Username={npgsql(user)};Password={npgsql(password)};"
                  "SSL Mode=VerifyFull;Root Certificate=/certs/postgres-ca.crt")
    content = ("Database__ConnectionString=" + compose_value(connection) + "\n"
               + "AdminAuth__Username=" + compose_value(admin) + "\n"
               + "AdminAuth__Password=" + compose_value(admin_password) + "\n"
               + "AdminAuth__Role=SuperAdmin\n")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as output:
        output.write(content)
    print("Configuracion privada creada con permisos 600.")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, EOFError, KeyboardInterrupt) as error:
        print(f"No se creo la configuracion: {error}", file=sys.stderr)
        sys.exit(1)
