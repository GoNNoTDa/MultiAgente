using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MultiAgente.Nucleo;

/// <summary>Una variable de entorno (stdio) o cabecera (http) de un servidor MCP. Las secretas van a la bóveda.</summary>
public sealed class ValorMcp
{
    public string Nombre { get; set; } = "";
    /// <summary>Valor en claro (solo si no es secreto). Puede llevar ${VARIABLE} del entorno del usuario.</summary>
    public string? Valor { get; set; }
    public bool Secreto { get; set; }
}

/// <summary>Un servidor MCP del catálogo de MultiAgente (común a todas las cuentas y agentes).</summary>
public sealed class ServidorMcpDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Nombre { get; set; } = "";
    /// <summary>stdio, http o sse.</summary>
    public string Tipo { get; set; } = "stdio";
    public string? Comando { get; set; }
    public List<string> Argumentos { get; set; } = [];
    public string? Url { get; set; }
    /// <summary>Variables de entorno (stdio) o cabeceras (http/sse).</summary>
    public List<ValorMcp> Valores { get; set; } = [];
    public bool Activo { get; set; } = true;
    /// <summary>De dónde salió (p. ej. «importado de ~/.claude.json»).</summary>
    public string? Origen { get; set; }
    /// <summary>Agentes que lo usan (Claude, Codex, Gemini); null o vacío: todos.</summary>
    public List<string>? Agentes { get; set; }

    public bool ParaAgente(string proveedor) => Agentes is not { Count: > 0 } || Agentes.Contains(proveedor);
}

/// <summary>
/// Servidores MCP de MultiAgente: se guardan en la base (sin secretos; los secretos en la bóveda) y se pasan a cada
/// sesión por ACP (<c>mcpServers</c>), así valen para Claude, Codex y Gemini y para todas las cuentas sin tocar sus
/// configuraciones.
/// </summary>
public static class CatalogoMcp
{
    private const string Ajuste = "mcp";
    private static readonly Regex PareceSecreto = new(@"(key|token|secret|password|passwd|pwd|auth|bearer|credential)", RegexOptions.IgnoreCase);

    public static List<ServidorMcpDef> Cargar(IRepositorio repo)
    {
        try
        {
            var json = repo.LeerAjuste(Ajuste);
            return json is null ? [] : JsonSerializer.Deserialize<List<ServidorMcpDef>>(json, Json.Opciones) ?? [];
        }
        catch (JsonException) { return []; }
    }

    public static void Guardar(IRepositorio repo, List<ServidorMcpDef> lista) =>
        repo.GuardarAjuste(Ajuste, JsonSerializer.Serialize(lista, Json.Opciones));

    public static string RefSecreto(ServidorMcpDef s, string nombre) => $"mcp:{s.Id}:{nombre}";

    /// <summary>¿Se guarda como secreto? (nombre que lo parece: *_KEY, TOKEN, Authorization…)</summary>
    public static bool EsSecreto(string nombre, string? valor) =>
        PareceSecreto.IsMatch(nombre) && !string.IsNullOrEmpty(valor) && !valor.StartsWith("${");

    /// <summary>
    /// Pone los valores secretos en la bóveda y los quita del servidor (queda solo la marca). Devuelve el servidor
    /// listo para guardar.
    /// </summary>
    public static ServidorMcpDef GuardarSecretos(ServidorMcpDef s, Boveda boveda)
    {
        foreach (var v in s.Valores)
        {
            if (!v.Secreto && EsSecreto(v.Nombre, v.Valor)) v.Secreto = true;
            if (v.Secreto && !string.IsNullOrEmpty(v.Valor))
            {
                boveda.Guardar(RefSecreto(s, v.Nombre), v.Valor);
                v.Valor = null;
            }
        }
        return s;
    }

    public static void BorrarSecretos(ServidorMcpDef s, Boveda boveda)
    {
        foreach (var v in s.Valores.Where(v => v.Secreto)) boveda.Borrar(RefSecreto(s, v.Nombre));
    }

    /// <summary>Definiciones ACP de los servidores activos (los secretos se leen de la bóveda en este momento).</summary>
    public static List<ServidorMcp> ParaSesion(IEnumerable<ServidorMcpDef> lista, Boveda boveda, string? path = null, string? proveedor = null)
    {
        var r = new List<ServidorMcp>();
        foreach (var s in lista.Where(x => x.Activo && (proveedor is null || x.ParaAgente(proveedor))))
        {
            try { r.Add(new ServidorMcp(s.Nombre, Definicion(s, boveda, path))); }
            catch (ExcepcionUsuario ex) { Registro.Aviso($"MCP {s.Nombre}: {ex.Message}"); }
        }
        return r;
    }

    /// <summary>Formato ACP v1: stdio {name, command (absoluto), args, env[]}; http/sse {type, name, url, headers[]}.</summary>
    public static JsonObject Definicion(ServidorMcpDef s, Boveda boveda, string? path = null)
    {
        JsonArray Valores() => new(s.Valores.Select(v => (JsonNode)new JsonObject
        {
            ["name"] = v.Nombre,
            ["value"] = Expandir(v.Secreto ? boveda.Obtener(RefSecreto(s, v.Nombre)) ?? "" : v.Valor ?? ""),
        }).ToArray());

        if (s.Tipo is "http" or "sse")
        {
            if (string.IsNullOrWhiteSpace(s.Url)) throw new ExcepcionUsuario("falta la dirección");
            return new JsonObject { ["type"] = s.Tipo, ["name"] = s.Nombre, ["url"] = Expandir(s.Url), ["headers"] = Valores() };
        }
        if (string.IsNullOrWhiteSpace(s.Comando)) throw new ExcepcionUsuario("falta la orden");
        var (exe, previos) = ResolverOrden(Expandir(s.Comando), path);
        return new JsonObject
        {
            ["name"] = s.Nombre,
            ["command"] = exe,
            ["args"] = new JsonArray(previos.Concat(s.Argumentos.Select(Expandir)).Select(a => (JsonNode)a).ToArray()),
            ["env"] = Valores(),
        };
    }

    /// <summary>${NOMBRE} → variable de entorno del usuario (como hacen Claude Code y Gemini en sus configuraciones).</summary>
    public static string Expandir(string texto) =>
        Regex.Replace(texto, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");

    /// <summary>
    /// ACP pide la ruta absoluta del ejecutable. «npx» o «uvx» se buscan en el PATH; los lanzadores .cmd/.bat de Windows
    /// se ejecutan con cmd.exe /c (un .cmd no se puede lanzar directamente).
    /// </summary>
    public static (string Exe, string[] Previos) ResolverOrden(string orden, string? path = null)
    {
        string? encontrado = null;
        if (Path.IsPathRooted(orden)) encontrado = File.Exists(orden) ? orden : null;
        else
        {
            var exts = Path.HasExtension(orden) ? [""] : new[] { ".exe", ".cmd", ".bat", ".com" };
            foreach (var dir in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var e in exts)
                {
                    string c;
                    try { c = Path.Combine(dir.Trim('"'), orden + e); } catch (ArgumentException) { continue; }
                    if (File.Exists(c)) { encontrado = c; break; }
                }
                if (encontrado is not null) break;
            }
        }
        if (encontrado is null) throw new ExcepcionUsuario($"no se encuentra «{orden}» en este equipo");
        if (encontrado.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || encontrado.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            return (Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe", ["/d", "/c", encontrado]);
        return (encontrado, []);
    }
}

/// <summary>
/// Lee (solo lectura) los servidores MCP que el usuario ya tiene configurados en Claude Code, Codex y Gemini, para
/// traerlos al catálogo de MultiAgente.
/// </summary>
public static class ImportarMcp
{
    public static List<ServidorMcpDef> Buscar(string? perfil = null)
    {
        perfil ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var r = new List<ServidorMcpDef>();
        r.AddRange(DeJson(Path.Combine(perfil, ".claude.json"), "~/.claude.json (Claude Code)"));
        r.AddRange(DeJson(Path.Combine(perfil, ".gemini", "settings.json"), "~/.gemini/settings.json (Gemini)"));
        r.AddRange(DeToml(Path.Combine(perfil, ".codex", "config.toml"), "~/.codex/config.toml (Codex)"));
        return r;
    }

    /// <summary>Formato de Claude Code y Gemini: { "mcpServers": { nombre: { command, args, env | url/httpUrl, headers, type } } }.</summary>
    public static List<ServidorMcpDef> DeJson(string fichero, string origen)
    {
        var r = new List<ServidorMcpDef>();
        if (!File.Exists(fichero)) return r;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(fichero))?["mcpServers"] is not JsonObject servidores) return r;
            foreach (var (nombre, def) in servidores)
            {
                if (def is not JsonObject d) continue;
                var s = new ServidorMcpDef { Nombre = nombre, Origen = origen };
                var tipo = Cadena(d["type"]);
                var url = Cadena(d["httpUrl"]) ?? Cadena(d["url"]);
                if (url is not null)
                {
                    // Gemini: «httpUrl» es HTTP y «url» es SSE. Claude: lo dice «type» (http por defecto).
                    s.Tipo = d["httpUrl"] is not null ? "http" : tipo is "sse" or "http" ? tipo : origen.Contains("Gemini") ? "sse" : "http";
                    s.Url = url;
                    if (d["headers"] is JsonObject h)
                        foreach (var (k, v) in h) s.Valores.Add(new ValorMcp { Nombre = k, Valor = Cadena(v), Secreto = CatalogoMcp.EsSecreto(k, Cadena(v)) });
                }
                else
                {
                    s.Comando = Cadena(d["command"]);
                    if (s.Comando is null) continue;
                    if (d["args"] is JsonArray a) s.Argumentos = a.Select(Cadena).OfType<string>().ToList();
                    if (d["env"] is JsonObject env)
                        foreach (var (k, v) in env) s.Valores.Add(new ValorMcp { Nombre = k, Valor = Cadena(v), Secreto = CatalogoMcp.EsSecreto(k, Cadena(v)) });
                }
                r.Add(s);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Registro.Aviso($"No se pudo leer {fichero}: {ex.Message}");
        }
        return r;
    }

    /// <summary>
    /// Formato de Codex (subconjunto de TOML): [mcp_servers.nombre] con command, args, url; [mcp_servers.nombre.env] y
    /// [mcp_servers.nombre.http_headers] con pares clave = "valor".
    /// </summary>
    public static List<ServidorMcpDef> DeToml(string fichero, string origen)
    {
        var r = new Dictionary<string, ServidorMcpDef>();
        if (!File.Exists(fichero)) return [];
        try
        {
            ServidorMcpDef? actual = null;
            var seccion = "";
            foreach (var bruta in File.ReadAllLines(fichero))
            {
                var linea = bruta.Trim();
                if (linea.Length == 0 || linea.StartsWith('#')) continue;
                var cab = Regex.Match(linea, @"^\[mcp_servers\.(""[^""]+""|[A-Za-z0-9_\-]+)(?:\.(env|http_headers|headers))?\]$");
                if (cab.Success)
                {
                    var nombre = cab.Groups[1].Value.Trim('"');
                    if (!r.TryGetValue(nombre, out actual)) r[nombre] = actual = new ServidorMcpDef { Nombre = nombre, Origen = origen };
                    seccion = cab.Groups[2].Value;
                    continue;
                }
                if (linea.StartsWith('[')) { actual = null; continue; }
                if (actual is null) continue;
                var kv = Regex.Match(linea, @"^([A-Za-z0-9_\-""]+)\s*=\s*(.+)$");
                if (!kv.Success) continue;
                var clave = kv.Groups[1].Value.Trim('"');
                var valor = kv.Groups[2].Value.Trim();
                if (seccion is "env" or "http_headers" or "headers")
                {
                    var v = Texto(valor);
                    actual.Valores.Add(new ValorMcp { Nombre = clave, Valor = v, Secreto = CatalogoMcp.EsSecreto(clave, v) });
                    continue;
                }
                switch (clave)
                {
                    case "command": actual.Comando = Texto(valor); break;
                    case "args": actual.Argumentos = Regex.Matches(valor, @"""((?:[^""\\]|\\.)*)""|'([^']*)'").Select(m => m.Groups[1].Success ? Regex.Unescape(m.Groups[1].Value) : m.Groups[2].Value).ToList(); break;
                    case "url": actual.Url = Texto(valor); actual.Tipo = "http"; break;
                    case "env":
                        foreach (Match m in Regex.Matches(valor, @"""?([A-Za-z0-9_]+)""?\s*=\s*""((?:[^""\\]|\\.)*)"""))
                            actual.Valores.Add(new ValorMcp { Nombre = m.Groups[1].Value, Valor = Regex.Unescape(m.Groups[2].Value), Secreto = CatalogoMcp.EsSecreto(m.Groups[1].Value, m.Groups[2].Value) });
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Registro.Aviso($"No se pudo leer {fichero}: {ex.Message}");
        }
        return r.Values.Where(s => s.Comando is not null || s.Url is not null).ToList();
    }

    private static string? Texto(string v)
    {
        v = v.Trim();
        if (v.StartsWith('"') && v.EndsWith('"') && v.Length >= 2) return Regex.Unescape(v[1..^1]);
        if (v.StartsWith('\'') && v.EndsWith('\'') && v.Length >= 2) return v[1..^1];
        return v;
    }

    private static string? Cadena(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
