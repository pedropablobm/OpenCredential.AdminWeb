# OpenCredential Admin Web

Interfaz web administrativa para gestionar la aplicación OpenCredential. Permite gestionar:

- usuarios
- carreras
- semestres
- computadores
- importacion masiva por archivo plano
- visualizacion grafica del estado y uso de los equipos

## Creditos y licencia

OpenCredential AdminWeb fue desarrollado por Pedro Pablo Bermúdez Medina y Yuri Mercedes Bermúdez Mazuera.

Licencia: BSD 3-Clause. Consulta el archivo `LICENSE`.

## Ejecucion local

```powershell
dotnet run --project .\OpenCredential.AdminWeb.csproj
```

## Modos de persistencia

La aplicacion soporta dos modos:

- `Json`: util para demo o pruebas rapidas.
- `Sql`: recomendado para produccion en PostgreSQL o MySQL.

Configuracion base:

```json
"Database": {
  "Mode": "Sql",
  "Provider": "PostgreSql",
  "ConnectionString": "Host=localhost;Port=5432;Database=opencredential_admin;Username=opencredential;Password=<define-aqui-una-clave-segura>",
  "AutoInitialize": true
}
```

Cuando `AutoInitialize=true`, la aplicacion crea si faltan las tablas:

- `users`
- `careers`
- `levels`
- `computers`
- `rooms`
- `room_positions`
- `usage_records`
- `admin_audit_log`

Esto mantiene compatibilidad con el esquema academico del proyecto y agrega las tablas necesarias para administrar equipos y estadisticas de uso.

## Estado operativo de equipos

La consola ya no se basa solo en `logoutstamp IS NULL` para determinar ocupacion. Cuando la base usa `login_sessions` con heartbeat y reconciliacion de sesiones, AdminWeb calcula estados operativos mas precisos:

- `Disponible`
- `Ocupado`
- `Bloqueado`
- `Desconectado`
- `Sesion huerfana`
- `Deshabilitado`

En PostgreSQL y MySQL/MariaDB la lectura contempla estos campos cuando existen:

- `session_state`
- `last_heartbeat_at`
- `session_end_reason`
- `client_session_id`
- `windows_session_id`

Esto permite distinguir una sesion realmente activa de una bloqueada, desconectada o huerfana.

## Acceso administrativo

La consola ahora exige autenticacion por cookie para todos los endpoints de administracion bajo `/api`, excepto:

- `/api/auth/login`
- `/api/auth/logout`
- `/api/auth/me`
- `/health`

Configuracion base:

```json
"AdminAuth": {
  "Enabled": true,
  "Username": "admin",
  "Password": "<define-aqui-una-clave-segura>",
  "PasswordHash": "",
  "HashMethod": "BCRYPT",
  "Role": "SuperAdmin",
  "CookieName": "opencredential_admin",
  "SessionHours": 12,
  "Accounts": []
}
```

Recomendaciones:

- En produccion define `AdminAuth__Username` y `AdminAuth__Password` desde variables de entorno o secretos del despliegue
- Si prefieres no guardar clave en texto plano, usa `AdminAuth__PasswordHash` junto con `AdminAuth__HashMethod`
- Publica la app detras de HTTPS cuando la expongas fuera de la red interna
- No versiones `App_Data/`, `keys/`, `.env`, cookies ni logs

Roles soportados:

- `SuperAdmin`: acceso total, incluyendo auditoria
- `Coordinator`: gestion de usuarios, carreras, semestres, claves e importacion
- `Operator`: gestion de computadores y registros de uso
- `Viewer`: solo lectura de dashboard y resumen

Tambien puedes configurar varias cuentas:

```json
"AdminAuth": {
  "Enabled": true,
  "Accounts": [
    { "Username": "admin", "Password": "cambiar", "Role": "SuperAdmin" },
    { "Username": "coordinacion", "Password": "cambiar", "Role": "Coordinator" },
    { "Username": "soporte", "Password": "cambiar", "Role": "Operator" },
    { "Username": "consulta", "Password": "cambiar", "Role": "Viewer" }
  ]
}
```

## Auditoria

La consola registra eventos administrativos en la tabla `admin_audit_log` o en el almacenamiento JSON, incluyendo:

- login correcto y login fallido
- logout
- creacion, edicion y eliminacion de usuarios, carreras, semestres y equipos
- restablecimiento de claves
- importacion masiva de usuarios

Cada evento guarda actor, accion, entidad, resumen, IP remota y fecha UTC.

## Contenedores

La aplicacion quedo preparada para desplegarse en Docker y correr sin cambios sobre una VM Linux o contenedor LXC en Proxmox que tenga Docker Engine y Docker Compose.

Para conectar con una base PostgreSQL existente sin crear otro servidor de base de datos, consulta [el despliegue externo](docs/EXTERNAL_POSTGRES_DEPLOY.md). Utiliza un Compose separado, autenticacion obligatoria y un puerto limitado a loopback.

Para anadir un proxy HTTPS aislado en Docker sin modificar Apache, consulta [el proxy LAN](docs/LAN_PROXY.md).

`bash scripts/install-lan.sh` puede crear la configuracion privada, solicitar la firma de un certificado HTTPS por SSH y pedir confirmacion antes de desplegar. Conserva los archivos existentes y requiere una CA previamente preparada y confiable. Esta ruta de instalacion aun necesita validacion integral en Linux antes de usarse en produccion.

### Construir imagen

```bash
docker build -t opencredential-adminweb:latest .
```

### Ejecutar contenedor

```bash
docker run -d \
  --name opencredential-adminweb \
  -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e AdminAuth__Username=admin \
  -e AdminAuth__Password=CAMBIA_ESTA_CLAVE \
  -e ADMINWEB_DATA_DIR=/data \
  -v opencredential_adminweb_data:/data \
  --restart unless-stopped \
  opencredential-adminweb:latest
```

Si usas la imagen publicada en Docker Hub:

```bash
docker run -d \
  --name opencredential-adminweb \
  -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e AdminAuth__Username=admin \
  -e AdminAuth__Password=CAMBIA_ESTA_CLAVE \
  -e AdminAuth__Role=SuperAdmin \
  -e ADMINWEB_DATA_DIR=/data \
  -v opencredential_adminweb_data:/data \
  --restart unless-stopped \
  pedropablobm/opencredential-adminweb:test
```

La aplicacion puede iniciar en modo `Json` y luego configurarse desde la interfaz para conectarse a una base externa PostgreSQL o MySQL.

**Actualizacion de integridad y seguridad:** no se crean cuentas demo por defecto. Define una cuenta administrativa por secretos del despliegue. `Database__SeedDemoData=true` solo esta permitido expresamente en Development. Los datos JSON y las claves de cookies deben permanecer en un volumen privado, nunca en una imagen o un commit.

Las solicitudes HTTP remotas requieren HTTPS. El puerto HTTP de Docker por si solo no ofrece TLS: configura un reverse proxy HTTPS y su IP exacta mediante `ADMINWEB_TRUSTED_PROXY` (`ForwardedHeaders__TrustedProxies__0`). No confies en cualquier proxy ni publiques directamente PostgreSQL. `/health` comprueba el proceso; `/health/ready` comprueba acceso al repositorio.

## Correcciones coordinadas y pruebas

### Interfaz accesible, resiliencia y usuarios paginados

El panel incorpora etiquetas de login, foco visible, paneles modales operables con teclado, confirmacion de cambios pendientes y errores de guardado sin vaciar formularios. La barra de conexion muestra la ultima carga correcta y conserva los datos anteriores cuando falla la actualizacion. El listado de usuarios ahora pagina y filtra en servidor mediante `GET /api/users`; el resumen compacto evita transferir todos los usuarios e historiales manuales. Los contratos existentes siguen disponibles.

Consulta [la guia de interfaz y paginacion](docs/ADMINWEB_UI_PAGING.md) para pruebas, limites y compatibilidad. Estos cambios no sustituyen la autenticacion directa PostgreSQL/MySQL de Desktop ni habilitan acceso invitado.

- Editar o importar sin clave conserva el algoritmo y hash existentes. Omitir `groupIds` conserva grupos; `[]` los vacia expresamente.
- Claves nuevas: al menos 12 caracteres y hasta 72 bytes UTF-8; no se eliminan sus espacios. Los hashes legados HEX y Base64 siguen verificandose.
- La importacion tiene previsualizacion y confirmacion, limite de 2 MiB / 10000 filas, validacion por fila y soporte de comillas CSV sin saltos de linea internos. Las filas rechazadas se informan y no detienen las demas. Los nuevos usuarios requieren clave inicial; no se generan claves desconocidas para ellos.
- Los coordinadores no pueden asignar grupos administrativos ni modificar cuentas privilegiadas/configuradas. Operadores y viewers no reciben la lista de usuarios; solo SuperAdmin recibe auditoria.
- Las cookies se revocan al cambiar clave, estado, membresias o rol administrativo efectivo. La accion normal en la tabla de usuarios es **Desactivar**, que conserva historial; la API de eliminacion sigue siendo destructiva.
- Las peticiones de escritura a `/api` requieren la cabecera `X-OpenCredential-Request: 1`, incluida en ambas interfaces. Los clientes externos deben actualizarse.
- Persistencia JSON: contrato privado de hashes/tokens y reemplazo atomico del archivo. Los hashes perdidos previamente no se pueden reconstruir: restablece las claves afectadas.
- SQL: IDs atomicos mediante `admin_id_sequences`, transacciones para usuarios/grupos/mapas/tokens, migraciones compatibles con MySQL 8 y PostgreSQL. PostgreSQL agrega un indice unico sobre `LOWER(username)`: los duplicados historicos requieren revision previa, no se borran automaticamente.
- Desktop y AdminWeb comparten timestamps **UTC**. Dashboard e informes SQL calculan horas desde `login_sessions`, no desde registros manuales. Revisar historicos antiguos almacenados en hora local antes de actualizar.

Pruebas locales (solo generan datos sinteticos en temporales y un servidor HTTP de loopback):

```powershell
dotnet run --project .\Tests\RegressionTests.csproj
node --test .\Tests\frontend.test.cjs
dotnet build .\OpenCredential.AdminWeb.csproj -c Release
```

Las regresiones SQL opcionales aceptan `--postgres-port PUERTO` y `--mysql-port PUERTO` despues de `--`. Solo usar instancias **desechables** de localhost: crean bases nuevas para comprobar concurrencia, rollback, tokens e informes. No usar bases de produccion.

`Tests/container-smoke.sh` comprueba login, lectura autenticada y PDF dentro de un contenedor Linux desechable. Requiere una cuenta `container-test` configurada con una clave efimera en `AdminAuth__Accounts__0__Password`; no usar una cuenta institucional. El script no forma parte de la imagen publicada.

Pruebas adicionales de transporte, sin acceso a red externa ni cambios en certificados del anfitrion:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tests\Test-Transport.ps1
```

El contenedor usa una CA efimera y servidores locales HTTPS/SMTP. Comprueba certificados validos/invalidos y entrega STARTTLS, pero no certifica una CA institucional ni un servidor de correo real. Las pruebas HTTP tambien verifican que los encabezados de proxy no configurado no evadan HTTPS y que un proxy confiable emita cookies Secure y registre la IP correcta.

## Recuperacion de claves por correo

Configura en secretos/variables `RecoveryDelivery__Enabled`, `RecoveryDelivery__Host`, `RecoveryDelivery__Port`, `RecoveryDelivery__Sender`, `RecoveryDelivery__Username` y `RecoveryDelivery__Password`. En Compose corresponden a las variables `RECOVERY_SMTP_*` de `.env.example`. Se usa SMTP con STARTTLS (normalmente puerto 587); no se omite validacion de certificados.

Cuando no hay SMTP configurado, la recuperacion devuelve 503 e indica contactar al administrador: no simula un envio exitoso. Con SMTP, la respuesta no confirma si la cuenta existe y nunca expone el token. Solo Development permite revelar un token si se habilita expresamente `AdminAuth__PortalRevealRecoveryTokenInResponse`.

Antes de desplegar: respaldar base y volumen de datos, probar TLS/SMTP institucional y validar el login real de Windows en una VM. La paginacion SQL de todos los listados y las pruebas de carga siguen pendientes; el endpoint de snapshot conserva colecciones completas por compatibilidad.

### Configurar base externa desde la interfaz

1. Inicia sesion como `SuperAdmin`.
2. Ve al panel `Configuracion de base de datos`.
3. Selecciona `PostgreSQL` o `MySQL / MariaDB`.
4. Captura host, puerto, base de datos, usuario, clave y modo SSL.
5. Usa `Probar conexion`.
6. Usa `Ajustar tablas de AdminWeb` para crear o corregir las tablas auxiliares de la consola web sobre una base ya existente de OpenCredential.
7. Si la conexion debe quedar persistida para los siguientes reinicios, usa `Guardar configuracion`.
8. Reinicia el contenedor:

```bash
docker restart opencredential-adminweb
```

La configuracion se guarda en `/data/adminweb-runtime.json`, por eso es importante mantener montado el volumen `opencredential_adminweb_data:/data`.

### Ajustar tablas auxiliares sin reinstalar

Si OpenCredential ya fue desplegado antes y solo necesitas que la consola agregue o corrija sus tablas complementarias:

1. Ve a `Configuracion`
2. Captura o confirma la conexion actual
3. Pulsa `Probar conexion`
4. Pulsa `Ajustar tablas de AdminWeb`

Ese boton reutiliza la misma logica de esquema del modo `AutoInitialize`, pero ejecutada manualmente desde la interfaz. Sirve para:

- crear tablas nuevas de AdminWeb si faltan
- agregar columnas nuevas compatibles con versiones recientes
- dejar lista la consola sin reinstalar ni reconstruir la base base de OpenCredential

### Ejecutar con Compose

```bash
docker compose up -d --build
```

El `compose` incluido levanta:

- `opencredential-adminweb`
- `postgres`

## Persistencia

Segun el modo:

- `Json`: los datos se almacenan en `admin-store.json`
- `Sql`: los datos se guardan en PostgreSQL o MySQL

Para `Json`:

- En desarrollo: `OpenCredential.AdminWeb/App_Data/admin-store.json`
- En contenedor: `/data/admin-store.json`

La ruta se puede cambiar con la variable de entorno `ADMINWEB_DATA_DIR`.

Las llaves de autenticacion por cookie tambien se persisten en:

- En desarrollo: `OpenCredential.AdminWeb/App_Data/keys`
- En contenedor: `/data/keys`

## Reverse Proxy

La app acepta encabezados `X-Forwarded-*`, por lo que se puede publicar detras de Nginx, Traefik o HAProxy en Proxmox.

Ejemplo Nginx:

```nginx
server {
    listen 80;
    server_name admin.midominio.local;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    }
}
```

## Salud del servicio

Endpoint de verificacion:

```text
/health
```

## Despliegue sugerido en Proxmox

Recomendacion: no instales Docker directamente sobre el host Proxmox VE. Mantén el host limpio y crea una VM Debian/Ubuntu para esta aplicacion.

### Instalacion automatizada

En una VM Debian/Ubuntu recien creada:

```bash
sudo apt-get update
sudo apt-get install -y git
git clone https://github.com/pedropablobm/OpenCredential.AdminWeb.git
cd OpenCredential.AdminWeb
sudo bash scripts/install-proxmox-vm.sh
```

Variables opcionales:

```bash
sudo ADMINWEB_PORT=8080 ADMIN_USERNAME=admin bash scripts/install-proxmox-vm.sh
```

El script instala Docker, clona el repositorio en `/opt/opencredential-adminweb`, genera credenciales seguras en `.env` y ejecuta `docker compose up -d --build`.

Nota: si el repositorio es privado, primero debes autenticar `git clone` con un token de GitHub o subir este script a una ubicacion publica. Las URL de `raw.githubusercontent.com` no funcionan sin acceso publico al archivo.

### Instalacion manual

1. Crear una VM Debian/Ubuntu en Proxmox.
2. Instalar Docker Engine y Docker Compose Plugin.
3. Clonar este repositorio en el servidor.
4. Crear un archivo `.env` con credenciales seguras a partir de `.env.example`.
5. Ejecutar `docker compose up -d --build`.
6. Publicar el puerto `8080` directamente o detras de un reverse proxy.
7. Respaldar los volumenes Docker `opencredential_postgres_data` y `opencredential_adminweb_data`.

## Siguiente paso recomendado

Para MySQL/MariaDB basta con cambiar:

- `Database__Provider=MySql`
- `Database__ConnectionString=Server=...;Port=3306;Database=...;User ID=...;Password=...`

Y despues, desde la interfaz:

1. `Probar conexion`
2. `Ajustar tablas de AdminWeb`
3. `Guardar configuracion`

## Secretos y archivos que no deben versionarse

- `App_Data/adminweb-runtime.json`
- `App_Data/keys/`
- `.env`
- cookies exportadas
- logs locales
- artefactos de `bin/`, `obj/` y `artifacts/`
