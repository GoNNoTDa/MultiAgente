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
    // Cualquier programa del equipo puede conectarse a 127.0.0.1: límites para que nadie lo deje sin memoria
    private const int MaxCuerpo = 64 * 1024 * 1024;
    private const int MaxConexiones = 32;

    private readonly TcpListener _escucha = new(IPAddress.Loopback, 0);
    private readonly Func<PeticionHttp, RespuestaHttp, CancellationToken, Task> _atender;
    private readonly Func<IReadOnlyDictionary<string, string>, bool>? _admitir;
    private readonly SemaphoreSlim _conexiones = new(MaxConexiones);
    private readonly CancellationTokenSource _fin = new();
    private readonly Task _bucle;

    /// <summary>Lo que se espera a que llegue la petición entera (la respuesta puede tardar lo que haga falta).</summary>
    public TimeSpan PlazoLectura { get; init; } = TimeSpan.FromSeconds(30);

    /// <param name="atender">Atiende cada petición.</param>
    /// <param name="admitir">Si se da, decide con las cabeceras, antes de leer el cuerpo; si no admite, 401.</param>
    public ServidorHttp(Func<PeticionHttp, RespuestaHttp, CancellationToken, Task> atender,
                        Func<IReadOnlyDictionary<string, string>, bool>? admitir = null)
    {
        _atender = atender;
        _admitir = admitir;
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
            if (!_conexiones.Wait(0)) { cliente.Dispose(); continue; } // demasiadas a la vez: se cierra sin leerla
            _ = Task.Run(async () =>
            {
                try { await Conexion(cliente); }
                finally { _conexiones.Release(); }
            });
        }
    }

    private async Task Conexion(TcpClient cliente)
    {
        using (cliente)
        {
            var red = cliente.GetStream();
            try
            {
                PeticionHttp? peticion;
                using (var plazo = CancellationTokenSource.CreateLinkedTokenSource(_fin.Token))
                {
                    plazo.CancelAfter(PlazoLectura);
                    peticion = await Leer(red, _admitir, plazo.Token);
                }
                if (peticion is null) return;
                var respuesta = new RespuestaHttp(red);
                if (peticion == Rechazada)
                {
                    await respuesta.Enviar(401, "application/json",
                        "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"Pase no válido.\"}}", _fin.Token);
                    return;
                }
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

    /// <summary>Marca de la petición que no se admite (se responde 401 sin leer su cuerpo).</summary>
    private static readonly PeticionHttp Rechazada = new("", "", new Dictionary<string, string>(), []);

    /// <summary>Lee la petición; null si está mal formada o es demasiado grande.</summary>
    private static async Task<PeticionHttp?> Leer(NetworkStream red, Func<IReadOnlyDictionary<string, string>, bool>? admitir, CancellationToken ct)
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
        if (admitir is not null && !admitir(cabeceras)) return Rechazada;
        var resto = new MemoryStream();
        resto.Write(todo, fin + 4, todo.Length - fin - 4);

        byte[] cuerpo;
        if (cabeceras.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out var largo))
        {
            if (largo is < 0 or > MaxCuerpo) return null;
            while (resto.Length < largo)
            {
                var n = await red.ReadAsync(trozo, ct);
                if (n == 0) break;
                resto.Write(trozo, 0, n);
            }
            cuerpo = resto.ToArray()[..(int)Math.Min(largo, resto.Length)];
        }
        else if (cabeceras.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            if (await PorTrozos(red, resto.ToArray(), trozo, ct) is not { } trozos) return null;
            cuerpo = trozos;
        }
        else cuerpo = [];
        return new PeticionHttp(primera[0], primera[1], cabeceras, cuerpo);
    }

    /// <summary>Cuerpo «chunked»; null si está mal formado o pasa de <see cref="MaxCuerpo"/>.</summary>
    private static async Task<byte[]?> PorTrozos(NetworkStream red, byte[] leido, byte[] trozo, CancellationToken ct)
    {
        var salida = new MemoryStream();
        var pendiente = new MemoryStream();
        pendiente.Write(leido);
        var pos = 0; // lo ya consumido de «pendiente»
        async Task<bool> Asegurar(int cuantos)
        {
            while (pendiente.Length - pos < cuantos)
            {
                var n = await red.ReadAsync(trozo, ct);
                if (n == 0) return false;
                pendiente.Write(trozo, 0, n);
            }
            return true;
        }
        while (true)
        {
            int finLinea;
            while ((finLinea = pendiente.GetBuffer().AsSpan(pos, (int)pendiente.Length - pos).IndexOf("\r\n"u8)) < 0)
            {
                if (pendiente.Length - pos > 1024) return null; // la línea del tamaño es corta
                if (!await Asegurar((int)pendiente.Length - pos + 1)) return salida.ToArray();
            }
            var linea = Encoding.ASCII.GetString(pendiente.GetBuffer(), pos, finLinea).Split(';')[0].Trim();
            if (!int.TryParse(linea, System.Globalization.NumberStyles.HexNumber, null, out var tam) || tam < 0) return null;
            pos += finLinea + 2;
            if (tam == 0) return salida.ToArray();
            if (salida.Length + tam > MaxCuerpo) return null;
            if (!await Asegurar(tam + 2)) return salida.ToArray();
            salida.Write(pendiente.GetBuffer(), pos, tam);
            pos += tam + 2;
            // Se descarta lo consumido para no acumular todo el cuerpo dos veces
            var queda = pendiente.GetBuffer().AsSpan(pos, (int)pendiente.Length - pos).ToArray();
            pendiente.SetLength(0);
            pendiente.Write(queda);
            pos = 0;
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
