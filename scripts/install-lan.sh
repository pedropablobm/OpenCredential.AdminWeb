#!/usr/bin/env bash
# Assisted deployment; existing private files are never overwritten.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
umask 077

fail() { printf 'Error: %s\n' "$1" >&2; exit 1; }
[[ $(uname -s) == Linux ]] || fail 'Se requiere Docker Engine en Linux.'
for tool in docker openssl ss; do command -v "$tool" >/dev/null || fail "Falta $tool."; done
docker compose version >/dev/null || fail 'Falta el plugin Docker Compose.'
docker info >/dev/null 2>&1 || fail 'No se puede acceder al motor Docker.'
[[ -t 0 ]] || fail 'Se necesita una terminal interactiva.'
if [[ ! -e adminweb.production.env && ! -L adminweb.production.env ]]; then
    command -v python3 >/dev/null || fail 'Se requiere python3 para crear la configuracion privada.'
    echo 'Se creara una configuracion privada nueva para PostgreSQL existente.'
    python3 scripts/create-lan-config.py
fi
[[ -f adminweb.production.env ]] || fail 'La configuracion privada no es un archivo regular.'
[[ -f postgres-ca.crt ]] || fail 'Falta postgres-ca.crt.'
base_compose=(docker compose -p oc-admin-external -f docker-compose.external-postgres.yml)
existing=$("${base_compose[@]}" ps -aq adminweb)
if [[ -n "$existing" ]]; then
    docker inspect -f '{{.State.Running}}' "$existing" 2>/dev/null | grep -qx true || fail 'El contenedor existente esta detenido; revisa su configuracion persistida antes de continuar.'
    if "${base_compose[@]}" exec -T adminweb test -e /data/adminweb-runtime.json; then
        fail 'Existe una configuracion SQL persistida en /data que prevalece sobre el archivo privado. Revisala sin mostrar credenciales antes de continuar.'
    fi
fi
if [[ ! -f proxy-tls/server.crt || ! -f proxy-tls/server.key ]]; then
    command -v python3 >/dev/null || fail 'Se requiere python3 para preparar el certificado.'
    command -v ssh >/dev/null && command -v scp >/dev/null || fail 'Se requieren ssh y scp.'
    echo 'El asistente puede solicitar la firma por SSH sin copiar la clave de la CA.'
    python3 scripts/sign-lan-certificate.py
fi
[[ -f proxy-tls/server.crt && -f proxy-tls/server.key ]] || fail 'Falta el certificado o su clave.'

read -r -p 'IP o nombre usado para acceder a AdminWeb: ' server_name
[[ "$server_name" =~ ^[a-zA-Z0-9.:-]+$ ]] || fail 'IP o nombre no valido.'
read -r -p 'Ruta del certificado publico de la CA HTTPS [postgres-ca.crt]: ' ca
ca=${ca:-postgres-ca.crt}
[[ -f "$ca" ]] || fail 'No existe el certificado publico de la CA HTTPS.'
if [[ "$server_name" =~ ^[0-9.]+$ ]]; then
    openssl verify -CAfile "$ca" -verify_ip "$server_name" proxy-tls/server.crt >/dev/null || fail 'El certificado HTTPS o su IP no son validos.'
else
    openssl verify -CAfile "$ca" -verify_hostname "$server_name" proxy-tls/server.crt >/dev/null || fail 'El certificado HTTPS o su nombre no son validos.'
fi
cert_key=$(openssl x509 -in proxy-tls/server.crt -pubkey -noout | openssl pkey -pubin -outform DER | openssl dgst -sha256)
private_key=$(openssl pkey -in proxy-tls/server.key -pubout -outform DER | openssl dgst -sha256)
[[ "$cert_key" == "$private_key" ]] || fail 'El certificado HTTPS no corresponde a su clave privada.'

if ss -ltnH 'sport = :8443' | grep -q .; then
    fail 'El puerto 8443 ya esta ocupado. No se modificara el servicio existente.'
fi
[[ "${ADMINWEB_PORT:-18766}" == 18766 ]] || fail 'El proxy requiere el puerto local 18766.'
network=oc-admin-external_default
if ! docker network inspect "$network" >/dev/null 2>&1; then
    echo 'Es necesario crear primero el contenedor AdminWeb y su red Docker.'
    echo 'Esta operacion no crea PostgreSQL ni elimina volumenes; AdminWeb podria escribir en la base existente.'
    read -r -p 'Confirma respaldo y compatibilidad del esquema escribiendo SI: ' initialize
    [[ "$initialize" == SI ]] || { echo 'Cancelado sin desplegar.'; exit 0; }
    docker compose -p oc-admin-external -f docker-compose.external-postgres.yml up -d --build
fi
gateway=$(docker network inspect "$network" --format '{{range .IPAM.Config}}{{.Gateway}}{{end}}' 2>/dev/null) || fail 'No se pudo inspeccionar la red Docker.'
[[ "$gateway" =~ ^[0-9a-fA-F:.]+$ ]] || fail 'No se pudo determinar una puerta de enlace valida.'
export ADMINWEB_PROXY_GATEWAY="$gateway"
compose=(docker compose -p oc-admin-external -f docker-compose.external-postgres.yml -f docker-compose.lan-proxy.yml)
"${compose[@]}" config --quiet || fail 'La configuracion Compose no es valida.'

printf '\nSe construira AdminWeb y se creara el proxy HTTPS en el puerto 8443.\n'
printf 'AdminWeb puede recrearse; Apache, PostgreSQL y los volumenes no se eliminaran.\n'
printf 'La puerta de enlace %s sera un proxy confiable; valida el origen observado tras el arranque.\n' "$gateway"
read -r -p 'Escribe SI para continuar: ' confirm
[[ "$confirm" == SI ]] || { echo 'Cancelado sin desplegar.'; exit 0; }
"${compose[@]}" up -d --build
echo 'Servicios iniciados. Verificando acceso HTTPS; esto no valida todos los permisos SQL.'
if command -v curl >/dev/null; then
    status=$(curl --silent --show-error --noproxy '*' --output /dev/null --write-out '%{http_code}' --cacert "$ca" "https://${server_name}:8443/health/ready") || {
        echo 'No se pudo comprobar HTTPS/readiness; revisa el proxy y la base antes de usar el servicio.' >&2
        exit 1
    }
    [[ "$status" == 200 ]] || fail "La comprobacion HTTPS/readiness devolvio HTTP $status, no 200."
else
    echo 'Falta curl: verifica manualmente /health/ready antes de usar el servicio.' >&2
    exit 1
fi
printf '\nAcceso: https://%s:8443\n' "$server_name"
echo 'Comprueba autenticacion, cookie Secure, IP de auditoria y confianza de la CA en los navegadores.'
