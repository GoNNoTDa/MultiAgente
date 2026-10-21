using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MultiAgente.Nucleo;

/// <summary>Petición HTTP recibida por <see cref="ServidorHttp"/>.</summary>
public sealed record PeticionHttp(string Metodo, string Ruta, IReadOnlyDictionary<string, string> Cabeceras, byte[] Cuerpo)
{
    public string? Cabecera(string nombre) => Cabeceras.TryGetValue(nombre, out var v) ? v : null;
    public string Texto => Encoding.UTF8.GetString(Cuerpo);
}

/// <summary>Respuesta de <see cref="ServidorHttp"/>: de una vez o por eventos (SSE), siempre cerrando la conexión al terminar.</summary>
public sealed class RespuestaHttp(Stream salida)
{
    private bool _empezada;

    public bool Empezada => _empezada;

    public async Task Enviar(int estado, string tipo, string cuerpo, CancellationToken ct = default)
    {
        var datos = Encoding.UTF8.GetBytes(cuerpo);
        await Cabeceras(estado, $"Content-Type: {tipo}\r\nContent-Length: {datos.Length}\r\n", ct);
        await salida.WriteAsync(datos, ct);
        await salida.FlushAsync(ct);
    }

    /// <summary>Empieza una respuesta de eventos (text/event-stream); cada evento se manda con <see cref="Evento"/>.</summary>
    public Task EmpezarEventos(CancellationToken ct = default) =>
        Cabeceras(200, "Content-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-cache\r\n", ct);

    public async Task Evento(string tipo, string datos, CancellationToken ct = default)
    {
        await salida.WriteAsync(Encoding.UTF8.GetBytes($"event: {tipo}\ndata: {datos}\n\n"), ct);
        await salida.FlushAsync(ct);
    }

    private async Task Cabeceras(int estado, string extra, CancellationToken ct)
    {
        if (_empezada) throw new InvalidOperationException("La respuesta ya se había empezado.");
        _empezada = true;
        var frase = Enum.IsDefined(typeof(HttpStatusCode), estado) ? ((HttpStatusCode)estado).ToString() : "Status";
        await salida.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {estado} {frase}\r\n{extra}Connection: close\r\n\r\n"), ct);
    }
}

/// <summary>
/// Servidor HTTP/1.1 mínimo solo en 127.0.0.1 y con un puerto libre cualquiera: lo justo para que un agente local hable con
/// MultiAgente (una petición por conexión, cuerpo con Content-Length o por trozos). Sin dependencias ni permisos de
/// administrador (no usa http.sys).
/// </summary>
public sealed class ServidorHttp : IAsyncDisposable
{
    private readonly TcpListener _escucha = new(IPAddress.Loopback, 0);
    private readonly Func<PeticionHttp, RespuestaHttp, CancellationToken, Task> _atender;
    private readonly CancellationTokenSource _fin = new();
    private readonly Task _bucle;

    public ServidorHttp(Func<PeticionHttp, RespuestaHttp, CancellationToken, Task> atender)
    {
        _atender = atender;
        _escucha.Start();
        Puerto = ((IPEndPoint)_escucha.LocalEndpoint).Port;
        _bucle = Task.Run(Escuchar);
    }

    public int Puerto { get; }
    public string Base => $"http://127.0.0.1:{Puerto}";

    private async Task Escuchar()
    {
        while (!_fin.IsCancellationRequested)
        {
            TcpClient cliente;
            try { cliente = await _escucha.AcceptTcpClientAsync(_fin.Token); }
            catch (Exception) when (_fin.IsCancellationRequested) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => Conexion(cliente));
        }
    }

    private async Task Conexion(TcpClient cliente)
    {
        using (cliente)
        {
            var red = cliente.GetStream();
            try
            {
                var peticion = await Leer(red, _fin.Token);
                if (peticion is null) return;
                var respuesta = new RespuestaHttp(red);
                try { await _atender(peticion, respuesta, _fin.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Registro.Error($"Servidor local: {peticion.Metodo} {peticion.Ruta}", ex);
                    if (!respuesta.Empezada) await respuesta.Enviar(500, "application/json", "{\"error\":\"interno\"}");
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private static async Task<PeticionHttp?> Leer(NetworkStream red, CancellationToken ct)
    {
        // Cabeceras: hasta la línea en blanco (máximo 64 KB)
        var buffer = new MemoryStream();
        var trozo = new byte[8192];
        int fin;
        while (true)
        {
            var n = await red.ReadAsync(trozo, ct);
            if (n == 0) return null;
            buffer.Write(trozo, 0, n);
            fin = Buscar(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
            if (fin >= 0) break;
            if (buffer.Length > 65536) return null;
        }
        var todo = buffer.ToArray();
        var lineas = Encoding.ASCII.GetString(todo, 0, fin).Split("\r\n");
        var primera = lineas[0].Split(' ');
        if (primera.Length < 2) return null;
        var cabeceras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lineas.Skip(1))
        {
            var i = l.IndexOf(':');
            if (i > 0) cabeceras[l[..i].Trim()] = l[(i + 1)..].Trim();
        }
        var resto = new MemoryStream();
        resto.Write(todo, fin + 4, todo.Length - fin - 4);

        byte[] cuerpo;
        if (cabeceras.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out var largo))
        {
            if (largo > 64 * 1024 * 1024) return null;
            while (resto.Length < largo)
            {
                var n = await red.ReadAsync(trozo, ct);
                if (n == 0) break;
                resto.Write(trozo, 0, n);
            }
            cuerpo = resto.ToArray()[..(int)Math.Min(largo, resto.Length)];
        }
        else if (cabeceras.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            cuerpo = await PorTrozos(red, resto, trozo, ct);
        else cuerpo = [];
        return new PeticionHttp(primera[0], primera[1], cabeceras, cuerpo);
    }

    private static async Task<byte[]> PorTrozos(NetworkStream red, MemoryStream leido, byte[] trozo, CancellationToken ct)
    {
        var salida = new MemoryStream();
        var pendiente = new List<byte>(leido.ToArray());
        async Task<bool> Asegurar(int cuantos)
        {
            while (pendiente.Count < cuantos)
            {
                var n = await red.ReadAsync(trozo, ct);
                if (n == 0) return false;
                pendiente.AddRange(trozo.AsSpan(0, n).ToArray());
            }
            return true;
        }
        while (true)
        {
            int finLinea;
            while ((finLinea = Buscar([.. pendiente], pendiente.Count, "\r\n"u8)) < 0)
                if (!await Asegurar(pendiente.Count + 1)) return salida.ToArray();
            var tam = Convert.ToInt32(Encoding.ASCII.GetString([.. pendiente.GetRange(0, finLinea)]).Split(';')[0].Trim(), 16);
            pendiente.RemoveRange(0, finLinea + 2);
            if (tam == 0) return salida.ToArray();
            if (!await Asegurar(tam + 2)) return salida.ToArray();
            salida.Write([.. pendiente.GetRange(0, tam)]);
            pendiente.RemoveRange(0, tam + 2);
        }
    }

    private static int Buscar(byte[] datos, int largo, ReadOnlySpan<byte> patron) =>
        datos.AsSpan(0, largo).IndexOf(patron);

    public async ValueTask DisposeAsync()
    {
        _fin.Cancel();
        _escucha.Stop();
        try { await _bucle; } catch (Exception) { }
        _fin.Dispose();
    }
}
