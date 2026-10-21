using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MultiAgente.Nucleo;

/// <summary>Un elemento compartido (skill, orden, agente…) para enseñarlo en la interfaz.</summary>
/// <param name="Nombre">Nombre (el de la carpeta o fichero, o el de su cabecera).</param>
/// <param name="Descripcion">Descripción de su cabecera, si la tiene.</param>
/// <param name="Ruta">Carpeta o fichero.</param>
public sealed record ElementoCompartido(string Nombre, string? Descripcion, string Ruta);

/// <summary>Un plugin de Claude Code instalado en la carpeta compartida.</summary>
public sealed record PluginClaude(string Id, string Nombre, string? Tienda, string? Version, bool Activo);

/// <summary>
/// Lo que comparten las cuentas además de las conversaciones: skills (una sola carpeta para Claude, Codex y Gemini,
/// con el formato abierto SKILL.md), órdenes, agentes, plugins y estilos de cada proveedor, sus instrucciones globales
/// (CLAUDE.md, AGENTS.md, GEMINI.md) y unas instrucciones comunes a todos.
/// </summary>
public static class Extensiones
{
    public const string InicioBloque = "<!-- MultiAgente: instrucciones comunes (se editan en MultiAgente › Extensiones) -->";
    public const string FinBloque = "<!-- /MultiAgente -->";

    /// <summary>Carpeta común de las skills (la misma para todos los proveedores).</summary>
    public static string Skills(Rutas r) => Path.Combine(r.Comun, "skills");

    /// <summary>Instrucciones comunes a todos los agentes.</summary>
    public static string Instrucciones(Rutas r) => Path.Combine(r.Comun, "INSTRUCCIONES.md");

    /// <summary>Fichero de instrucciones globales de cada proveedor (relativo a la carpeta de la cuenta).</summary>
    public static string? FicheroInstrucciones(string proveedor) => proveedor switch
    {
        "Claude" => "CLAUDE.md",
        "Codex" => "AGENTS.md",
        "Gemini" => Path.Combine(".gemini", "GEMINI.md"),
        _ => null,
    };

    /// <summary>Ficheros que se mantienen iguales en todas las cuentas de un proveedor (copia: gana el más reciente).</summary>
    public static IReadOnlyList<string> FicherosSincronizados(string proveedor) => proveedor switch
    {
        // settings.json lleva los plugins activos, permisos y hooks del usuario; no el inicio de sesión
        "Claude" => ["CLAUDE.md", "settings.json"],
        "Codex" => ["AGENTS.md"],
        // El settings.json de Gemini guarda el tipo de autenticación de la cuenta: no se comparte
        "Gemini" => [Path.Combine(".gemini", "GEMINI.md")],
        _ => [],
    };

    // ---------------------------------------------------------------- sincronizar ficheros

    /// <summary>
    /// Deja los ficheros compartidos de la cuenta iguales a los comunes: si la cuenta tiene uno más reciente (lo cambió
    /// el agente, p. ej. al recordar algo), pasa a ser el común. Las instrucciones globales llevan además el bloque de
    /// instrucciones comunes.
    /// </summary>
    public static void Sincronizar(Rutas rutas, string proveedor, string carpetaCuenta)
    {
        var comunes = File.Exists(Instrucciones(rutas)) ? File.ReadAllText(Instrucciones(rutas)) : "";
        foreach (var rel in FicherosSincronizados(proveedor))
        {
            try
            {
                var comun = Path.Combine(rutas.Comun, proveedor, rel);
                var propio = Path.Combine(carpetaCuenta, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(comun)!);
                Directory.CreateDirectory(Path.GetDirectoryName(propio)!);
                if (File.Exists(propio) && (!File.Exists(comun) || File.GetLastWriteTimeUtc(propio) > File.GetLastWriteTimeUtc(comun).AddSeconds(1)))
                    File.Copy(propio, comun, overwrite: true);
                var texto = File.Exists(comun) ? File.ReadAllText(comun) : null;
                if (rel == FicheroInstrucciones(proveedor)) texto = AplicarBloque(texto ?? "", comunes);
                if (texto is null) continue;
                EscribirSiCambia(comun, texto);
                EscribirSiCambia(propio, texto);
                File.SetLastWriteTimeUtc(propio, File.GetLastWriteTimeUtc(comun));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Registro.Aviso($"No se pudo sincronizar {rel} de {carpetaCuenta}: {ex.Message}");
            }
        }
    }

    private static void EscribirSiCambia(string f, string texto)
    {
        if (File.Exists(f) && File.ReadAllText(f) == texto) return;
        File.WriteAllText(f + ".tmp", texto, new UTF8Encoding(false));
        File.Move(f + ".tmp", f, overwrite: true);
    }

    /// <summary>Pone (o quita, si están vacías) las instrucciones comunes en su bloque marcado, sin tocar el resto.</summary>
    public static string AplicarBloque(string documento, string comunes)
    {
        // El bloque y la línea en blanco que lo separa del resto
        var patron = new Regex(Regex.Escape(InicioBloque) + ".*?" + Regex.Escape(FinBloque) + @"\r?\n?(\r?\n)?", RegexOptions.Singleline);
        var sinBloque = patron.Replace(documento, "");
        if (string.IsNullOrWhiteSpace(comunes)) return sinBloque;
        var bloque = $"{InicioBloque}\n{comunes.Trim()}\n{FinBloque}\n";
        return sinBloque.Length == 0 ? bloque : bloque + "\n" + sinBloque.TrimStart('\r', '\n');
    }

    public static void GuardarInstrucciones(Rutas rutas, string texto)
    {
        Directory.CreateDirectory(rutas.Comun);
        File.WriteAllText(Instrucciones(rutas), texto, new UTF8Encoding(false));
    }

    // ---------------------------------------------------------------- listar

    /// <summary>Skills de la carpeta común: cada carpeta con un SKILL.md (nombre y descripción de su cabecera).</summary>
    public static List<ElementoCompartido> ListarSkills(string carpeta)
    {
        if (!Directory.Exists(carpeta)) return [];
        return Directory.GetDirectories(carpeta)
            .Select(d => (d, f: Path.Combine(d, "SKILL.md")))
            .Where(x => File.Exists(x.f))
            .Select(x =>
            {
                var cab = Cabecera(File.ReadAllText(x.f));
                return new ElementoCompartido(cab.GetValueOrDefault("name") ?? Path.GetFileName(x.d), cab.GetValueOrDefault("description"), x.d);
            })
            .OrderBy(e => e.Nombre, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Ficheros de una carpeta de órdenes o agentes (.md o .toml), con la descripción de su cabecera si la tiene.</summary>
    public static List<ElementoCompartido> ListarFicheros(string carpeta)
    {
        if (!Directory.Exists(carpeta)) return [];
        return Directory.EnumerateFiles(carpeta, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                var texto = File.ReadAllText(f);
                var desc = f.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)
                    ? Regex.Match(texto, @"^description\s*=\s*""([^""]*)""", RegexOptions.Multiline).Groups[1].Value
                    : Cabecera(texto).GetValueOrDefault("description");
                var rel = Path.GetRelativePath(carpeta, f);
                return new ElementoCompartido(Path.ChangeExtension(rel, null).Replace(Path.DirectorySeparatorChar, ':'),
                                              string.IsNullOrEmpty(desc) ? null : desc, f);
            })
            .OrderBy(e => e.Nombre, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Cabecera YAML sencilla (--- clave: valor ---) de un Markdown.</summary>
    public static Dictionary<string, string> Cabecera(string texto)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var m = Regex.Match(texto, @"\A﻿?---\s*\r?\n(.*?)\r?\n---", RegexOptions.Singleline);
        if (!m.Success) return d;
        foreach (var l in m.Groups[1].Value.Split('\n'))
        {
            var kv = Regex.Match(l.TrimEnd('\r'), @"^([A-Za-z0-9_\-]+)\s*:\s*(.*)$");
            if (kv.Success) d[kv.Groups[1].Value] = kv.Groups[2].Value.Trim().Trim('"', '\'');
        }
        return d;
    }

    /// <summary>Plugins de Claude instalados (carpeta compartida) y si están activos (settings.json común).</summary>
    public static List<PluginClaude> PluginsClaude(Rutas rutas)
    {
        var r = new List<PluginClaude>();
        var instalados = Path.Combine(rutas.Comun, "Claude", "plugins", "installed_plugins.json");
        var activos = LeerActivos(rutas);
        if (!File.Exists(instalados)) return r;
        try
        {
            var raiz = JsonNode.Parse(File.ReadAllText(instalados));
            var plugins = raiz?["plugins"] as JsonObject ?? raiz as JsonObject;
            if (plugins is null) return r;
            foreach (var (id, info) in plugins)
            {
                if (id is "version" or "plugins") continue;
                var datos = info is JsonArray a ? a.FirstOrDefault() : info;
                var partes = id.Split('@', 2);
                r.Add(new PluginClaude(id, partes[0], partes.Length > 1 ? partes[1] : null, datos?["version"]?.ToString(),
                                       activos.TryGetValue(id, out var act) ? act : true));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Registro.Aviso($"No se pudo leer {instalados}: {ex.Message}");
        }
        return r.OrderBy(p => p.Nombre, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, bool> LeerActivos(Rutas rutas)
    {
        var f = Path.Combine(rutas.Comun, "Claude", "settings.json");
        var d = new Dictionary<string, bool>();
        try
        {
            if (File.Exists(f) && JsonNode.Parse(File.ReadAllText(f))?["enabledPlugins"] is JsonObject e)
                foreach (var (k, v) in e) d[k] = v is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return d;
    }

    /// <summary>Activa o desactiva un plugin en el settings.json común (llega a cada cuenta al abrir su próxima sesión).</summary>
    public static void ActivarPlugin(Rutas rutas, string id, bool activo)
    {
        var f = Path.Combine(rutas.Comun, "Claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        var raiz = (File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) as JsonObject : null) ?? new JsonObject();
        if (raiz["enabledPlugins"] is not JsonObject e) raiz["enabledPlugins"] = e = new JsonObject();
        e[id] = activo;
        File.WriteAllText(f + ".tmp", raiz.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(f + ".tmp", f, overwrite: true);
    }

    // ---------------------------------------------------------------- importar y quitar

    /// <summary>Copia una carpeta (skill) o fichero (orden, agente) a la carpeta común, sin pisar lo que ya haya.</summary>
    public static string Copiar(string origen, string carpetaDestino)
    {
        Directory.CreateDirectory(carpetaDestino);
        var nombre = Path.GetFileName(Path.TrimEndingDirectorySeparator(origen));
        var destino = Path.Combine(carpetaDestino, nombre);
        if (Directory.Exists(origen))
        {
            if (Directory.Exists(destino)) throw new ExcepcionUsuario($"Ya hay una que se llama «{nombre}».");
            CopiarCarpeta(origen, destino);
        }
        else if (File.Exists(origen))
        {
            if (File.Exists(destino)) throw new ExcepcionUsuario($"Ya hay uno que se llama «{nombre}».");
            File.Copy(origen, destino);
        }
        else throw new ExcepcionUsuario($"No existe {origen}.");
        return destino;
    }

    private static void CopiarCarpeta(string origen, string destino)
    {
        Directory.CreateDirectory(destino);
        foreach (var f in Directory.GetFiles(origen)) File.Copy(f, Path.Combine(destino, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(origen))
        {
            var nombre = Path.GetFileName(d);
            if (nombre is ".git" or "node_modules") continue;
            CopiarCarpeta(d, Path.Combine(destino, nombre));
        }
    }

    /// <summary>Quita una skill, orden o agente: se mueve a <c>datos\papelera</c> (se puede recuperar a mano).</summary>
    public static void Quitar(Rutas rutas, string ruta)
    {
        var comun = Path.GetFullPath(rutas.Comun) + Path.DirectorySeparatorChar;
        var completa = Path.GetFullPath(ruta);
        if (!completa.StartsWith(comun, StringComparison.OrdinalIgnoreCase))
            throw new ExcepcionUsuario("Solo se pueden quitar elementos de las carpetas compartidas de MultiAgente.");
        var papelera = Path.Combine(rutas.Datos, "papelera", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(papelera);
        var destino = Path.Combine(papelera, Path.GetFileName(Path.TrimEndingDirectorySeparator(completa)));
        if (Directory.Exists(completa)) Directory.Move(completa, destino);
        else if (File.Exists(completa)) File.Move(completa, destino);
    }

    /// <summary>Skill nueva con una plantilla de SKILL.md.</summary>
    public static string NuevaSkill(Rutas rutas, string nombre, string descripcion)
    {
        nombre = Regex.Replace(nombre.Trim().Normalize(NormalizationForm.FormD), @"\p{Mn}", "").ToLowerInvariant();
        nombre = Regex.Replace(nombre, "[^a-z0-9]+", "-").Trim('-');
        if (nombre.Length < 2) throw new ExcepcionUsuario("Escribe un nombre para la skill (letras y números).");
        var carpeta = Path.Combine(Skills(rutas), nombre);
        if (Directory.Exists(carpeta)) throw new ExcepcionUsuario($"Ya hay una skill que se llama «{nombre}».");
        Directory.CreateDirectory(carpeta);
        File.WriteAllText(Path.Combine(carpeta, "SKILL.md"),
            $"---\nname: {nombre}\ndescription: {descripcion.Trim().Replace('\n', ' ')}\n---\n\n# {nombre}\n\nInstrucciones para el agente.\n",
            new UTF8Encoding(false));
        return carpeta;
    }
}
