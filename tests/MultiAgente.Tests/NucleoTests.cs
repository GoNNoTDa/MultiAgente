using MultiAgente.Nucleo;

namespace MultiAgente.Tests;

public class RutasTests
{
    [Fact]
    public void Las_rutas_de_datos_cuelgan_de_la_raiz()
    {
        using var c = new CarpetaPrueba("rutas");
        var r = new Rutas(c.Ruta + Path.DirectorySeparatorChar);
        Assert.Equal(c.Ruta, r.Raiz);
        Assert.Equal(Path.Combine(c.Ruta, "datos", "multiagente.db"), r.BaseDatos);
        Assert.Equal(Path.Combine(c.Ruta, "ui"), r.Ui);
        r.CrearCarpetas();
        Assert.True(Directory.Exists(r.Logs));
        Assert.True(Directory.Exists(r.Cuentas));
        Assert.True(Directory.Exists(r.Sesiones));
    }

    [Fact]
    public void Dentro_de_la_carpeta_se_guarda_relativa_y_fuera_absoluta()
    {
        using var c = new CarpetaPrueba("rutas");
        var r = new Rutas(c.Ruta);
        var cuenta = Path.Combine(r.Cuentas, "claude-1");
        Assert.Equal(Path.Combine("datos", "cuentas", "claude-1"), r.ParaGuardar(cuenta));
        Assert.Equal(cuenta, r.Resolver(r.ParaGuardar(cuenta)));

        var proyecto = @"D:\proyectos\web";
        Assert.Equal(proyecto, r.ParaGuardar(proyecto));
        Assert.Equal(proyecto, r.Resolver(proyecto));

        // Una carpeta hermana que empieza igual no está «dentro»
        var hermana = c.Ruta + "_otra";
        Assert.Equal(hermana, r.ParaGuardar(hermana));
    }

    [Fact]
    public void Lo_guardado_relativo_sigue_valiendo_tras_mover_la_carpeta()
    {
        using var a = new CarpetaPrueba("origen");
        using var b = new CarpetaPrueba("destino");
        var guardada = new Rutas(a.Ruta).ParaGuardar(Path.Combine(a.Ruta, "datos", "cuentas", "codex-1"));
        Assert.Equal(Path.Combine(b.Ruta, "datos", "cuentas", "codex-1"), new Rutas(b.Ruta).Resolver(guardada));
    }
}

public class RegistroTests
{
    [Theory]
    [InlineData("clave sk-ant-api03-AbCdEfGhIjKlMnOpQrStUv", "sk-ant-api03")]
    [InlineData("OPENAI sk-proj-AbCdEfGhIjKlMnOpQrSt1234", "AbCdEfGhIjKlMnOpQrSt1234")]
    [InlineData("gemini AIzaSyA1234567890abcdefghijklmnopqrstu", "AIzaSyA1234567890")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.abcdefgh", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("{\"accessToken\":\"abcd1234efgh5678\",\"expiresAt\":1}", "abcd1234efgh5678")]
    [InlineData("refresh_token=zzzzyyyyxxxx1111", "zzzzyyyyxxxx1111")]
    [InlineData("google ya29.a0AfH6SMBxyz-123456", "a0AfH6SMBxyz")]
    [InlineData("groq gsk_AbCdEfGhIjKlMnOpQrStUvWx1234", "AbCdEfGhIjKlMnOpQrSt")]
    [InlineData("nvidia nvapi-AbCdEfGhIjKlMnOpQrStUv-1234", "AbCdEfGhIjKlMnOpQrSt")]
    [InlineData("fireworks fw_AbCdEfGhIjKlMnOpQr12", "AbCdEfGhIjKlMnOpQr")]
    [InlineData("grok xai-AbCdEfGhIjKlMnOpQrStUv12", "AbCdEfGhIjKlMnOpQrSt")]
    [InlineData("hf hf_AbCdEfGhIjKlMnOpQrStUv12", "AbCdEfGhIjKlMnOpQrSt")]
    [InlineData("openrouter sk-or-v1-0123456789abcdef0123456789abcdef", "0123456789abcdef0123")]
    [InlineData("gh ghp_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789", "AbCdEfGhIjKlMnOpQrSt")]
    [InlineData("oauth gho_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789", "AbCdEfGhIjKlMnOpQrSt")]
    [InlineData("pat github_pat_11ABCDEFG0123456789_abcdefghijklmnop", "11ABCDEFG0123456789")]
    [InlineData("gitlab glpat-AbCdEfGhIjKlMnOpQrSt", "AbCdEfGhIjKlMnOpQrSt")]
    [InlineData("slack xoxb-123456789012-abcdefghijkl", "123456789012")]
    [InlineData("slack xoxp-123456789012-abcdefghijkl", "abcdefghijkl")]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE listo", "IOSFODNN7EXAMPLE")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.SflKxwRJSMeKKF2QT4fwpM", "SflKxwRJSMeKKF2QT4fwpM")]
    [InlineData("{\"api_key\": \"abcd1234efgh5678\"}", "abcd1234efgh5678")]
    [InlineData("api-key=abcd1234efgh5678", "abcd1234efgh5678")]
    [InlineData("x-api-key: abcd1234efgh5678", "abcd1234efgh5678")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA")]
    [InlineData("Authorization: Basic dXNlcjpwYXNz", "dXNlcjpwYXNz")]
    public void Tapa_tokens_y_claves(string texto, string secreto)
    {
        var tapado = Registro.Tapar(texto);
        Assert.DoesNotContain(secreto, tapado);
        Assert.Contains("•••", tapado);
    }

    [Fact]
    public void No_toca_texto_normal()
    {
        const string texto = "Sesión 7a7cb388-83c9-47c5-b00a-4485131a0ca9 abierta con la cuenta Claude · Cuenta 1 en C:\\proyectos";
        Assert.Equal(texto, Registro.Tapar(texto));
    }

    [Theory]
    [InlineData("Basic configuration del servidor")]
    [InlineData("Abierto C:\\proyectos\\fw_utils\\hf_datos\\ghp_x")]
    [InlineData("modelo xai-grok y nvapi-corta, sk-corta, gsk_poco")]
    [InlineData("token caducado; pide otro")]
    [InlineData("Authorization: Bearer")]
    public void No_tapa_palabras_cortas_ni_rutas(string texto) => Assert.Equal(texto, Registro.Tapar(texto));

    [Fact]
    public void Autorizacion_bearer_tapa_solo_el_valor() =>
        Assert.Equal("Authorization: Bearer •••", Registro.Tapar("Authorization: Bearer abcdefghijklmnop1234"));

    [Fact]
    public void Escribe_en_el_fichero_del_dia_sin_secretos()
    {
        using var c = new CarpetaPrueba("registro");
        Registro.Iniciar(c.Ruta);
        Registro.Info("probando con sk-ant-api03-SECRETOSECRETOSECRETO");
        var fichero = Path.Combine(c.Ruta, $"{DateTime.Now:yyyyMMdd}.log");
        var texto = File.ReadAllText(fichero);
        Assert.Contains("INFO", texto);
        Assert.DoesNotContain("SECRETOSECRETO", texto);
    }
}

public class InstanciaUnicaTests
{
    [Fact]
    public void El_nombre_no_depende_de_mayusculas_ni_de_la_barra_final()
    {
        Assert.Equal(InstanciaUnica.Nombre(@"C:\MultiAgente"), InstanciaUnica.Nombre(@"c:\multiagente\"));
        Assert.NotEqual(InstanciaUnica.Nombre(@"C:\MultiAgente"), InstanciaUnica.Nombre(@"D:\MultiAgente"));
    }

    [Fact]
    public void La_segunda_copia_de_la_misma_carpeta_pide_mostrarse_y_otra_carpeta_convive()
    {
        using var a = new CarpetaPrueba("instancia");
        using var b = new CarpetaPrueba("instancia");
        using var primera = InstanciaUnica.Tomar(a.Ruta);
        Assert.NotNull(primera);
        using var pedido = new ManualResetEventSlim();
        primera!.MostrarPedido += pedido.Set;

        // La segunda se pide desde otro hilo: el mutex es reentrante en el mismo hilo
        InstanciaUnica? segunda = null;
        var hilo = new Thread(() => segunda = InstanciaUnica.Tomar(a.Ruta));
        hilo.Start();
        hilo.Join();
        Assert.Null(segunda);
        Assert.True(pedido.Wait(TimeSpan.FromSeconds(5)), "la primera no recibió la petición de mostrarse");

        InstanciaUnica? otraCarpeta = null;
        var hilo2 = new Thread(() => otraCarpeta = InstanciaUnica.Tomar(b.Ruta));
        hilo2.Start();
        hilo2.Join();
        Assert.NotNull(otraCarpeta);
        otraCarpeta!.Dispose();
    }

    [Fact]
    public void Con_cerrar_la_segunda_pide_el_cierre_a_la_primera()
    {
        using var a = new CarpetaPrueba("instancia");
        using var primera = InstanciaUnica.Tomar(a.Ruta);
        using var cierre = new ManualResetEventSlim();
        var mostrar = false;
        primera!.CierrePedido += cierre.Set;
        primera.MostrarPedido += () => mostrar = true;

        var hilo = new Thread(() => Assert.Null(InstanciaUnica.Tomar(a.Ruta, pedirCierre: true)));
        hilo.Start();
        hilo.Join();
        Assert.True(cierre.Wait(TimeSpan.FromSeconds(5)), "la primera no recibió la petición de cierre");
        Assert.False(mostrar);
    }
}

public class RequisitosTests
{
    [Fact]
    public void Encuentra_los_lanzadores_de_npm_con_su_version()
    {
        using var c = new CarpetaPrueba("requisitos");
        c.Fichero("claude-agent-acp.cmd", "@echo off");
        c.Fichero(@"node_modules\@agentclientprotocol\claude-agent-acp\package.json", """{ "name": "x", "version": "0.94.0" }""");
        c.Fichero("gemini.cmd", "@echo off");
        c.Fichero(@"node_modules\@google\gemini-cli\package.json", """{ "version": "0.37.0" }""");

        var r = Requisitos.Comprobar(c.Ruta).ToDictionary(x => x.Id);
        Assert.True(r["claude-agent-acp"].Encontrado);
        Assert.Equal("0.94.0", r["claude-agent-acp"].Version);
        Assert.Equal("0.37.0", r["gemini"].Version);
        Assert.False(r["codex"].Encontrado);
        Assert.False(r["node"].Encontrado);
        Assert.False(Requisitos.ProveedorListo(r.Values.ToList(), "Gemini")); // falta Node.js
    }

    [Fact]
    public void Un_proveedor_esta_listo_con_node_su_cli_y_su_adaptador()
    {
        using var c = new CarpetaPrueba("requisitos");
        foreach (var o in new[] { "node.exe", "codex.cmd", "codex-acp.cmd", "gemini.cmd" }) c.Fichero(o);
        var r = Requisitos.Comprobar($"\"{c.Ruta}\";C:\\no\\existe").ToList();
        Assert.True(Requisitos.ProveedorListo(r, "Codex"));
        Assert.True(Requisitos.ProveedorListo(r, "Gemini"));
        Assert.False(Requisitos.ProveedorListo(r, "Claude"));
    }
}
