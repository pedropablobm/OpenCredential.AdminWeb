# AdminWeb: accesibilidad, resiliencia y paginacion

## Alcance y compatibilidad

Cambios exclusivos de AdminWeb. No cambian proveedor SQL, esquema de usuarios, credenciales, roles, formatos de Desktop ni su conexion directa a PostgreSQL/MySQL. No se implementan ni habilitan un plugin invitado, autorizaciones offline, una API de autenticacion de equipos ni la administracion remota de caches.

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
