# Proxy HTTPS para AdminWeb en Linux

Este archivo complementa `EXTERNAL_POSTGRES_DEPLOY.md`. El proxy es un contenedor Nginx independiente, no cambia Apache y escucha en el puerto 8443 del host. Solo es compatible con Docker Engine en Linux: usa la red del host para acceder a `127.0.0.1:18766`, por lo que comparte sus puertos. Comprueba que 8443 este libre y protege su acceso con el firewall. No cambies `ADMINWEB_PORT` sin actualizar tambien el upstream del proxy.

## Certificados

Se necesita un certificado de servidor HTTPS valido para la IP o nombre usado por los navegadores, incluido en su SAN, y una clave privada correspondiente. En el servidor de AdminWeb crea `proxy-tls/` con permisos restrictivos y coloca `server.crt` y `server.key` dentro. Nginx debe poder leerlos. La CA privada permanece fuera de esta carpeta; no copies ni publiques su clave. Los navegadores deben confiar en la CA que firmo el certificado HTTPS. Restringe el acceso y el respaldo de la clave del servidor; `proxy-tls/` se excluye de Git y del contexto Docker.

## Preparacion

Comprueba sin modificar servicios que el puerto este libre y descubre la puerta de enlace de la red del proyecto existente:

```bash
sudo ss -ltnp | grep ':8443 ' || echo 'Puerto 8443 sin listener'
docker network inspect oc-admin-external_default --format '{{range .IPAM.Config}}Subred={{.Subnet}} PuertaDeEnlace={{.Gateway}}{{end}}'
```

Para una instalacion nueva, la red se crea al levantar primero el Compose externo. La puerta de enlace es un valor de ejemplo de la instalacion, no universal. Verifica que el origen visto por AdminWeb sea realmente esa puerta de enlace; depende del motor y de la red. No uses rangos amplios como proxies confiables. El proxy sobrescribe las cabeceras reenviadas, pero confiar en la puerta de enlace tambien implica confiar en procesos locales capaces de conectarse al puerto loopback; protege el host y el acceso a Docker.

## Arranque

Despues de crear los certificados y verificar que el puerto esta libre, establece la puerta de enlace obtenida mediante inspeccion en tu sesion, sin editar el archivo privado. A continuacion, este comando **recrea AdminWeb si cambia su configuracion y crea el proxy**; no toca Apache ni borra el volumen:

```bash
export ADMINWEB_PROXY_GATEWAY='<puerta-de-enlace-inspeccionada>'
docker compose -p oc-admin-external -f docker-compose.external-postgres.yml -f docker-compose.lan-proxy.yml up -d --build
```

Usa los dos archivos Compose y la misma variable tambien en las operaciones posteriores. El override corrige el healthcheck usando GET. Si el certificado o la configuracion de Nginx son invalidos, el proxy no arrancara; no expongas la aplicacion hasta comprobar autenticacion, certificado, cookies Secure, auditoria y que el trafico por HTTP no permita acceso remoto.

Prueba desde un cliente que confie en la CA: `https://<IP-o-nombre-del-certificado>:8443`. Comprueba `/health/ready`, que verifica una consulta pero no todos los permisos o tablas. `/health` es solo liveness. No se han habilitado sesiones invitadas.

Para retirar este proxy, planifica la operacion y revisa el estado primero; no elimines volumenes ni uses `down -v`. Conserva respaldos seguros de `/data` y de la configuracion privada.
