# Invitados temporales: primera fase local

Esta fase introduce únicamente reglas de dominio y regresiones sintéticas en `Services/GuestAccessPolicy.cs`. **No existe todavía una pantalla, API, tabla persistente, enrolamiento de equipos ni plugin Desktop de invitados. No concede acceso a Windows.** No usar este modelo como mecanismo de seguridad completo.

La política predeterminada está deshabilitada, con duración predeterminada de 60 minutos y máximo de 120. El modelo verifica equipo asignado, plazo de activación, duración, uso previo, revocación, expiración y cierre. El límite local puede reducir, pero no ampliar, la duración central. La expiración se calcula desde la primera activación y no se renueva mediante el modelo.

Las operaciones inmutables todavía no son una reserva de uso único entre procesos: su implementación real requiere persistencia transaccional y una comparación de estado atómica. Antes de aceptar conexiones de equipos faltan identidad de dispositivo, aprobación, auditoría, credenciales temporales, validación de permisos y protección contra repetición. El reloj y la integridad del estado local necesitan un diseño y pruebas adicionales.

## Trabajo pendiente

1. Definir migración compatible y repositorios SQL PostgreSQL/MySQL, incluyendo revocación y reserva atómica, sin alterar credenciales existentes.
2. Crear API y UI administrativa autorizadas por rol y alcance, con auditoría y manejo de errores. No reutilizar cookies administrativas como identidad de equipos.
3. Enrolar dispositivos con credenciales individuales revocables y transporte autenticado. Diseñar autorizaciones offline firmadas, vinculadas a equipo y sin clave de firma en Desktop.
4. Implementar plugin explícito desactivado por defecto y aprobación de administrador local. No convertir usuarios inexistentes, contraseñas incorrectas ni cuentas bloqueadas en invitados.
5. Implementar vencimiento persistente, avisos y bloqueo de sesión; demostrar que los demás proveedores Windows no permiten eludir el vencimiento. Autenticación correcta debe llevar al cierre de la sesión temporal antes de iniciar una sesión normal.
6. Validar primero en VM con cuenta de recuperación: reinicios, concurrencia, reloj, desconexión, revocación diferida, bloqueo/desbloqueo, RDP y perfiles sin borrado automático.

Prueba local: `dotnet run --project .\Tests\RegressionTests.csproj --no-restore` desde la raíz de AdminWeb. No inicia Desktop, no escribe en el registro ni solicita credenciales reales.
