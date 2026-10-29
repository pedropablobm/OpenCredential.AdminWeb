# AdminWeb: accesibilidad, resiliencia y paginacion

## Alcance y compatibilidad

Cambios exclusivos de AdminWeb. No cambian proveedor SQL, esquema de usuarios, credenciales, roles, formatos de Desktop ni su conexion directa a PostgreSQL/MySQL. No se implementan ni habilitan un plugin invitado, autorizaciones offline, una API de autenticacion de equipos ni la administracion remota de caches.

En la primera fase de simplificacion visual, Inicio muestra cuatro indicadores principales y conserva indicadores secundarios, filtros y graficos en una seccion desplegable. La importacion aclara que las cuentas nuevas requieren clave inicial. La configuracion muestra un resumen no secreto del modo TLS configurado, distingue la conexion suministrada por el despliegue de una configuracion guardada y pide confirmacion antes de ajustar el esquema o activar inicializacion automatica. El resumen no verifica que la conexion ni el certificado funcionen.

Para PostgreSQL, el formulario admite `VerifyCA` y `VerifyFull` con una ruta opcional de CA dentro del contenedor. La CA debe estar montada y ser accesible; probar la conexion verifica el certificado antes de guardar. Guardar genera una configuracion persistida que prevalece sobre el entorno al reiniciar y contiene la clave en el volumen privado `/data`; su respaldo debe protegerse. Una clave guardada solo se reutiliza si proveedor, host, puerto, base y usuario no cambian; la clave procedente del entorno no se devuelve ni se reutiliza. Para MySQL, el selector web no modifica TLS y se usa la configuracion predeterminada del proveedor; esto no equivale a `VerifyFull`.

En Usuarios, la tabla omite el algoritmo de hash, que sigue disponible en las opciones avanzadas del formulario; editar sin cambiar la clave conserva el algoritmo existente. En Equipos se muestran tres indicadores principales y los demas permanecen disponibles en una seccion desplegable. La leyenda completa del mapa tambien se puede desplegar sin retirar estados ni filtros. Los desplegables del formulario participan en el recorrido de teclado del dialogo.

Reportes presenta cuatro indicadores principales y conserva los restantes en un desplegable; los graficos, destacados y sesiones estan separados en pestañas con navegacion de teclado. Los filtros adicionales siguen activos al cerrar su seccion y se indica su cantidad. El CSV sigue exportando las sesiones cargadas en el navegador y el PDF consulta con los filtros actuales; no se han cambiado contratos, datos ni limites de exportacion.

Importaciones separa la revision del archivo y su confirmacion explicita. Cambiar el archivo o cerrar la sesion invalida la revision; una interrupcion durante la confirmacion advierte que el resultado puede ser incierto y exige revisar los datos antes de reintentar. La busqueda superior indica su ambito real y se deshabilita en secciones que no disponen de busqueda; conserva los filtros de cada modulo.

La carga de usuarios y la de reportes son independientes del resumen: si una falla, se informa como actualizacion parcial sin ocultar un resumen que si respondio. Se conservan resultados anteriores solo cuando corresponden a los mismos filtros; con filtros diferentes se ocultan hasta obtener datos validos. Las exportaciones de reportes se bloquean mientras se actualiza o si su ultima carga fallo, para no exportar datos antiguos bajo filtros nuevos. La ultima actualizacion global corresponde al resumen, no certifica que todas las secciones hayan cargado.

Las pestañas de Equipos y Catalogos exponen su seleccion y admiten flechas, Inicio y Fin con teclado. En el portal personal, un fallo al consultar el historial ya no se presenta como ausencia de accesos: se muestra el error y un boton para reintentar. Las respuestas tardias de una sesion anterior no deben cerrar ni actualizar una sesion nueva.

- Login con etiquetas permanentes, nombres/autocompletado adecuados y mensajes anunciables.
- Paneles laterales como dialogos identificados: foco inicial, ciclo de Tab/Shift+Tab, Escape, devolucion de foco y fondo inerte. Los paneles cerrados son `hidden` e `inert`, no solo trasladados fuera de pantalla.
- Confirmacion antes de descartar cambios de formularios/mapas; guardados fallidos conservan entradas, muestran error y permiten reintentar. Se impiden envios de formulario duplicados mientras una peticion esta pendiente.
- Foco visible, enlace para saltar al contenido y respeto a movimiento reducido/colores forzados. Los resultados de restablecer claves no anuncian la clave en texto plano.
- Estado de actualizacion visible para todos los roles, ultima carga correcta y reintento manual. Los fallos conservan el ultimo conjunto de datos valido en vez de reemplazarlo por ceros. El indicador describe carga de datos, no disponibilidad de cada cliente Desktop.
- La configuracion SQL no se vuelve a cargar en cada refresco, para no sobrescribir ediciones pendientes. Los informes solo se solicitan al visitar su vista, no desde todos los modulos.
- Las respuestas antiguas no repueblan datos tras cerrar sesion; un 401 antiguo tampoco cierra una sesion nueva.

## API aditiva

### GET /api/users

Requiere sesion administrativa y rol `SuperAdmin` o `Coordinator`. `Operator`, `Viewer`, usuarios del portal y peticiones anonimas no reciben este listado. Las restricciones existentes para editar cuentas privilegiadas siguen vigentes.

| Parametro | Valor |
|---|---|
| `page` | Desde 1, maximo 1000000; predeterminado 1 |
| `pageSize` | 1–100; predeterminado 20 |
| `search` | Hasta 200 caracteres; usuario, documento, correo, nombres o apellidos |
| `status` | `active`, `inactive` u omitido |
| `careerId` | ID positivo u omitido |

Respuesta: `items`, `page`, `pageSize`, `total`, `totalPages`, `registeredUsers`, `activeUsers`. Una pagina posterior al final se ajusta a la ultima disponible; un resultado vacio tiene una pagina. Los totales registrados/activos son globales; `total` corresponde a los filtros. El panel solicita actualmente 8 filas por pagina.

PostgreSQL/MySQL ejecutan filtros, conteos y `LIMIT/OFFSET` en servidor. Conteos y filas comparten una transaccion de lectura `RepeatableRead`; las consultas grandes admiten cancelacion. Los metadatos de esquema/grupos reutilizan consultas pequenas sincronas existentes. La busqueda esta parametrizada y `%`, `_`, `!` se tratan como caracteres literales. El comportamiento de acentos sigue la colacion del proveedor. El listado SQL no selecciona `password_hash`; su DTO tampoco contiene ese campo.

El modo JSON utiliza el mismo contrato, filtrando el almacenamiento local en memoria; no se presenta como una optimizacion SQL.

### GET /api/summary?compact=true

Devuelve catalogos, equipos, salas y el mapa, sin todos los usuarios ni `usageRecords`. La auditoria solo corresponde a `SuperAdmin`, igual que antes. El resumen sin parametro conserva su contrato completo para clientes existentes.

El dashboard SQL deja de cargar hashes, informacion personal innecesaria, grupos de cada usuario e historial de uso manual. Todavia utiliza una proyeccion minima de usuarios y las consultas existentes de sesiones: **no** se ha completado la agregacion integral en SQL ni la paginacion de todos los informes/equipos. No se introducen limites silenciosos que cambien sus totales.

## Pruebas

Los siguientes comandos compilan/generan datos sinteticos en temporales y lanzan servidores aislados de loopback; no deben apuntar a servicios institucionales:

```powershell
dotnet run --project .\Tests\RegressionTests.csproj
node --test .\Tests\frontend.test.cjs
dotnet build .\OpenCredential.AdminWeb.csproj -c Release
```

Las variantes SQL existentes `--postgres-port` y `--mysql-port` ahora comprueban tambien paginacion, filtros, caracteres especiales, grupos, cancelacion, ausencia de hashes y contrato compacto. **Solo** usar bases desechables: el runner crea bases y datos de prueba. No usar puertos productivos.

Prueba opcional de navegador (dependencia solo de pruebas; no se incorpora a la aplicacion):

```powershell
# Instalar en un entorno de pruebas aislado, no en la imagen de produccion.
python -m pip install -r .\Tests\requirements-browser.txt
# Si no existe un navegador de pruebas compatible:
python -m playwright install chromium
python .\Tests\test_admin_ui.py
# Tambien admite --browser con la ruta explicita de Chromium existente.
```

El script arranca su propio AdminWeb en modo JSON con datos/clave efimeros y perfil Chromium nuevo, siempre headless. No acepta URL de un despliegue existente. Comprueba paginacion, teclado, foco, cambios pendientes, error de guardado, caida/reconexion, preservacion de configuracion editada y viewport estrecho. Detiene solo su proceso de prueba al terminar. Los datos/logs sinteticos quedan en el temporal privado, no en Git.

## Despliegue y pendientes

Validacion local del 2026-10-23: compilacion Release sin errores/advertencias; 13 grupos de regresion por ejecucion con PostgreSQL 16 y MySQL 8.4 desechables, incluidas las pruebas JSON/HTTP; 7 pruebas de frontend; 6 escenarios de navegador Chromium headless con datos sinteticos. No se usaron bases reales ni perfiles de navegador personales. Los contenedores de prueba usaron almacenamiento tmpfs y se retiraron al terminar; los servicios existentes no se modificaron.

No requiere migracion ni configuracion nueva. Desplegar el backend y los archivos estaticos juntos; una interfaz nueva necesita el endpoint paginado. Volver a la imagen/codigo anterior permite revertir sin restaurar ni borrar datos.

Pendientes: Narrador/NVDA reales, auditoria WCAG integral, pruebas de carga, consultas de sesiones/agregaciones completamente SQL, paginacion del resto de listados y la fase Desktop de sincronizacion/invitados. No se modifica Docker Compose, no se reinician servicios existentes ni se publica una imagen por ejecutar estas pruebas.
