# Configuracion PostgreSQL con TLS verificado

El formulario permite `VerifyCA` y `VerifyFull` y una ruta opcional de CA publica dentro del contenedor. `VerifyFull` valida tanto la cadena de certificados como el nombre o la IP del servidor. Si se usa una CA privada, debe montarse dentro del contenedor; indicar una ruta no verifica por si solo su contenido. Probar conexion valida los ajustes antes de guardarlos.

El resumen muestra la configuracion suministrada por el despliegue o la configuracion guardada. La configuracion guardada en `/data/adminweb-runtime.json` prevalece al iniciar y puede estar pendiente de reinicio. No contiene la clave en la respuesta web, pero el archivo de `/data` si contiene credenciales y debe respaldarse y protegerse. Una clave guardada solo se reutiliza para el mismo proveedor, servidor, puerto, base y usuario; no se reutiliza la clave procedente del despliegue.

El formulario no configura TLS de MySQL. Si MySQL requiere verificar su servidor, administra esa conexion mediante la configuracion del despliegue y evita sustituirla desde el formulario. No se ha agregado una migracion automatica de configuraciones anteriores: si existe una configuracion guardada con TLS deshabilitado, no reinicies a ciegas. Primero prepara una recuperacion con respaldo protegido y valida una conexion `VerifyFull` con su CA.
