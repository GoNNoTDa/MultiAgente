# MultiAgente — Estado

Plan: `Documentacion/MultiAgente_Plan.md` (leer antes de seguir) · Maqueta: `Documentacion/MultiAgente_Maqueta.html`.

## Fases

- [x] Análisis del gestor anterior y alcance acordado con el usuario (20/10/2026)
- [x] Plan v1, maqueta, README, CLAUDE.md
- [ ] **F0 · Comprobaciones**
  - [x] Instalar en este PC lo que faltaba: `@openai/codex` 0.163.0, `@agentclientprotocol/claude-agent-acp` 0.94.0, `@agentclientprotocol/codex-acp` 2.2.2 (ya estaban Claude Code 2.1.292 y Gemini CLI 0.37.0)
  - [x] ACP de los tres: `initialize` y `session/new` con carpeta de cuenta temporal (`tools\Sondear-Agentes.mjs`)
  - [ ] Login aislado por cuenta en los tres (necesita al usuario: `/login`, `codex login`, clave de Gemini) y que siga valiendo tras mover la carpeta
  - [ ] Mensaje real con imagen, petición de permiso y respuesta, `session/load` tras cerrar y abrir
  - [ ] Dos cuentas del mismo proveedor compartiendo la carpeta de sesiones: `session/load` desde la otra cuenta
  - [ ] Cómo llega un error de límite por ACP en cada agente (forma del error / `stopReason`)
  - [ ] Consumo previo: endpoint de uso de Claude con el token de una carpeta de cuenta; qué informa codex-acp
- [x] **F1 · Esqueleto portátil** (20/10/2026)
  - [x] Solución `MultiAgente.sln`: Nucleo (Rutas, Registro con tapado de secretos, InstanciaUnica por carpeta, Requisitos), Datos (SQLite, esquema 1 del plan §8, ajustes), App (ventana WinForms + WebView2 con `ui\`), Tests
  - [x] Interfaz inicial: lateral, bienvenida con los agentes detectados y sus versiones, «Instalar lo que falta», caja de escribir (desactivada hasta tener cuentas), carpeta y versión al pie
  - [x] Posición de la ventana guardada en la base; tema claro/oscuro de Windows (también la barra de título)
  - [x] Órdenes de prueba: `--comprobar [json]`, `--captura <png>`, `--fuera`, `--cerrar`
  - [x] `tools\Empaquetar.ps1` (exe autocontenido de un solo fichero, ~49 MB, + `ui\`, `tools\`, README) y `tools\Probar-Portatil.ps1`
  - [x] Pruebas: 21 unitarias en verde; portabilidad 14/14 (copia en ruta con espacios, datos dentro, nada en %APPDATA%/%LOCALAPPDATA%, captura, instancia única, `--cerrar` sin dejar procesos del navegador, mover la carpeta ya usada)
- [x] **F2 · Un agente** (20/10/2026)
  - [x] Núcleo: cliente ACP (`ConexionAcp`), `SesionAgente` (arranque, recuperar sesión o traspaso, mensajes en cola, permisos, interrumpir, opciones de modo/modelo/esfuerzo, MCP por sesión), conversación canónica `hilo.jsonl`, bóveda AES-GCM, proveedores y entorno aislado por cuenta, orden de cada agente con Node sin cmd.exe
  - [x] Cuentas: suscripción (Autorizar… abre la CLI oficial con la carpeta de la cuenta), clave de API, **API compatible** con catálogo de 31 proveedores (`catalogo\proveedores.json`) y lista de modelos en vivo; Codex compatible con `config.toml` en la carpeta de la cuenta
  - [x] Datos: repositorio de cuentas, proyectos, sesiones y tramos; esquema 2 (Endpoint, Modelo, Catalogo)
  - [x] Interfaz: lateral con proyectos y sesiones (estado y proveedor), inicio con carpeta + cuenta + caja, sesión (markdown, código coloreado, herramientas con diferencias, plan de tareas, razonamiento, avisos), barra de permiso, cola, chips de opciones, órdenes «/», arrastrar y soltar con rutas reales, Ctrl+V de capturas, adjuntar, interrumpir (■ o Esc dos veces), cambiar la cuenta de la sesión, archivar; vista de cuentas con el catálogo
  - [x] Pruebas: 51 (con agente falso por ACP real) + 3 con los agentes reales con cuenta vacía (`MULTIAGENTE_PRUEBA_REAL=1`) + portabilidad 14/14 + interfaz con puente simulado (`tools\ui-prueba.html`)
  - [ ] Prueba real con cuentas autorizadas (necesita al usuario: Autorizar… o una clave)
- [x] **F3 · Varias cuentas** (20/10/2026)
  - [x] El «cerebro» pasa al núcleo (`Gestor`, con `IRepositorio` y `OpcionesGestor`): se prueba entero sin ventana
  - [x] Equipos (`Equipo`, tabla EquipoCuenta): «Principal» automático con todas las cuentas; reglas Prioridad, Rotación, La menos usada y Suscripciones primero; pasar a otro proveedor; cuenta atrás
  - [x] `Enrutador` (lógica pura): margen de cada cuenta (umbrales 92 % 5 h y 95 % semana, enfriamiento), cuenta para sesiones nuevas y para el relevo (primero mismo proveedor y misma API), hora de reinicio leída del mensaje de límite
  - [x] Consumo de las suscripciones de Claude (servicio de uso, cada 3 min; 1 min por encima del 80 %) y enfriamiento tras un error de límite (guardado entre arranques)
  - [x] Límite detectado como error ACP o como respuesta corta del agente («usage limit reached…»)
  - [x] Relevo: por límite (con «Continúa donde lo dejaste»), por consumo (al terminar el turno, sin cortarlo) o al escribir con la cuenta agotada (al momento); mismo proveedor → `session/load` de la misma sesión desde las conversaciones compartidas; otro → traspaso con resumen vivo
  - [x] Conversaciones compartidas entre cuentas de un proveedor (`datos\comun\<proveedor>`, uniones NTFS reparadas al arrancar y tras mover la carpeta)
  - [x] Resumen vivo (`resumen.md`) con `claude -p` + Haiku tras los turnos (activable; solo equipos con más de una cuenta)
  - [x] Interfaz: «Automático · equipo» o cuenta fija al crear la sesión y en la cabecera, banda de relevo con cuenta atrás (Cambiar ya / Cancelar), línea «Sigue en…», consumo y enfriamiento en el lateral y en Cuentas, edición de equipos
  - [x] Pruebas: 75 (8 de relevo de principio a fin con el agente falso, tabla del enrutador, consumo, uniones reales) + 3 reales + portabilidad
  - [ ] Prueba real: relevo entre dos cuentas autorizadas de verdad (Claude ↔ Claude por `session/load` compartido; Claude → GLM/Codex por traspaso)
- [x] **F4 · Extensiones compartidas** (20/10/2026)
  - [x] MCP: catálogo propio (`CatalogoMcp`, en la base sin secretos; secretos en la bóveda) que se pasa a cada sesión por ACP en el formato v1 del esquema oficial (stdio con ruta absoluta y `cmd /c` para los .cmd, http/sse con cabeceras, `${VARIABLE}`); solo los transportes que el agente declara; importar de `~/.claude.json`, `~/.gemini/settings.json` y `~/.codex/config.toml` (solo lectura)
  - [x] Carpetas compartidas: skills (una sola carpeta para Claude, Codex y Gemini), órdenes, agentes, plugins y estilos de Claude, prompts de Codex, órdenes y extensiones de Gemini
  - [x] Ficheros sincronizados entre cuentas de un proveedor (gana el más reciente): CLAUDE.md y settings.json (plugins activos) de Claude, AGENTS.md de Codex, GEMINI.md de Gemini
  - [x] Instrucciones comunes en un bloque marcado dentro de los tres ficheros de instrucciones
  - [x] Plugins de Claude: se instalan con /plugin y valen para todas las cuentas; activar/desactivar desde MultiAgente
  - [x] Pantalla «Extensiones»: MCP (añadir, editar, activar, quitar, importar), skills (nueva, importar de tu configuración, añadir carpeta, quitar a la papelera), órdenes/agentes/prompts, plugins, instrucciones comunes (y copiar las tuyas)
  - [x] Pruebas: 82 (+ importar de los tres formatos, definición ACP, bloque e instrucciones entre cuentas, skills comunes, plugins, MCP que llega al agente filtrado por transporte) + 3 reales + portabilidad
- [x] **F5 · Estadísticas y límites** (20/10/2026)
  - [x] Uso de cada turno a partir de lo que informa el agente por ACP (`usage_update`: contexto y coste acumulado; `usage` de la respuesta: tokens acumulados) → tabla `UsoTurno` (esquema 3)
  - [x] Límites por cuenta: umbrales de relevo de 5 h y semanal (por defecto 92 % y 95 %) y presupuesto mensual; aviso al 80 % y, al agotarlo, la cuenta queda sin margen (el equipo releva); el gasto del mes se recalcula al cambiar de mes
  - [x] Pantalla «Estadísticas»: hoy / 7 / 30 días, turnos, tokens y coste; por cuenta, por día y sesiones que más han usado
  - [x] Contexto usado en la cabecera de la sesión; al arrancar se reabre la última sesión que se miraba; icono propio (`tools\Generar-Icono.ps1`)
  - [x] Pruebas: 86 (+ umbrales y presupuesto, uso por turno con acumulados, presupuesto agotado → aviso + relevo + estadísticas) + 3 reales + portabilidad
- [x] **v0.6 · Proyectos, cambios, consumo e historiales** (20/10/2026)
  - [x] Alta de cuentas arreglada (el campo «tipo» se pisaba con el tipo de mensaje); al autorizar, la lista se refresca sola
  - [x] Proyectos a mano (esquema 4: varios por carpeta), plegar, mover sesiones (arrastrar o menú), renombrar, quitar
  - [x] Visor de cambios: git de la carpeta (`CambiosGit`, sin tocar el índice) y ediciones de la sesión
  - [x] Vista «Consumo y límites»: 5 h, semanal (reinicios) y mes frente a presupuesto, por cuenta
  - [x] Importar conversaciones previas (Claude Code, Codex, Gemini CLI): sesión con la conversación; con una cuenta del mismo proveedor se copia el original a `datos\comun` y se retoma con `session/load`
  - [x] Pruebas: 92
- [x] **v0.7 · Traductor OpenAI → Claude Code** (20/10/2026)
  - [x] `Traductor` + `ServidorHttp` (TcpListener en 127.0.0.1, sin http.sys): Messages de Anthropic ↔ Chat Completions, a trozos (SSE) y entero; razonamiento, texto, imágenes, herramientas; junta mensajes seguidos del usuario; reintenta sin stream_options o con max_tokens 8192; 429 → rate_limit_error (relevo)
  - [x] Pase por cuenta: la clave del proveedor no sale de MultiAgente
  - [x] Catálogo: NVIDIA, Groq, Cerebras, Mistral, Together y «Otro compatible con OpenAI» (`traducir: true`)
  - [x] Menos ruido de Codex (aviso de metadatos, MCP que no arranca, errores HTTP en lenguaje llano); MCP por agente
  - [x] Pruebas: 98 (con Claude Code real a través del traductor y un proveedor falso)
- [x] **v0.8 · Repaso de la interfaz** (21/10/2026)
  - [x] Crítica y pasadas de diseño (`PRODUCT.md`): tarjeta de permiso con el cambio o la orden a la vista y un solo botón principal, banda de relevo compacta, grupo «Te necesita», «Archivadas» con restaurar, archivar con «Deshacer», iconos propios, diálogo propio en vez de las ventanas del sistema, paleta neutra con contraste comprobado
- [x] **v0.9 · Idiomas** (21/10/2026)
  - [x] Interfaz en castellano, inglés y catalán (`ui/idiomas.js`, `ui/idiomas/en.js` y `ca.js`); selector abajo del lateral; la primera vez, el idioma de Windows
  - [x] Los mensajes del núcleo llegan en castellano y la interfaz los traduce, también los que llevan datos (patrones)
  - [x] Pruebas: 104 (`IdiomasTests`: ningún texto sin traducir y los mismos datos en cada traducción)
  - [x] Diálogos de Windows para elegir carpeta o ficheros: el título llega ya traducido desde la interfaz
  - [x] Errores de arranque (antes de abrir la ventana) en el idioma elegido: la interfaz lo guarda en `datos\idioma.txt`; si no hay, el de Windows (`TextosArranque`)
  - [x] Pruebas: 106
- [ ] Prueba con cuentas reales (todo lo que depende de credenciales: login, relevo real, MCP con un servidor de verdad, consumo de Claude, coste que informa cada agente)

## Decisiones (plan §13)

Cerradas el 20/10/2026 con las propuestas: D1 clave de la bóveda en la carpeta · D2 Claude, Codex y Gemini (API) en F2 ·
D3 relevo a otro proveedor automático con cuenta atrás si el equipo lo permite · D4 resumen vivo con el modelo más barato.

## Bitácora

- 20/10 Análisis completo del gestor anterior. Decisión del usuario: programa aparte, «Claude Code de Windows + multi-cuenta
  y multi-proveedor (CLI y API), reparto y relevo con el mismo contexto», nada más complicado. Portátil copiando la
  carpeta; lo que por naturaleza se instala (Node, CLIs, adaptadores) se instala.
- 20/10 F0: los tres agentes responden por ACP v1 con la misma forma y declaran imagen y `loadSession`. Detalle en plan §12.
  Los adaptadores `@zed-industries/*-acp` están renombrados a `@agentclientprotocol/*`.
- 20/10 F1 hecha. Lecciones: (1) PowerShell 5.1 lee los ficheros UTF-8 sin BOM como ANSI: no editar con
  `Get-Content`/`Set-Content` (estropeó tildes; reparado) y los `.ps1` se guardan con BOM; (2) los procesos de WebView2
  siguen unos segundos con `datos\webview` abierto tras cerrar la ventana: al cerrar se espera a `BrowserProcessExited`
  (máx. 5 s) para que la carpeta se pueda copiar o mover en seguida; (3) con `ShowInTaskbar = false` el proceso no tiene
  `MainWindowHandle` (`CloseMainWindow` no sirve): para cerrar desde fuera, `MultiAgente.exe --cerrar`; (4) el paquete de
  WebView2 referencia siempre su control WPF: se quita en el .csproj (aviso MSB3277). Barra de título propia: aplazada
  (por ahora la de Windows con tema claro/oscuro).
- 20/10 F2 hecha. Peticiones del usuario durante la fase: que funcione como Claude Code (lo hace: los agentes trabajan en
  el disco con sus herramientas), extensiones compartidas (→ F4), proveedores por API como GLM (→ catálogo y API compatible),
  «lo más completo posible con listas actualizadas» (→ catálogo investigado en la documentación oficial + listas en vivo).
  Lecciones: (1) en System.Text.Json.Nodes, un nodo ya enganchado a un objeto no se puede poner en otro: el agente falso
  perdía las respuestas de error (se construye el resultado antes del objeto); (2) el lector ACP nunca debe morir por un
  mensaje raro (try/catch por mensaje); (3) al cerrar tras un error, el aviso de «agente cerrado» no debe pisar el motivo
  real; (4) seguir al final de la conversación con un indicador «pegado» y ResizeObserver, no midiendo antes de cada cambio.
- 20/10 F3 hecha. Para probar el relevo de verdad se pasó la lógica a `Nucleo/Gestor` y las pruebas la ejecutan en un
  «bucle único» (como el hilo de la ventana). Pendiente de comprobar con cuentas reales: que `claude-agent-acp` recupere
  con otra cuenta la sesión de la carpeta compartida, y cómo llega exactamente el límite de cada agente por ACP (se cubren
  las dos formas: error y respuesta corta).
- 20/10 F4 hecha. Formato MCP de ACP comprobado en el esquema oficial del SDK (`@agentclientprotocol/sdk/schema/v1`).
  Lección: el agente falso escribía su registro mientras la prueba lo leía y la escritura fallaba en silencio (ahora
  reintenta y devuelve error; la prueba lee con FileShare). Comentario del usuario: el gestor anterior «es muy loco y
  demasiado»; para cerrar le bastan estadísticas y límites de consumo → F5 acotada.
- 20/10 F5 hecha. El uso sale del propio ACP (esquema v1: `usage_update` con `used`, `size` y `cost`; respuesta del turno
  con `usage` acumulado): no se estima nada. Cuando un agente no informa coste, las estadísticas lo dicen («—») y el
  presupuesto no se puede vigilar en esa cuenta. Plan completo (F0-F5); queda probarlo con cuentas reales.
- 22/10 Revisión crítica completa (seguridad, núcleo, interfaz y pruebas) y arreglo de lo más grave: (1) el límite de uso
  se reconocía por «limit»/«rate» sueltos (un error de contexto o «generate» enfriaba el equipo entero): ahora frases
  completas, sin errores de contexto, y la respuesta corta solo cuenta al principio y en turnos sin herramientas;
  (2) volver a abrir (o escribir en) una sesión con el agente caído lo vuelve a arrancar; (3) tras un límite la cola espera
  al relevo y pasa a la cuenta nueva detrás de «Continúa…» (`Gestor._aMandar`, sobrevive a un destino que falla);
  (4) abrir rutas de la conversación con lista blanca (`AperturaSegura`) y nunca rutas de red (fuga de NTLM);
  (5) servidor local con plazo de lectura, 32 conexiones como mucho, tope de 64 MB también por trozos y pase comprobado
  antes de leer el cuerpo. Pruebas: 147. Pendiente de la revisión: procesos hijos sin Job Object, secretos de MCP en
  argumentos/URL, prefijos de claves en el registro, repintados de la interfaz y migraciones con datos antiguos.
- 22/10 Buscar, archivar un proyecto y llevar a otro PC (petición del usuario):
  (1) «Buscar» (lateral o Ctrl+K, `GestorBusqueda`): títulos y mensajes de usuario y agente de todas las sesiones, también
  archivadas, sin mayúsculas ni tildes (CompareInfo invariante); filtro rápido sobre el jsonl en bruto antes de leerlo;
  50 como mucho, las más recientes primero; al abrir un resultado baja al mensaje y lo resalta. Las archivadas que no salen
  en el lateral (solo trae 100) se abren con los datos del resultado (`S.encontradas`).
  (2) «⋯» del proyecto › «Archivar todas sus sesiones…»: pregunta, deja las que trabajan o piden permiso y se deshace.
  (3) «Llevar a otro PC…» (`Traslado`): .zip con programa y datos con la app abierta (copia de la base con la API de copia
  de SQLite); sin credenciales (inicio de sesión de cada CLI, `sessions\*.key` de Claude Code, bóveda), sin caché del
  navegador, registros ni uniones NTFS (se rehacen al arrancar). Con los datos reales de este PC: 533 MB, 0 credenciales.
  Pruebas: 154. `tools\ui-prueba.html` simula las tres cosas.
- 22/10 Peticiones del usuario probando la app:
  (1) Archivadas: mandarlas a otro proyecto las restaura ahí (antes seguían archivadas); «Restaurar» y «Restaurar en».
  (2) Salir con sesiones trabajando o pidiendo permiso pregunta antes (diálogo propio; no con `--cerrar` ni al apagar
  Windows; una segunda cruz cierra sin más). `Gestor.Ocupadas()`.
  (3) Acciones del agente seguidas (herramientas y razonamiento) en un bloque plegado «N acciones · la última».
  (4) Límites revisados: un porcentaje leído antes del reinicio de su bloque cuenta como 0 (`EstadoCuenta.AHora`; la
  consulta de uso falla a menudo); el coste no se suma otra vez al recuperar una sesión (Claude Code vuelve a dar el total:
  `coste:<nativa>` en ajustes); los tokens de la respuesta ACP son del turno, no acumulados; hora Unix de
  «usage limit reached|…»; el lateral enseña el consumo que más aprieta; textos de consumo traducidos.
  (5) Compactación visible: claude-agent-acp la manda como herramienta «compact» (con `rawOutput` de tokens) a los clientes
  sin avisos de compactación; se guarda como entrada «compactacion» y se pinta como hito.
  (6) «Copiar» en cada bloque de código y en cada respuesta entera (Markdown). Pruebas: 159.
- 22/10 (2) Más peticiones del usuario:
  (1) Hacía falta pulsar dos veces para cambiar de conversación: cada aviso de un agente (y volver a la ventana) rehacía el
  lateral entero, y si caía entre pulsar y soltar, el clic se perdía. Ahora no se repinta con el botón pulsado (se hace al
  soltar, después del clic) ni si no ha cambiado nada (`firmaDelArbol`).
  (2) «… está trabajando · 2 min 05 s»: `SesionAgente.TurnoDesde` (también al reabrir la ventana a mitad de turno).
  (3) Panel «Actividad» a la derecha (como el de cambios, uno a la vez y con el mismo ancho): tareas del último plan y
  subagentes (herramientas «Agent»/«Task») con sus acciones (`parentToolUseId` de claude-agent-acp → `Entrada.Padre`),
  la última y lo que dicen; se abre solo cuando hay actividad salvo que el usuario lo cierre. Los textos de un subagente
  ya no se mezclan con la respuesta: van a su herramienta (`Entrada.Texto`). Pruebas: 160.
- 22/10 (3) (1) A veces lo enviado no aparecía hasta cambiar de conversación: pulsar dentro de ella (abrir un bloque,
  seleccionar, «Copiar») recalculaba al soltar si se estaba al final y, si el contenido había crecido, dejaba de seguirlo;
  lo nuevo se pintaba fuera de la vista. Ahora solo cuenta la barra de desplazamiento (y rueda/teclado), y al enviar
  siempre baja. Además, reabrir una sesión a mitad de turno manda también lo del turno en curso (`SesionAgente.Conversacion`).
  (2) Un borrador por sesión (texto y adjuntos): ya no se comparte la caja entre conversaciones; el texto se guarda
  también para la próxima vez. Pruebas: 161.
- 22/10 (4) Robustez y avisos (bloques 2 y 3 del plan acordado con el usuario):
  (1) Procesos: cada agente va en su «job» de Windows (`Acp/TrabajoProcesos`, KILL_ON_JOB_CLOSE): al cerrar la sesión mueren
  él y lo que haya lanzado (servidores, MCP), y si MultiAgente se cierra, se cuelga o lo matan, Windows los cierra. También
  `claude -p` del resumen vivo. No van en un job lo que abre el usuario (autorizar, documentos). Al salir, los agentes se
  cierran a la vez; un turno que termina con la sesión ya cerrada no lanza relevo ni resumen.
  (2) Rendimiento: el texto que llega a trozos se agrupa (un aviso cada 60 ms como mucho, en orden con lo demás); la
  conversación pinta las últimas 150 entradas con «Ver lo anterior»; las sesiones que no se miran ni trabajan sueltan su
  conversación; las ediciones se cuentan una vez.
  (3) Secretos: argumentos y direcciones de los MCP con claves van a la bóveda (marca `{{secreto:…}}` en la base); más
  nombres secretos (PAT, DSN, cookie, session); el registro tapa los prefijos de los proveedores del catálogo y otros
  comunes, api-key/x-api-key y Authorization Basic. Prueba de una base del esquema 1 con datos que se pone al día.
  (4) Avisos de Windows (`AvisosWindows`): si MultiAgente no está delante, notificación al terminar un turno, al pedir
  permiso o al pararse por un error, y el botón de la barra de tareas parpadea; al pulsarla se abre la sesión.
  Pruebas: 199.
