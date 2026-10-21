// Comprueba por ACP qué ofrece cada agente instalado (capacidades, formas de iniciar sesión y si crea sesiones)
// con una carpeta de cuenta vacía y temporal: no toca la configuración real del usuario.
// Uso: node tools/Sondear-Agentes.mjs [claude|codex|gemini ...]
import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const AGENTES = {
  claude: { orden: 'claude-agent-acp', args: [], variable: 'CLAUDE_CONFIG_DIR' },
  codex:  { orden: 'codex-acp',        args: [], variable: 'CODEX_HOME' },
  gemini: { orden: 'gemini',           args: ['--acp'], variable: 'GEMINI_CLI_HOME' },
};

function sondear(nombre) {
  const a = AGENTES[nombre];
  const carpeta = mkdtempSync(join(tmpdir(), `multiagente_sonda_${nombre}_`));
  const env = { ...process.env, [a.variable]: carpeta };
  for (const k of Object.keys(env)) if (k.startsWith('CLAUDE_CODE_') || k === 'CLAUDECODE') delete env[k];
  const hijo = spawn(a.orden, a.args, { env, cwd: carpeta, shell: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let id = 0, resto = '', errores = '';
  const pendientes = new Map();
  const pedir = (method, params) => new Promise((ok) => {
    const n = ++id; pendientes.set(n, ok);
    hijo.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: n, method, params }) + '\n');
  });
  hijo.stdout.on('data', (d) => {
    resto += d; let i;
    while ((i = resto.indexOf('\n')) >= 0) {
      const linea = resto.slice(0, i).trim(); resto = resto.slice(i + 1);
      if (!linea.startsWith('{')) continue;
      try {
        const m = JSON.parse(linea);
        if (m.id != null && pendientes.has(m.id)) { pendientes.get(m.id)(m); pendientes.delete(m.id); }
      } catch { /* línea que no es JSON-RPC */ }
    }
  });
  hijo.stderr.on('data', (d) => { errores += d; });
  const limite = (ms) => new Promise((ok) => setTimeout(() => ok({ error: { message: `sin respuesta en ${ms / 1000} s` } }), ms));

  return (async () => {
    const r = { agente: nombre };
    const ini = await Promise.race([pedir('initialize', {
      protocolVersion: 1,
      clientCapabilities: { fs: { readTextFile: false, writeTextFile: false }, terminal: false },
      clientInfo: { name: 'MultiAgente-sonda', version: '0.0.1' },
    }), limite(60000)]);
    if (ini.error) r.initialize = { error: ini.error.message };
    else {
      const res = ini.result;
      r.protocolo = res.protocolVersion;
      r.agente_info = res.agentInfo;
      r.capacidades = res.agentCapabilities;
      r.autenticacion = (res.authMethods || []).map((m) => `${m.id}: ${m.name}`);
      const s = await Promise.race([pedir('session/new', { cwd: carpeta, mcpServers: [] }), limite(60000)]);
      r.session_new = s.error ? { error: s.error.message, codigo: s.error.code } : {
        sessionId: s.result.sessionId, modos: s.result.modes?.availableModes?.map((m) => m.id),
        modelos: s.result.models?.availableModels?.map((m) => m.modelId),
      };
    }
    if (errores.trim()) r.stderr = errores.trim().split('\n').slice(-3);
    // shell:true deja un cmd intermedio: hay que cerrar el árbol entero para soltar la carpeta temporal
    spawnSync('taskkill', ['/PID', String(hijo.pid), '/T', '/F'], { stdio: 'ignore' });
    await new Promise((ok) => setTimeout(ok, 1000));
    try { rmSync(carpeta, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 }); }
    catch { r.aviso = `no se pudo borrar ${carpeta}`; }
    return r;
  })();
}

const elegidos = process.argv.slice(2).length ? process.argv.slice(2) : Object.keys(AGENTES);
for (const n of elegidos) console.log(JSON.stringify(await sondear(n), null, 2));
process.exit(0);
