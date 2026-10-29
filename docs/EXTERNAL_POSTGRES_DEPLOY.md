# AdminWeb en Linux con PostgreSQL existente

Este procedimiento no crea PostgreSQL ni inicializa su esquema automaticamente. El esquema existente debe ser compatible; revisa `SqlAdminRepository.EnsureSchema`, la version instalada y las migraciones necesarias con el administrador de la base. Haz un respaldo comprobado antes de autorizar cualquier cambio de esquema. `AutoInitialize=false` no convierte la aplicacion en solo lectura: las operaciones administrativas y algunos procesos de descubrimiento pueden escribir datos. No uses el boton de aplicar esquema sin autorizacion y respaldo.

## Preparacion

1. Instala Docker Engine y el plugin Compose en Linux. Comprueba la conectividad desde el contenedor al puerto PostgreSQL y que el servidor admite TLS. El nombre DNS de la conexion debe coincidir con el certificado. Necesitas un proxy HTTPS en el servidor y su IP de origen efectiva vista por el contenedor. Restringe tambien el acceso de red a PostgreSQL.
2. Obtiene esta version del codigo mediante una fuente aprobada. Los cambios locales no publicados no apareceran al clonar el repositorio remoto; transfiere el codigo de forma segura o publicalo solo con autorizacion. Ejecuta los siguientes comandos desde la raiz de AdminWeb.
3. Copia el certificado publico de la CA que firmo el certificado de PostgreSQL a `postgres-ca.crt`. No copies claves privadas. Crea la configuracion privada:

```bash
umask 077
cp adminweb.production.env.example adminweb.production.env
chmod 600 adminweb.production.env
${EDITOR:-vi} adminweb.production.env
```

Rellena la cadena de conexion, usuario y clave administrativa directamente en el editor; no compartas el archivo. Usa `SSL Mode=VerifyFull` y `Root Certificate=/certs/postgres-ca.crt`. Escapa los valores especiales de la cadena segun las reglas de Npgsql y considera tambien las reglas de interpolacion de Compose para `env_file`: por ejemplo, los valores entre comillas simples son literales. La cuenta de base necesita los permisos apropiados sobre el esquema existente; aplica minimo privilegio. No configures `ADMINWEB_ISOLATED_PREVIEW` en produccion.

Las variables del archivo quedan en el entorno del contenedor: administradores de Docker pueden leerlas mediante inspeccion. Protege el host, sus respaldos y el acceso a Docker; no publiques `docker compose config` ni volcados de inspeccion que puedan incluir secretos. Los archivos de `/data`, incluida la configuracion guardada desde la interfaz y las claves de Data Protection, tambien son sensibles.

4. Configura un proxy HTTPS externo que termine TLS y reenvie a `http://127.0.0.1:18766`. Configura `ForwardedHeaders__TrustedProxies__0` con la IP real del proxy observada desde el contenedor, no una IP supuesta: depende de la red Docker. Solo confia en proxies controlados que sobrescriban las cabeceras reenviadas. Sin un proxy correctamente configurado no expongas el panel; valida cookies `Secure`, origen, IP de auditoria y redirecciones HTTPS antes de usarlo.

## Despliegue

Los siguientes comandos construyen una imagen y crean o actualizan el servicio, sin levantar PostgreSQL:

```bash
docker compose -p oc-admin-external -f docker-compose.external-postgres.yml build
docker compose -p oc-admin-external -f docker-compose.external-postgres.yml up -d
```

Inspeccion sin mostrar configuracion ni credenciales:

```bash
docker compose -p oc-admin-external -f docker-compose.external-postgres.yml ps
curl -fsS http://127.0.0.1:18766/health
curl -fsS http://127.0.0.1:18766/health/ready
```

`/health` comprueba el proceso; `/health/ready` consulta el repositorio, pero no verifica todas las tablas, permisos ni operaciones. El healthcheck de Docker usa `/health`. Comprueba tambien desde HTTPS la autenticacion y operaciones autorizadas, inicialmente con datos de prueba. Si revisas logs, trata su contenido como sensible.

## Precedencia y persistencia

El volumen `adminweb_external_data` conserva claves y configuracion. La aplicacion carga `/data/adminweb-runtime.json` **despues** de las variables de entorno: si existe, sus valores pueden cambiar la base y `AutoInitialize`. Emplea un proyecto Compose y volumen nuevos y verifica su procedencia antes de arrancar. Guardar la configuracion SQL desde la interfaz genera ese archivo; no lo uses para cambiar esta instalacion sin evaluar sus efectos. La pantalla de aplicacion del esquema puede modificar la base aun con inicializacion automatica desactivada.

## Actualizacion y recuperacion

Antes de actualizar, conserva una copia verificada del volumen `/data` y un respaldo consistente de PostgreSQL coordinado con su administrador; protege ambos como secretos. Conserva ademas la imagen anterior identificada por ID y su configuracion. Construir y ejecutar `up -d` modifica el servicio; valida salud, acceso HTTPS y operaciones despues. Para revertir, despliega explicitamente la imagen anterior con la configuracion compatible, sin borrar el volumen. Una restauracion de PostgreSQL puede sobrescribir cambios posteriores y requiere autorizacion, ventana de mantenimiento y un procedimiento de recuperacion probado; volver a una imagen anterior no revierte migraciones. No uses `down -v` ni elimines volumenes.

El acceso de invitados es experimental: todavia no tiene API, interfaz, persistencia completa ni plugin Desktop y no esta disponible para produccion.
