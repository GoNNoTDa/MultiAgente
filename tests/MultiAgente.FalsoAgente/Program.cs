// Agente ACP falso para las pruebas: habla JSON-RPC por stdin/stdout como claude-agent-acp, codex-acp o gemini --acp,
// sin gastar nada. Según el texto del mensaje:
//   «permiso» → pide permiso para escribir un fichero; «lento» → escribe durante 10 s (para interrumpir);
//   «limite» → error de límite de uso («limite tarde»: tras 1,5 s); «muere» → el proceso se cierra;
//   «plan» → manda una lista de tareas; otro → «Recibido: …».
// Variables: FALSO_SIN_AUTH=1 → session/new falla por autenticación; FALSO_CLAVE_ESPERADA → el método api-key exige
// que OPENAI_API_KEY valga eso. Escribe en FALSO_REGISTRO (si se da) lo que recibe, para comprobarlo en las pruebas.
// Uso como Claude Code: tokens de cada turno en la respuesta y coste acumulado de la sesión, que al recuperarla
// (session/load) sigue desde lo que llevaba (se guarda en FALSO_REGISTRO.costes, si se da).
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

var salida = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
var entrada = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
var cerrojo = new SemaphoreSlim(1, 1);
var esperando = new ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>>();
var cancelado = new ConcurrentDictionary<string, bool>();
long ultimoId = 1000;
long turnos = 0;
var costeAcumulado = new ConcurrentDictionary<string, double>();
var registro = Environment.GetEnvironmentVariable("FALSO_REGISTRO");

async Task Escribir(JsonObject m)
{
    await cerrojo.WaitAsync();
    try { await salida.WriteLineAsync(m.ToJsonString()); }
    finally { cerrojo.Release(); }
}

string? FicheroCostes() => registro is null ? null : registro + ".costes";

double CosteGuardado(string sesion)
{
    var f = FicheroCostes();
    if (f is null || !File.Exists(f)) return 0;
    var linea = File.ReadAllLines(f).LastOrDefault(l => l.StartsWith(sesion + "="));
    return linea is null ? 0 : double.Parse(linea[(sesion.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture);
}

void GuardarCoste(string sesion, double coste)
{
    if (FicheroCostes() is { } f)
        lock (esperando) File.AppendAllText(f, $"{sesion}={coste.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
}

void Anotar(string linea)
{
    if (registro is null) return;
    // La prueba puede estar leyendo el fichero a la vez: se reintenta
    for (var i = 0; i < 20; i++)
    {
        try
        {
            lock (esperando)
            {
                using var f = new FileStream(registro, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var b = Encoding.UTF8.GetBytes(linea + "\n");
                f.Write(b, 0, b.Length);
            }
            return;
        }
        catch (IOException) { Thread.Sleep(25); }
    }
}

Task Actualizar(string sesion, JsonObject update) => Escribir(new JsonObject
{
    ["jsonrpc"] = "2.0", ["method"] = "session/update",
    ["params"] = new JsonObject { ["sessionId"] = sesion, ["update"] = update },
});

Task Texto(string sesion, string tipo, string texto) => Actualizar(sesion, new JsonObject
{
    ["sessionUpdate"] = tipo, ["content"] = new JsonObject { ["type"] = "text", ["text"] = texto },
});

async Task<JsonNode?> PedirAlCliente(string metodo, JsonObject p)
{
    var id = Interlocked.Increment(ref ultimoId);
    var tcs = new TaskCompletionSource<JsonNode?>();
    esperando[id] = tcs;
    await Escribir(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = metodo, ["params"] = p });
    return await tcs.Task;
}

JsonObject Modos() => new()
{
    ["currentModeId"] = "default",
    ["availableModes"] = new JsonArray(
        new JsonObject { ["id"] = "default", ["name"] = "Preguntar" },
        new JsonObject { ["id"] = "acceptEdits", ["name"] = "Aceptar ediciones" }),
};

JsonObject Modelos() => new()
{
    ["currentModelId"] = "rapido",
    ["availableModels"] = new JsonArray(
        new JsonObject { ["modelId"] = "rapido", ["name"] = "Rápido" },
        new JsonObject { ["modelId"] = "listo", ["name"] = "Listo" }),
};

async Task<JsonNode?> Atender(string metodo, JsonNode? p)
{
    switch (metodo)
    {
        case "initialize":
            return new JsonObject
            {
                ["protocolVersion"] = 1,
                ["agentCapabilities"] = new JsonObject
                {
                    ["loadSession"] = true,
                    ["promptCapabilities"] = new JsonObject { ["image"] = true, ["embeddedContext"] = true },
                },
                ["authMethods"] = new JsonArray(new JsonObject { ["id"] = "api-key", ["name"] = "API Key" }),
            };
        case "authenticate":
            var esperada = Environment.GetEnvironmentVariable("FALSO_CLAVE_ESPERADA");
            if (esperada is not null && Environment.GetEnvironmentVariable("OPENAI_API_KEY") != esperada)
                throw new FalloAcp(-32000, "Invalid API key");
            return new JsonObject();
        case "session/new":
        {
            if (Environment.GetEnvironmentVariable("FALSO_SIN_AUTH") == "1") throw new FalloAcp(-32000, "Authentication required");
            var id = "falso-" + Guid.NewGuid().ToString("N")[..8];
            Anotar($"nueva {id} cwd={p?["cwd"]} mcp={(p?["mcpServers"] as JsonArray)?.Count ?? 0}");
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                await Actualizar(id, new JsonObject
                {
                    ["sessionUpdate"] = "available_commands_update",
                    ["availableCommands"] = new JsonArray(new JsonObject { ["name"] = "revisar", ["description"] = "Revisa el código" }),
                });
            });
            return new JsonObject { ["sessionId"] = id, ["modes"] = Modos(), ["models"] = Modelos() };
        }
        case "session/load":
        {
            var id = p?["sessionId"]?.GetValue<string>() ?? "";
            if (!id.StartsWith("falso-")) throw new FalloAcp(-32002, "Session not found");
            Anotar($"cargada {id}");
            costeAcumulado[id] = CosteGuardado(id);
            await Texto(id, "user_message_chunk", "mensaje antiguo");
            await Texto(id, "agent_message_chunk", "respuesta antigua");
            return new JsonObject { ["modes"] = Modos(), ["models"] = Modelos() };
        }
        case "session/set_mode":
            await Actualizar(p!["sessionId"]!.GetValue<string>(), new JsonObject
            {
                ["sessionUpdate"] = "current_mode_update", ["currentModeId"] = p["modeId"]?.DeepClone(),
            });
            return new JsonObject();
        case "session/set_model":
            Anotar($"modelo {p?["modelId"]}");
            return new JsonObject();
        case "session/prompt":
            return await Prompt(p!);
        default:
            throw new FalloAcp(-32601, "Method not found: " + metodo);
    }
}

async Task<JsonNode?> Prompt(JsonNode p)
{
    var sesion = p["sessionId"]!.GetValue<string>();
    cancelado[sesion] = false;
    var bloques = p["prompt"] as JsonArray ?? [];
    var textos = bloques.Where(b => b?["type"]?.GetValue<string>() == "text").Select(b => b!["text"]!.GetValue<string>()).ToList();
    var imagenes = bloques.Count(b => b?["type"]?.GetValue<string>() == "image");
    var enlaces = bloques.Count(b => b?["type"]?.GetValue<string>() == "resource_link");
    var texto = textos.LastOrDefault() ?? "";
    var traspaso = textos.Any(t => t.Contains("ya estaba en marcha"));
    Anotar($"prompt {sesion} textos={textos.Count} imagenes={imagenes} enlaces={enlaces} traspaso={traspaso} texto={texto}");

    await Texto(sesion, "agent_thought_chunk", "Pensando…");

    if (texto.Contains("limite"))
    {
        if (texto.Contains("tarde")) await Task.Delay(1500); // da tiempo a dejar mensajes en cola
        throw new FalloAcp(-32000, "Usage limit reached for this account");
    }
    if (texto.Contains("muere")) Environment.Exit(3);
    if (texto.Contains("subagente"))
    {
        // Como Claude Code: herramienta «Agent»; lo que hace y dice el subagente lleva su id en parentToolUseId
        JsonObject Meta(string? padre = null, string? nombre = null)
        {
            var cc = new JsonObject();
            if (nombre is not null) cc["toolName"] = nombre;
            if (padre is not null) cc["parentToolUseId"] = padre;
            return new JsonObject { ["claudeCode"] = cc };
        }
        await Actualizar(sesion, new JsonObject
        {
            ["sessionUpdate"] = "tool_call", ["toolCallId"] = "sub1", ["title"] = "Revisar las pruebas", ["kind"] = "think",
            ["status"] = "in_progress", ["_meta"] = Meta(nombre: "Agent"),
        });
        await Actualizar(sesion, new JsonObject
        {
            ["sessionUpdate"] = "tool_call", ["toolCallId"] = "sub1-leer", ["title"] = "Leer Pruebas.cs", ["kind"] = "read",
            ["status"] = "completed", ["_meta"] = Meta("sub1", "Read"),
        });
        await Actualizar(sesion, new JsonObject
        {
            ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Miro las pruebas." },
            ["_meta"] = Meta("sub1"),
        });
        await Actualizar(sesion, new JsonObject
        {
            ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "sub1", ["status"] = "completed", ["_meta"] = Meta(nombre: "Agent"),
        });
    }
    if (texto.Contains("compacta"))
    {
        // Como Claude Code con un cliente sin avisos de compactación: herramienta «compact» con los tokens en rawOutput
        var meta = new JsonObject { ["claudeCode"] = new JsonObject { ["toolName"] = "compact" } };
        await Actualizar(sesion, new JsonObject
        {
            ["sessionUpdate"] = "tool_call", ["toolCallId"] = "compacta1", ["title"] = "Compact conversation", ["kind"] = "think",
            ["status"] = "in_progress", ["_meta"] = meta.DeepClone(),
        });
        await Actualizar(sesion, new JsonObject
        {
            ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "compacta1", ["status"] = "completed", ["_meta"] = meta.DeepClone(),
            ["rawOutput"] = new JsonObject { ["trigger"] = "automatic", ["preTokens"] = 182000, ["postTokens"] = 31000, ["durationMs"] = 42000 },
        });
        await Actualizar(sesion, new JsonObject { ["sessionUpdate"] = "usage_update", ["used"] = 31000, ["size"] = 200000 });
    }

    // Lo que hace Codex con un proveedor que no es OpenAI: aviso de metadatos, arranque de MCP fallido, error HTTP como texto
    if (texto.Contains("ruido"))
    {
        await Texto(sesion, "agent_message_chunk", "Warning: Model metadata for `nvidia/x` not found. Defaulting to fallback metadata; this can degrade performance and cause issues.\n");
        for (var i = 0; i < 2; i++)
            await Actualizar(sesion, new JsonObject
            {
                ["sessionUpdate"] = "tool_call", ["toolCallId"] = "mcp" + i, ["title"] = "mcp__meta__startup", ["kind"] = "other", ["status"] = "failed",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "content", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "MCP server `meta` failed to start: The meta MCP server is not logged in." } }),
            });
        if (texto.Contains("404"))
        {
            await Texto(sesion, "agent_message_chunk", "unexpected status 404 Not Found: 404 page not found, url: https://integrate.api.nvidia.com/v1/responses\n\n");
            return new JsonObject { ["stopReason"] = "end_turn" };
        }
    }

    if (texto.Contains("plan"))
        await Actualizar(sesion, new JsonObject
        {
            ["sessionUpdate"] = "plan",
            ["entries"] = new JsonArray(
                new JsonObject { ["content"] = "Leer el código", ["priority"] = "high", ["status"] = "completed" },
                new JsonObject { ["content"] = "Cambiarlo", ["priority"] = "high", ["status"] = "in_progress" },
                new JsonObject { ["content"] = "Probarlo", ["priority"] = "medium", ["status"] = "pending" }),
        });

    if (texto.Contains("permiso"))
    {
        var ruta = Path.Combine(Environment.CurrentDirectory, "prueba.txt");
        await Actualizar(sesion, new JsonObject
        {
            ["sessionUpdate"] = "tool_call", ["toolCallId"] = "t1", ["title"] = "Escribir prueba.txt", ["kind"] = "edit",
            ["status"] = "pending", ["locations"] = new JsonArray(new JsonObject { ["path"] = ruta }),
        });
        var r = await PedirAlCliente("session/request_permission", new JsonObject
        {
            ["sessionId"] = sesion,
            ["toolCall"] = new JsonObject { ["toolCallId"] = "t1", ["title"] = "Escribir prueba.txt", ["kind"] = "edit" },
            ["options"] = new JsonArray(
                new JsonObject { ["optionId"] = "si", ["name"] = "Permitir", ["kind"] = "allow_once" },
                new JsonObject { ["optionId"] = "no", ["name"] = "Rechazar", ["kind"] = "reject_once" }),
        });
        var elegido = r?["outcome"]?["outcome"]?.GetValue<string>() == "selected" ? r["outcome"]!["optionId"]?.GetValue<string>() : null;
        if (elegido == "si")
        {
            await Actualizar(sesion, new JsonObject
            {
                ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "t1", ["status"] = "completed",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "diff", ["path"] = ruta, ["oldText"] = null, ["newText"] = "hola\n" }),
            });
            await Texto(sesion, "agent_message_chunk", "Permitido: fichero escrito.");
        }
        else
        {
            await Actualizar(sesion, new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "t1", ["status"] = "failed" });
            await Texto(sesion, "agent_message_chunk", elegido is null ? "Cancelado." : "Rechazado.");
        }
        return new JsonObject { ["stopReason"] = cancelado.GetValueOrDefault(sesion) ? "cancelled" : "end_turn" };
    }

    if (texto.Contains("lento"))
    {
        for (var i = 0; i < 50; i++)
        {
            if (cancelado.GetValueOrDefault(sesion)) return new JsonObject { ["stopReason"] = "cancelled" };
            await Texto(sesion, "agent_message_chunk", ".");
            await Task.Delay(200);
        }
        return new JsonObject { ["stopReason"] = "end_turn" };
    }

    // Uso como lo informan los agentes: contexto y coste acumulado (usage_update) y tokens acumulados en la respuesta
    var turno = Interlocked.Increment(ref turnos);
    var coste = texto.Contains("caro") ? 5.0 : 0.25;
    costeAcumulado[sesion] = costeAcumulado.GetValueOrDefault(sesion) + coste;
    GuardarCoste(sesion, costeAcumulado[sesion]);
    await Actualizar(sesion, new JsonObject
    {
        ["sessionUpdate"] = "usage_update", ["used"] = 1000 * turno, ["size"] = 200000,
        ["cost"] = new JsonObject { ["amount"] = costeAcumulado[sesion], ["currency"] = "USD" },
    });

    var respuesta = $"Recibido: {texto.Split('\n')[0]}";
    if (imagenes > 0) respuesta += $" · imágenes: {imagenes}";
    if (enlaces > 0) respuesta += $" · ficheros: {enlaces}";
    if (traspaso) respuesta += " · con traspaso";
    foreach (var trozo in respuesta.Chunk(8)) await Texto(sesion, "agent_message_chunk", new string(trozo));
    return new JsonObject
    {
        ["stopReason"] = "end_turn",
        // Los de este turno (crecen con cada turno del proceso, como el contexto)
        ["usage"] = new JsonObject { ["inputTokens"] = 100 * turno, ["outputTokens"] = 10 * turno, ["totalTokens"] = 110 * turno },
    };
}

while (await entrada.ReadLineAsync() is { } linea)
{
    if (string.IsNullOrWhiteSpace(linea)) continue;
    var m = JsonNode.Parse(linea)!.AsObject();
    var metodo = m["method"]?.GetValue<string>();
    var id = m["id"];
    if (metodo is null && id is not null)
    {
        if (esperando.TryRemove(id.GetValue<long>(), out var tcs)) tcs.TrySetResult(m["result"]?.DeepClone());
        continue;
    }
    if (metodo == "session/cancel")
    {
        cancelado[m["params"]?["sessionId"]?.GetValue<string>() ?? ""] = true;
        Anotar("cancelar");
        continue;
    }
    if (id is null) continue;
    var idCopia = id.DeepClone();
    var parametros = m["params"]?.DeepClone();
    _ = Task.Run(async () =>
    {
        JsonObject respuesta;
        try
        {
            // Primero el resultado: si falla, idCopia no debe haber quedado enganchado a otro objeto
            var resultado = await Atender(metodo!, parametros) ?? new JsonObject();
            respuesta = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idCopia, ["result"] = resultado };
        }
        catch (Exception ex)
        {
            respuesta = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = idCopia,
                ["error"] = new JsonObject { ["code"] = ex is FalloAcp f ? f.Codigo : -32603, ["message"] = ex.Message },
            };
        }
        await Escribir(respuesta);
    });
}

internal sealed class FalloAcp(int codigo, string mensaje) : Exception(mensaje)
{
    public int Codigo { get; } = codigo;
}
