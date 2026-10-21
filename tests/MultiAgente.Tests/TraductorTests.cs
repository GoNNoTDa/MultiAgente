using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using MultiAgente.Datos;
using MultiAgente.Nucleo;

namespace MultiAgente.Tests;

/// <summary>
/// Proveedor falso con la API de OpenAI (Chat Completions): contesta según el último mensaje. «lee» → pide la herramienta
/// Read; tras el resultado de una herramienta → texto con lo leído; «lento-429» → límite; si no → razonamiento y texto.
/// </summary>
internal sealed class ProveedorOpenAIFalso : IAsyncDisposable
{
    public readonly List<JsonObject> Peticiones = [];
    public string? ClaveVista;
    private readonly ServidorHttp _servidor;

    public ProveedorOpenAIFalso(string? ficheroParaLeer = null)
    {
        _servidor = new ServidorHttp(async (p, r, ct) =>
        {
            var o = (JsonNode.Parse(p.Cuerpo) as JsonObject)!;
            lock (Peticiones) Peticiones.Add(o);
            ClaveVista = p.Cabecera("Authorization");
            var mensajes = (JsonArray)o["messages"]!;
            var ultimo = (JsonObject)mensajes[^1]!;
            var texto = ultimo["content"] is JsonValue v ? v.GetValue<string>() : "";
            if (o["model"]?.GetValue<string>() == "en-cola")
            {
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { }
                return;
            }
            if (o["model"]?.GetValue<string>() == "no-existe")
            {
                await r.Enviar(404, "application/json", "{\"status\":404,\"title\":\"Not Found\",\"detail\":\"Function 'x': Not found for account 'y'\"}", ct);
                return;
            }
            if (texto.Contains("429"))
            {
                await r.Enviar(429, "application/json", "{\"error\":{\"message\":\"Rate limit reached\"}}", ct);
                return;
            }
            var trozos = new List<JsonObject>();
            JsonObject Trozo(JsonObject delta, string? fin = null) => new()
            {
                ["id"] = "c1", ["object"] = "chat.completion.chunk",
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = fin }),
            };
            // Resultado de herramienta después de la última respuesta (Claude Code le añade texto suyo detrás, en otro mensaje)
            var trasAsistente = mensajes.Skip(mensajes.Select((x, i) => (x, i)).LastOrDefault(t => t.x!["role"]!.GetValue<string>() == "assistant").i + 1);
            var resultado = trasAsistente.OfType<JsonObject>().FirstOrDefault(x => x["role"]!.GetValue<string>() == "tool");
            if (resultado is not null && mensajes.Any(x => x!["role"]!.GetValue<string>() == "assistant"))
            {
                trozos.Add(Trozo(new JsonObject { ["content"] = "Leído: " }));
                trozos.Add(Trozo(new JsonObject { ["content"] = resultado["content"]!.GetValue<string>().Trim() }, "stop"));
            }
            else if (texto.Contains("LEER") && ficheroParaLeer is not null && o["tools"] is JsonArray)
            {
                var args = $"{{\"file_path\":{System.Text.Json.JsonSerializer.Serialize(ficheroParaLeer)}}}";
                trozos.Add(Trozo(new JsonObject { ["content"] = "Voy a leerlo." }));
                trozos.Add(Trozo(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["id"] = "call_1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "Read", ["arguments"] = args[..10] } }) }));
                trozos.Add(Trozo(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["function"] = new JsonObject { ["arguments"] = args[10..] } }) }, "tool_calls"));
            }
            else
            {
                trozos.Add(Trozo(new JsonObject { ["reasoning_content"] = "Pienso " }));
                trozos.Add(Trozo(new JsonObject { ["reasoning_content"] = "un poco." }));
                trozos.Add(Trozo(new JsonObject { ["content"] = "Hola desde " }));
                trozos.Add(Trozo(new JsonObject { ["content"] = "el proveedor falso." }, "stop"));
            }
            if (o["stream"]?.GetValue<bool>() != true)
            {
                var contenido = string.Concat(trozos.Select(t => t["choices"]![0]!["delta"]!["content"]?.GetValue<string>()));
                await r.Enviar(200, "application/json", new JsonObject
                {
                    ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["finish_reason"] = "stop", ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = contenido } }),
                    ["usage"] = new JsonObject { ["prompt_tokens"] = 12, ["completion_tokens"] = 5 },
                }.ToJsonString(), ct);
                return;
            }
            await r.EmpezarEventos(ct);
            foreach (var t in trozos) await Escribir(r, t.ToJsonString(), ct);
            await Escribir(r, new JsonObject { ["choices"] = new JsonArray(), ["usage"] = new JsonObject { ["prompt_tokens"] = 120, ["completion_tokens"] = 30 } }.ToJsonString(), ct);
            await Escribir(r, "[DONE]", ct);
        });
    }

    // Formato de OpenAI: solo líneas «data:», sin «event:»
    private static Task Escribir(RespuestaHttp r, string datos, CancellationToken ct) => r.Evento("x", datos, ct);

    public string Url => _servidor.Base + "/v1";
    public ValueTask DisposeAsync() => _servidor.DisposeAsync();
}

public class TraductorTests
{
    private static readonly DestinoTraductor Destino = new("https://x/v1", "k", "moonshotai/kimi-k2.6");

    [Fact]
    public void Pasa_una_peticion_de_Anthropic_a_Chat_Completions()
    {
        var a = JsonNode.Parse("""
        {
          "model": "claude-haiku-4-5", "max_tokens": 32000, "stream": true,
          "system": [ { "type": "text", "text": "Eres un agente." }, { "type": "text", "text": "Sé breve.", "cache_control": { "type": "ephemeral" } } ],
          "tools": [
            { "name": "Read", "description": "Lee", "input_schema": { "$schema": "http://json-schema.org/draft-07/schema#", "type": "object", "properties": { "file_path": { "type": "string" } } } },
            { "type": "web_search_20250305", "name": "web_search" }
          ],
          "tool_choice": { "type": "any" },
          "messages": [
            { "role": "user", "content": "hola" },
            { "role": "assistant", "content": [ { "type": "thinking", "thinking": "x", "signature": "s" }, { "type": "text", "text": "Leo." },
                                                { "type": "tool_use", "id": "t1", "name": "Read", "input": { "file_path": "a.txt" } } ] },
            { "role": "user", "content": [ { "type": "tool_result", "tool_use_id": "t1", "content": [ { "type": "text", "text": "contenido" } ] },
                                           { "type": "text", "text": "¿y bien?" },
                                           { "type": "image", "source": { "type": "base64", "media_type": "image/png", "data": "AAAA" } } ] }
          ]
        }
        """)!.AsObject();
        var o = Traductor.AOpenAI(a, Destino);
        Assert.Equal("moonshotai/kimi-k2.6", o["model"]!.GetValue<string>()); // los modelos de Claude pasan al de la cuenta
        Assert.Equal(32000, o["max_tokens"]!.GetValue<int>());
        Assert.True(o["stream_options"]!["include_usage"]!.GetValue<bool>());
        var m = o["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "tool", "user"], m.Select(x => x!["role"]!.GetValue<string>()));
        Assert.Equal("Eres un agente.\nSé breve.", m[0]!["content"]!.GetValue<string>());
        Assert.Equal("Leo.", m[2]!["content"]!.GetValue<string>());
        Assert.Equal("{\"file_path\":\"a.txt\"}", m[2]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal(("t1", "contenido"), (m[3]!["tool_call_id"]!.GetValue<string>(), m[3]!["content"]!.GetValue<string>()));
        Assert.Equal("image_url", m[4]!["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64,AAAA", m[4]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>());
        var h = Assert.Single(o["tools"]!.AsArray()); // la herramienta propia de Anthropic (web_search) no pasa
        Assert.Equal("Read", h!["function"]!["name"]!.GetValue<string>());
        Assert.Null(h["function"]!["parameters"]!["$schema"]);
        Assert.Equal("required", o["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public void Pasa_una_respuesta_completa_a_un_mensaje_de_Anthropic()
    {
        var o = JsonNode.Parse("""
        { "choices": [ { "finish_reason": "tool_calls", "message": { "role": "assistant", "reasoning_content": "pienso", "content": "Voy.",
            "tool_calls": [ { "id": "c9", "type": "function", "function": { "name": "Bash", "arguments": "{\"command\":\"dir\"}" } } ] } } ],
          "usage": { "prompt_tokens": 10, "completion_tokens": 4 } }
        """)!.AsObject();
        var a = Traductor.AAnthropic(o, "m");
        Assert.Equal("tool_use", a["stop_reason"]!.GetValue<string>());
        var c = a["content"]!.AsArray();
        Assert.Equal(["thinking", "text", "tool_use"], c.Select(x => x!["type"]!.GetValue<string>()));
        Assert.Equal("dir", c[2]!["input"]!["command"]!.GetValue<string>());
        Assert.Equal(10, a["usage"]!["input_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task Si_el_proveedor_no_empieza_a_contestar_lo_dice_en_vez_de_quedarse_esperando()
    {
        await using var proveedor = new ProveedorOpenAIFalso();
        await using var t = new Traductor { EsperaPrimeraRespuesta = TimeSpan.FromSeconds(1) };
        var pase = t.Registrar("c", new DestinoTraductor(proveedor.Url, "k", "en-cola"));
        foreach (var aTrozos in new[] { true, false })
        {
            var (estado, cuerpo) = await Pedir(t, pase, $$"""{"model":"en-cola","max_tokens":10,"stream":{{aTrozos.ToString().ToLowerInvariant()}},"messages":[{"role":"user","content":"hola"}]}""");
            Assert.Equal(400, estado);
            Assert.Contains("saturado", JsonNode.Parse(cuerpo)!["error"]!["message"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Prueba_el_modelo_antes_de_usarlo()
    {
        await using var proveedor = new ProveedorOpenAIFalso();
        Assert.Null(await Traductor.ProbarModelo(proveedor.Url, "k", "modelo-x"));
        Assert.Contains("no tiene «no-existe» disponible", await Traductor.ProbarModelo(proveedor.Url, "k", "no-existe"));
        Assert.Null(await Traductor.ProbarModelo("http://127.0.0.1:1/v1", "k", "modelo-x")); // sin respuesta: no se sabe, no se bloquea
    }

    private static async Task<(int Estado, string Cuerpo)> Pedir(Traductor t, string? pase, string cuerpo)
    {
        using var http = new HttpClient();
        using var pet = new HttpRequestMessage(HttpMethod.Post, t.Url + "/v1/messages?beta=true") { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") };
        if (pase is not null) pet.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pase);
        using var r = await http.SendAsync(pet);
        return ((int)r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Traduce_a_trozos_con_razonamiento_texto_y_herramientas_y_protege_la_clave()
    {
        await using var proveedor = new ProveedorOpenAIFalso(@"C:\x\a.txt");
        await using var t = new Traductor();
        var pase = t.Registrar("cuenta1", new DestinoTraductor(proveedor.Url, "clave-real", "modelo-x"));

        Assert.Equal(401, (await Pedir(t, null, "{}")).Estado);
        Assert.Equal(401, (await Pedir(t, "cuenta1.otro", "{}")).Estado);

        var (estado, sse) = await Pedir(t, pase, """{"model":"claude-sonnet-5","max_tokens":100,"stream":true,"messages":[{"role":"user","content":"hola"}]}""");
        Assert.Equal(200, estado);
        Assert.Equal("Bearer clave-real", proveedor.ClaveVista); // la clave del proveedor la pone el traductor
        var eventos = sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(e => e.Split('\n')).ToList();
        Assert.Equal(["message_start", "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop",
                      "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop", "message_delta", "message_stop"],
                     eventos.Select(e => e[0]["event: ".Length..]));
        var datos = eventos.Select(e => JsonNode.Parse(e[1]["data: ".Length..])!).ToList();
        Assert.Equal("thinking", datos[1]["content_block"]!["type"]!.GetValue<string>());
        Assert.Equal("text", datos[5]["content_block"]!["type"]!.GetValue<string>());
        Assert.Equal("el proveedor falso.", datos[7]["delta"]!["text"]!.GetValue<string>());
        Assert.Equal("end_turn", datos[9]["delta"]!["stop_reason"]!.GetValue<string>());
        Assert.Equal(30, datos[9]["usage"]!["output_tokens"]!.GetValue<int>());

        (estado, sse) = await Pedir(t, pase, """{"model":"modelo-x","max_tokens":100,"stream":true,"tools":[{"name":"Read","input_schema":{"type":"object"}}],"messages":[{"role":"user","content":"LEER a.txt"}]}""");
        var bloques = sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(e => JsonNode.Parse(e.Split('\n')[1][6..])!).ToList();
        var herramienta = bloques.Single(b => b["content_block"]?["type"]?.GetValue<string>() == "tool_use")["content_block"]!;
        Assert.Equal(("call_1", "Read"), (herramienta["id"]!.GetValue<string>(), herramienta["name"]!.GetValue<string>()));
        var json = string.Concat(bloques.Where(b => b["delta"]?["type"]?.GetValue<string>() == "input_json_delta").Select(b => b["delta"]!["partial_json"]!.GetValue<string>()));
        Assert.Equal(@"C:\x\a.txt", JsonNode.Parse(json)!["file_path"]!.GetValue<string>());
        Assert.Equal("tool_use", bloques.Single(b => b["type"]!.GetValue<string>() == "message_delta")["delta"]!["stop_reason"]!.GetValue<string>());

        (estado, var error) = await Pedir(t, pase, """{"model":"modelo-x","max_tokens":10,"messages":[{"role":"user","content":"da 429"}]}""");
        Assert.Equal(429, estado); // el límite llega como límite: Claude Code y el relevo lo reconocen
        Assert.Equal("rate_limit_error", JsonNode.Parse(error)!["error"]!["type"]!.GetValue<string>());

        (estado, var completo) = await Pedir(t, pase, """{"model":"modelo-x","max_tokens":10,"messages":[{"role":"user","content":"hola"}]}""");
        Assert.Equal("Hola desde el proveedor falso.", JsonNode.Parse(completo)!["content"]!.AsArray().Last()!["text"]!.GetValue<string>());
    }

    private static string Corto(string? s) => s is null ? "" : s.Replace('\n', ' ')[..Math.Min(400, s.Length)];

    /// <summary>Claude Code de verdad (claude-agent-acp) trabajando contra el traductor y el proveedor falso: texto y una herramienta.</summary>
    [Fact]
    public async Task Claude_Code_real_trabaja_a_traves_del_traductor()
    {
        if (Environment.GetEnvironmentVariable("MULTIAGENTE_PRUEBA_REAL") != "1") return;
        using var c = new CarpetaPrueba("traductor-real");
        var trabajo = Path.Combine(c.Ruta, "proyecto");
        var cuenta = Path.Combine(c.Ruta, "cuenta");
        Directory.CreateDirectory(trabajo);
        Directory.CreateDirectory(cuenta);
        var fichero = Path.Combine(trabajo, "nota.txt");
        File.WriteAllText(fichero, "palabra secreta: alcachofa\n");
        await using var proveedor = new ProveedorOpenAIFalso(fichero);
        await using var t = new Traductor();
        var pase = t.Registrar("c", new DestinoTraductor(proveedor.Url, "clave-real", "modelo-falso"));
        var p = Proveedores.Claude;
        var entorno = Proveedores.Entorno(p, cuenta, pase, endpoint: t.Url, modelo: "modelo-falso");
        entorno["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        await using var a = new SesionAgente(new Sesion { Carpeta = trabajo, ModoPermisos = "bypassPermissions" },
            new Cuenta { Alias = "Traducida", Proveedor = "Claude", Tipo = TipoCuenta.Api, Carpeta = cuenta, Modelo = "modelo-falso" },
            p, Proveedores.OrdenAcp(p)!, entorno, new Hilo(Path.Combine(c.Ruta, "sesion")), null);
        await a.Arrancar();

        async Task Turno(string texto, int agentes)
        {
            a.Enviar(texto);
            var limite = DateTime.UtcNow.AddSeconds(120);
            while (!(a.Estado == EstadoSesion.Esperando && a.Hilo.Entradas.Count(e => e.Tipo == "agente") >= agentes))
            {
                if (a.Estado == EstadoSesion.PidePermiso && a.PermisoPendiente is { } per)
                    a.ResponderPermiso(per.Id, per.Opciones.Select(o => o?["optionId"]?.GetValue<string>()).FirstOrDefault(o => o?.StartsWith("allow") == true)
                                                ?? per.Opciones[0]?["optionId"]?.GetValue<string>());
                Assert.True(DateTime.UtcNow < limite, "Claude Code no terminó el turno: " + string.Join(" | ", a.Hilo.Entradas.Select(e => $"{e.Tipo}:{e.Texto}{e.Titulo}"))
                    + " ## Últimas peticiones: " + string.Join(" ## ", proveedor.Peticiones.TakeLast(3).Select(x => $"tools={x["tools"]?.AsArray().Count} " + string.Join(" / ",
                        x["messages"]!.AsArray().TakeLast(2).Select(m => m!["role"] + ":" + Corto(m["content"]?.ToJsonString()))))));
                await Task.Delay(200);
            }
        }
        await Turno("hola", 1);
        Assert.Contains(a.Hilo.Entradas, e => e.Tipo == "agente" && e.Texto!.Contains("Hola desde el proveedor falso."));
        await Turno("LEER nota.txt", 3);
        var resumen = string.Join(" | ", a.Hilo.Entradas.Select(e => $"{e.Tipo}/{e.Estado}:{e.Texto}{e.Titulo}"));
        Assert.True(a.Hilo.Entradas.Any(e => e.Tipo == "herramienta" && e.Estado == "completed"), resumen);
        Assert.True(a.Hilo.Entradas.Any(e => e.Tipo == "agente" && e.Texto!.Contains("alcachofa")), resumen);
        Assert.Contains(a.Hilo.Entradas, e => e.Tipo == "agente" && e.Texto!.Contains("alcachofa"));
        Assert.Equal("Bearer clave-real", proveedor.ClaveVista);
    }

    /// <summary>Toda la aplicación: cuenta del catálogo con «traducir», sesión nueva y respuesta a través del traductor.</summary>
    [Fact]
    public async Task Una_cuenta_del_catalogo_trabaja_con_Claude_Code_a_traves_del_traductor()
    {
        if (Environment.GetEnvironmentVariable("MULTIAGENTE_PRUEBA_REAL") != "1") return;
        using var c = new CarpetaPrueba("traductor-gestor");
        var rutas = new Rutas(Path.Combine(c.Ruta, "MultiAgente"));
        rutas.CrearCarpetas();
        Directory.CreateDirectory(Path.GetDirectoryName(rutas.Catalogo)!);
        File.Copy(CatalogoTests.FicheroCatalogo(), rutas.Catalogo);
        var trabajo = Path.Combine(c.Ruta, "proyecto");
        Directory.CreateDirectory(trabajo);
        await using var proveedor = new ProveedorOpenAIFalso();
        using var bd = BaseDatos.Abrir(rutas.BaseDatos);
        using var bucle = new BucleUnico();
        var gestor = await bucle.Ejecutar(() => new Gestor(rutas, new Repositorio(bd), _ => { }, bucle.Poner, "prueba",
            new OpcionesGestor
            {
                Temporizadores = false, CompartirCarpetas = false, GenerarResumen = _ => Task.FromResult<string?>(null),
                EntornoExtra = new Dictionary<string, string?> { ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1" },
            }));
        try
        {
            var repo = new Repositorio(bd);
            await bucle.Ejecutar(() => { gestor.CrearCuenta("Claude", "Api", "Mi servidor", "clave-real", "openai-compatible", proveedor.Url, "modelo-falso"); return true; });
            var cuenta = repo.Cuentas().Single();
            var id = await bucle.Ejecutar(() => gestor.NuevaSesion(trabajo, "cuenta:" + cuenta.Id, "hola", []));
            var limite = DateTime.UtcNow.AddSeconds(120);
            while (!await bucle.Ejecutar(() => gestor.Viva(id)?.Hilo.Entradas.Any(e => e.Tipo == "agente" && e.Texto!.Contains("proveedor falso")) == true))
            {
                Assert.True(DateTime.UtcNow < limite, "No contestó: " + string.Join(" | ", await bucle.Ejecutar(() => gestor.Viva(id)?.Hilo.Entradas.Select(e => $"{e.Tipo}:{e.Texto}") ?? [])));
                await Task.Delay(200);
            }
            Assert.Equal("Bearer clave-real", proveedor.ClaveVista);
            Assert.Contains(proveedor.Peticiones, x => x["model"]?.GetValue<string>() == "modelo-falso");
        }
        finally
        {
            await bucle.Ejecutar(async () => await gestor.DisposeAsync());
        }
    }
}
