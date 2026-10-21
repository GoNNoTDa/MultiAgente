using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MultiAgente.Nucleo;

/// <summary>Una conversación que ya estaba en el equipo (de Claude Code, Codex o Gemini CLI fuera de MultiAgente).</summary>
/// <param name="Origen">Proveedor que la tuvo: Claude, Codex o Gemini.</param>
/// <param name="Id">Su id de sesión en ese agente.</param>
/// <param name="Fichero">Dónde está (no se toca: solo se lee y, para retomarla, se copia).</param>
/// <param name="Mensajes">Mensajes del usuario.</param>
public sealed record HistorialPrevio(string Origen, string Id, string Fichero, string? Titulo, string? Carpeta, DateTime Fecha, int Mensajes);

/// <summary>
/// Historiales de conversaciones previas del equipo: se buscan en la configuración de siempre del usuario
/// (<c>~/.claude/projects</c>, <c>~/.codex/sessions</c>, <c>~/.gemini/tmp/*/chats</c>), solo leyendo, y se pasan al formato
/// de MultiAgente. Para que el mismo agente pueda retomarla de verdad, el fichero original se copia a la carpeta común de
/// su proveedor.
/// </summary>
public static partial class Historiales
{
    // ================================================================= buscar

    public static List<HistorialPrevio> Buscar(string perfil)
    {
        var lista = new List<HistorialPrevio>();
        lista.AddRange(Ficheros(Path.Combine(perfil, ".claude", "projects"), "*.jsonl", recursivo: false, porCarpeta: true).Select(MiraClaude).OfType<HistorialPrevio>());
        lista.AddRange(Ficheros(Path.Combine(perfil, ".codex", "sessions"), "rollout-*.jsonl", recursivo: true).Select(MiraCodex).OfType<HistorialPrevio>());
        var tmp = Path.Combine(perfil, ".gemini", "tmp");
        if (Directory.Exists(tmp))
            foreach (var d in Directory.EnumerateDirectories(tmp))
                lista.AddRange(Ficheros(Path.Combine(d, "chats"), "*.json", recursivo: false).Select(f => MiraGemini(f, d)).OfType<HistorialPrevio>());
        return [.. lista.OrderByDescending(h => h.Fecha)];
    }

    private static IEnumerable<string> Ficheros(string carpeta, string patron, bool recursivo, bool porCarpeta = false)
    {
        if (!Directory.Exists(carpeta)) return [];
        try
        {
            // Claude Code: un fichero por sesión en la carpeta de cada proyecto (las subcarpetas son de subagentes)
            if (porCarpeta) return Directory.EnumerateDirectories(carpeta).SelectMany(d => Directory.EnumerateFiles(d, patron)).ToList();
            return Directory.EnumerateFiles(carpeta, patron, recursivo ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<JsonObject> Lineas(string fichero)
    {
        using var lector = new StreamReader(new FileStream(fichero, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        string? linea;
        while ((linea = lector.ReadLine()) is not null)
        {
            if (linea.Length == 0 || linea[0] != '{') continue;
            JsonObject? o;
            try { o = JsonNode.Parse(linea) as JsonObject; }
            catch (JsonException) { continue; } // línea a medias (el agente la estaba escribiendo)
            if (o is not null) yield return o;
        }
    }

    private static HistorialPrevio? Seguro(Func<HistorialPrevio?> f, string fichero)
    {
        try { return f(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Registro.Aviso($"Historial que no se puede leer: {fichero} ({ex.Message})");
            return null;
        }
    }

    private static HistorialPrevio? MiraClaude(string f) => Seguro(() =>
    {
        string? titulo = null, primero = null, carpeta = null;
        var mensajes = 0;
        foreach (var o in Lineas(f))
        {
            var tipo = Txt(o["type"]);
            if (tipo == "custom-title") titulo = Txt(o["customTitle"]) ?? titulo;
            else if (tipo == "summary") titulo ??= Txt(o["summary"]);
            else if (tipo == "user" && o["isSidechain"]?.GetValue<bool>() != true && o["isMeta"]?.GetValue<bool>() != true)
            {
                carpeta ??= Txt(o["cwd"]);
                var texto = TextoUsuario(o["message"]?["content"]);
                if (texto is null) continue;
                mensajes++;
                primero ??= texto;
            }
        }
        return mensajes == 0 ? null : new HistorialPrevio("Claude", Path.GetFileNameWithoutExtension(f), f, titulo ?? Titulo(primero), carpeta, File.GetLastWriteTime(f), mensajes);
    }, f);

    private static HistorialPrevio? MiraCodex(string f) => Seguro(() =>
    {
        string? id = null, carpeta = null, primero = null;
        var mensajes = 0;
        foreach (var o in Lineas(f))
        {
            var p = o["payload"] as JsonObject;
            if (Txt(o["type"]) == "session_meta") { id ??= Txt(p?["id"]); carpeta ??= Txt(p?["cwd"]); continue; }
            if (Txt(o["type"]) != "response_item" || Txt(p?["type"]) != "message" || Txt(p?["role"]) != "user") continue;
            var texto = Limpiar(TextoCodex(p?["content"]));
            if (texto is null) continue;
            mensajes++;
            primero ??= texto;
        }
        return mensajes == 0 || id is null ? null : new HistorialPrevio("Codex", id, f, Titulo(primero), carpeta, File.GetLastWriteTime(f), mensajes);
    }, f);

    private static HistorialPrevio? MiraGemini(string f, string carpetaProyecto) => Seguro(() =>
    {
        var o = JsonNode.Parse(File.ReadAllText(f)) as JsonObject;
        var usuario = (o?["messages"] as JsonArray ?? []).OfType<JsonObject>().Where(m => Txt(m["type"]) == "user").Select(m => Limpiar(TextoGemini(m["content"]))).OfType<string>().ToList();
        var id = Txt(o?["sessionId"]);
        if (usuario.Count == 0 || id is null) return null;
        var raiz = Path.Combine(carpetaProyecto, ".project_root");
        var carpeta = File.Exists(raiz) ? File.ReadAllText(raiz).Trim() : null;
        var fecha = DateTime.TryParse(Txt(o?["lastUpdated"]), out var d) ? d : File.GetLastWriteTime(f);
        return new HistorialPrevio("Gemini", id, f, Titulo(usuario[0]), carpeta, fecha, usuario.Count);
    }, f);

    // ================================================================= pasar a MultiAgente

    /// <summary>La conversación en entradas de MultiAgente (usuario, agente, razonamiento, herramientas y sus ediciones).</summary>
    public static List<Entrada> Leer(HistorialPrevio h) => h.Origen switch
    {
        "Claude" => LeerClaude(h.Fichero),
        "Codex" => LeerCodex(h.Fichero),
        "Gemini" => LeerGemini(h.Fichero),
        _ => [],
    };

    private static List<Entrada> LeerClaude(string f)
    {
        var entradas = new List<Entrada>();
        var herramientas = new Dictionary<string, Entrada>();
        foreach (var o in Lineas(f))
        {
            if (o["isSidechain"]?.GetValue<bool>() == true || o["isMeta"]?.GetValue<bool>() == true) continue;
            var tipo = Txt(o["type"]);
            var hora = Hora(o["timestamp"]);
            var contenido = o["message"]?["content"];
            if (tipo == "user")
            {
                if (contenido is JsonArray bloques)
                    foreach (var b in bloques.OfType<JsonObject>().Where(b => Txt(b["type"]) == "tool_result"))
                        if (Txt(b["tool_use_id"]) is { } tid && herramientas.TryGetValue(tid, out var he))
                            he.Estado = b["is_error"]?.GetValue<bool>() == true ? "failed" : "completed";
                if (TextoUsuario(contenido) is { } texto) entradas.Add(new Entrada { Tipo = "usuario", Texto = texto, Hora = hora });
            }
            else if (tipo == "assistant" && contenido is JsonArray bloques)
            {
                foreach (var b in bloques.OfType<JsonObject>())
                {
                    switch (Txt(b["type"]))
                    {
                        case "text" when !string.IsNullOrWhiteSpace(Txt(b["text"])):
                            entradas.Add(new Entrada { Tipo = "agente", Texto = Txt(b["text"]), Hora = hora });
                            break;
                        case "thinking" when !string.IsNullOrWhiteSpace(Txt(b["thinking"])):
                            entradas.Add(new Entrada { Tipo = "pensamiento", Texto = Txt(b["thinking"]), Hora = hora });
                            break;
                        case "tool_use":
                            var e = HerramientaClaude(Txt(b["name"]) ?? "Herramienta", b["input"] as JsonObject ?? [], hora);
                            if (Txt(b["id"]) is { } id) herramientas[id] = e;
                            entradas.Add(e);
                            break;
                    }
                }
            }
        }
        return entradas;
    }

    private static Entrada HerramientaClaude(string nombre, JsonObject datos, DateTime hora)
    {
        var ruta = Txt(datos["file_path"]) ?? Txt(datos["notebook_path"]) ?? Txt(datos["path"]);
        var detalle = ruta ?? Txt(datos["command"]) ?? Txt(datos["pattern"]) ?? Txt(datos["query"]) ?? Txt(datos["url"]) ?? Txt(datos["description"]);
        var e = new Entrada
        {
            Tipo = "herramienta", Hora = hora, Estado = "completed",
            Titulo = detalle is null ? nombre : $"{nombre} {Recortar(detalle.Split('\n')[0], 120)}",
            Clase = nombre switch
            {
                "Read" => "read", "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => "edit", "Bash" or "PowerShell" => "execute",
                "Grep" or "Glob" => "search", "WebFetch" or "WebSearch" => "fetch", _ => "other",
            },
        };
        if (ruta is not null) e.Ubicaciones = new JsonArray(new JsonObject { ["path"] = ruta });
        if (Txt(datos["command"]) is { } orden) e.Datos = new JsonObject { ["command"] = Recortar(orden, 4000) };
        // Las ediciones, como diferencias: así salen en el visor de cambios («Esta sesión»)
        if (nombre == "Edit" && ruta is not null)
            e.Contenido = new JsonArray(Dif(ruta, Txt(datos["old_string"]), Txt(datos["new_string"])));
        else if (nombre == "Write" && ruta is not null)
            e.Contenido = new JsonArray(Dif(ruta, null, Txt(datos["content"])));
        else if (nombre == "MultiEdit" && ruta is not null && datos["edits"] is JsonArray cambios)
            e.Contenido = new JsonArray([.. cambios.OfType<JsonObject>().Select(x => (JsonNode)Dif(ruta, Txt(x["old_string"]), Txt(x["new_string"])))]);
        return e;
    }

    private static JsonObject Dif(string ruta, string? viejo, string? nuevo) =>
        new() { ["type"] = "diff", ["path"] = ruta, ["oldText"] = Recortar(viejo, 60_000), ["newText"] = Recortar(nuevo, 60_000) };

    private static List<Entrada> LeerCodex(string f)
    {
        var entradas = new List<Entrada>();
        var herramientas = new Dictionary<string, Entrada>();
        foreach (var o in Lineas(f))
        {
            if (Txt(o["type"]) != "response_item" || o["payload"] is not JsonObject p) continue;
            var hora = Hora(o["timestamp"]);
            switch (Txt(p["type"]))
            {
                case "message" when Txt(p["role"]) == "user":
                    if (Limpiar(TextoCodex(p["content"])) is { } u) entradas.Add(new Entrada { Tipo = "usuario", Texto = u, Hora = hora });
                    break;
                case "message" when Txt(p["role"]) == "assistant":
                {
                    var t = TextoCodex(p["content"]);
                    if (string.IsNullOrWhiteSpace(t) || t.StartsWith("[external_agent_tool_result]")) break;
                    // Conversaciones que Codex trajo de otro agente: las herramientas vienen como texto
                    var m = HerramientaExterna().Match(t);
                    if (m.Success)
                    {
                        var cuerpo = m.Groups[2].Value.Trim();
                        entradas.Add(new Entrada
                        {
                            Tipo = "herramienta", Hora = hora, Estado = "completed", Clase = "other",
                            Titulo = $"{m.Groups[1].Value} {Recortar(Regex.Replace(cuerpo.Split('\n')[0], @"^\w+:\s*", ""), 120)}".Trim(),
                        });
                    }
                    else entradas.Add(new Entrada { Tipo = "agente", Texto = t, Hora = hora });
                    break;
                }
                case "reasoning":
                {
                    var resumen = string.Join("\n\n", (p["summary"] as JsonArray ?? []).OfType<JsonObject>().Select(x => Txt(x["text"])).OfType<string>());
                    if (resumen.Length > 0) entradas.Add(new Entrada { Tipo = "pensamiento", Texto = resumen, Hora = hora });
                    break;
                }
                case "function_call" or "custom_tool_call" or "local_shell_call":
                {
                    var nombre = Txt(p["name"]) ?? "shell";
                    var argumentos = Txt(p["arguments"]) ?? Txt(p["input"]) ?? "";
                    string? orden = null;
                    try
                    {
                        if (argumentos.StartsWith('{') && JsonNode.Parse(argumentos)?["command"] is { } c)
                            orden = c is JsonArray a ? string.Join(' ', a.Select(Txt)) : Txt(c);
                    }
                    catch (JsonException) { }
                    var e = new Entrada
                    {
                        Tipo = "herramienta", Hora = hora, Estado = "completed",
                        Clase = nombre == "apply_patch" ? "edit" : orden is not null ? "execute" : "other",
                        Titulo = orden is null ? nombre : $"{nombre} {Recortar(orden.Split('\n')[0], 120)}",
                    };
                    if (nombre == "apply_patch")
                        e.Contenido = new JsonArray(new JsonObject { ["type"] = "content", ["content"] = new JsonObject { ["type"] = "text", ["text"] = Recortar(argumentos, 20_000) } });
                    if (Txt(p["call_id"]) is { } id) herramientas[id] = e;
                    entradas.Add(e);
                    break;
                }
            }
        }
        return entradas;
    }

    private static List<Entrada> LeerGemini(string f)
    {
        var entradas = new List<Entrada>();
        var o = JsonNode.Parse(File.ReadAllText(f)) as JsonObject;
        foreach (var m in (o?["messages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var hora = Hora(m["timestamp"]);
            var texto = TextoGemini(m["content"]);
            switch (Txt(m["type"]))
            {
                case "user":
                    if (Limpiar(texto) is { } u) entradas.Add(new Entrada { Tipo = "usuario", Texto = u, Hora = hora });
                    break;
                case "gemini":
                    foreach (var p in (m["thoughts"] as JsonArray ?? []).OfType<JsonObject>())
                        if (Txt(p["description"]) is { Length: > 0 } d)
                            entradas.Add(new Entrada { Tipo = "pensamiento", Texto = (Txt(p["subject"]) is { } s ? $"**{s}**\n" : "") + d, Hora = hora });
                    foreach (var t in (m["toolCalls"] as JsonArray ?? []).OfType<JsonObject>())
                        entradas.Add(new Entrada
                        {
                            Tipo = "herramienta", Hora = hora, Clase = "other",
                            Estado = Txt(t["status"]) is "error" or "cancelled" ? "failed" : "completed",
                            Titulo = Txt(t["displayName"]) ?? Txt(t["name"]) ?? "Herramienta",
                        });
                    if (!string.IsNullOrWhiteSpace(texto)) entradas.Add(new Entrada { Tipo = "agente", Texto = texto, Hora = hora });
                    break;
                case "error" or "info" or "warning" when !string.IsNullOrWhiteSpace(texto):
                    entradas.Add(new Entrada { Tipo = "aviso", Nivel = Txt(m["type"]) == "error" ? "error" : "info", Texto = texto, Hora = hora });
                    break;
            }
        }
        return entradas;
    }

    // ================================================================= retomarla con el mismo agente

    /// <summary>
    /// Copia el fichero original a la carpeta común del proveedor (donde lo buscan todas sus cuentas de MultiAgente), para
    /// que el agente la recupere con su historia completa. Devuelve el id de la sesión del agente, o null si ese agente no
    /// se puede retomar así (Gemini: se sigue con un traspaso de la conversación).
    /// </summary>
    public static string? CopiarParaRetomar(HistorialPrevio h, Rutas rutas, string perfil)
    {
        string destino;
        if (h.Origen == "Claude")
            destino = Path.Combine(CarpetasCompartidas.Comun(rutas, "Claude", "projects"), Path.GetFileName(Path.GetDirectoryName(h.Fichero)!), Path.GetFileName(h.Fichero));
        else if (h.Origen == "Codex")
            destino = Path.Combine(CarpetasCompartidas.Comun(rutas, "Codex", "sessions"), Path.GetRelativePath(Path.Combine(perfil, ".codex", "sessions"), h.Fichero));
        else return null;
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        if (!File.Exists(destino)) File.Copy(h.Fichero, destino);
        return h.Id;
    }

    // ================================================================= textos

    private static string? Txt(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static DateTime Hora(JsonNode? n) => DateTime.TryParse(Txt(n), out var d) ? d : DateTime.Now;

    private static string? Recortar(string? s, int max) => s is null || s.Length <= max ? s : s[..max] + "…";

    private static string? Titulo(string? texto)
    {
        var lineas = texto?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        var linea = lineas.FirstOrDefault(l => l != "[imagen]") ?? lineas.FirstOrDefault();
        return linea is null ? null : linea.Length <= 70 ? linea : linea[..67].TrimEnd() + "…";
    }

    private static string? TextoUsuario(JsonNode? contenido)
    {
        if (contenido is JsonValue) return Limpiar(Txt(contenido));
        if (contenido is not JsonArray a) return null;
        var partes = new List<string>();
        foreach (var b in a.OfType<JsonObject>())
        {
            if (Txt(b["type"]) == "text" && Txt(b["text"]) is { } t) partes.Add(t);
            else if (Txt(b["type"]) == "image") partes.Add("[imagen]");
        }
        return partes.Count == 0 ? null : Limpiar(string.Join("\n", partes));
    }

    private static string TextoCodex(JsonNode? contenido) =>
        string.Join("\n", (contenido as JsonArray ?? []).OfType<JsonObject>().Select(x => Txt(x["text"])).OfType<string>());

    private static string? TextoGemini(JsonNode? contenido) => contenido switch
    {
        JsonValue => Txt(contenido),
        JsonArray a => string.Join("\n", a.OfType<JsonObject>().Select(x => Txt(x["text"])).OfType<string>()),
        _ => null,
    };

    private static readonly string[] Ruido =
    [
        "<local-command", "<task-notification>", "<environment_context>", "<user_instructions>", "<permissions instructions>",
        "# AGENTS.md instructions", "This session is being continued from a previous conversation", "Caveat: The messages below",
        "<turn_aborted>", "[Request interrupted",
    ];

    /// <summary>Texto que escribió el usuario, sin lo que añade el agente por su cuenta; null si no queda nada suyo.</summary>
    private static string? Limpiar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        texto = BloqueSistema().Replace(texto, "").Replace("[external unsupported block: image]", "[imagen]").Trim();
        if (texto.Length == 0 || Ruido.Any(r => texto.StartsWith(r, StringComparison.Ordinal))) return null;
        // «/orden argumentos» de Claude Code
        var orden = OrdenClaude().Match(texto);
        if (orden.Success) return ("/" + orden.Groups[1].Value.TrimStart('/') + " " + orden.Groups[2].Value).Trim();
        return texto;
    }

    [GeneratedRegex(@"<system-reminder>.*?</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex BloqueSistema();

    [GeneratedRegex(@"<command-name>(.*?)</command-name>(?:.*?<command-args>(.*?)</command-args>)?", RegexOptions.Singleline)]
    private static partial Regex OrdenClaude();

    [GeneratedRegex(@"^\[external_agent_tool_call:\s*([^\]]+)\]\s*(.*?)(?:\[/external_agent_tool_call\])?\s*$", RegexOptions.Singleline)]
    private static partial Regex HerramientaExterna();
}
