using System.Text.Json.Nodes;
using MultiAgente.Nucleo;
using MultiAgente.Nucleo.Acp;

namespace MultiAgente.Tests;

/// <summary>Sesión viva con el agente falso (ACP de verdad, sin gastar): <c>tests\MultiAgente.FalsoAgente</c>.</summary>
public class AgenteTests
{
    private static string FalsoAgente()
    {
        // tests\MultiAgente.Tests\bin\<Config>\net10.0-windows\ → tests\MultiAgente.FalsoAgente\bin\<Config>\net10.0\
        var aqui = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var config = aqui.Parent!.Name;
        var dll = Path.GetFullPath(Path.Combine(aqui.FullName, "..", "..", "..", "..", "MultiAgente.FalsoAgente", "bin", config, "net10.0", "MultiAgente.FalsoAgente.dll"));
        Assert.True(File.Exists(dll), $"Falta el agente falso: {dll}");
        return dll;
    }

    private sealed class Montaje : IAsyncDisposable
    {
        public required CarpetaPrueba Carpeta { get; init; }
        public required SesionAgente Agente { get; set; }
        public string RegistroFalso => Path.Combine(Carpeta.Ruta, "falso.log");
        public string LeerRegistro() => File.Exists(RegistroFalso) ? File.ReadAllText(RegistroFalso) : "";
        public async ValueTask DisposeAsync()
        {
            await Agente.DisposeAsync();
            Carpeta.Dispose();
        }
    }

    private static SesionAgente Crear(CarpetaPrueba c, Hilo? hilo = null, string? nativa = null, TipoCuenta tipo = TipoCuenta.Login,
                                      string? clave = null, Dictionary<string, string?>? extra = null, IReadOnlyList<ServidorMcp>? mcp = null)
    {
        var trabajo = Path.Combine(c.Ruta, "proyecto");
        Directory.CreateDirectory(trabajo);
        var cuenta = new Cuenta { Alias = "Prueba", Proveedor = "Codex", Tipo = tipo, Carpeta = Path.Combine(c.Ruta, "cuenta") };
        var entorno = Proveedores.Entorno(Proveedores.Codex, cuenta.Carpeta, clave);
        entorno["FALSO_REGISTRO"] = Path.Combine(c.Ruta, "falso.log");
        foreach (var (k, v) in extra ?? []) entorno[k] = v;
        var sesion = new Sesion { Carpeta = trabajo };
        return new SesionAgente(sesion, cuenta, Proveedores.Codex, new OrdenAgente("dotnet", [FalsoAgente()]), entorno,
                                hilo ?? new Hilo(Path.Combine(c.Ruta, "sesion")), nativa, mcp);
    }

    private static async Task<Montaje> Arrancado(string tema, Func<CarpetaPrueba, SesionAgente>? crear = null)
    {
        var c = new CarpetaPrueba(tema);
        var a = (crear ?? (x => Crear(x)))(c);
        await a.Arrancar();
        return new Montaje { Carpeta = c, Agente = a };
    }

    private static async Task Esperar(Func<bool> condicion, string que, int segundos = 20)
    {
        var limite = DateTime.UtcNow.AddSeconds(segundos);
        while (!condicion())
        {
            if (DateTime.UtcNow > limite) Assert.Fail($"No llegó a tiempo: {que}");
            await Task.Delay(50);
        }
    }

    private static Task EsperarTurno(SesionAgente a, int entradasMinimas) =>
        Esperar(() => a.Estado == EstadoSesion.Esperando && a.Cola.Count == 0 && a.Hilo.Cuantas >= entradasMinimas, "fin del turno");

    private static string? TextoAgente(SesionAgente a) => a.Hilo.Entradas.LastOrDefault(e => e.Tipo == "agente")?.Texto;

    [Fact]
    public async Task Arranca_responde_y_guarda_la_conversacion()
    {
        await using var m = await Arrancado("agente");
        var a = m.Agente;
        Assert.Equal(EstadoSesion.Esperando, a.Estado);
        Assert.StartsWith("falso-", a.SesionNativa);
        Assert.True(a.AdmiteImagenes);
        Assert.Contains(a.Opciones, o => o?["category"]?.GetValue<string>() == "mode");
        Assert.Contains(a.Opciones, o => o?["category"]?.GetValue<string>() == "model");
        await Esperar(() => a.Comandos.Count == 1, "órdenes del agente");

        var cambios = new List<Entrada>();
        a.EntradaCambiada += (_, e) => { lock (cambios) cambios.Add(e); };
        a.Enviar("hola agente");
        await EsperarTurno(a, 3);

        Assert.Equal("Recibido: hola agente", TextoAgente(a));
        Assert.Contains(a.Hilo.Entradas, e => e.Tipo == "pensamiento");
        Assert.Contains(a.Hilo.Entradas, e => e is { Tipo: "usuario", Texto: "hola agente", Cuenta: "Prueba" });
        lock (cambios) Assert.True(cambios.Count(e => e.Tipo == "agente") > 1, "el texto debería llegar a trozos");

        var releido = new Hilo(a.Hilo.Carpeta);
        Assert.Equal(a.Hilo.Cuantas, releido.Cuantas);
        Assert.Equal("Recibido: hola agente", releido.Entradas.Last(e => e.Tipo == "agente").Texto);
    }

    [Fact]
    public async Task Quita_el_ruido_de_Codex_y_explica_los_errores_del_proveedor()
    {
        await using var m = await Arrancado("ruido");
        var a = m.Agente;
        a.Enviar("ruido");
        await EsperarTurno(a, 4);
        a.Enviar("ruido otra vez");
        await EsperarTurno(a, 7);

        Assert.DoesNotContain(a.Hilo.Entradas, e => e.Texto?.Contains("Model metadata") == true);
        Assert.DoesNotContain(a.Hilo.Entradas, e => e.Tipo == "herramienta");
        Assert.DoesNotContain(a.Hilo.Entradas, e => e.Tipo == "agente" && string.IsNullOrWhiteSpace(e.Texto));
        var mcp = Assert.Single(a.Hilo.Entradas, e => e.Tipo == "aviso"); // una vez por sesión, no en cada turno
        Assert.Contains("«meta»", mcp.Texto);
        Assert.Contains("pide iniciar sesión", mcp.Texto);
        Assert.Equal("Recibido: ruido otra vez", TextoAgente(a));

        a.Enviar("ruido 404");
        await Esperar(() => a.Estado == EstadoSesion.Esperando && a.Hilo.Entradas.Count(e => e.Tipo == "usuario") == 3 && a.Hilo.Entradas.Last().Tipo == "aviso", "error explicado");
        var error = a.Hilo.Entradas.Last();
        Assert.Equal("error", error.Nivel);
        Assert.Contains("no admite", error.Texto);
    }

    [Fact]
    public async Task Manda_imagenes_y_ficheros_adjuntos()
    {
        await using var m = await Arrancado("agente");
        var png = m.Carpeta.Fichero("captura.png", "no es un png de verdad");
        var txt = m.Carpeta.Fichero("notas.txt", "texto");
        m.Agente.Enviar("mira esto", [new Adjunto(png, "captura.png", "image/png", true), new Adjunto(txt, "notas.txt", "text/plain", false)]);
        await EsperarTurno(m.Agente, 3);
        Assert.Equal("Recibido: mira esto · imágenes: 1 · ficheros: 1", TextoAgente(m.Agente));
        Assert.Equal(2, m.Agente.Hilo.Entradas.First(e => e.Tipo == "usuario").Adjuntos!.Count);
    }

    [Fact]
    public async Task Pide_permiso_y_sigue_con_la_respuesta_del_usuario()
    {
        await using var m = await Arrancado("agente");
        var a = m.Agente;
        a.Enviar("necesito permiso");
        await Esperar(() => a.PermisoPendiente is not null, "petición de permiso");
        Assert.Equal(EstadoSesion.PidePermiso, a.Estado);
        var p = a.PermisoPendiente!;
        Assert.Equal("Escribir prueba.txt", p.Titulo);
        Assert.Equal(2, p.Opciones.Count);

        a.ResponderPermiso(p.Id, "si");
        await EsperarTurno(a, 4);
        Assert.Null(a.PermisoPendiente);
        var h = a.Hilo.Entradas.Single(e => e.Tipo == "herramienta");
        Assert.Equal("completed", h.Estado);
        Assert.Equal("edit", h.Clase);
        Assert.Equal("diff", h.Contenido?[0]?["type"]?.GetValue<string>());
        Assert.Equal("Permitido: fichero escrito.", TextoAgente(a));
    }

    [Fact]
    public async Task Rechazar_el_permiso_lo_dice_el_agente()
    {
        await using var m = await Arrancado("agente");
        m.Agente.Enviar("permiso");
        await Esperar(() => m.Agente.PermisoPendiente is not null, "petición de permiso");
        m.Agente.ResponderPermiso(m.Agente.PermisoPendiente!.Id, "no");
        await EsperarTurno(m.Agente, 4);
        Assert.Equal("failed", m.Agente.Hilo.Entradas.Single(e => e.Tipo == "herramienta").Estado);
        Assert.Equal("Rechazado.", TextoAgente(m.Agente));
    }

    [Fact]
    public async Task Interrumpir_corta_el_turno_y_los_mensajes_en_cola_siguen_despues()
    {
        await using var m = await Arrancado("agente");
        var a = m.Agente;
        a.Enviar("escribe lento");
        await Esperar(() => a.Estado == EstadoSesion.Trabajando, "trabajando");
        a.Enviar("segundo mensaje");
        Assert.Equal(["segundo mensaje"], a.Cola);

        await a.Interrumpir();
        await Esperar(() => TextoAgente(a) == "Recibido: segundo mensaje" && a.Estado == EstadoSesion.Esperando, "el mensaje en cola");
        Assert.Contains(a.Hilo.Entradas, e => e is { Tipo: "aviso", Nivel: "interrumpido" });
        Assert.Contains("cancelar", m.LeerRegistro());
        Assert.Empty(a.Cola);
    }

    [Fact]
    public async Task Al_reabrir_recupera_la_sesion_del_agente_sin_duplicar_el_historial()
    {
        var c = new CarpetaPrueba("agente");
        var hilo = new Hilo(Path.Combine(c.Ruta, "sesion"));
        string nativa;
        await using (var a = Crear(c, hilo))
        {
            await a.Arrancar();
            a.Enviar("primero");
            await EsperarTurno(a, 3);
            nativa = a.SesionNativa!;
        }
        var antes = new Hilo(hilo.Carpeta).Cuantas;
        await using var m = new Montaje { Carpeta = c, Agente = Crear(c, new Hilo(hilo.Carpeta), nativa) };
        await m.Agente.Arrancar();
        Assert.Equal(nativa, m.Agente.SesionNativa);
        Assert.Contains($"cargada {nativa}", m.LeerRegistro());
        Assert.Equal(antes, m.Agente.Hilo.Cuantas); // el historial que reenvía el agente no se duplica

        m.Agente.Enviar("segundo");
        await EsperarTurno(m.Agente, antes + 3);
        Assert.Equal("Recibido: segundo", TextoAgente(m.Agente)); // sin traspaso: el agente ya tenía el contexto
    }

    [Fact]
    public async Task Si_la_sesion_del_agente_ya_no_existe_abre_otra_con_traspaso()
    {
        var c = new CarpetaPrueba("agente");
        var hilo = new Hilo(Path.Combine(c.Ruta, "sesion"));
        hilo.Guardar(new Entrada { Tipo = "usuario", Texto = "haz la tarea A" });
        hilo.Guardar(new Entrada { Tipo = "agente", Texto = "Hecha la tarea A" });
        var creada = (string?)null;
        await using var m = new Montaje { Carpeta = c, Agente = Crear(c, hilo, "inexistente") };
        m.Agente.SesionNativaCreada += (_, id) => creada = id;
        await m.Agente.Arrancar();
        Assert.StartsWith("falso-", m.Agente.SesionNativa);
        Assert.Equal(m.Agente.SesionNativa, creada);

        m.Agente.Enviar("sigue");
        await EsperarTurno(m.Agente, 5);
        Assert.Equal("Recibido: sigue · con traspaso", TextoAgente(m.Agente));
    }

    [Fact]
    public async Task Sin_sesion_iniciada_lo_dice_en_lenguaje_llano()
    {
        using var c = new CarpetaPrueba("agente");
        await using var a = Crear(c, extra: new() { ["FALSO_SIN_AUTH"] = "1" });
        var ex = await Assert.ThrowsAsync<ExcepcionUsuario>(() => a.Arrancar());
        Assert.True(ex.Message.Contains("no tiene la sesión iniciada"), ex.Message);
        Assert.Equal(EstadoSesion.Error, a.Estado);
    }

    [Fact]
    public async Task Cuenta_de_api_se_autentica_con_su_clave()
    {
        await using (var m = await Arrancado("agente", c => Crear(c, tipo: TipoCuenta.Api, clave: "clave-buena",
                         extra: new() { ["FALSO_CLAVE_ESPERADA"] = "clave-buena" })))
            Assert.Equal(EstadoSesion.Esperando, m.Agente.Estado);

        using var c2 = new CarpetaPrueba("agente");
        await using var mala = Crear(c2, tipo: TipoCuenta.Api, clave: "otra", extra: new() { ["FALSO_CLAVE_ESPERADA"] = "clave-buena" });
        var ex = await Assert.ThrowsAsync<ExcepcionUsuario>(() => mala.Arrancar());
        Assert.True(ex.Message.Contains("clave de API"), ex.Message);
    }

    [Fact]
    public async Task Lo_del_subagente_queda_en_su_herramienta_y_no_en_la_respuesta()
    {
        await using var m = await Arrancado("agente");
        var a = m.Agente;
        Assert.Null(a.TurnoDesde);
        a.Enviar("lanza un subagente");
        await Esperar(() => a.TurnoDesde is not null || a.Estado == EstadoSesion.Esperando && a.Hilo.Cuantas >= 3, "turno");
        await EsperarTurno(a, 4);
        Assert.Null(a.TurnoDesde); // al terminar el turno ya no cuenta

        var entradas = a.Hilo.Entradas;
        var sub = Assert.Single(entradas, e => e.Id == "sub1");
        Assert.Equal(("Agent", "completed", "Miro las pruebas."), (sub.Herramienta, sub.Estado, sub.Texto));
        var hija = Assert.Single(entradas, e => e.Id == "sub1-leer");
        Assert.Equal(("sub1", "Read"), (hija.Padre, hija.Herramienta));
        Assert.Equal("Recibido: lanza un subagente", TextoAgente(a)); // sin el texto del subagente mezclado
    }

    [Fact]
    public async Task La_compactacion_queda_como_hito_con_sus_tokens()
    {
        await using var m = await Arrancado("agente");
        m.Agente.Enviar("compacta y sigue");
        await EsperarTurno(m.Agente, 3);
        var c = Assert.Single(m.Agente.Hilo.Entradas, e => e.Tipo == "compactacion");
        Assert.Equal("completed", c.Estado);
        Assert.Equal((182000, 31000, "automatic"),
                     (c.Datos!["preTokens"]!.GetValue<int>(), c.Datos["postTokens"]!.GetValue<int>(), c.Datos["trigger"]!.GetValue<string>()));
        Assert.DoesNotContain(m.Agente.Hilo.Entradas, e => e.Tipo == "herramienta");
    }

    [Fact]
    public async Task El_limite_de_uso_queda_como_aviso_de_limite()
    {
        await using var m = await Arrancado("agente");
        m.Agente.Enviar("llega al limite");
        await EsperarTurno(m.Agente, 2);
        var aviso = m.Agente.Hilo.Entradas.Last(e => e.Tipo == "aviso");
        Assert.Equal("limite", aviso.Nivel);
        Assert.Contains("límite de uso", aviso.Texto);
    }

    [Fact]
    public async Task Cambia_modo_y_modelo_y_lo_recuerda_en_la_sesion()
    {
        await using var m = await Arrancado("agente");
        var a = m.Agente;
        await a.CambiarOpcion("model", "listo");
        Assert.Contains("modelo listo", m.LeerRegistro());
        Assert.Equal("listo", a.Sesion.Modelo);

        await a.CambiarOpcion("mode", "acceptEdits");
        Assert.Equal("acceptEdits", a.Sesion.ModoPermisos);
        await Esperar(() => a.Opciones.First(o => o?["id"]?.GetValue<string>() == "mode")!["currentValue"]?.ToString() == "acceptEdits", "modo nuevo");
    }

    [Fact]
    public async Task Pasa_los_servidores_mcp_al_crear_la_sesion_y_manda_el_plan()
    {
        var mcp = new ServidorMcp("prueba", new JsonObject { ["name"] = "prueba", ["command"] = "npx", ["args"] = new JsonArray("-y", "algo"), ["env"] = new JsonArray() });
        await using var m = await Arrancado("agente", c => Crear(c, mcp: [mcp]));
        Assert.Contains("mcp=1", m.LeerRegistro());

        m.Agente.Enviar("haz un plan");
        await EsperarTurno(m.Agente, 4);
        var plan = m.Agente.Hilo.Entradas.Single(e => e.Tipo == "plan");
        Assert.Equal(3, (plan.Pasos as JsonArray)!.Count);
    }
}
