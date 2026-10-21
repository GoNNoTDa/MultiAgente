# MultiAgente

**Español** · [English](README.en.md)

Trabaja con **Claude Code, Codex y Gemini** desde una sola ventana, como la aplicación de escritorio de Claude Code, pero
con **varias cuentas de varios proveedores**:

- Cada sesión usa una cuenta concreta o un **equipo** de cuentas que se reparten el trabajo.
- Cuando una cuenta se agota, la sesión **sigue en otra** (del mismo proveedor o de otro) **sin perder el contexto**.
- Arrastrar y soltar ficheros, pegar capturas con Ctrl+V, permisos con botones, modelo y modo de permisos a mano.
- **Portátil**: copia la carpeta a otro PC y sigue igual (como mucho, vuelve a autorizar alguna cuenta).
- Interfaz en **castellano, inglés y catalán** (*Idioma*, abajo del lateral; la primera vez, el de Windows).

> **Estado: completo, pendiente de probar con cuentas reales (v0.9).** Cuentas de suscripción, de clave y de API compatible (GLM, DeepSeek, Kimi, Qwen,
> OpenRouter, Ollama…), sesiones con permisos y adjuntos, **equipos con reparto y relevo automático** entre cuentas y
> proveedores, **extensiones compartidas** (MCP, skills, órdenes, plugins, instrucciones), **estadísticas, consumo y
> límites**, **proyectos**, **visor de cambios** e **importar conversaciones** previas del PC. Plan: [Documentacion/MultiAgente_Plan.md](Documentacion/MultiAgente_Plan.md) ·
> Maqueta: [Documentacion/MultiAgente_Maqueta.html](Documentacion/MultiAgente_Maqueta.html).

---

## 1. Requisitos (una vez por PC)

| Qué | Para qué | Cómo |
|---|---|---|
| Windows 10 u 11 | — | — |
| WebView2 | La ventana de MultiAgente | Viene con Windows 10/11. Si faltara: [descarga de Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/) |
| Node.js LTS | Las CLIs de los agentes | [nodejs.org](https://nodejs.org) (versión LTS, instalación normal) |
| Las CLIs y sus adaptadores | Los agentes | Script de abajo (solo instala lo que falte) |

No hace falta instalar .NET: MultiAgente lo lleva dentro.

### Instalar las CLIs y los adaptadores

Desde la carpeta de MultiAgente, en PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File tools\Instalar-Requisitos.ps1
```

Instala con `npm install -g` (para tu usuario, sin administrador) lo que no tengas de esta lista. Lo que ya tengas no
lo toca (añade `-Actualizar` para llevarlo todo a la última versión):

| Paquete | Qué es |
|---|---|
| `@anthropic-ai/claude-code` | Claude Code |
| `@agentclientprotocol/claude-agent-acp` | Adaptador para que MultiAgente hable con Claude Code |
| `@openai/codex` | Codex CLI |
| `@agentclientprotocol/codex-acp` | Adaptador para Codex |
| `@google/gemini-cli` | Gemini CLI (no necesita adaptador) |

Si solo vas a usar un proveedor, basta con su CLI y su adaptador.

Para comprobar que los agentes responden (no toca tu configuración; usa carpetas temporales):

```powershell
node tools\Sondear-Agentes.mjs
```

## 2. Poner en marcha MultiAgente

1. Copia la carpeta `MultiAgente` donde quieras (por ejemplo `C:\MultiAgente`). Tiene que ser un sitio donde puedas
   escribir: la primera vez crea dentro la carpeta `datos\`.
2. Abre `MultiAgente.exe`. La pantalla de inicio enseña qué agentes encuentra en el equipo y su versión; si falta algo,
   el botón «Instalar lo que falta» abre el instalador de requisitos (§1). Al volver a la ventana se comprueba otra vez.
3. Ve a **Cuentas y proveedores › Añadir cuenta** y da de alta las que quieras (ver §3).
4. Crea un **equipo** en **Cuentas y proveedores › Equipos** (o usa el «Principal», que se crea solo con todas tus cuentas).
5. **Nueva sesión** → elige la carpeta del proyecto y la cuenta → escribe. El agente trabaja en esa carpeta como en Claude
   Code: crea y edita ficheros, ejecuta órdenes y lleva su lista de tareas; cuando necesita permiso, aparecen botones.
   Arrastra ficheros o pega capturas (Ctrl+V) en la caja; «/» muestra las órdenes del agente; ■ o Esc dos veces interrumpe.

## 3. Añadir cuentas

Cada cuenta tiene su propia carpeta dentro de `datos\cuentas\`, así que puedes tener varias del mismo proveedor sin que
se pisen, y **no se toca** la configuración que ya tengas en tu PC (`~/.claude`, `~/.codex`, `~/.gemini`).

| Proveedor | Por inicio de sesión | Por clave de API |
|---|---|---|
| **Claude** | «Autorizar…» abre Claude Code en una ventana: escribe `/login`, sigue los pasos del navegador con el correo de esa cuenta y cierra la ventana | Pega una clave de [console.anthropic.com](https://console.anthropic.com) |
| **Codex** | «Autorizar…» abre `codex login`: entra con tu cuenta de ChatGPT en el navegador | Pega una clave de [platform.openai.com](https://platform.openai.com/api-keys) |
| **Gemini** | Cuenta de Google (las cuentas personales pueden no estar admitidas; mejor una clave o una cuenta de empresa) | Pega una clave de [Google AI Studio](https://aistudio.google.com/apikey) |

Las claves se guardan cifradas en `datos\boveda.bin`, nunca en la base de datos ni en los registros.

**Otros proveedores por API** (tipo de cuenta «Otros modelos con Claude Code» o «con Codex»): GLM (Z.ai y Zhipu), DeepSeek,
Kimi, MiniMax, Qwen (Alibaba), OpenRouter, DeepInfra, Fireworks, Vercel AI Gateway, Grok (xAI), NVIDIA (build.nvidia.com), Groq, Cerebras, Mistral, Together, Azure OpenAI, y modelos en tu
propio equipo con Ollama, LM Studio o LiteLLM. Elige el proveedor, pega la clave (los locales no la piden), revisa la
dirección y pulsa «Actualizar lista» para ver los modelos que ofrece ahora. Con Claude Code tienes todas sus herramientas con
ese modelo. La lista de proveedores está en `catalogo\proveedores.json` y se puede ampliar sin recompilar.

**Proveedores que solo tienen la API de OpenAI** (NVIDIA, Groq, Cerebras, Mistral, Together o «Otro compatible con OpenAI»
para un servidor propio con vLLM, por ejemplo): MultiAgente lleva un **traductor** que los pone a trabajar con Claude Code.

- Claude Code habla con el traductor, que está dentro de MultiAgente y solo atiende en este equipo. El traductor pasa cada
  petición al formato de OpenAI y traduce la respuesta al vuelo: texto, razonamiento, imágenes y herramientas.
- La clave del proveedor no sale de MultiAgente: Claude Code recibe solo un pase interno.
- Al añadir una de estas cuentas, MultiAgente prueba antes el modelo con una petición mínima: hay proveedores (NVIDIA) que
  listan modelos que no dan a todas las cuentas.
- **Modelo…** en cada cuenta de API cambia el modelo sin borrar la cuenta. **Probar cuáles funcionan** prueba con tu clave
  todos los modelos de chat del proveedor y deja solo los que responden.
- Si el proveedor no empieza a responder en 2 minutos (un modelo saturado, en cola), se corta con un mensaje claro en vez
  de dejar la sesión «trabajando» para siempre.

## 4. Equipos, reparto y relevo

- Al añadir la primera cuenta se crea el equipo **Principal** con todas (las nuevas se añaden al final). En **Cuentas y
  proveedores › Equipos** se cambia el orden (▲▼), quién entra, la regla, si puede pasar a otro proveedor y la cuenta atrás.
  Se pueden crear más equipos (por ejemplo «Solo Claude» o «Baratos»).
- Reglas: **Prioridad** (la primera con margen), **Rotación** (cada sesión nueva en la siguiente), **La menos usada** y
  **Suscripciones primero** (las de clave de API, de reserva).
- Al crear una sesión elige **«Automático · Principal»** (el equipo decide y releva) o una **cuenta fija**. Se puede
  cambiar después desde la cabecera de la sesión.
- **Relevo**: cuando la cuenta llega a su límite (o, en Claude, al 92 % del bloque de 5 h o al 95 % semanal) aparece una banda
  «… → sigue en «Cuenta 2» en 60 s · Cambiar ya · Cancelar». Nunca corta un turno: espera a que termine. Si el límite cortó
  el turno, la cuenta nueva recibe «Continúa donde lo dejaste».
- **Mismo contexto**: entre cuentas del mismo proveedor la conversación sigue tal cual (comparten sus conversaciones en
  `datos\comun`). Con otro proveedor, el agente nuevo recibe un **traspaso**: el resumen vivo de la sesión (lo mantiene una
  llamada breve a Claude Haiku tras los turnos; se puede desactivar) y los últimos mensajes.
- La cuenta agotada queda «agotada · vuelve a las HH:MM» y no se usa hasta entonces.

## 5. Proyectos, visor de cambios e historiales

- **Proyectos** (lateral, «＋» junto a «Proyectos» › «Nuevo proyecto…»): un nombre y la carpeta donde trabajan sus sesiones.
  Puede haber varios en la misma carpeta. Con el «＋» de cada proyecto empiezas una sesión en él; su «⋯» (o clic derecho)
  cambia el nombre o la carpeta, o lo quita (sus sesiones pasan a «Sin proyecto»; no se borra nada). Los proyectos se pliegan
  con un clic.
- **Organizar las sesiones**: arrastra una sesión a otro proyecto, o usa su «⋯» (clic derecho) › «Mover a». Ahí mismo se le
  cambia el nombre (o con doble clic en su título, arriba). Moverla no cambia su carpeta de trabajo.
- **Visor de cambios** («± Cambios» en la cabecera de la sesión): panel a la derecha, con el ancho que quieras (arrastra su
  borde).
  - **Carpeta**: todo lo que ha cambiado desde el último commit de git (de cualquier agente o a mano), fichero a fichero,
    con las diferencias coloreadas.
  - **Esta sesión**: las ediciones que han hecho los agentes en la sesión (vale también sin git).
  - Se actualiza solo al terminar cada turno. Solo lee: no hace commits ni deshace nada.
- **Importar conversaciones** («＋» › «Importar conversaciones de este PC…»): lista las conversaciones de Claude Code
  (`~/.claude`), Codex (`~/.codex`) y Gemini CLI (`~/.gemini`) que ya tienes. Busca, filtra, marca las que quieras e
  importa.
  - Cada una pasa a ser una sesión en el proyecto de su carpeta, con toda la conversación. Los originales no se tocan.
  - Sigue con una cuenta del mismo proveedor si la tienes. Así Claude Code y Codex la **retoman tal cual**; si no, el agente
    recibe la conversación como traspaso.

## 6. Extensiones compartidas

En **🧩 Extensiones** (lateral) está lo que usan todas tus cuentas. Los cambios valen para las sesiones que se abran después.

- **Servidores MCP**: añádelos a mano o con «Importar de mi configuración…» (lee, sin tocarlos, `~/.claude.json`,
  `~/.codex/config.toml` y `~/.gemini/settings.json`). MultiAgente se los pasa a cada sesión, sea de Claude, Codex o
  Gemini. Las claves se guardan cifradas.
- **Skills** (formato SKILL.md): una sola carpeta para los tres agentes. Crea una, impórtalas de tu configuración o añade
  una carpeta.
- **Órdenes, subagentes y prompts** de cada agente, compartidos entre sus cuentas.
- **Plugins de Claude Code**: se instalan con `/plugin` en cualquier sesión de Claude y quedan para todas tus cuentas de
  Claude; aquí se activan o desactivan.
- **Instrucciones comunes**: normas para todos (se añaden al CLAUDE.md, AGENTS.md y GEMINI.md de cada cuenta). Lo que cada
  agente recuerde por su cuenta se comparte entre las cuentas de su proveedor.

## 7. Estadísticas, consumo y límites

- **⏱ Consumo y límites** (lateral): una tarjeta por cuenta con el % usado del **bloque de 5 horas** y del **semanal**,
  cuándo se reinicia cada uno y el **mes** frente a su presupuesto. La marca de cada barra es donde el equipo pasa a otra
  cuenta. Solo las suscripciones de Claude informan del 5 h y el semanal; las demás lo dicen en su tarjeta. «Actualizar
  ahora» lo vuelve a leer.
- **📊 Estadísticas** (lateral): hoy, 7 o 30 días. Turnos, tokens y coste por cuenta, por día y las sesiones que más han
  usado. Son los datos que informa cada agente; el coste suele darse solo en las cuentas de API (si no, sale «—»).
- **Límites** (Cuentas y proveedores › «Límites…» en cada cuenta):
  - a qué % del bloque de 5 horas y del semanal se pasa a otra cuenta (por defecto 92 % y 95 %; solo suscripciones de
    Claude informan de ese consumo);
  - **presupuesto del mes**: aviso al 80 %; al agotarlo la cuenta no se usa hasta el mes siguiente (el equipo releva).
- En la cabecera de cada sesión se ve el **contexto usado**.

## 8. Llevarlo a otro PC

1. En el PC nuevo, instala los requisitos (§1).
2. **Cierra MultiAgente** en el PC viejo y copia la carpeta entera.
3. Ábrelo en el nuevo. Tus cuentas, equipos, proyectos y sesiones están ahí. Si alguna cuenta sale «Falta autorizar»,
   pulsa «Autorizar…».
4. Las sesiones trabajan en la ruta de su carpeta: si en el PC nuevo un proyecto está en otro sitio, ponlo en la misma ruta
   (o empieza sesiones nuevas en la nueva; para que el proyecto apunte a ella: «⋯» › «Cambiar nombre o carpeta…»).

**Dónde se guarda todo**: en la carpeta `datos\` junto a `MultiAgente.exe`; nada en el registro de Windows ni en
`%APPDATA%`.

| Qué | Dónde |
|---|---|
| Cuentas, equipos, proyectos, sesiones, límites, servidores MCP y ajustes | `datos\multiagente.db` |
| Claves de API y de los MCP (cifradas) | `datos\boveda.bin` (su llave: `datos\boveda.clave`) |
| Inicio de sesión de cada cuenta | `datos\cuentas\<cuenta>` |
| Conversaciones de los agentes, skills, órdenes y plugins compartidos | `datos\comun` |
| Conversación de cada sesión (y su resumen) | `datos\sesiones\<id>` |
| Adjuntos y capturas | `datos\adjuntos` |
| Registro técnico | `datos\logs` |
| Preferencias de la ventana (idioma, ancho del visor, proyectos plegados…) | `datos\webview` |
| Idioma para los avisos que salen antes de abrir la ventana | `datos\idioma.txt` |

**No uses la misma carpeta en dos PCs a la vez**: al renovarse un inicio de sesión en uno, el otro puede perderlo.
Copiar es mudarse.

## 9. Seguridad

- La carpeta `datos\` lleva tus inicios de sesión y tus claves: trátala como una contraseña. No la dejes en una carpeta
  compartida ni la subas a un repositorio (el `.gitignore` ya la excluye).
- Los permisos de lo que hace cada agente (editar ficheros, ejecutar órdenes) son los de su CLI: MultiAgente solo te
  enseña la petición y responde lo que pulses.
- Usa cuentas que tengas derecho a usar así: repartir el trabajo entre suscripciones para saltarse sus límites puede
  ir contra las condiciones de cada proveedor.

## 10. Problemas frecuentes

| Síntoma | Solución |
|---|---|
| `Sondear-Agentes` dice «no se reconoce» un agente | Falta su CLI o su adaptador: vuelve a ejecutar `tools\Instalar-Requisitos.ps1` |
| Codex: «Authentication required» | La cuenta no tiene sesión iniciada: «Autorizar…» |
| Gemini: «Gemini API key is missing or not configured» | Añade la cuenta de Gemini con una clave de API |
| La ventana sale en blanco | Falta WebView2: instálalo desde el enlace de §1 |
| Una sesión con un proveedor por API se queda «trabajando» | El modelo está saturado o no está disponible para tu cuenta: **Modelo… › Probar cuáles funcionan** |

## Para desarrolladores

- Normas y contexto: [CLAUDE.md](CLAUDE.md). Plan: [Documentacion/MultiAgente_Plan.md](Documentacion/MultiAgente_Plan.md).
  Estado: [tasks/MultiAgente_Estado.md](tasks/MultiAgente_Estado.md). Qué costaría llevarlo a Mac: [Documentacion/MultiAgente_Mac.md](Documentacion/MultiAgente_Mac.md).
- Tecnología: anfitrión .NET 10 WinForms con WebView2, interfaz HTML/JS (`src/MultiAgente.App/ui`), SQLite, xUnit. Habla
  con los agentes por el [Agent Client Protocol](https://agentclientprotocol.com) (ACP).
- Requisitos para compilar: .NET 10 SDK.
- Compilar y probar: `dotnet build MultiAgente.sln` · `dotnet test MultiAgente.sln`.
- Carpeta lista para copiar: `powershell -ExecutionPolicy Bypass -File tools\Empaquetar.ps1` → `dist\MultiAgente`.
- Prueba de portabilidad (empaqueta, copia a otra ruta, arranca, captura): `tools\Probar-Portatil.ps1`.
- Pruebas con los agentes reales (cuenta vacía, sin gastar): `$env:MULTIAGENTE_PRUEBA_REAL='1'; dotnet test`.
- Interfaz sin la aplicación (puente simulado): `python -m http.server 8766` en la raíz y abrir
  `http://127.0.0.1:8766/tools/ui-prueba.html`.
- Órdenes de `MultiAgente.exe` para pruebas: `--comprobar [salida.json]` (sin ventana), `--captura <png>`, `--fuera`
  (ventana fuera de la pantalla) y `--cerrar` (cierra el MultiAgente abierto desde esa carpeta).
- Idiomas de la interfaz: los textos se escriben en castellano en `app.js` con `trad('…')` (los de `index.html`, tal
  cual) y sus traducciones están en `ui/idiomas/en.js` y `ca.js`, con el mismo texto como clave. `IdiomasTests` avisa
  si falta alguna. Los mensajes del núcleo (C#) llegan en castellano y la interfaz los traduce con `traducir()`.
- Librerías de terceros de la interfaz y sus licencias: [src/MultiAgente.App/ui/lib/LICENCIAS.md](src/MultiAgente.App/ui/lib/LICENCIAS.md).

## Licencia

MultiAgente es software libre con licencia [GNU GPL v3](LICENSE): puedes usarlo, estudiarlo, modificarlo y
redistribuirlo; si distribuyes una versión modificada, tiene que llevar la misma licencia y su código. Las librerías de
terceros de la interfaz conservan las suyas ([LICENCIAS.md](src/MultiAgente.App/ui/lib/LICENCIAS.md)).

Claude, Codex, Gemini y el resto de nombres de productos son marcas de sus dueños. MultiAgente es un proyecto
independiente, sin relación con Anthropic, OpenAI, Google ni NVIDIA: usa sus herramientas oficiales con tus propias cuentas.
