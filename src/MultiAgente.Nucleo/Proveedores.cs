using MultiAgente.Nucleo.Acp;

namespace MultiAgente.Nucleo;

/// <summary>
/// Lo que distingue a un proveedor: con qué agente ACP se habla, qué variable aísla la carpeta de cada cuenta, cómo se
/// inicia sesión y en qué variable va la clave de API.
/// </summary>
/// <param name="Id">Claude, Codex o Gemini.</param>
/// <param name="Nombre">Nombre para el usuario.</param>
/// <param name="VariableCarpeta">Variable con la carpeta de la cuenta.</param>
/// <param name="RequisitoAcp">Requisito (<see cref="Requisitos"/>) que habla ACP.</param>
/// <param name="ArgumentosAcp">Argumentos para que hable ACP.</param>
/// <param name="VariablesClave">Variables en las que se pone la clave de API.</param>
/// <param name="MetodoAutenticacionClave">Método ACP <c>authenticate</c> para cuentas con clave (null si no hace falta).</param>
/// <param name="RequisitoLogin">Requisito con el que se inicia sesión en una ventana.</param>
/// <param name="ArgumentosLogin">Argumentos para iniciar sesión.</param>
/// <param name="FicheroLogin">Fichero (relativo a la carpeta de la cuenta) que existe cuando hay sesión iniciada.</param>
/// <param name="DondeClave">Dónde se consigue una clave de API.</param>
public sealed record Proveedor(
    string Id, string Nombre, string VariableCarpeta, string RequisitoAcp, string[] ArgumentosAcp,
    string[] VariablesClave, string? MetodoAutenticacionClave, string RequisitoLogin, string[] ArgumentosLogin,
    string FicheroLogin, string DondeClave);

public static class Proveedores
{
    public static readonly Proveedor Claude = new(
        "Claude", "Claude", "CLAUDE_CONFIG_DIR", "claude-agent-acp", [],
        ["ANTHROPIC_API_KEY"], null, "claude", ["auth", "login"], ".credentials.json",
        "https://console.anthropic.com/settings/keys");

    public static readonly Proveedor Codex = new(
        "Codex", "Codex", "CODEX_HOME", "codex-acp", [],
        ["OPENAI_API_KEY", "CODEX_API_KEY"], "api-key", "codex", ["login"], "auth.json",
        "https://platform.openai.com/api-keys");

    public static readonly Proveedor Gemini = new(
        "Gemini", "Gemini", "GEMINI_CLI_HOME", "gemini", ["--acp"],
        ["GEMINI_API_KEY"], "gemini-api-key", "gemini", [], @".gemini\oauth_creds.json",
        "https://aistudio.google.com/apikey");

    public static IReadOnlyList<Proveedor> Todos { get; } = [Claude, Codex, Gemini];

    public static Proveedor? Buscar(string? id) => Todos.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Variables que nunca se heredan: podrían colar otra cuenta, otra clave o la sesión de otro agente.</summary>
    private static readonly string[] NoHeredar =
    [
        "CLAUDE_CONFIG_DIR", "CODEX_HOME", "GEMINI_CLI_HOME", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN",
        "OPENAI_API_KEY", "CODEX_API_KEY", "GEMINI_API_KEY", "GOOGLE_API_KEY", "CLAUDECODE",
    ];

    /// <summary>
    /// Entorno para lanzar un agente con una cuenta: su carpeta y, si es de API, su clave. Quita lo heredado que podría
    /// mezclar cuentas (<see cref="NoHeredar"/> y las <c>CLAUDE_CODE_*</c> de una sesión padre). null = quitar.
    /// </summary>
    /// <param name="endpoint">API compatible: dirección (Claude: compatible con Anthropic; Codex: con OpenAI Responses).</param>
    /// <param name="modelo">Modelo fijado por la cuenta (API compatible).</param>
    /// <param name="extra">Variables recomendadas por el catálogo para ese proveedor (mapeo de modelos, tiempos…).</param>
    public static Dictionary<string, string?> Entorno(Proveedor p, string carpetaCuenta, string? clave,
                                                      IEnumerable<string>? heredadas = null,
                                                      string? endpoint = null, string? modelo = null,
                                                      IReadOnlyDictionary<string, string>? extra = null)
    {
        var e = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in NoHeredar) e[v] = null;
        heredadas ??= Environment.GetEnvironmentVariables().Keys.Cast<string>();
        foreach (var v in heredadas)
            if (v.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase) ||
                v.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)) e[v] = null;
        e[p.VariableCarpeta] = carpetaCuenta;
        if (endpoint is not null && p == Claude)
        {
            // API compatible con la de Anthropic (GLM, DeepSeek, Kimi…): el agente de Claude Code contra otro destino.
            // La clave va en AUTH_TOKEN (Bearer) y API_KEY vacía: si tuviera valor, Claude Code la usaría antes.
            e["ANTHROPIC_BASE_URL"] = endpoint;
            e["ANTHROPIC_AUTH_TOKEN"] = clave;
            e["ANTHROPIC_API_KEY"] = "";
            e["API_TIMEOUT_MS"] = "3000000";
            foreach (var (k, v) in extra ?? new Dictionary<string, string>()) e[k] = v;
            if (!string.IsNullOrWhiteSpace(modelo))
                foreach (var v in new[] { "ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL",
                                          "ANTHROPIC_DEFAULT_FABLE_MODEL", "CLAUDE_CODE_SUBAGENT_MODEL" })
                    e[v] = modelo;
        }
        else if (endpoint is not null && p == Codex)
        {
            // API compatible con OpenAI Responses: el config.toml de la carpeta de la cuenta apunta a MULTIAGENTE_API_KEY
            e["MULTIAGENTE_API_KEY"] = clave;
            foreach (var v in p.VariablesClave) e[v] = clave;
        }
        else if (clave is not null)
            foreach (var v in p.VariablesClave) e[v] = clave;
        e["DISABLE_AUTOUPDATER"] = "1";
        return e;
    }

    /// <summary>La cuenta tiene credencial: sesión iniciada (Login) o clave en la bóveda (Api).</summary>
    public static bool Autorizada(Cuenta c, Rutas rutas, Boveda boveda)
    {
        var p = Buscar(c.Proveedor);
        if (p is null) return false;
        if (c.Tipo == TipoCuenta.Api) return c.RefClave is not null && boveda.Contiene(c.RefClave);
        return File.Exists(Path.Combine(rutas.Resolver(c.Carpeta), p.FicheroLogin));
    }

    /// <summary>Orden para hablar ACP con el proveedor; null si falta algo por instalar.</summary>
    public static OrdenAgente? OrdenAcp(Proveedor p, IReadOnlyList<Requisito>? requisitos = null)
    {
        var o = Requisitos.Orden(p.RequisitoAcp, requisitos);
        return o is null ? null : o with { Argumentos = [.. o.Argumentos, .. p.ArgumentosAcp] };
    }
}
