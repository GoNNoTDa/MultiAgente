# MultiAgente

[Español](README.md) · **English**

Work with **Claude Code, Codex and Gemini** from a single window, like the Claude Code desktop app, but with **several
accounts from several providers**:

- Each session uses one specific account or a **team** of accounts that share the work.
- When an account runs out, the session **carries on in another one** (same provider or a different one) **without
  losing the context**.
- Drag and drop files, paste screenshots with Ctrl+V, permission buttons, model and permission mode at hand.
- **Portable**: copy the folder to another PC and keep going (at most, authorize an account again).

> **Status: feature-complete, still being tested with real accounts (v0.7).** Subscription, API-key and
> API-compatible accounts (GLM, DeepSeek, Kimi, Qwen, OpenRouter, Ollama…), sessions with permissions and attachments,
> **teams with automatic load sharing and handover** between accounts and providers, **shared extensions** (MCP, skills,
> commands, plugins, instructions), **statistics, usage and limits**, **projects**, a **diff viewer** and **importing
> previous conversations** from the PC.

> **Note:** the user interface is in Spanish. This guide gives each label in Spanish followed by its meaning, e.g.
> **Cuentas y proveedores** (Accounts and providers).

---

## 1. Requirements (once per PC)

| What | What for | How |
|---|---|---|
| Windows 10 or 11 | — | — |
| WebView2 | The MultiAgente window | Ships with Windows 10/11. If missing: [Microsoft download](https://developer.microsoft.com/microsoft-edge/webview2/) |
| Node.js LTS | The agents' CLIs | [nodejs.org](https://nodejs.org) (LTS version, normal install) |
| The CLIs and their adapters | The agents | Script below (only installs what is missing) |

You don't need to install .NET: MultiAgente carries its own.

### Installing the CLIs and adapters

From the MultiAgente folder, in PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File tools\Instalar-Requisitos.ps1
```

It installs with `npm install -g` (for your user, no administrator rights) whatever you don't already have from this
list. What you already have is left alone (add `-Actualizar` to bring everything to the latest version):

| Package | What it is |
|---|---|
| `@anthropic-ai/claude-code` | Claude Code |
| `@agentclientprotocol/claude-agent-acp` | Adapter so MultiAgente can talk to Claude Code |
| `@openai/codex` | Codex CLI |
| `@agentclientprotocol/codex-acp` | Adapter for Codex |
| `@google/gemini-cli` | Gemini CLI (needs no adapter) |

If you only use one provider, its CLI and adapter are enough.

To check that the agents answer (it doesn't touch your configuration; it uses temporary folders):

```powershell
node tools\Sondear-Agentes.mjs
```

MultiAgente talks to every agent through the [Agent Client Protocol](https://agentclientprotocol.com) (ACP).

## 2. Getting started

1. Copy the `MultiAgente` folder wherever you like (for example `C:\MultiAgente`). It must be somewhere you can write:
   the first time it creates a `datos\` (data) folder inside.
2. Open `MultiAgente.exe`. The home screen shows which agents it finds on the PC and their versions; if something is
   missing, the **Instalar lo que falta** (Install what's missing) button opens the requirements installer (§1). It
   checks again when you come back to the window.
3. Go to **Cuentas y proveedores › Añadir cuenta** (Accounts and providers › Add account) and add the accounts you want
   (see §3).
4. Create a **team** in **Cuentas y proveedores › Equipos** (Teams), or use **Principal** (Main), which is created
   automatically with all your accounts.
5. **Nueva sesión** (New session) → choose the project folder and the account → type. The agent works in that folder as
   in Claude Code: it creates and edits files, runs commands and keeps its task list; when it needs permission, buttons
   appear. Drag files or paste screenshots (Ctrl+V) into the box; `/` shows the agent's commands; ■ or Esc twice
   interrupts.

## 3. Adding accounts

Each account has its own folder inside `datos\cuentas\`, so you can have several from the same provider without them
clashing, and the configuration you already have on your PC (`~/.claude`, `~/.codex`, `~/.gemini`) is **never touched**.

| Provider | Sign-in | API key |
|---|---|---|
| **Claude** | **Autorizar…** (Authorize) opens Claude Code in a window: type `/login`, follow the browser steps with that account's email and close the window | Paste a key from [console.anthropic.com](https://console.anthropic.com) |
| **Codex** | **Autorizar…** opens `codex login`: sign in with your ChatGPT account in the browser | Paste a key from [platform.openai.com](https://platform.openai.com/api-keys) |
| **Gemini** | Google account (personal accounts may not be accepted; a key or a work account is better) | Paste a key from [Google AI Studio](https://aistudio.google.com/apikey) |

Keys are stored encrypted in `datos\boveda.bin`, never in the database or the logs.

**Other API providers** (account type *Otros modelos con Claude Code* or *con Codex*, "Other models with Claude Code /
with Codex"): GLM (Z.ai and Zhipu), DeepSeek, Kimi, MiniMax, Qwen (Alibaba), OpenRouter, DeepInfra, Fireworks, Vercel AI
Gateway, Grok (xAI), NVIDIA (build.nvidia.com), Groq, Cerebras, Mistral, Together, Azure OpenAI, and models on your own
machine with Ollama, LM Studio or LiteLLM. Pick the provider, paste the key (local ones don't need it), check the address
and press **Actualizar lista** (Refresh list) to see the models it offers right now. With Claude Code you get all its
tools with that model. The provider list lives in `catalogo\proveedores.json` and can be extended without recompiling.

**Providers that only have the OpenAI API** (NVIDIA, Groq, Cerebras, Mistral, Together, or *Otro compatible con
OpenAI* — "Other OpenAI-compatible" — for your own server, e.g. vLLM): MultiAgente includes a **translator** that puts
them to work with Claude Code.

- Claude Code talks to the translator, which runs inside MultiAgente and only listens on this PC. It converts each
  request to the OpenAI format and translates the answer on the fly: text, reasoning, images and tools.
- The provider's key never leaves MultiAgente: Claude Code only gets an internal pass.
- When you add one of these accounts, MultiAgente tests the model first with a minimal request. Some providers (NVIDIA)
  list models they don't serve to every account.
- **Modelo…** (Model) on each API account changes the model without deleting the account. **Probar cuáles funcionan**
  (Test which ones work) tries every chat model of the provider with your key and keeps only those that answer.
- If the provider doesn't start answering within 2 minutes (a saturated model stuck in a queue), the request is stopped
  with a clear message instead of leaving the session "working" forever.

## 4. Teams, load sharing and handover

- When you add your first account, the **Principal** (Main) team is created with all of them (new ones are appended).
  In **Cuentas y proveedores › Equipos** you change the order (▲▼), who is in, the rule, whether it may switch to another
  provider and the countdown. You can create more teams (for example "Claude only" or "Cheap ones").
- Rules: **Prioridad** (Priority: the first one with headroom), **Rotación** (Rotation: each new session on the next
  one), **La menos usada** (Least used) and **Suscripciones primero** (Subscriptions first: API-key accounts as backup).
- When creating a session choose **Automático · Principal** (the team decides and hands over) or a **fixed account**.
  You can change it later from the session header.
- **Handover**: when the account hits its limit (or, for Claude, 92 % of the 5-hour block or 95 % of the weekly one) a
  banner appears: "… → continues on 'Account 2' in 60 s · Switch now · Cancel". It never cuts a turn: it waits for it to
  finish. If the limit cut the turn, the new account receives "Continue where you left off".
- **Same context**: between accounts of the same provider the conversation simply continues (they share their
  conversations in `datos\comun`). With another provider, the new agent receives a **handover package**: the session's
  live summary (kept up to date by a short Claude Haiku call after turns; it can be turned off) and the latest messages.
- The exhausted account shows "agotada · vuelve a las HH:MM" (exhausted · back at HH:MM) and isn't used until then.

## 5. Projects, diff viewer and history

- **Projects** (sidebar, **＋** next to *Proyectos* › *Nuevo proyecto…*): a name and the folder its sessions work in.
  Several projects can share a folder. A project's **＋** starts a session in it; its **⋯** (or right-click) renames it,
  changes its folder or removes it (its sessions go to *Sin proyecto*, "No project"; nothing is deleted). Projects
  collapse with a click.
- **Organizing sessions**: drag a session onto another project, or use its **⋯** (right-click) › *Mover a* (Move to).
  The same menu renames it (or double-click its title at the top). Moving it doesn't change its working folder.
- **Diff viewer** (**± Cambios**, "Changes", in the session header): a panel on the right whose width you can drag.
  - **Carpeta** (Folder): everything that changed since the last git commit (by any agent or by hand), file by file,
    with coloured diffs.
  - **Esta sesión** (This session): the edits the agents made in the session (works without git too).
  - It refreshes by itself at the end of each turn. Read-only: it never commits or reverts anything.
- **Import conversations** (**＋** › *Importar conversaciones de este PC…*): lists the Claude Code (`~/.claude`), Codex
  (`~/.codex`) and Gemini CLI (`~/.gemini`) conversations you already have. Search, filter, tick the ones you want and
  import.
  - Each becomes a session in the project of its folder, with the whole conversation. The originals are not touched.
  - It continues with an account of the same provider if you have one, so Claude Code and Codex **resume it as it
    was**; otherwise the agent receives the conversation as a handover.

## 6. Shared extensions

**🧩 Extensiones** (sidebar) holds what all your accounts use. Changes apply to sessions opened afterwards.

- **MCP servers**: add them by hand or with *Importar de mi configuración…* (Import from my configuration; it reads,
  without touching them, `~/.claude.json`, `~/.codex/config.toml` and `~/.gemini/settings.json`). MultiAgente passes them
  to every session, whether Claude, Codex or Gemini; each server can be limited to some agents (useful when one needs its
  own sign-in in each agent). Secrets are stored encrypted.
- **Skills** (SKILL.md format): a single folder for the three agents. Create one, import them from your configuration or
  add a folder.
- **Commands, subagents and prompts** of each agent, shared between its accounts.
- **Claude Code plugins**: installed with `/plugin` in any Claude session and kept for all your Claude accounts; here
  you turn them on or off.
- **Common instructions**: rules for everyone (added to each account's CLAUDE.md, AGENTS.md and GEMINI.md). Whatever
  each agent remembers on its own is shared between the accounts of its provider.

## 7. Statistics, usage and limits

- **⏱ Consumo y límites** (Usage and limits, sidebar): one card per account with the % used of the **5-hour block** and
  the **weekly** limit, when each resets, and the **month** against its budget. The mark on each bar is where the team
  switches to another account. Only Claude subscriptions report the 5-hour and weekly usage; the others say so on their
  card. *Actualizar ahora* (Refresh now) reads it again.
- **📊 Estadísticas** (Statistics, sidebar): today, 7 or 30 days. Turns, tokens and cost per account, per day and the
  sessions that used the most. This is what each agent reports; cost is usually only given for API accounts (otherwise
  "—").
- **Limits** (*Cuentas y proveedores › Límites…* on each account):
  - at what % of the 5-hour and weekly blocks to switch to another account (92 % and 95 % by default; only Claude
    subscriptions report that usage);
  - **monthly budget**: warning at 80 %; once spent, the account isn't used until next month (the team hands over).
- Each session header shows the **context used**.

## 8. Moving to another PC

1. On the new PC, install the requirements (§1).
2. **Close MultiAgente** on the old PC and copy the whole folder.
3. Open it on the new one. Your accounts, teams, projects and sessions are there. If an account shows *Falta
   autorizar* (Needs authorizing), press **Autorizar…**.
4. Sessions work in their folder's path: if a project lives somewhere else on the new PC, put it at the same path (or
   start new sessions in the new location; to point the project at it: **⋯** › *Cambiar nombre o carpeta…*).

**Where everything is stored**: in the `datos\` folder next to `MultiAgente.exe`; nothing in the Windows registry or in
`%APPDATA%`.

| What | Where |
|---|---|
| Accounts, teams, projects, sessions, limits, MCP servers and settings | `datos\multiagente.db` |
| API and MCP keys (encrypted) | `datos\boveda.bin` (its key: `datos\boveda.clave`) |
| Each account's sign-in | `datos\cuentas\<account>` |
| The agents' conversations, shared skills, commands and plugins | `datos\comun` |
| Each session's conversation (and its summary) | `datos\sesiones\<id>` |
| Attachments and screenshots | `datos\adjuntos` |
| Technical log | `datos\logs` |
| Window preferences (viewer width, collapsed projects…) | `datos\webview` |

**Don't use the same folder on two PCs at once**: when a sign-in is renewed on one, the other may lose it. Copying is
moving.

## 9. Security

- The `datos\` folder holds your sign-ins and keys: treat it like a password. Don't leave it in a shared folder or push
  it to a repository (`.gitignore` already excludes it).
- What each agent may do (edit files, run commands) is governed by its own CLI: MultiAgente only shows you the request
  and answers with what you press.
- Use accounts you are entitled to use this way: spreading work across subscriptions to get around their limits may go
  against each provider's terms.

## 10. Troubleshooting

| Symptom | Fix |
|---|---|
| `Sondear-Agentes` says an agent is "not recognized" | Its CLI or adapter is missing: run `tools\Instalar-Requisitos.ps1` again |
| Codex: "Authentication required" | The account isn't signed in: **Autorizar…** |
| Gemini: "Gemini API key is missing or not configured" | Add the Gemini account with an API key |
| The window is blank | WebView2 is missing: install it from the link in §1 |
| A session stays "working" with an API provider | The model is saturated or not available to your account: **Modelo… › Probar cuáles funcionan** |

## For developers

- Rules and context (in Spanish): [CLAUDE.md](CLAUDE.md). Design plan: [Documentacion/MultiAgente_Plan.md](Documentacion/MultiAgente_Plan.md).
  Status: [tasks/MultiAgente_Estado.md](tasks/MultiAgente_Estado.md).
- Stack: .NET 10 WinForms host with WebView2, HTML/JS interface (`src/MultiAgente.App/ui`), SQLite, xUnit.
- To build: .NET 10 SDK.
- Build and test: `dotnet build MultiAgente.sln` · `dotnet test MultiAgente.sln`.
- Ready-to-copy folder: `powershell -ExecutionPolicy Bypass -File tools\Empaquetar.ps1` → `dist\MultiAgente`.
- Portability test (packages, copies to another path, starts, takes a screenshot): `tools\Probar-Portatil.ps1`.
- Tests with the real agents (empty account, nothing is spent): `$env:MULTIAGENTE_PRUEBA_REAL='1'; dotnet test`.
- Interface without the application (simulated bridge): `python -m http.server 8766` in the repository root and open
  `http://127.0.0.1:8766/tools/ui-prueba.html`.
- `MultiAgente.exe` switches for testing: `--comprobar [output.json]` (no window), `--captura <png>`, `--fuera`
  (window off-screen) and `--cerrar` (closes the MultiAgente opened from that folder).
- Third-party libraries in the interface and their licences: [src/MultiAgente.App/ui/lib/LICENCIAS.md](src/MultiAgente.App/ui/lib/LICENCIAS.md).

## Licence

MultiAgente is free software under the [GNU GPL v3](LICENSE): you may use, study, modify and redistribute it; if you
distribute a modified version, it must carry the same licence and its source code. The interface's third-party libraries
keep their own licences ([LICENCIAS.md](src/MultiAgente.App/ui/lib/LICENCIAS.md)).

Claude, Codex, Gemini and other product names are trademarks of their owners. MultiAgente is an independent project, not
affiliated with Anthropic, OpenAI, Google or NVIDIA: it uses their official tools with your own accounts.
