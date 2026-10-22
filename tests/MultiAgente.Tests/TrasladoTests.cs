using System.Diagnostics;
using System.IO.Compression;
using MultiAgente.Datos;
using MultiAgente.Nucleo;

namespace MultiAgente.Tests;

/// <summary>«Llevar a otro PC»: el .zip lleva programa y datos, nunca credenciales, cachés, registros ni uniones.</summary>
public class TrasladoTests
{
    private static void Escribir(string ruta, string texto = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        File.WriteAllText(ruta, texto);
    }

    [Fact]
    public async Task Lleva_programa_y_datos_sin_credenciales_ni_caches_y_con_la_base_coherente()
    {
        using var c = new CarpetaPrueba("traslado");
        var rutas = new Rutas(Path.Combine(c.Ruta, "MultiAgente"));
        rutas.CrearCarpetas();
        var destino = Path.Combine(c.Ruta, "usb");
        Directory.CreateDirectory(destino);

        Escribir(Path.Combine(rutas.Raiz, "MultiAgente.exe"));
        Escribir(Path.Combine(rutas.Ui, "index.html"));
        Escribir(rutas.Boveda);
        Escribir(Path.ChangeExtension(rutas.Boveda, ".clave"));
        Escribir(rutas.Idioma, "ca");
        Escribir(Path.Combine(rutas.Logs, "hoy.log"));
        Escribir(Path.Combine(rutas.WebView, "EBWebView", "cache.bin"));
        Escribir(Path.Combine(rutas.Cuentas, "Claude A", ".credentials.json"), "token");
        Escribir(Path.Combine(rutas.Cuentas, "Claude A", "settings.json"), "{}");
        Escribir(Path.Combine(rutas.Cuentas, "Claude A", "sessions", "123.abcdef.key"), "clave de proceso");
        Escribir(Path.Combine(rutas.Cuentas, "Codex B", "auth.json"), "token");
        Escribir(Path.Combine(rutas.Cuentas, "Codex B", "config.toml"));
        Escribir(Path.Combine(rutas.Cuentas, "Gemini C", ".gemini", "oauth_creds.json"), "token");
        Escribir(Path.Combine(rutas.Cuentas, "Gemini C", ".gemini", "settings.json"));
        Escribir(Path.Combine(rutas.Comun, "Claude", "projects", "sesion.jsonl"), "conversación nativa");
        // Unión como las de las conversaciones compartidas: no se copia (se rehace al arrancar)
        var union = Path.Combine(rutas.Cuentas, "Claude A", "projects");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", "mklink", "/J", union, Path.Combine(rutas.Comun, "Claude", "projects") }, CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        Assert.True(Directory.Exists(union));

        using var bd = BaseDatos.Abrir(rutas.BaseDatos);
        new Repositorio(bd).GuardarSesion(new Sesion { Id = "s1", Titulo = "Facturas", Carpeta = @"C:\web" });
        var hilo = Path.Combine(rutas.Sesiones, "s1", "hilo.jsonl");
        Escribir(hilo, "{\"tipo\":\"usuario\"}\n");

        ResultadoTraslado r;
        // La conversación abierta para escribir mientras se crea el .zip (como con una sesión trabajando)
        using (new FileStream(hilo, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            r = await Traslado.Crear(rutas, destino, bd.CopiarA);

        Assert.StartsWith(Path.Combine(destino, "MultiAgente-"), r.Zip);
        Assert.Empty(r.Omitidos);
        Assert.Empty(Directory.GetFiles(destino, "*.parcial"));
        using var zip = ZipFile.OpenRead(r.Zip);
        var nombres = zip.Entries.Select(e => e.FullName).Order().ToList();
        Assert.Equal(
        [
            "MultiAgente/datos/comun/Claude/projects/sesion.jsonl",
            "MultiAgente/datos/cuentas/Claude A/settings.json",
            "MultiAgente/datos/cuentas/Codex B/config.toml",
            "MultiAgente/datos/cuentas/Gemini C/.gemini/settings.json",
            "MultiAgente/datos/idioma.txt",
            "MultiAgente/datos/multiagente.db",
            "MultiAgente/datos/sesiones/s1/hilo.jsonl",
            "MultiAgente/MultiAgente.exe",
            "MultiAgente/ui/index.html",
        ], nombres, StringComparer.Ordinal);

        // La base del .zip se abre en el otro PC con lo que había
        var otroPc = Path.Combine(c.Ruta, "otro-pc");
        zip.ExtractToDirectory(otroPc);
        using var copia = BaseDatos.Abrir(Path.Combine(otroPc, "MultiAgente", "datos", "multiagente.db"));
        Assert.Equal("Facturas", new Repositorio(copia).Sesion("s1")!.Titulo);
    }

    [Fact]
    public async Task No_deja_el_fichero_dentro_de_la_propia_carpeta_ni_en_una_que_no_existe()
    {
        using var c = new CarpetaPrueba("traslado-mal");
        var rutas = new Rutas(Path.Combine(c.Ruta, "MultiAgente"));
        rutas.CrearCarpetas();
        await Assert.ThrowsAsync<ExcepcionUsuario>(() => Traslado.Crear(rutas, rutas.Datos, _ => { }));
        await Assert.ThrowsAsync<ExcepcionUsuario>(() => Traslado.Crear(rutas, Path.Combine(c.Ruta, "no-existe"), _ => { }));
    }
}
