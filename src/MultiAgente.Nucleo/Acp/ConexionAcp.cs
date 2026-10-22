using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiAgente.Nucleo.Acp;

/// <summary>Cómo arrancar un agente: ejecutable y argumentos (sin pasar por cmd.exe).</summary>
public sealed record OrdenAgente(string Ejecutable, IReadOnlyList<string> Argumentos);

/// <summary>Error devuelto por el agente (JSON-RPC) o porque el agente se ha cerrado.</summary>
public sealed class ErrorAcp(int codigo, string mensaje, JsonNode? datos = null) : Exception(mensaje)
{
    public int Codigo { get; } = codigo;
    public JsonNode? Datos { get; } = datos;
}

/// <summary>
/// Conexión ACP (Agent Client Protocol) con un agente: JSON-RPC 2.0, un mensaje por línea, por la entrada y la salida
/// estándar del proceso del agente. Hace peticiones, recibe notificaciones (<c>session/update</c>…) y atiende las
/// peticiones del agente (<c>session/request_permission</c>).
/// </summary>
public sealed class ConexionAcp : IAsyncDisposable
{
    private readonly Process _proceso;
    private TrabajoProcesos? _trabajo;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pendientes = new();
    private readonly SemaphoreSlim _escritura = new(1, 1);
    private readonly string _nombre;
    private long _ultimoId;
    private volatile bool _cerrada;

    private ConexionAcp(Process proceso, string nombre)
    {
        _proceso = proceso;
        _nombre = nombre;
    }

    /// <summary>Notificación del agente (método y parámetros). Se llama en orden, desde el hilo lector.</summary>
    public event Action<string, JsonNode?>? Notificacion;

    /// <summary>El agente se ha cerrado (motivo para el registro).</summary>
    public event Action<string>? Cerrada;

    /// <summary>Atiende las peticiones del agente al cliente. Devuelve el «result»; una excepción se devuelve como error.</summary>
    public Func<string, JsonNode?, Task<JsonNode?>>? AtenderPeticion { get; set; }

    public bool Viva => !_cerrada;
    public int Pid => _proceso.Id;

    /// <summary>
    /// Arranca el agente. <paramref name="entorno"/>: variables que se fijan (un valor null la quita del entorno heredado).
    /// </summary>
    public static ConexionAcp Iniciar(OrdenAgente orden, string carpeta, IReadOnlyDictionary<string, string?> entorno, string nombre)
    {
        var psi = new ProcessStartInfo(orden.Ejecutable)
        {
            WorkingDirectory = carpeta,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in orden.Argumentos) psi.ArgumentList.Add(a);
        foreach (var (k, v) in entorno)
        {
            if (v is null) psi.Environment.Remove(k);
            else psi.Environment[k] = v;
        }

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!p.Start()) throw new ErrorAcp(-1, $"No se pudo arrancar {orden.Ejecutable}");
        var c = new ConexionAcp(p, nombre) { _trabajo = TrabajoProcesos.Atar(p) };
        Registro.Info($"{nombre}: agente arrancado (pid {p.Id}) en {carpeta}");
        _ = Task.Run(c.LeerSalida);
        _ = Task.Run(c.LeerErrores);
        return c;
    }

    /// <summary>Petición al agente; devuelve su «result» o lanza <see cref="ErrorAcp"/>.</summary>
    public async Task<JsonNode?> Pedir(string metodo, object? parametros, CancellationToken ct = default)
    {
        if (_cerrada) throw new ErrorAcp(-1, "El agente está cerrado.");
        var id = Interlocked.Increment(ref _ultimoId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendientes[id] = tcs;
        await Escribir(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = metodo,
            ["params"] = ANodo(parametros),
        });
        using (ct.Register(() => tcs.TrySetCanceled(ct)))
        {
            try { return await tcs.Task; }
            finally { _pendientes.TryRemove(id, out _); }
        }
    }

    /// <summary>Notificación al agente (sin respuesta), p. ej. <c>session/cancel</c>.</summary>
    public Task Notificar(string metodo, object? parametros) =>
        _cerrada ? Task.CompletedTask : Escribir(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = metodo, ["params"] = ANodo(parametros) });

    private static JsonNode? ANodo(object? o) => o switch
    {
        null => null,
        JsonNode n => n.DeepClone(),
        _ => JsonSerializer.SerializeToNode(o, Json.Opciones),
    };

    private async Task Escribir(JsonObject mensaje)
    {
        var linea = mensaje.ToJsonString();
        await _escritura.WaitAsync();
        try
        {
            await _proceso.StandardInput.WriteAsync(linea + "\n");
            await _proceso.StandardInput.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new ErrorAcp(-1, "El agente se ha cerrado.");
        }
        finally { _escritura.Release(); }
    }

    private async Task LeerSalida()
    {
        try
        {
            while (await _proceso.StandardOutput.ReadLineAsync() is { } linea)
            {
                if (linea.Length == 0 || linea[0] != '{') continue;
                JsonNode? m;
                try { m = JsonNode.Parse(linea); }
                catch (JsonException) { Registro.Aviso($"{_nombre}: línea que no es JSON-RPC"); continue; }
                if (m is not JsonObject o) continue;
                // Nada de lo que llegue puede parar el lector: sin él, todas las peticiones se quedarían esperando
                try
                {
                    var metodo = Cadena(o["method"]);
                    var id = o["id"];
                    if (metodo is null && id is not null)
                        Responder(id, o);
                    else if (metodo is not null && id is not null)
                        _ = Task.Run(() => AtenderAgente(id.DeepClone(), metodo, o["params"]?.DeepClone()));
                    else if (metodo is not null)
                        Notificacion?.Invoke(metodo, o["params"]);
                }
                catch (Exception ex) { Registro.Error($"{_nombre}: al procesar un mensaje del agente", ex); }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        Cerrar("su salida terminó");
    }

    private void Responder(JsonNode id, JsonObject o)
    {
        if (!long.TryParse(id.ToString(), out var n) || !_pendientes.TryRemove(n, out var tcs)) return;
        if (o["error"] is JsonObject e)
        {
            var codigo = e["code"] is JsonValue cv && int.TryParse(cv.ToString(), out var c) ? c : 0;
            tcs.TrySetException(new ErrorAcp(codigo, Cadena(e["message"]) ?? "Error del agente", e["data"]?.DeepClone()));
        }
        else
            tcs.TrySetResult(o["result"]?.DeepClone());
    }

    private static string? Cadena(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString();

    private async Task AtenderAgente(JsonNode id, string metodo, JsonNode? parametros)
    {
        JsonObject respuesta;
        try
        {
            if (AtenderPeticion is null) throw new ErrorAcp(-32601, $"Método no admitido: {metodo}");
            var resultado = await AtenderPeticion(metodo, parametros);
            respuesta = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = resultado ?? new JsonObject() };
        }
        catch (Exception ex)
        {
            var codigo = ex is ErrorAcp ea ? ea.Codigo : -32603;
            if (ex is not ErrorAcp) Registro.Error($"{_nombre}: al atender {metodo}", ex);
            respuesta = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id,
                ["error"] = new JsonObject { ["code"] = codigo, ["message"] = ex.Message },
            };
        }
        try { await Escribir(respuesta); } catch (ErrorAcp) { }
    }

    private async Task LeerErrores()
    {
        try
        {
            while (await _proceso.StandardError.ReadLineAsync() is { } linea)
                if (!string.IsNullOrWhiteSpace(linea))
                    Registro.Info($"{_nombre} (stderr): {(linea.Length > 400 ? linea[..400] + "…" : linea)}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void Cerrar(string motivo)
    {
        if (_cerrada) return;
        _cerrada = true;
        foreach (var (_, tcs) in _pendientes) tcs.TrySetException(new ErrorAcp(-1, "El agente se ha cerrado."));
        _pendientes.Clear();
        Registro.Info($"{_nombre}: conexión cerrada ({motivo})");
        try { Cerrada?.Invoke(motivo); } catch (Exception ex) { Registro.Error($"{_nombre}: al avisar del cierre", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        try { _proceso.StandardInput.Close(); } catch (Exception) { }
        try
        {
            using var espera = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _proceso.WaitForExitAsync(espera.Token);
        }
        catch (OperationCanceledException)
        {
            try { _proceso.Kill(entireProcessTree: true); } catch (Exception) { }
        }
        catch (InvalidOperationException) { }
        // Lo que el agente haya dejado en marcha (servidores MCP, órdenes en segundo plano) se cierra con él
        _trabajo?.Dispose();
        Cerrar("cerrada por la aplicación");
        _proceso.Dispose();
        _escritura.Dispose();
    }
}
