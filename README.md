# OpenCredential AdminWeb

Interfaz web administrativa para gestionar:

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
$env:DOTNET_CLI_HOME='C:\Github\pgina-mysql8\.dotnet-home'
dotnet run --project C:\Github\pgina-mysql8\OpenCredential.AdminWeb\OpenCredential.AdminWeb.csproj
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
  "ConnectionString": "Host=localhost;Port=5432;Database=opencredential_admin;Username=opencredential;Password=secret",
  "AutoInitialize": true
}
```

Cuando `AutoInitialize=true`, la aplicacion crea si faltan las tablas:

- `users`
- `careers`
- `levels`
- `computers`
- `usage_records`

Esto mantiene compatibilidad con el esquema academico del proyecto y agrega las tablas necesarias para administrar equipos y estadisticas de uso.

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
  "Password": "AdminWeb2026!",
  "PasswordHash": "",
  "HashMethod": "BCRYPT",
  "Role": "SuperAdmin",
  "CookieName": "opencredential_admin",
  "SessionHours": 12,
  "Accounts": []
}
```

Recomendaciones:

- En produccion cambia inmediatamente `AdminAuth__Username` y `AdminAuth__Password`
- Si prefieres no guardar clave en texto plano, usa `AdminAuth__PasswordHash` junto con `AdminAuth__HashMethod`
- Publica la app detras de HTTPS cuando la expongas fuera de la red interna

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
  -e AdminAuth__Password=AdminWeb2026! \
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

### Configurar base externa desde la interfaz

1. Inicia sesion como `SuperAdmin`.
2. Ve al panel `Configuracion de base de datos`.
3. Selecciona `PostgreSQL` o `MySQL / MariaDB`.
4. Captura host, puerto, base de datos, usuario, clave y modo SSL.
5. Usa `Probar conexion`.
6. Si la conexion es correcta, usa `Guardar configuracion`.
7. Reinicia el contenedor:

```bash
docker restart opencredential-adminweb
```

La configuracion se guarda en `/data/adminweb-runtime.json`, por eso es importante mantener montado el volumen `opencredential_adminweb_data:/data`.

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
4. Crear un archivo `.env` con credenciales seguras.
5. Ejecutar `docker compose up -d --build`.
6. Publicar el puerto `8080` directamente o detras de un reverse proxy.
7. Respaldar los volumenes Docker `opencredential_postgres_data` y `opencredential_adminweb_data`.

## Siguiente paso recomendado

Para MySQL/MariaDB basta con cambiar:

- `Database__Provider=MySql`
- `Database__ConnectionString=Server=...;Port=3306;Database=...;User ID=...;Password=...`
