# Propuesta: servidor de aplicaciones para Desktop

Estado: propuesta; no existe todavia una API ni un plugin Desktop implementados.

## Compatibilidad

El plugin `DatabaseAuth` actual mantiene sus conexiones directas PostgreSQL/MySQL y su cache offline. Un plugin nuevo podria implementar `IPluginAuthentication`, `IPluginAuthorization` e `IPluginAuthenticationGateway` sin sustituirlo. La configuracion y el orden de plugins deben evitar que otra ruta de autenticacion permita el acceso involuntariamente: el motor acepta que al menos un plugin de autenticacion tenga exito, mientras que todos los plugins de autorizacion habilitados deben aprobar.

Los endpoints actuales de login administrativo y del portal crean sesiones web; no constituyen un contrato de inicio de sesion de equipos. No deben tratarse como API equivalente ni conceder privilegios de administrador a estaciones.

## Contrato a disenar

- API versionada dedicada a estaciones, con autenticacion individual de dispositivo, autorizacion por alcance y revocacion. Definir alta, rotacion y recuperacion de credenciales del dispositivo antes de implementar.
- TLS con comprobacion de CA y nombre del servidor; sin aceptar certificados invalidos. No registrar contrasenas, tokens ni hashes en logs.
- Autenticacion del usuario, estado y bloqueo, autorizacion de equipo/grupos y acciones de sesion con respuestas diferenciadas para denegacion y fallo de conectividad. Definir identificadores de operacion e idempotencia para reintentos.
- Establecer semantica transaccional para intentos fallidos, migracion de hashes, concurrencia, tiempos limite y eventos de inicio/cierre; la API no debe entregar hashes de la base al cliente como mecanismo de autenticacion online.
- Disenar explicitamente el modo offline: politica opt-in, cache local protegida, caducidad, alcance y tratamiento de revocacion diferida. Solo un fallo de conectividad elegible puede activar fallback; una denegacion o error TLS no debe convertirse en acceso offline. La compatibilidad con la cache actual debe analizarse antes de compartirla.
- Limitar intentos por dispositivo y cuenta, auditar sin secretos, minimizar datos y asegurar que el acceso administrativo siga separado.

## Entrega propuesta

1. Inventariar las reglas, operaciones de sesion y configuraciones del plugin actual; acordar contrato y amenazas.
2. Implementar API y pruebas de autorizacion, concurrencia, bloqueo, recuperacion y errores sin modificar clientes existentes.
3. Implementar el plugin opcional y probar autenticacion, autorizacion, gateway, perdida de red y recuperacion en Windows.
4. Desplegar primero el servidor compatible y luego habilitar el plugin por equipos piloto; conservar la configuracion directa para reversión controlada.

No se promete equivalencia funcional ni acceso de invitados hasta implementar y validar esos flujos. Un servidor de aplicaciones introduce dependencia de disponibilidad; readiness no garantiza por si solo que todas las operaciones de acceso funcionen.
