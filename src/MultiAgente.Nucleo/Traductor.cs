using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiAgente.Nucleo;

/// <summary>A dónde manda el traductor lo que le pide una cuenta.</summary>
/// <param name="BaseUrl">Dirección de la API compatible con OpenAI (termina en /v1 o equivalente).</param>
/// <param name="Clave">Clave del proveedor (se queda en MultiAgente: el agente no la ve).</param>
/// <param name="Modelo">Modelo de la cuenta: el que se usa cuando el agente pide uno de Claude.</param>
public sealed record DestinoTraductor(string BaseUrl, string? Clave, string? Modelo);

/// <summary>
/// Traductor local para que Claude Code trabaje con proveedores que solo tienen la API de OpenAI (Chat Completions): NVIDIA,
/// Groq, Cerebras, Mistral, Together, un servidor propio… Claude Code habla la API de Anthropic con el traductor
/// (<c>ANTHROPIC_BASE_URL</c> = <see cref="Url"/>); el traductor la pasa a Chat Completions, con texto, imágenes,
/// herramientas y razonamiento, y devuelve la respuesta en el formato de Anthropic, también a trozos (SSE).
/// Cada cuenta recibe un pase propio (<c>ANTHROPIC_AUTH_TOKEN</c>): sin él no se atiende, así ningún otro programa del
/// equipo puede usar la clave del proveedor, que nunca sale de MultiAgente.
/// </summary>
public sealed class Traductor : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DestinoTraductor> _destinos = new();
    private readonly HttpClient _http;
    private readonly ServidorHttp _servidor;

    public Traductor(HttpMessageHandler? manejador = null)
    {
        _http = manejador is null ? new HttpClient() : new HttpClient(manejador, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(15);
        _servidor = new ServidorHttp(Atender);
    }

    /// <summary>
    /// Lo que se espera a que el proveedor empiece a contestar. Los gratuitos (NVIDIA) ponen las peticiones en cola cuando
    /// un modelo está saturado y pueden no contestar nunca: mejor cortar y decirlo que dejar la sesión «trabajando».
    /// </summary>
    public TimeSpan EsperaPrimeraRespuesta { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Dirección que se pone en ANTHROPIC_BASE_URL.</summary>
    public string Url => _servidor.Base;

    /// <summary>Da de alta (o cambia) el destino de una cuenta y devuelve su pase.</summary>
    public string Registrar(string cuentaId, DestinoTraductor destino)
    {
        foreach (var (pase, d) in _destinos) if (pase.StartsWith(cuentaId + ".", StringComparison.Ordinal)) _destinos.TryRemove(pase, out _);
        var nuevo = cuentaId + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _destinos[nuevo] = destino;
        return nuevo;
    }

    private DestinoTraductor? Destino(PeticionHttp p)
    {
        var pase = p.Cabecera("x-api-key");
        var auth = p.Cabecera("Authorization");
        if (string.IsNullOrEmpty(pase) && auth?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true) pase = auth[7..].Trim();
        return pase is not null && _destinos.TryGetValue(pase, out var d) ? d : null;
    }

    private async Task Atender(PeticionHttp p, RespuestaHttp r, CancellationToken ct)
    {
        var ruta = p.Ruta.Split('?')[0].TrimEnd('/');
        var destino = Destino(p);
        if (destino is null)
        {
            await Error(r, 401, "authentication_error", "Pase no válido para el traductor de MultiAgente.", ct);
            return;
        }
        switch (p.Metodo, ruta)
        {
            case ("POST", "/v1/messages"):
                await Mensajes(p, r, destino, ct);
                break;
            case ("POST", "/v1/messages/count_tokens"):
                // Estimación: unos 4 caracteres por token (el proveedor no da esta cuenta)
                await r.Enviar(200, "application/json", new JsonObject { ["input_tokens"] = Math.Max(1, p.Cuerpo.Length / 4) }.ToJsonString(), ct);
                break;
            case ("GET", "/v1/models"):
                var modelos = new JsonArray();
                if (destino.Modelo is { } m) modelos.Add(new JsonObject { ["type"] = "model", ["id"] = m, ["display_name"] = m });
                await r.Enviar(200, "application/json", new JsonObject { ["data"] = modelos, ["has_more"] = false }.ToJsonString(), ct);
                break;
            default:
                await Error(r, 404, "not_found_error", $"El traductor no atiende {p.Metodo} {ruta}.", ct);
                break;
        }
    }

    private static Task Error(RespuestaHttp r, int estado, string tipo, string mensaje, CancellationToken ct) =>
        r.Enviar(estado, "application/json", new JsonObject
        {
            ["type"] = "error", ["error"] = new JsonObject { ["type"] = tipo, ["message"] = mensaje },
        }.ToJsonString(), ct);

    private static string TipoError(int estado) => estado switch
    {
        400 or 422 => "invalid_request_error",
        401 => "authentication_error",
        403 => "permission_error",
        404 => "not_found_error",
        413 => "request_too_large",
        429 => "rate_limit_error",
        529 or 503 => "overloaded_error",
        _ => "api_error",
    };

    // ================================================================= una petición de mensajes

    private async Task Mensajes(PeticionHttp p, RespuestaHttp r, DestinoTraductor destino, CancellationToken ct)
    {
        JsonObject anthropic;
        try { anthropic = JsonNode.Parse(p.Cuerpo) as JsonObject ?? throw new JsonException("vacía"); }
        catch (JsonException ex)
        {
            await Error(r, 400, "invalid_request_error", "Petición no válida: " + ex.Message, ct);
            return;
        }
        var aTrozos = anthropic["stream"]?.GetValue<bool>() == true;
        var openai = AOpenAI(anthropic, destino);
        var modelo = openai["model"]!.GetValue<string>();

        // Algunos proveedores no aceptan stream_options o un max_tokens tan alto como el que pide Claude Code: se reintenta
        HttpResponseMessage? resp = null;
        for (var intento = 0; intento < 3; intento++)
        {
            resp?.Dispose();
            try
            {
                using var espera = CancellationTokenSource.CreateLinkedTokenSource(ct);
                espera.CancelAfter(EsperaPrimeraRespuesta);
                resp = await Pedir(destino, openai, aTrozos, espera.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await Saturado(r, modelo, ct);
                return;
            }
            if (resp.IsSuccessStatusCode || (int)resp.StatusCode != 400) break;
            var texto = await resp.Content.ReadAsStringAsync(ct);
            if (texto.Contains("stream_options", StringComparison.OrdinalIgnoreCase) && openai.Remove("stream_options")) continue;
            if (texto.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) || texto.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase))
            {
                var actual = openai["max_tokens"]?.GetValue<int>() ?? 0;
                if (actual > 8192) { openai["max_tokens"] = 8192; continue; }
            }
            resp.Dispose();
            await Error(r, 400, "invalid_request_error", Recortar(texto), ct);
            return;
        }
        using (resp)
        {
            if (!resp!.IsSuccessStatusCode)
            {
                var texto = await resp.Content.ReadAsStringAsync(ct);
                Registro.Aviso($"Traductor: {(int)resp.StatusCode} de {destino.BaseUrl} con {modelo}: {Recortar(texto, 300)}");
                await Error(r, (int)resp.StatusCode, TipoError((int)resp.StatusCode), Recortar(texto), ct);
                return;
            }
            if (aTrozos)
            {
                try { await ATrozos(resp, r, modelo, EsperaPrimeraRespuesta, ct); }
                catch (TimeoutException) { await Saturado(r, modelo, ct); }
            }
            else await r.Enviar(200, "application/json", AAnthropic(JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) as JsonObject ?? [], modelo).ToJsonString(), ct);
        }
    }

    /// <summary>
    /// El proveedor no empieza a contestar. Se devuelve como petición rechazada (400), no como fallo del servidor: así Claude
    /// Code no reintenta durante minutos y el usuario ve el motivo.
    /// </summary>
    private async Task Saturado(RespuestaHttp r, string modelo, CancellationToken ct)
    {
        var mensaje = $"El proveedor no ha empezado a responder en {EsperaPrimeraRespuesta.TotalSeconds:0} s con «{modelo}»: el modelo está saturado o en cola. Prueba más tarde o cambia de modelo (Cuentas y proveedores › «Modelo…»).";
        Registro.Aviso("Traductor: " + mensaje);
        if (r.Empezada) await r.Evento("error", new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = mensaje } }.ToJsonString(), ct);
        else await Error(r, 400, "invalid_request_error", mensaje, ct);
    }

    private async Task<HttpResponseMessage> Pedir(DestinoTraductor destino, JsonObject openai, bool aTrozos, CancellationToken ct)
    {
        var pet = new HttpRequestMessage(HttpMethod.Post, destino.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(openai.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(destino.Clave)) pet.Headers.Authorization = new AuthenticationHeaderValue("Bearer", destino.Clave);
        if (aTrozos) pet.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await _http.SendAsync(pet, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static string Recortar(string s, int max = 2000) => s.Length <= max ? s : s[..max] + "…";

    // ================================================================= Anthropic → OpenAI

    /// <summary>Pasa una petición de la API de Anthropic (Messages) a Chat Completions de OpenAI.</summary>
    public static JsonObject AOpenAI(JsonObject a, DestinoTraductor destino)
    {
        var modelo = a["model"]?.GetValue<string>();
        // Claude Code pide modelos de Claude para tareas pequeñas (títulos, resúmenes): se usa el de la cuenta
        if (string.IsNullOrEmpty(modelo) || modelo.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) modelo = destino.Modelo ?? modelo;
        var mensajes = new JsonArray();
        var sistema = Texto(a["system"]);
        if (!string.IsNullOrEmpty(sistema)) mensajes.Add(new JsonObject { ["role"] = "system", ["content"] = sistema });

        foreach (var m in (a["messages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var rol = m["role"]?.GetValue<string>() ?? "user";
            var contenido = m["content"];
            if (contenido is JsonValue)
            {
                mensajes.Add(new JsonObject { ["role"] = rol, ["content"] = contenido.GetValue<string>() });
                continue;
            }
            var bloques = (contenido as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (rol == "assistant")
            {
                var texto = string.Concat(bloques.Where(b => Tipo(b) == "text").Select(b => b["text"]?.GetValue<string>()));
                var llamadas = new JsonArray();
                foreach (var b in bloques.Where(b => Tipo(b) == "tool_use"))
                    llamadas.Add(new JsonObject
                    {
                        ["id"] = b["id"]?.GetValue<string>(), ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = b["name"]?.GetValue<string>(), ["arguments"] = (b["input"] ?? new JsonObject()).ToJsonString() },
                    });
                var msg = new JsonObject { ["role"] = "assistant", ["content"] = texto.Length > 0 ? texto : null };
                if (llamadas.Count > 0) msg["tool_calls"] = llamadas;
                if (texto.Length > 0 || llamadas.Count > 0) mensajes.Add(msg);
                continue;
            }
            // Usuario: primero los resultados de herramientas (mensajes «tool»), luego lo demás
            foreach (var b in bloques.Where(b => Tipo(b) == "tool_result"))
            {
                var resultado = Texto(b["content"]);
                if (b["is_error"]?.GetValue<bool>() == true) resultado = "Error: " + resultado;
                mensajes.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = b["tool_use_id"]?.GetValue<string>(), ["content"] = resultado });
            }
            var partes = new JsonArray();
            foreach (var b in bloques)
            {
                if (Tipo(b) == "text") partes.Add(new JsonObject { ["type"] = "text", ["text"] = b["text"]?.GetValue<string>() });
                else if (Tipo(b) == "image" && b["source"] is JsonObject s)
                {
                    var url = s["type"]?.GetValue<string>() == "base64"
                        ? $"data:{s["media_type"]?.GetValue<string>()};base64,{s["data"]?.GetValue<string>()}"
                        : s["url"]?.GetValue<string>();
                    if (url is not null) partes.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
                }
                else if (Tipo(b) == "document") partes.Add(new JsonObject { ["type"] = "text", ["text"] = "[documento adjunto que este modelo no puede leer]" });
            }
            if (partes.Count == 0) continue;
            // Solo texto: como cadena (lo aceptan todos los proveedores)
            mensajes.Add(partes.All(x => x?["type"]?.GetValue<string>() == "text")
                ? new JsonObject { ["role"] = "user", ["content"] = string.Join("\n\n", partes.Select(x => x!["text"]?.GetValue<string>())) }
                : new JsonObject { ["role"] = "user", ["content"] = partes });
        }

        var o = new JsonObject { ["model"] = modelo, ["messages"] = Juntar(mensajes) };
        if (a["max_tokens"] is JsonValue mt) o["max_tokens"] = mt.GetValue<int>();
        if (a["temperature"] is JsonValue t) o["temperature"] = t.DeepClone();
        if (a["top_p"] is JsonValue tp) o["top_p"] = tp.DeepClone();
        if (a["stop_sequences"] is JsonArray ss && ss.Count > 0) o["stop"] = ss.DeepClone();
        if (a["stream"]?.GetValue<bool>() == true)
        {
            o["stream"] = true;
            o["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
        if (a["tools"] is JsonArray herramientas && herramientas.Count > 0)
        {
            var lista = new JsonArray();
            foreach (var h in herramientas.OfType<JsonObject>())
            {
                if (h["name"] is null || (h["type"]?.GetValue<string>() is { } tipo && tipo != "custom" && h["input_schema"] is null)) continue; // herramientas propias de Anthropic
                var esquema = (h["input_schema"]?.DeepClone() as JsonObject) ?? new JsonObject { ["type"] = "object" };
                esquema.Remove("$schema");
                lista.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = h["name"]!.GetValue<string>(), ["description"] = h["description"]?.GetValue<string>() ?? "", ["parameters"] = esquema },
                });
            }
            if (lista.Count > 0)
            {
                o["tools"] = lista;
                o["tool_choice"] = (a["tool_choice"]?["type"]?.GetValue<string>()) switch
                {
                    "any" => "required",
                    "none" => "none",
                    "tool" => new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = a["tool_choice"]!["name"]?.GetValue<string>() } },
                    _ => "auto",
                };
            }
        }
        return o;
    }

    /// <summary>
    /// Junta los mensajes seguidos del usuario (Claude Code manda los avisos del sistema como mensajes aparte): hay
    /// proveedores que exigen que usuario y asistente se alternen.
    /// </summary>
    private static JsonArray Juntar(JsonArray mensajes)
    {
        var r = new JsonArray();
        foreach (var m in mensajes.OfType<JsonObject>().ToList())
        {
            mensajes.Remove(m);
            if (r.Count > 0 && r[^1] is JsonObject previo && previo["role"]?.GetValue<string>() == "user" && m["role"]?.GetValue<string>() == "user")
            {
                var partes = new JsonArray();
                foreach (var c in new[] { previo["content"], m["content"] })
                    if (c is JsonArray a) foreach (var x in a.ToList()) { a.Remove(x); partes.Add(x); }
                    else partes.Add(new JsonObject { ["type"] = "text", ["text"] = c?.GetValue<string>() ?? "" });
                previo["content"] = partes.All(x => x?["type"]?.GetValue<string>() == "text")
                    ? string.Join("\n\n", partes.Select(x => x!["text"]?.GetValue<string>()))
                    : partes;
                continue;
            }
            r.Add(m);
        }
        return r;
    }

    private static string? Tipo(JsonNode? b) => b?["type"]?.GetValue<string>();

    /// <summary>Texto de un campo que puede ser cadena o lista de bloques.</summary>
    private static string Texto(JsonNode? n) => n switch
    {
        JsonValue v => v.GetValue<string>(),
        JsonArray a => string.Join("\n", a.OfType<JsonObject>().Select(b => Tipo(b) switch
        {
            "text" => b["text"]?.GetValue<string>(),
            "image" => "[imagen]",
            _ => null,
        }).OfType<string>()),
        _ => "",
    };

    // ================================================================= OpenAI → Anthropic

    private static string Fin(string? motivo) => motivo switch
    {
        "length" => "max_tokens",
        "tool_calls" or "function_call" => "tool_use",
        _ => "end_turn",
    };

    private static string IdMensaje() => "msg_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
    private static string IdHerramienta() => "toolu_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    private static string? Razonamiento(JsonNode? m) =>
        m?["reasoning_content"]?.GetValue<string>() ?? (m?["reasoning"] is JsonValue r ? r.GetValue<string>() : null);

    /// <summary>Respuesta completa de Chat Completions → mensaje de Anthropic.</summary>
    public static JsonObject AAnthropic(JsonObject o, string modelo)
    {
        var eleccion = (o["choices"] as JsonArray)?.FirstOrDefault();
        var m = eleccion?["message"];
        var contenido = new JsonArray();
        if (Razonamiento(m) is { Length: > 0 } pensado) contenido.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = pensado, ["signature"] = "" });
        if (m?["content"] is JsonValue c && c.GetValue<string>() is { Length: > 0 } texto) contenido.Add(new JsonObject { ["type"] = "text", ["text"] = texto });
        foreach (var l in (m?["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
            contenido.Add(new JsonObject
            {
                ["type"] = "tool_use", ["id"] = l["id"]?.GetValue<string>() ?? IdHerramienta(), ["name"] = l["function"]?["name"]?.GetValue<string>(),
                ["input"] = Argumentos(l["function"]?["arguments"]?.GetValue<string>()),
            });
        return new JsonObject
        {
            ["id"] = IdMensaje(), ["type"] = "message", ["role"] = "assistant", ["model"] = modelo, ["content"] = contenido,
            ["stop_reason"] = Fin(eleccion?["finish_reason"]?.GetValue<string>()), ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = o["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0,
                ["output_tokens"] = o["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0,
            },
        };
    }

    private static JsonNode Argumentos(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject { ["_texto"] = json }; }
    }

    /// <summary>Respuesta a trozos de Chat Completions → eventos de Anthropic (message_start, content_block_*, message_delta…).</summary>
    private static async Task ATrozos(HttpResponseMessage resp, RespuestaHttp r, string modelo, TimeSpan esperaPrimera, CancellationToken ct)
    {
        using var lector = new StreamReader(await resp.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
        // Hasta el primer dato no se contesta nada: si no llega a tiempo, el error todavía puede ir como respuesta normal
        string? primera;
        using (var espera = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            espera.CancelAfter(esperaPrimera);
            try { primera = await lector.ReadLineAsync(espera.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException(); }
        }
        await r.EmpezarEventos(ct);
        await r.Evento("message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = IdMensaje(), ["type"] = "message", ["role"] = "assistant", ["model"] = modelo, ["content"] = new JsonArray(),
                ["stop_reason"] = null, ["stop_sequence"] = null, ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 },
            },
        }.ToJsonString(), ct);

        var indice = -1;
        string? abierto = null;                       // thinking, text, tool_use
        var herramientas = new Dictionary<int, int>(); // índice de OpenAI → bloque de Anthropic
        int? actualHerramienta = null;
        string? motivo = null;
        int entrada = 0, salida = 0;

        async Task Cerrar()
        {
            if (abierto is null) return;
            await r.Evento("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = indice }.ToJsonString(), ct);
            abierto = null;
            actualHerramienta = null;
        }
        async Task Abrir(string tipo, JsonObject bloque)
        {
            await Cerrar();
            indice++;
            abierto = tipo;
            await r.Evento("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = indice, ["content_block"] = bloque }.ToJsonString(), ct);
        }
        Task Delta(JsonObject delta) =>
            r.Evento("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = indice, ["delta"] = delta }.ToJsonString(), ct);

        var linea = primera;
        for (; linea is not null; linea = await lector.ReadLineAsync(ct))
        {
            if (!linea.StartsWith("data:", StringComparison.Ordinal)) continue;
            var datos = linea[5..].Trim();
            if (datos == "[DONE]") break;
            JsonObject? trozo;
            try { trozo = JsonNode.Parse(datos) as JsonObject; }
            catch (JsonException) { continue; }
            if (trozo is null) continue;
            if (trozo["error"] is JsonObject err)
            {
                await Cerrar();
                await r.Evento("error", new JsonObject
                {
                    ["type"] = "error", ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = err["message"]?.GetValue<string>() ?? err.ToJsonString() },
                }.ToJsonString(), ct);
                return;
            }
            if (trozo["usage"] is JsonObject u)
            {
                entrada = u["prompt_tokens"]?.GetValue<int>() ?? entrada;
                salida = u["completion_tokens"]?.GetValue<int>() ?? salida;
            }
            var eleccion = (trozo["choices"] as JsonArray)?.FirstOrDefault();
            if (eleccion is null) continue;
            var d = eleccion["delta"];
            if (Razonamiento(d) is { Length: > 0 } pensado)
            {
                if (abierto != "thinking") await Abrir("thinking", new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" });
                await Delta(new JsonObject { ["type"] = "thinking_delta", ["thinking"] = pensado });
            }
            if (d?["content"] is JsonValue cv && cv.GetValue<string>() is { Length: > 0 } texto)
            {
                if (abierto != "text") await Abrir("text", new JsonObject { ["type"] = "text", ["text"] = "" });
                await Delta(new JsonObject { ["type"] = "text_delta", ["text"] = texto });
            }
            foreach (var l in (d?["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var i = l["index"]?.GetValue<int>() ?? 0;
                if (!herramientas.ContainsKey(i))
                {
                    await Abrir("tool_use", new JsonObject
                    {
                        ["type"] = "tool_use", ["id"] = l["id"]?.GetValue<string>() is { Length: > 0 } id ? id : IdHerramienta(),
                        ["name"] = l["function"]?["name"]?.GetValue<string>() ?? "", ["input"] = new JsonObject(),
                    });
                    herramientas[i] = indice;
                    actualHerramienta = i;
                }
                else if (actualHerramienta != i) continue; // trozos de otra llamada ya cerrada: no se pueden reabrir
                if (l["function"]?["arguments"]?.GetValue<string>() is { Length: > 0 } args)
                    await Delta(new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = args });
            }
            if (eleccion["finish_reason"]?.GetValue<string>() is { } f) motivo = f;
        }
        await Cerrar();
        await r.Evento("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = Fin(motivo ?? (herramientas.Count > 0 ? "tool_calls" : "stop")), ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["input_tokens"] = entrada, ["output_tokens"] = salida },
        }.ToJsonString(), ct);
        await r.Evento("message_stop", "{\"type\":\"message_stop\"}", ct);
    }

    /// <summary>
    /// Prueba un modelo con la petición más pequeña posible (1 token). Devuelve null si responde, o el motivo en lenguaje
    /// llano si el proveedor dice que no lo tiene o no acepta la clave. Si no se puede saber (sin red, lento), null.
    /// </summary>
    public static async Task<string?> ProbarModelo(string baseUrl, string? clave, string modelo, HttpMessageHandler? manejador = null, CancellationToken ct = default)
    {
        var (codigo, texto) = await Probar(baseUrl, clave, modelo, manejador, ct);
        return codigo switch
        {
            401 or 403 => "El proveedor no acepta la clave (no es válida o no tiene permiso).",
            404 => $"El proveedor no tiene «{modelo}» disponible para tu cuenta. Elige otro modelo.",
            400 or 422 when texto.Contains("model", StringComparison.OrdinalIgnoreCase) => $"El proveedor no acepta «{modelo}»: {texto}",
            _ => null, // funciona, o 429, 5xx, sin red…: no se sabe; ya se verá al usarlo
        };
    }

    /// <summary>Código HTTP de la petición mínima (0 si no hubo respuesta) y el principio del texto de error.</summary>
    public static async Task<(int Codigo, string Texto)> Probar(string baseUrl, string? clave, string modelo, HttpMessageHandler? manejador = null, CancellationToken ct = default)
    {
        using var http = manejador is null ? new HttpClient() : new HttpClient(manejador, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(30);
        using var pet = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(new JsonObject
            {
                ["model"] = modelo, ["max_tokens"] = 1,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hola" }),
            }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(clave)) pet.Headers.Authorization = new AuthenticationHeaderValue("Bearer", clave);
        try
        {
            using var r = await http.SendAsync(pet, ct);
            if (r.IsSuccessStatusCode) return (200, "");
            var texto = Recortar(await r.Content.ReadAsStringAsync(ct), 300);
            Registro.Aviso($"Prueba de {modelo} en {baseUrl}: {(int)r.StatusCode} {texto}");
            return ((int)r.StatusCode, texto);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Registro.Aviso($"Prueba de {modelo} en {baseUrl}: {ex.Message}");
            return (0, ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _servidor.DisposeAsync();
        _http.Dispose();
    }
}
