# Compatibilidad con OpenCredential Desktop

Esta rama parte de AdminWeb `8424d4177d957371320f0859b63dbec7da21adfb`. El plugin Desktop `DatabaseAuth` conserva su conexion directa a PostgreSQL/MySQL y su cache offline; esta rama no instala ni modifica el plugin.

| Componente | Estado | Validacion |
|---|---|---|
| Desktop DatabaseAuth | Se conserva sin modificaciones; usa por defecto `users.hash_method` y `users.password_hash`. | Verificacion aislada de los 12 formatos generados por AdminWeb con `UserEntry` y `BCryptHasher` de Desktop; falta prueba completa de login Desktop. |
| AdminWeb JSON | Editar un usuario sin nueva clave conserva algoritmo y hash existentes. | Prueba sintetica con credencial MD5 heredada. |
| AdminWeb MySQL | Editar sin nueva clave conserva algoritmo y hash. Las conexiones admiten caracteres especiales en claves. | Prueba sintetica contra MySQL 8.4 en Linux. |
| AdminWeb PostgreSQL | Editar sin nueva clave conserva algoritmo y hash. Las conexiones admiten caracteres especiales en claves. | Prueba sintetica contra PostgreSQL 16 en Linux, con TLS `Require` de laboratorio. |
| Esquema y despliegue | Sin cambios de migracion ni Docker Compose en esta fase. | No se ha validado actualizacion de una base existente ni se garantiza compatibilidad integral. |

Las claves nuevas o restablecidas conservan espacios iniciales y finales; Desktop verifica la contraseña recibida sin recortarla. Las contraseñas creadas por versiones anteriores no se modifican: si antes se eliminaron espacios, el usuario debe seguir usando la clave efectivamente guardada hasta que se restablezca. El cambio se probo con cuentas sinteticas en JSON, MySQL y PostgreSQL, incluyendo recuperacion por token; no constituye una prueba completa del inicio de sesion Desktop.

La prueba `dotnet run --project Tests/DesktopHashCompatibility/DesktopHashCompatibility.csproj` enlaza los archivos fuente `UserEntry.cs` y `BCryptHasher.cs` del repositorio Desktop vecino. Si Desktop esta en otra ubicacion, pasar la propiedad `DesktopSourceRoot` al compilar. El ensayo sustituye el logger y la configuracion de registro por la codificacion HEX predeterminada, y utiliza la biblioteca BCrypt referenciada por AdminWeb, no el ensamblado compilado de Desktop. No cubre instalaciones con codificacion Base64 configurada ni sustituye las pruebas del servicio Windows, autorizacion y cache offline.

Antes de usar esta rama en una instalacion real, probar ambos proveedores con copias sinteticas del esquema y validar inicio de sesion Desktop, bloqueo, grupos, sesiones y modo offline. Respaldar la base y `/data`; `Database__AutoInitialize=true` puede crear o alterar objetos, por lo que no debe activarse sobre una base existente sin revisar sus efectos. Mantener la version anterior disponible para reversión; cambiar de version no revierte migraciones ni datos.
