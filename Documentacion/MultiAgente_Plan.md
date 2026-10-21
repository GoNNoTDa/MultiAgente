# MultiAgente — Plan (v1, 20/10/2026)

> Documento de referencia. Antes de tocar nada: leer este plan y `tasks/MultiAgente_Estado.md`.
> Maqueta de la interfaz: `Documentacion/MultiAgente_Maqueta.html`.
> Hereda el «alma» de un gestor anterior de sesiones y cuentas de Claude Code (proyecto privado), pero es un programa aparte y mucho más pequeño.

## 1. Qué es

**Lo mismo que Claude Code para Windows, pero con N cuentas de varios proveedores (Claude, Codex, Gemini…), por
inicio de sesión de su CLI o por clave de API, que se reparten el trabajo y se relevan cuando una se agota sin perder
el contexto.** Y portátil: **se copia la carpeta a otro PC y funciona igual**; como mucho hay que volver a autorizar
alguna CLI.

### 1.1 Dentro (v1)

- Lateral con proyectos y sesiones; conversación pintada limpia (markdown, código, diferencias, herramientas plegadas).
- Caja de escribir como la de Claude Code: **arrastrar y soltar** ficheros y carpetas, **Ctrl+V** con capturas,
  adjuntar, `@fichero`, `/órdenes`, historial, mensajes en cola mientras trabaja, interrumpir.
- Permisos con botones, modo de permisos, modelo y esfuerzo, anillo de contexto.
- **Chip de cuenta** junto al de modelo: «Automático (equipo)» o una cuenta concreta.
- **Cuentas y equipos**: alta, autorizar, consumo, umbrales y regla de reparto.
- **Relevo** cuando una cuenta se agota, con cuenta atrás cancelable y el mismo contexto.
- **Cualquier proveedor por API** (20/10/2026, petición del usuario): GLM, DeepSeek, Kimi, MiniMax, Qwen, OpenRouter,
  DeepInfra, Fireworks, Vercel, Ollama, LM Studio, LiteLLM… con el agente de Claude Code (API compatible con Anthropic) o
  con el de Codex (API compatible con OpenAI Responses). Catálogo editable y **listas de modelos pedidas al proveedor** (§6.1).
- Funciona como Claude Code: los agentes crean y editan ficheros, ejecutan órdenes y llevan sus tareas en la carpeta del
  proyecto; MultiAgente enseña lo que hacen y pide los permisos con botones.
- **Extensiones compartidas** (petición del usuario, fase F4): servidores MCP, skills, plugins y órdenes, comunes a las cuentas.

### 1.2 Fuera (no es objetivo; quizá más adelante)

Telegram, máquinas virtuales, DOCUMENTA-ME, Planio, base de datos compartida, DevExpress, varias interfaces,
leer la pantalla de la consola, colaboración entre agentes (revisor, segunda opinión, pizarra), tienda de empresa.

## 2. Principios (el alma del gestor anterior)

1. **No sustituye a las CLIs oficiales**: las lanza y las coordina; quien trabaja es `claude`, `codex` o `gemini`.
2. **El inicio de sesión lo hace siempre la herramienta oficial**, en su ventana. La aplicación nunca pide ni guarda
   usuario y contraseña. Tokens y claves **nunca** en la base de datos ni en el registro.
3. **Nunca se corta un turno**: el relevo espera a que termine (salvo que el límite ya lo haya cortado).
4. **Cuenta atrás cancelable** en todo cambio automático.
5. **Lenguaje llano** al usuario; el detalle técnico, al registro (`datos\logs`).
6. **Pocas piezas**: si algo no hace falta para «Claude Code multi-cuenta», no entra.

## 3. Portabilidad: la carpeta lleva la aplicación y los datos

Requisito del usuario (20/10/2026): copiar la carpeta a otro PC y que funcione igual; como mucho, volver a autorizar
las CLIs. Matiz del usuario: **lo que por naturaleza se instala, se instala** (sin volverse loco con la portabilidad).

**Se instala una vez en cada PC** (README y `tools\Instalar-Requisitos.ps1`, que solo instala lo que falta):

- Node.js LTS.
- Las CLIs y los adaptadores ACP con `npm install -g` (usuario, sin administrador): `@anthropic-ai/claude-code`,
  `@agentclientprotocol/claude-agent-acp`, `@openai/codex`, `@agentclientprotocol/codex-acp`, `@google/gemini-cli`.
- WebView2 (ya viene con Windows 10/11).

**Viaja en la carpeta**:

```
MultiAgente\                     ← lo que se copia
  MultiAgente.exe                aplicación .NET 10 autocontenida (no hace falta instalar .NET)
  ui\                            interfaz HTML/CSS/JS (WebView2)
  datos\
    multiagente.db               SQLite: proyectos, cuentas, equipos, sesiones, ajustes
    sesiones\<id>\               hilo.jsonl (conversación canónica), resumen.md, adjuntos\
    cuentas\<alias>\             carpeta de cada cuenta (CLAUDE_CONFIG_DIR / CODEX_HOME / GEMINI_CLI_HOME)
    comun\<proveedor>\           lo que comparten las cuentas de un proveedor (todo salvo el login)
    boveda.bin                   claves de API cifradas
    webview\                     perfil del navegador embebido
    logs\
```

Reglas:

- **Los datos de MultiAgente, dentro de la carpeta**: nada en el registro de Windows, `%APPDATA%` ni `%LOCALAPPDATA%`
  (salvo accesos directos o arranque con Windows si el usuario los pide). Las CLIs instaladas son del sistema.
- **Rutas internas siempre relativas** a la carpeta. Las de los proyectos son absolutas: si en el otro PC no existen, la
  sesión sale «Carpeta no encontrada · Localizar…».
- Al lanzar un agente se fija la variable de la carpeta de su cuenta (`CLAUDE_CONFIG_DIR`, `CODEX_HOME`,
  `GEMINI_CLI_HOME`) y se limpian las variables heredadas de otra sesión de agente (lección del gestor anterior:
  `CLAUDE_CODE_*`). Al arrancar se comprueba que estén las CLIs y los adaptadores; si falta algo, se dice cuál y cómo
  instalarlo (botón «Instalar lo que falta»).
- **Instancia única por carpeta** (mutex con el hash de la ruta): dos copias en el mismo PC pueden convivir.
- **Enlaces entre cuentas** (uniones NTFS): al copiar con el Explorador se convierten en carpetas reales. Al arrancar se
  comprueban; si una ya no es enlace, se funde su contenido en `comun\` y se vuelve a enlazar (técnica de
  `PreparadorCuenta` del gestor anterior).
- Los inicios de sesión de las CLIs viven en `datos\cuentas\<alias>\` y **viajan con la carpeta**: normalmente no hay
  que volver a autorizar. Si un token no vale en el otro PC, la cuenta sale «Falta autorizar · Autorizar…».
- **La carpeta es un secreto**: lleva los inicios de sesión. No dejarla en una carpeta de red compartida.
- **No usar la misma carpeta en dos PCs a la vez**: al renovar un token en uno, el otro puede quedarse sin sesión.
  Copiar = mudarse.

## 4. Conceptos

| Concepto | Qué es |
|---|---|
| **Proveedor** | Anthropic, OpenAI, Google (y más adelante, modelos locales compatibles) |
| **Cuenta** | Proveedor + credencial. Tipo **Login** (la CLI inicia sesión en su carpeta) o **Clave de API** (en la bóveda). Umbrales propios |
| **Equipo** | Cuentas intercambiables, en orden, con una regla de reparto y si admite pasar a otro proveedor |
| **Sesión** | La conversación de trabajo sobre una carpeta. Es **de la aplicación**: tiene uno o varios **tramos**, cada uno con su cuenta y su sesión nativa del motor |
| **Contexto** | Lo que todas las cuentas ven igual: la conversación canónica, el resumen vivo, `AGENTS.md` y los ficheros del proyecto |

## 5. Arquitectura

```
MultiAgente.exe (WinForms mínimo + WebView2 a pantalla completa)
  └─ ui\ (HTML/JS)  ⇄  puente JSON  ⇄  Núcleo
                                         ├─ Motores: cliente ACP (JSON-RPC por stdio) → claude | codex | gemini
                                         ├─ Enrutador: equipos, consumo, enfriamiento, relevo
                                         ├─ Contexto: conversación canónica, resumen vivo, paquete de traspaso
                                         └─ Datos: SQLite + ficheros de datos\
```

Proyectos de la solución:

| Proyecto | Papel |
|---|---|
| `MultiAgente.App` | Ventana única con WebView2 (barra de título de Windows con tema claro/oscuro; la propia, más adelante), puente con la interfaz |
| `MultiAgente.Nucleo` | Cliente ACP, adaptadores por proveedor, enrutador, contexto, cuentas, bóveda. Sin interfaz |
| `MultiAgente.Datos` | SQLite (`Microsoft.Data.Sqlite`, consultas directas; sin ORM) |
| `ui\` | Interfaz web. Punto de partida: la interfaz y la consola del gestor anterior (librerías marked, DOMPurify, highlight.js, jsdiff) |
| `MultiAgente.Tests` | xUnit, datos sintéticos |
| `MultiAgente.FalsoAgente` | Agente ACP falso (responde, pide permiso, simula «límite alcanzado») para probar sin gastar |
| `tools\Empaquetar.ps1` | Deja la carpeta portátil lista para copiar en `dist\MultiAgente` (exe autocontenido de un solo fichero + `ui\` + `tools\` + README) |
| `tools\Probar-Portatil.ps1` | Prueba de portabilidad: copia a otra ruta, arranca, captura, instancia única, cierre limpio, mover |
| `tools\Instalar-Requisitos.ps1` | Instala en el PC las CLIs y adaptadores que falten (`npm -g`) |
| `tools\Sondear-Agentes.mjs` | Comprueba por ACP qué ofrece cada agente, con una carpeta de cuenta temporal |

Sin DevExpress, sin servidor, sin base de datos compartida.

## 6. Motores: un solo protocolo (ACP)

Todos los agentes se hablan por **ACP (Agent Client Protocol)**: JSON-RPC por stdio con eventos estándar (texto, razonamiento,
herramienta, plan, petición de permiso, órdenes disponibles, modo) y sesiones que se crean (`session/new`), se recuperan
(`session/load`) y reciben mensajes con **bloques de texto, imagen y fichero** (`session/prompt`). Con eso:

- El estado es exacto (trabajando / esperando / pide permiso) para todos, sin hooks ni leer pantallas.
- Los adjuntos (arrastrar, Ctrl+V) se mandan como bloques de imagen o de fichero si el agente lo declara en
  `promptCapabilities`; si no, se guardan en `adjuntos\` y se cita la ruta.
- La interfaz pinta la conversación que llega por ACP; no depende del formato de transcripción de cada CLI.
- **Las cuentas de API usan el mismo motor**: cada CLI acepta su clave por variable de entorno.

| | Claude Code | Codex CLI | Gemini CLI |
|---|---|---|---|
| Paquete npm | `@anthropic-ai/claude-code` | `@openai/codex` | `@google/gemini-cli` |
| ACP | adaptador `claude-agent-acp` (`@agentclientprotocol/claude-agent-acp`) | adaptador `codex-acp` (`@agentclientprotocol/codex-acp`) | nativo: `gemini --acp` |
| Carpeta de la cuenta | `CLAUDE_CONFIG_DIR` | `CODEX_HOME` | `GEMINI_CLI_HOME` (crea `.gemini` dentro; documentado) |
| Login | `/login` en su ventana | `codex login` | cuenta de Google al arrancar |
| Clave de API | `ANTHROPIC_API_KEY` | `OPENAI_API_KEY` | `GEMINI_API_KEY` |
| Consumo previo | endpoint de uso OAuth (no documentado; el del gestor anterior) | el adaptador informó del consumo (prueba 14/10) | solo reactivo (error 429) |
| Probado en el gestor anterior (14/10) | conecta (sin probar a fondo: token caducado) | **entero**: permisos, herramientas, consumo, `session/load` | habla ACP; **cuenta personal rechazada** («migrate to Antigravity») |

Todo lo marcado «comprobar» se cierra en **F0** y pasa a una tabla de «Hechos comprobados» en este documento.

### 6.1 Proveedores por API: catálogo y modelos

- `catalogo\proveedores.json` (junto al exe, editable sin recompilar; versión 2026-10-20, investigado en la documentación
  oficial de cada proveedor, con fuentes y grado de confianza): por proveedor, el agente que lo usa, la dirección, dónde se
  saca la clave, cómo pedir la lista de modelos, la lista de respaldo y las variables recomendadas.
- **Con Claude Code** (API compatible con Anthropic): `ANTHROPIC_BASE_URL` = dirección (sin `/v1`), la clave en
  `ANTHROPIC_AUTH_TOKEN` y `ANTHROPIC_API_KEY=""` (si tuviera valor, Claude Code la usaría antes), `API_TIMEOUT_MS`, y el
  mapeo de modelos `ANTHROPIC_DEFAULT_OPUS/SONNET/HAIKU/FABLE_MODEL` y `CLAUDE_CODE_SUBAGENT_MODEL` (el del catálogo; el
  modelo elegido en la cuenta manda sobre él). Pasarelas: `CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1`.
- **Con Codex** (API compatible con OpenAI; Codex solo admite `wire_api = "responses"`, así que Mistral y el endpoint
  OpenAI de Gemini no valen sin proxy): MultiAgente escribe el `config.toml` **en la carpeta de la cuenta**
  (`CODEX_HOME`) con `[model_providers.multiagente]` y la clave en `MULTIAGENTE_API_KEY`. Nunca el `~/.codex` del usuario.
- **Lista de modelos**: botón «Actualizar lista» → `GET` a la dirección del catálogo con la clave (`x-api-key` +
  `anthropic-version`, `Bearer` o sin clave para los locales). Sin forma de listarlos (Qwen, Z.ai), la de respaldo.
- Agentes de suscripción: los modelos los da el propio agente por ACP (`configOptions`), siempre los de su versión.

## 7. Contexto compartido y relevo

### 7.1 Lo que garantiza el mismo contexto

1. **Conversación canónica** (`datos\sesiones\<id>\hilo.jsonl`): cada turno que llega por ACP se guarda en formato
   neutro (usuario, respuesta, herramientas resumidas, ficheros tocados, adjuntos, cuenta y modelo).
2. **Resumen vivo** (`resumen.md`): tras cada turno, un modelo barato lo actualiza: objetivo, hecho, pendiente,
   decisiones, ficheros. Está listo **antes** de que haga falta (cuando una cuenta se agota puede no quedar cuota para
   resumir).
3. **`AGENTS.md`** del proyecto como fuente única de normas; `CLAUDE.md` y `GEMINI.md` la incluyen (`@AGENTS.md` o
   copia generada con aviso).
4. **Los ficheros del proyecto** (y git), que son de todos.

### 7.2 Relevo, paso a paso

1. **Disparo**: consumo ≥ umbral de la cuenta (si el proveedor lo da), error de límite en un turno, o el usuario elige
   «Continuar con…».
2. **Destino**: según la regla del equipo, saltando las cuentas agotadas o «enfriando»; primero el mismo proveedor.
3. **Banda** sobre la caja: «Claude · Cuenta 1 al 96 % → sigue en Claude · Cuenta 2 en 60 s · Cambiar ya · Cancelar».
   Espera a que termine el turno.
4. **Mismo proveedor**: se cierra el motor de la cuenta A y se abre el de la B con `session/load` del mismo id (las
   cuentas de un proveedor comparten la carpeta de sesiones, como `projects\` en el gestor anterior). Se conserva todo.
5. **Otro proveedor, o si el paso 4 falla**: `session/new` en B con el **paquete de traspaso** como primer mensaje
   (plegado en la vista: «Traspaso desde Claude · Cuenta 1»): resumen vivo + últimos turnos literales (con tope de
   tamaño) + ficheros tocados + `git status` + «No repitas lo hecho; comprueba los ficheros antes de seguir».
6. Si el límite cortó el turno: «Continúa donde lo dejaste.».
7. Línea divisoria en la conversación: «Sigue en Codex · Empresa (la Cuenta 1 llegó al límite de 5 h)».

Se pierde la caché y el razonamiento interno del motor anterior; no se pierden el objetivo, las decisiones ni el trabajo.

### 7.3 Equipos y reparto

| Regla | Comportamiento |
|---|---|
| **Prioridad** (por defecto) | Usa la primera con margen; al agotarse, la siguiente |
| **Rotación** | Cada sesión nueva empieza en la siguiente cuenta del equipo |
| **Menos usada** | La de más margen |
| **Suscripción primero** | Las cuentas Login antes que las de API; las de API, con presupuesto |

- El reparto se decide al **crear** la sesión; el relevo, solo **entre turnos**.
- Cuenta agotada → «Enfriando hasta las HH:MM» (hora de reinicio si se conoce; si no, 1 h y se reintenta).
- Cuentas de API: gasto = tokens × precio (tabla editable) frente al presupuesto mensual; aviso al 80 %.
- Opción del equipo «Permitir pasar a otro proveedor» (si no, solo releva dentro del mismo).

## 8. Datos (SQLite)

| Tabla | Campos principales |
|---|---|
| `Proyecto` | Id, Nombre, Ruta, EquipoPreferido |
| `Cuenta` | Id, Alias, Proveedor, Tipo (Login/Api), Carpeta (relativa), RefClave, Umbrales (JSON), Presupuesto, Orden |
| `Equipo` / `EquipoCuenta` | Id, Nombre, Regla, PermiteOtroProveedor, CuentaAtras · EquipoId, CuentaId, Orden |
| `Sesion` | Id, ProyectoId, Titulo, Carpeta, EquipoId o CuentaFija, Modelo, Esfuerzo, ModoPermisos, Estado, Creada, UltimoUso, Archivada |
| `Tramo` | SesionId, Orden, CuentaId, SesionNativa, Desde, Hasta, Motivo |
| `Consumo` | CuentaId, Momento, Ventana (5h/semana/…), Porcentaje, Reinicio, Gasto |
| `Ajuste` | Clave, Valor |

El texto de las conversaciones va en `hilo.jsonl`, no en la base de datos. Las claves, en `boveda.bin`, nunca en la base.

## 9. Interfaz (ver la maqueta)

- **Ventana principal**: lateral (Nueva sesión, buscar, proyectos con sus sesiones y el punto del proveedor, consumo de
  las cuentas abajo) y conversación con la caja de escribir.
- **Caja**: arrastrar y soltar (zona resaltada «Suelta para adjuntar»), Ctrl+V, «+» adjuntar, miniaturas quitables,
  `@` y `/`; chips **modo de permisos · modelo y esfuerzo · cuenta**; anillo de contexto; Enviar / Interrumpir.
- **Chip de cuenta**: «Automático · Equipo Principal» o una cuenta; cada una con su consumo, «enfriando hasta…» o
  «falta autorizar».
- **Banda de relevo** con cuenta atrás; **banda de límite** ámbar desde el umbral de aviso.
- **Ajustes › Cuentas**: tarjeta por cuenta (proveedor, tipo, estado, consumo, umbrales, Autorizar…, Quitar) y «Añadir
  cuenta» (proveedor → Login o Clave de API). **Ajustes › Equipos**: orden por arrastre, regla, otro proveedor, cuenta
  atrás. **Ajustes › Agentes**: CLIs y adaptadores encontrados con su versión, e «Instalar lo que falta».
- **Autorizar…** abre una ventana de consola con la CLI de esa cuenta y su entorno; al cerrarla se comprueba el estado.

## 10. Seguridad

- Bóveda (`boveda.bin`) cifrada con AES-GCM. Origen de la clave: ver decisión D1.
- Nunca tokens ni claves en `multiagente.db`, en `logs\` ni en el paquete de traspaso (se tapan patrones de claves).
- Permisos de los agentes: los de cada CLI; la aplicación solo muestra la petición y responde lo que pulse el usuario.
- Solo herramientas y API oficiales de cada proveedor; nada de automatizar webs.
- **Condiciones de uso**: rotar suscripciones de una misma persona para saltarse límites puede incumplir las condiciones
  de los proveedores. Pensado para cuentas legítimas (puestos de equipo, claves de empresa); la política es del usuario.

## 11. Fases (pruebas automáticas + revisión crítica al cerrar cada una)

| Fase | Contenido | Comprobación |
|---|---|---|
| **F0 · Comprobaciones** | Aislar cuentas por variable en los tres; login y que el token sirva tras mover la carpeta; ACP de los tres: nueva, mensaje con imagen, permisos, `session/load`, error de límite, compartir sesiones entre dos cuentas del mismo proveedor | Tabla «Hechos comprobados» (§12) |
| **F1 · Esqueleto portátil** | Solución, exe autocontenido, ventana WebView2, `datos\` relativo, instancia por carpeta, registro, `Empaquetar.ps1` | Copiar la carpeta a otra ruta y arrancar sin nada más |
| **F2 · Un agente** | Cuentas (alta, autorizar, estado; suscripción, clave y API compatible con catálogo y lista de modelos), proyectos, sesión con un agente por ACP, conversación, caja completa, permisos, modos, modelo, interrumpir, cola, cambiar de cuenta con traspaso | E2E con FalsoAgente; agentes reales con cuenta vacía (`MULTIAGENTE_PRUEBA_REAL=1`) |
| **F3 · Varias cuentas** | Equipos, reglas, consumo (previo y reactivo), enfriamiento, resumen vivo, relevo mismo proveedor y entre proveedores, compartir sesiones entre cuentas de un proveedor | Tabla de casos del enrutador; E2E de relevo con FalsoAgente que simula el límite |
| **F4 · Extensiones compartidas** | Servidores MCP en un catálogo propio que se pasa a cada sesión por ACP (`mcpServers`: valen para todos los agentes y cuentas, sin tocar sus configuraciones); skills, plugins, órdenes y `AGENTS.md`/`CLAUDE.md` compartidos entre las cuentas de un proveedor (`datos\comun\<proveedor>`, enlaces que se reparan al arrancar); pantalla para verlos, añadirlos (también importando de `~/.claude`) y quitarlos | Pruebas de enlaces tras copiar la carpeta; MCP visto por el agente falso |
| **F5 · Estadísticas y límites** ✓ (acotada por el usuario el 20/10/2026: «con algunas estadísticas, límites de consumo, etc. ya tengo»; nada de la complejidad del gestor anterior) | Estadísticas sencillas (uso por cuenta y por sesión: turnos, tokens y coste cuando el agente lo informa) y límites de consumo (umbrales por cuenta editables; presupuesto mensual de las cuentas de API con aviso y reserva); lo mínimo de remate: icono y reabrir al arrancar las sesiones que estaban abiertas | Pruebas con el agente falso y portabilidad |

## 12. Hechos comprobados

(Se rellena en F0. Formato: hecho · cómo se comprobó · fecha.)

| Hecho | Fuente | Fecha |
|---|---|---|
| Los tres hablan ACP v1 con la misma forma: `initialize` → `session/new`; los tres declaran `loadSession: true` y `promptCapabilities` con `image` y `embeddedContext` (Gemini además `audio`) | `tools\Sondear-Agentes.mjs` (claude-agent-acp 0.94.0, codex-acp 2.2.2, gemini-cli 0.37.0) | 20/10/2026 |
| Claude (adaptador): sin `authMethods` (el login es el de la CLI); `session/new` responde aunque la carpeta no tenga sesión iniciada; modos `default`, `acceptEdits`, `plan`, `auto`, `bypassPermissions`; `sessionCapabilities`: resume, list, fork, close, delete, additionalDirectories; cola de mensajes (`promptQueueing`) | sonda | 20/10/2026 |
| Codex (adaptador): `authMethods` `api-key` y `chat-gpt`; sin sesión iniciada, `session/new` da «Authentication required» (-32000); `sessionCapabilities` como Claude más `subagents` | sonda | 20/10/2026 |
| Gemini: `authMethods` `oauth-personal`, `gemini-api-key`, `vertex-ai`, `gateway`; sin credencial, `session/new` da «Gemini API key is missing or not configured.» (-32000); `--experimental-acp` está obsoleto, se usa `--acp` | sonda y `gemini --help` | 20/10/2026 |
| Claude por ACP da las opciones como `configOptions` con categoría: `mode` (default, acceptEdits, plan, auto, bypassPermissions), `model` (default, opus, sonnet, haiku…), `thought_level` (low…max) y `fast`; se cambian con `session/set_config_option`. Avisa de la sesión con la notificación `_auth/status_update` | sonda con `session/new` | 20/10/2026 |
| Con una carpeta de cuenta vacía, los tres agentes reales arrancan por ACP y MultiAgente lo traduce a «no tiene la sesión iniciada» (Claude, al mandar el primer mensaje; Codex y Gemini, al crear la sesión) | `RealesTests` | 20/10/2026 |
| Codex solo admite `wire_api = "responses"`; `model_providers` solo se lee del `config.toml` de `CODEX_HOME`. En Claude Code, `ANTHROPIC_API_KEY` tiene prioridad sobre `ANTHROPIC_AUTH_TOKEN` | documentación oficial (investigación del catálogo) | 20/10/2026 |
| Paquetes vigentes: los antiguos `@zed-industries/*-acp` están renombrados a `@agentclientprotocol/*` | `npm view` | 20/10/2026 |
| Gemini CLI admite `GEMINI_CLI_HOME` para mover su carpeta de usuario (crea `.gemini` dentro) | documentación oficial geminicli.com | 20/10/2026 |
| Codex por `codex-acp` funciona entero (permisos, herramientas, consumo, `session/list` y `session/load`) | prueba en el gestor anterior | 14/10/2026 |
| Gemini CLI 0.39.1 habla ACP pero rechaza la cuenta personal de Google | prueba en el gestor anterior | 14/10/2026 |

## 13. Decisiones (cerradas por el usuario el 20/10/2026: «adelante con tus propuestas»)

| # | Asunto | Decisión |
|---|---|---|
| D1 | Clave de la bóveda | **Clave guardada en la carpeta** (coherente: los inicios de sesión de las CLIs ya viajan en ella; protege de lecturas casuales, no de quien tenga la carpeta). Alternativa: contraseña maestra que se pide una vez por PC |
| D2 | Proveedores de F2 | Claude (login y API), Codex (login y API) y Gemini (clave de API) |
| D3 | Relevo a otro proveedor | Automático con cuenta atrás si el equipo lo permite; si no, solo dentro del mismo proveedor |
| D4 | Modelo del resumen vivo | El más barato disponible en el equipo (p. ej. Haiku o Gemini Flash), configurable; sin cuenta disponible, se guardan los turnos y se resume después |

## 14. Riesgos

- **Las CLIs y los adaptadores ACP cambian a menudo**: se comprueba la versión instalada al arrancar, pruebas por adaptador con FalsoAgente y aviso claro si una versión no se reconoce.
- **Gemini con cuenta personal** probablemente no sirve: Gemini irá por clave de API o cuenta de empresa.
- **Consumo previo** solo existe en Claude (y quizá en Codex); en el resto, el relevo es reactivo (al primer error).
- **Enlaces tras copiar** y **rutas de proyecto distintas** en el otro PC: se reparan o se preguntan al arrancar.
- **Misma carpeta en dos PCs a la vez**: puede invalidar tokens (ver §3).
