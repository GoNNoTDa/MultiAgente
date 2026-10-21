# MultiAgente en Mac: análisis

Estudio del 21/10/2026 (versión 0.9.2). **No hay nada hecho**: es lo que costaría y lo que habría que comprobar antes.

## Resumen

Es viable sin reescribir el programa: la interfaz y casi todo el núcleo sirven tal cual. Lo que cambia es la ventana, unos
pocos sitios del núcleo que dan por hecho Windows y el empaquetado. Estimación: **2–3 semanas de trabajo, más pruebas en
un Mac de verdad**. El riesgo principal no es técnico: es si en macOS se pueden tener **varias suscripciones de Claude a la
vez** (ver «Inicio de sesión de Claude en macOS»).

## Qué sirve y qué no

| Parte | Tamaño | En Mac |
|---|---|---|
| Interfaz (HTML/CSS/JS) | ~3.300 líneas | Sirve; retoques: Cmd en vez de Ctrl, textos «Ctrl+V», arrastrar ficheros |
| Núcleo y datos (C#) | ~6.400 líneas | Casi todo sirve: relevo, equipos, traductor, SQLite, bóveda (AES-GCM), MCP, historiales, git |
| Ventana (WinForms + WebView2) | ~800 líneas | Hay que sustituirla: no existen en Mac |
| Scripts (.ps1) | 4 | Rehacer: instalar requisitos, empaquetar, probar portátil, generar icono |

## Qué hay que cambiar

1. **Ventana.** Propuesta: [Photino.NET](https://www.tryphotino.io/) (WKWebView en Mac, WebView2 en Windows): se queda .NET
   y la misma interfaz. Rehacer el puente de mensajes (`chrome.webview` → el de Photino), servir `ui` y `adjuntos` con un
   esquema propio, los diálogos de carpeta y ficheros, abrir en Finder y las capturas de prueba (`--captura`).
   Recomendado: mantener la ventana actual en Windows hasta que la de Mac esté probada; el núcleo, el puente y la
   interfaz, comunes.
2. **Arrastrar ficheros.** WKWebView no da la ruta del fichero soltado
   ([Apple Developer Forums](https://developer.apple.com/forums/thread/748072)): o código nativo para recoger las rutas, o
   leer el contenido en la interfaz y mandarlo como se hace ya con las capturas pegadas.
3. **Núcleo que da por hecho Windows** (unos 10 sitios):
   - `Requisitos`: busca `.exe`/`.cmd` y lanza con `cmd.exe /c`; en Mac, sin extensión y con permiso de ejecución.
   - `Mcp`: envuelve los `.cmd`/`.bat` con `cmd /c`.
   - `CarpetasCompartidas`: `mklink /J`; en Mac, `Directory.CreateSymbolicLink`.
   - `Gestor.Autorizar`: escribe un `autorizar.cmd` y abre una consola; en Mac, un `.command` abierto con Terminal
     (`open -a Terminal`), con las variables de entorno dentro del script.
   - `InstanciaUnica`: `Mutex` y `EventWaitHandle` con nombre; los segundos no existen en Mac en .NET → socket local o
     fichero bloqueado.
   - `TemaWindows` (dwmapi): fuera.
   - `Puente`: `explorer.exe` → `open` / `open -R`; el instalador `.ps1` → script de shell.
   - Rutas guardadas con `\` (base de datos, `@".gemini\oauth_creds.json"` en `Proveedores`): normalizar para que una
     carpeta copiada de Windows funcione en Mac.
   - Proyectos: `net10.0-windows` → `net10.0` en Nucleo, Datos y pruebas.
4. **PATH.** Una aplicación abierta desde Finder no hereda el PATH del terminal: no vería `node`, `claude` ni `codex` si
   están en Homebrew o nvm. Hay que leerlo del shell de inicio de sesión (`$SHELL -lc 'echo $PATH'`).
5. **Dónde van los datos.** Ver «¿Se puede guardar todo en local?».
6. **Distribución.** `.app` con Info.plist e icono `.icns`, para Apple Silicon e Intel (o universal), publicada con todo
   incluido. Sin firma, macOS avisa de «desarrollador no verificado» y hay que permitirla en Ajustes; firmar y notarizar
   requiere la cuenta de desarrollador de Apple (99 $ al año). Compilar y probar en Mac: GitHub Actions (gratis en
   repositorios públicos).

## Inicio de sesión de Claude en macOS

En Windows cada cuenta guarda su inicio de sesión en su carpeta (`CLAUDE_CONFIG_DIR`). En macOS, Claude Code lo guarda en
el **Llavero** (elemento «Claude Code-credentials»), y las fuentes no se ponen de acuerdo en si `CLAUDE_CONFIG_DIR` separa
también esa entrada: una dice que sí ([ccsw](https://docs.rs/crate/ccsw/0.1.0)); una petición a Anthropic de febrero de
2026 dice que no y que dos cuentas a la vez fallan
([claudeissues](https://claudeissues.com/issue/24963-feature-support-for-multiple-accounts-profiles)); hay herramientas
que, para cambiar de cuenta, reescriben la entrada del Llavero ([claudectl](https://github.com/Sawmills/claudectl)).

Si no se separa, varias suscripciones de Claude a la vez (lo que usa el relevo) no funcionarían con el inicio de sesión
normal. Codex y Gemini guardan su inicio de sesión en ficheros de su carpeta (comprobarlo también) y las cuentas de API no
tienen inicio de sesión.

## ¿Se puede guardar todo en local?

**Los datos de MultiAgente, sí.** macOS solo ejecuta una aplicación desde una ubicación temporal de solo lectura
(*App Translocation*) si está en cuarentena y no se ha movido. Si se descomprime y se mueve con Finder (o se le quita la
marca de cuarentena), puede escribir junto a ella: una carpeta `MultiAgente/` con `MultiAgente.app` y `datos/` al lado
mantiene la idea de «copiar la carpeta». Si no se puede escribir ahí, alternativa: `~/Library/Application Support/MultiAgente`.

**El inicio de sesión de Claude, sin el Llavero**, tres caminos (todos por probar en un Mac):

| Camino | Cómo | A favor | En contra |
|---|---|---|---|
| **Token de larga duración** (recomendado para probar primero) | `claude setup-token` imprime un token de un año; MultiAgente lo guarda en su bóveda cifrada (una por cuenta) y lo pasa como `CLAUDE_CODE_OAUTH_TOKEN` a cada sesión | Nada en el Llavero; cuentas en paralelo; se copia con la carpeta (como las claves de API) | Anthropic lo presenta para CI y scripts; hay avisos de errores 401 ([84903](https://claudeissues.com/issue/84903-claude-code-oauth-token-from-claude-setup-token-returns-401-invalid-bearer-token), [65320](https://claudeissues.com/issue/65320-setup-token-oauth-token-returns-401-invalid-bearer-token-with-claude-p-on-max-su)); con ese token puede borrar la entrada del Llavero al salir ([#37512](https://github.com/anthropics/claude-code/issues/37512)); hay que ver si el consumo de 5 h y semanal se sigue leyendo; renovar cada año |
| **Fichero `.credentials.json`** en la carpeta de la cuenta | Como en Windows y Linux | Igual que hoy | En macOS no es el comportamiento documentado: hay quien dice que lo lee si existe ([blog](https://oldeucryptoboi.substack.com/p/using-claude-code-cli-over-ssh-fixing)) y una petición para que lo haga ([#29816](https://github.com/anthropics/claude-code/issues/29816)); depende de la versión: frágil |
| **Claude solo por API** | Cuentas de Claude con clave de API | Funciona seguro | Pago por uso: no aprovecha las suscripciones |

Ojo con las condiciones de uso: usar el token de una suscripción desde un programa de terceros y repartir trabajo entre
suscripciones puede ir contra las condiciones de Anthropic (riesgo de bloqueo de la cuenta). Revisarlas antes.

## Plan si se decide

| Fase | Qué | Esfuerzo |
|---|---|---|
| 0 | En un Mac: dos inicios de sesión de Claude con carpetas distintas a la vez y, si falla, el token de larga duración en paralelo (incluido el consumo de 5 h y semanal). Si nada funciona, decidir si sigue mereciendo la pena | ½–1 día |
| 1 | Núcleo multiplataforma (punto 3 y 4) | 3–4 días |
| 2 | Ventana con Photino, puente, diálogos, arrastrar ficheros | 3–4 días |
| 3 | Mac: datos junto a la app, Cmd, instalador de requisitos, GitHub Actions en macOS | 2–3 días |
| 4 | `.app`, icono, firma (opcional) | 1–2 días |

Necesario: acceso a un Mac (desde Windows no se puede comprobar nada de esto) y decidir si se paga la firma de Apple.
