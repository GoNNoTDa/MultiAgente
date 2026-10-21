using MultiAgente.Nucleo;

namespace MultiAgente.Tests;

/// <summary>
/// Con los agentes reales instalados (solo con MULTIAGENTE_PRUEBA_REAL=1). Usan una carpeta de cuenta vacía y temporal:
/// no gastan nada y no tocan la configuración del usuario. Comprueban el circuito ACP real hasta el aviso de «falta
/// iniciar sesión».
/// </summary>
public class RealesTests
{
    private static bool Activas => Environment.GetEnvironmentVariable("MULTIAGENTE_PRUEBA_REAL") == "1";

    private static SesionAgente Crear(CarpetaPrueba c, Proveedor p)
    {
        var orden = Proveedores.OrdenAcp(p) ?? throw new InvalidOperationException($"{p.Id} no está instalado");
        var trabajo = Path.Combine(c.Ruta, "proyecto");
        var cuenta = Path.Combine(c.Ruta, "cuenta");
        Directory.CreateDirectory(trabajo);
        Directory.CreateDirectory(cuenta);
        return new SesionAgente(new Sesion { Carpeta = trabajo },
            new Cuenta { Alias = "Vacía", Proveedor = p.Id, Tipo = TipoCuenta.Login, Carpeta = cuenta },
            p, orden, Proveedores.Entorno(p, cuenta, null), new Hilo(Path.Combine(c.Ruta, "sesion")), null);
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("Codex")]
    [InlineData("Gemini")]
    public async Task Sin_sesion_iniciada_lo_dice_en_lenguaje_llano(string proveedor)
    {
        if (!Activas) return;
        using var c = new CarpetaPrueba("real");
        await using var a = Crear(c, Proveedores.Buscar(proveedor)!);
        string? mensaje = null;
        try
        {
            await a.Arrancar();
            // Claude crea la sesión sin login: el aviso llega al mandar el primer mensaje
            a.Enviar("hola");
            var limite = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < limite && !a.Hilo.Entradas.Any(e => e.Tipo is "aviso" or "agente"))
                await Task.Delay(200);
            var e = a.Hilo.Entradas.LastOrDefault(x => x.Tipo is "aviso" or "agente");
            mensaje = e?.Texto;
            Assert.Equal("aviso", e?.Tipo);
        }
        catch (ExcepcionUsuario ex) { mensaje = ex.Message; }
        Assert.True(mensaje?.Contains("no tiene la sesión iniciada") == true, $"{proveedor}: {mensaje}");
    }
}
