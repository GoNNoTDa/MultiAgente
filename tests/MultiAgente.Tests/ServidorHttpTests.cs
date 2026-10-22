using System.Net.Sockets;
using System.Text;
using MultiAgente.Nucleo;

namespace MultiAgente.Tests;

/// <summary>El servidor local escucha en 127.0.0.1, donde puede conectarse cualquier programa del equipo: no debe colgarse ni llenarse.</summary>
public class ServidorHttpTests
{
    private static ServidorHttp Servidor(List<string> atendidas, Func<IReadOnlyDictionary<string, string>, bool>? admitir = null) =>
        new(async (p, r, ct) =>
        {
            lock (atendidas) atendidas.Add(p.Texto);
            await r.Enviar(200, "text/plain", "ok:" + p.Texto, ct);
        }, admitir) { PlazoLectura = TimeSpan.FromSeconds(1) };

    /// <summary>Manda los bytes tal cual y lee hasta que el servidor cierra («» si cierra sin responder).</summary>
    private static async Task<string> Crudo(ServidorHttp s, string peticion, TimeSpan? espera = null)
    {
        using var c = new TcpClient();
        await c.ConnectAsync("127.0.0.1", s.Puerto);
        var red = c.GetStream();
        await red.WriteAsync(Encoding.ASCII.GetBytes(peticion));
        using var limite = new CancellationTokenSource(espera ?? TimeSpan.FromSeconds(10));
        var leido = new MemoryStream();
        var b = new byte[4096];
        try
        {
            int n;
            while ((n = await red.ReadAsync(b, limite.Token)) > 0) leido.Write(b, 0, n);
        }
        catch (IOException) { } // el servidor cortó la conexión
        return Encoding.UTF8.GetString(leido.ToArray());
    }

    [Fact]
    public async Task Una_peticion_que_no_termina_de_llegar_se_corta()
    {
        var atendidas = new List<string>();
        await using var s = Servidor(atendidas);
        var inicio = DateTime.UtcNow;
        Assert.Equal("", await Crudo(s, "POST / HTTP/1.1\r\nContent-Length: 10\r\n\r\nabc"));
        Assert.True(DateTime.UtcNow - inicio < TimeSpan.FromSeconds(8), "no cortó a tiempo");
        Assert.Empty(atendidas);
    }

    [Fact]
    public async Task Sin_pase_contesta_401_sin_esperar_al_cuerpo()
    {
        var atendidas = new List<string>();
        await using var s = Servidor(atendidas, c => c.GetValueOrDefault("x-api-key") == "bueno");
        // Anuncia 50 MB que nunca manda: si leyera el cuerpo, se quedaría esperando hasta el plazo
        var r = await Crudo(s, "POST / HTTP/1.1\r\nx-api-key: malo\r\nContent-Length: 52428800\r\n\r\n", TimeSpan.FromSeconds(3));
        Assert.StartsWith("HTTP/1.1 401", r);
        Assert.Contains("ok:hola", await Crudo(s, "POST / HTTP/1.1\r\nx-api-key: bueno\r\nContent-Length: 4\r\n\r\nhola"));
        Assert.Equal(["hola"], atendidas);
    }

    [Fact]
    public async Task Cuerpo_por_trozos_bien_formado_mal_formado_o_enorme()
    {
        var atendidas = new List<string>();
        await using var s = Servidor(atendidas);
        const string cabeceras = "POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n";
        Assert.Contains("ok:hola mundo", await Crudo(s, cabeceras + "5\r\nhola \r\n5;x=1\r\nmundo\r\n0\r\n\r\n"));
        Assert.Equal("", await Crudo(s, cabeceras + "zz\r\nhola\r\n0\r\n\r\n"));        // tamaño que no es hexadecimal
        Assert.Equal("", await Crudo(s, cabeceras + "7FFFFFFF\r\nhola\r\n0\r\n\r\n"));  // más de 64 MB
        Assert.Equal("", await Crudo(s, cabeceras + new string('1', 2000)));            // línea de tamaño sin fin
        Assert.Equal(["hola mundo"], atendidas);
    }

    [Fact]
    public async Task Con_demasiadas_conexiones_a_la_vez_cierra_las_que_sobran()
    {
        await using var s = new ServidorHttp(async (p, r, ct) => await r.Enviar(200, "text/plain", "ok", ct)) { PlazoLectura = TimeSpan.FromSeconds(20) };
        var abiertas = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 32; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync("127.0.0.1", s.Puerto);
                abiertas.Add(c);
            }
            await Task.Delay(300); // que el servidor las haya aceptado todas
            var inicio = DateTime.UtcNow;
            Assert.Equal("", await Crudo(s, "GET / HTTP/1.1\r\n\r\n"));
            Assert.True(DateTime.UtcNow - inicio < TimeSpan.FromSeconds(5), "la conexión 33 no se cerró en seguida");

            foreach (var c in abiertas) c.Dispose(); // al cerrarse, vuelve a atender
            abiertas.Clear();
            var limite = DateTime.UtcNow.AddSeconds(10);
            string r;
            while (!(r = await Crudo(s, "GET / HTTP/1.1\r\n\r\n")).Contains("ok"))
                Assert.True(DateTime.UtcNow < limite, "no volvió a atender");
        }
        finally { foreach (var c in abiertas) c.Dispose(); }
    }
}
