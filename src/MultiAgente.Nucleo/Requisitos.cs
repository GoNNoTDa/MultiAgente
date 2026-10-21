using System.Diagnostics;
using System.Text.Json;

namespace MultiAgente.Nucleo;

/// <summary>Un programa que MultiAgente necesita en el PC (Node.js, una CLI o un adaptador ACP).</summary>
/// <param name="Id">Identificador estable (la orden).</param>
/// <param name="Nombre">Nombre para el usuario.</param>
/// <param name="Proveedor">Claude, Codex o Gemini; null si es común (Node.js).</param>
/// <param name="Paquete">Paquete npm que lo instala; null si no se instala con npm.</param>
/// <param name="Ruta">Dónde está; null si no se encuentra.</param>
/// <param name="Version">Versión instalada si se puede saber sin ejecutarlo.</param>
public sealed record Requisito(string Id, string Nombre, string? Proveedor, string? Paquete, string? Ruta, string? Version)
{
    public bool Encontrado => Ruta is not null;
}

/// <summary>
/// Comprueba qué programas hay instalados buscándolos en el PATH, sin ejecutarlos (rápido y sin efectos).
/// Lo que falte se instala con <c>tools\Instalar-Requisitos.ps1</c>.
/// </summary>
public static class Requisitos
{
    private static readonly (string Id, string Nombre, string? Proveedor, string? Paquete)[] Lista =
    [
        ("node", "Node.js", null, null),
        ("claude", "Claude Code", "Claude", "@anthropic-ai/claude-code"),
        ("claude-agent-acp", "Adaptador ACP de Claude", "Claude", "@agentclientprotocol/claude-agent-acp"),
        ("codex", "Codex CLI", "Codex", "@openai/codex"),
        ("codex-acp", "Adaptador ACP de Codex", "Codex", "@agentclientprotocol/codex-acp"),
        ("gemini", "Gemini CLI", "Gemini", "@google/gemini-cli"),
    ];

    /// <summary>Comprueba todos los requisitos con el PATH indicado (por defecto, el del proceso).</summary>
    public static IReadOnlyList<Requisito> Comprobar(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        var carpetas = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           .Select(c => c.Trim('"'))
                           .ToArray();
        return Lista.Select(r =>
        {
            var ruta = Buscar(r.Id, carpetas);
            return new Requisito(r.Id, r.Nombre, r.Proveedor, r.Paquete, ruta, ruta is null ? null : Version(ruta, r.Paquete));
        }).ToList();
    }

    /// <summary>Un proveedor se puede usar si están su CLI y su adaptador (Gemini habla ACP sin adaptador) y Node.js.</summary>
    public static bool ProveedorListo(IReadOnlyList<Requisito> requisitos, string proveedor) =>
        requisitos.Where(r => r.Proveedor == proveedor || r.Id == "node").All(r => r.Encontrado);

    /// <summary>
    /// Cómo ejecutar un requisito sin pasar por cmd.exe: un .exe tal cual; un lanzador de npm (.cmd), con node.exe y el
    /// script de su paquete (así los argumentos llegan intactos y se puede cerrar el proceso de verdad).
    /// </summary>
    public static Acp.OrdenAgente? Orden(string id, IReadOnlyList<Requisito>? requisitos = null)
    {
        requisitos ??= Comprobar();
        var r = requisitos.FirstOrDefault(x => x.Id == id);
        if (r?.Ruta is null) return null;
        if (r.Ruta.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return new(r.Ruta, []);
        var node = requisitos.FirstOrDefault(x => x.Id == "node")?.Ruta;
        if (r.Paquete is not null && ScriptDelPaquete(r.Ruta, r.Paquete, id) is { } script)
        {
            // Hay paquetes cuyo «bin» es ya un ejecutable nativo (Claude Code)
            if (script.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return new(script, []);
            if (node is not null) return new(node, [script]);
        }
        return new(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", ["/d", "/c", r.Ruta]);
    }

    private static string? ScriptDelPaquete(string lanzador, string paquete, string orden)
    {
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(lanzador)!, "node_modules", paquete.Replace('/', Path.DirectorySeparatorChar));
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "package.json")));
            if (!doc.RootElement.TryGetProperty("bin", out var bin)) return null;
            var rel = bin.ValueKind == JsonValueKind.String ? bin.GetString()
                    : bin.TryGetProperty(orden, out var b) ? b.GetString() : null;
            if (rel is null) return null;
            var js = Path.GetFullPath(Path.Combine(dir, rel));
            return File.Exists(js) ? js : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? Buscar(string orden, IEnumerable<string> carpetas)
    {
        foreach (var carpeta in carpetas)
        foreach (var ext in new[] { ".exe", ".cmd" })
        {
            string candidato;
            try { candidato = Path.Combine(carpeta, orden + ext); }
            catch (ArgumentException) { continue; }
            if (File.Exists(candidato)) return candidato;
        }
        return null;
    }

    private static string? Version(string ruta, string? paquete)
    {
        try
        {
            if (paquete is not null)
            {
                // Lanzador de npm: junto a él está node_modules\<paquete>\package.json
                var pj = Path.Combine(Path.GetDirectoryName(ruta)!, "node_modules", paquete.Replace('/', Path.DirectorySeparatorChar), "package.json");
                if (File.Exists(pj))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(pj));
                    if (doc.RootElement.TryGetProperty("version", out var v)) return v.GetString();
                }
            }
            if (ruta.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var v = FileVersionInfo.GetVersionInfo(ruta).ProductVersion;
                if (!string.IsNullOrWhiteSpace(v)) return v.Split('+')[0];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Registro.Aviso($"No se pudo leer la versión de {ruta}: {ex.Message}");
        }
        return null;
    }
}
