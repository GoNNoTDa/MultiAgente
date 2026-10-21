# MultiAgente

Claude Code de escritorio con N cuentas de varios proveedores (Claude, Codex, Gemini; por login de su CLI o por clave de
API), reparto entre cuentas y relevo con el mismo contexto. Portátil: la carpeta lleva la aplicación y sus datos.
Nace de un gestor anterior de sesiones y cuentas de Claude Code (proyecto privado): mismo espíritu, mucha menos complejidad.

- **Antes de tocar nada**: leer `Documentacion/MultiAgente_Plan.md` y `tasks/MultiAgente_Estado.md`.
  Maqueta: `Documentacion/MultiAgente_Maqueta.html`.
- **Simple ante todo**: si algo no hace falta para «Claude Code multi-cuenta y multi-proveedor», no entra.
- Stack: .NET 10 (`net10.0-windows`, publicación autocontenida) + WebView2 con la interfaz en HTML/JS (`ui\`) + SQLite
  (`Microsoft.Data.Sqlite`, sin ORM) + xUnit. Sin DevExpress. Los agentes se hablan **solo por ACP**
  (`claude-agent-acp`, `codex-acp`, `gemini --acp`); nada de leer pantallas ni transcripciones internas.
- **Portabilidad**: los datos de MultiAgente viven en `datos\` junto al exe, con rutas relativas; nada en el registro ni en
  `%APPDATA%`/`%LOCALAPPDATA%`. Lo que por naturaleza se instala (Node.js, CLIs, adaptadores, WebView2) se instala en el
  PC (`tools\Instalar-Requisitos.ps1`); no se empaqueta.
- **Credenciales**: el login lo hace siempre la CLI oficial en la carpeta de su cuenta (`CLAUDE_CONFIG_DIR`,
  `CODEX_HOME`, `GEMINI_CLI_HOME`). Claves de API solo en `datos\boveda.bin`. Tokens y claves **nunca** en la base de
  datos, en el registro (`datos\logs`) ni en el paquete de traspaso. Nunca tocar la configuración real del usuario
  (`~/.claude`, `~/.codex`, `~/.gemini`): las pruebas usan carpetas de cuenta temporales.
- Mensajes al usuario en lenguaje llano; el detalle técnico al log. Textos en castellano.
- Pruebas: datos sintéticos; agente falso (`MultiAgente.FalsoAgente`) para no gastar; pruebas con agentes reales solo
  con variable de entorno, mensajes mínimos y modelo barato. Tras cada fase: pruebas + revisión crítica.
- Ficheros en UTF-8; los `.ps1` con BOM (PowerShell 5.1). No editar con `Get-Content`/`Set-Content` de PowerShell 5.1
  (estropea las tildes): usar la herramienta de edición.
- Probar la portabilidad tras cambios de arranque, rutas o datos: `tools\Probar-Portatil.ps1`.
- Commits sin atribución de Claude.
