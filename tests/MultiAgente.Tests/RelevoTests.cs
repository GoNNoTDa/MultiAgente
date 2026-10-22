using System.Collections.Concurrent;
using System.Text.Json;
using MultiAgente.Datos;
using MultiAgente.Nucleo;
using MultiAgente.Nucleo.Acp;

namespace MultiAgente.Tests;

/// <summary>
/// Un solo hilo que hace de «hilo de la ventana»: el gestor y lo que llega de los agentes se ejecutan ahí, en orden,
/// como en la aplicación.
/// </summary>
internal sealed class BucleUnico : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback, object?)> _cola = [];
    private readonly Thread _hilo;

    public BucleUnico()
    {
        _hilo = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (f, e) in _cola.GetConsumingEnumerable()) f(e);
        }) { IsBackground = true, Name = "BucleUnico" };
        _hilo.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (!_cola.IsAddingCompleted) _cola.Add((d, state));
    }

    public void Poner(Action a) => Post(_ => a(), null);

    public Task<T> Ejecutar<T>(Func<Task<T>> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Poner(async () =>
        {
            try { tcs.SetResult(await f()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public Task Ejecutar(Func<Task> f) => Ejecutar(async () => { await f(); return true; });
    public Task<T> Ejecutar<T>(Func<T> f) => Ejecutar(() => Task.FromResult(f()));

    public void Dispose() => _cola.CompleteAdding();
}

/// <summary>Relevo de principio a fin: gestor real, SQLite, uniones y el agente falso por ACP.</summary>
public class RelevoTests
{
    private sealed class Montaje : IAsyncDisposable
    {
        public required CarpetaPrueba Carpeta { get; init; }
        public required BucleUnico Bucle { get; init; }
        public required BaseDatos Bd { get; init; }
        public required Gestor Gestor { get; init; }
        public required ConcurrentQueue<JsonElement> Mensajes { get; init; }
        public required ConcurrentDictionary<string, EstadoCuenta?> Consumo { get; init; }
        public string Registro => Path.Combine(Carpeta.Ruta, "falso.log");
        public string LeerRegistro() => File.Exists(Registro) ? File.ReadAllText(Registro) : "";
        public string Proyecto => Path.Combine(Carpeta.Ruta, "proyecto");

        public Task<T> En<T>(Func<Task<T>> f) => Bucle.Ejecutar(f);
        public Task En(Func<Task> f) => Bucle.Ejecutar(f);
        public Task<T> En<T>(Func<T> f) => Bucle.Ejecutar(f);

        public async Task<Cuenta> Cuenta(string alias) => (await En(() => new Repositorio(Bd).Cuentas())).Single(c => c.Alias == alias);
        public async Task<Equipo> Equipo() => (await En(() => new Repositorio(Bd).Equipos())).Single(e => e.Predeterminado);
        public async Task<Sesion> Sesion(string id) => (await En(() => new Repositorio(Bd).Sesion(id)))!;

        public async ValueTask DisposeAsync()
        {
            await En(async () => await Gestor.DisposeAsync());
            Bucle.Dispose();
            Bd.Dispose();
            Carpeta.Dispose();
        }
    }

    private static async Task<Montaje> Montar(params (string Alias, string Proveedor)[] cuentas)
    {
        var c = new CarpetaPrueba("relevo");
        var rutas = new Rutas(Path.Combine(c.Ruta, "MultiAgente"));
        rutas.CrearCarpetas();
        Directory.CreateDirectory(Path.Combine(c.Ruta, "proyecto"));
        var bd = BaseDatos.Abrir(rutas.BaseDatos);
        var bucle = new BucleUnico();
        var mensajes = new ConcurrentQueue<JsonElement>();
        var consumo = new ConcurrentDictionary<string, EstadoCuenta?>();
        var falso = new OrdenAgente("dotnet", [AgenteFalso.Dll()]);
        var op = new OpcionesGestor
        {
            ComprobarRequisitos = () => [],
            OrdenAgente = (_, _) => falso,
            Autorizada = (_, _, _) => true,
            Consumo = (cu, _) => Task.FromResult(consumo.GetValueOrDefault(cu.Alias)),
            GenerarResumen = _ => Task.FromResult<string?>("## Objetivo\nResumen de prueba"),
            EntornoExtra = new Dictionary<string, string?> { ["FALSO_REGISTRO"] = Path.Combine(c.Ruta, "falso.log") },
            Temporizadores = false,
        };
        var gestor = await bucle.Ejecutar(() => new Gestor(rutas, new Repositorio(bd),
            m => mensajes.Enqueue(JsonSerializer.SerializeToElement(m, Json.Opciones)), bucle.Poner, "prueba", op));
        foreach (var (alias, prov) in cuentas)
            await bucle.Ejecutar(() => { gestor.CrearCuenta(prov, "Api", alias, "clave-" + alias, null, null, null); return true; });
        var m = new Montaje { Carpeta = c, Bucle = bucle, Bd = bd, Gestor = gestor, Mensajes = mensajes, Consumo = consumo };
        // Relevo sin cuenta atrás para no esperar en las pruebas
        var e = await m.Equipo();
        await m.En(() => { gestor.GuardarEquipo(e.Id, e.Nombre, e.Regla.ToString(), true, 0, e.Miembros); return true; });
        return m;
    }

    private static async Task Esperar(Func<Task<bool>> condicion, string que, int segundos = 25)
    {
        var limite = DateTime.UtcNow.AddSeconds(segundos);
        while (!await condicion())
        {
            if (DateTime.UtcNow > limite) Assert.Fail($"No llegó a tiempo: {que}");
            await Task.Delay(100);
        }
    }

    private static async Task<string?> UltimoTextoAgente(Montaje m, string sesion) =>
        await m.En(() => m.Gestor.Viva(sesion)?.Hilo.Entradas.LastOrDefault(e => e.Tipo == "agente")?.Texto);

    private static Task EsperarRespuesta(Montaje m, string sesion, string texto) =>
        Esperar(async () => await UltimoTextoAgente(m, sesion) == texto &&
                            await m.En(() => m.Gestor.Viva(sesion)?.Estado == EstadoSesion.Esperando), $"respuesta «{texto}»");

    private static async Task<string> Nueva(Montaje m, string texto)
    {
        var e = await m.Equipo();
        var id = await m.En(() => m.Gestor.NuevaSesion(m.Proyecto, "equipo:" + e.Id, texto, []));
        await EsperarRespuesta(m, id, "Recibido: " + texto);
        return id;
    }

    [Fact]
    public async Task Al_llegar_al_limite_sigue_en_otra_cuenta_del_mismo_proveedor_con_la_misma_sesion()
    {
        await using var m = await Montar(("Claude A", "Claude"), ("Claude B", "Claude"));
        var id = await Nueva(m, "hola");
        var a = await m.Cuenta("Claude A");
        Assert.Equal(a.Id, (await m.Sesion(id)).CuentaId);
        var nativa = await m.En(() => m.Gestor.Viva(id)!.SesionNativa);

        await m.En(() => m.Gestor.Enviar(id, "llega al limite", []));
        await EsperarRespuesta(m, id, "Recibido: Continúa donde lo dejaste.");

        var b = await m.Cuenta("Claude B");
        Assert.Equal(b.Id, (await m.Sesion(id)).CuentaId);
        Assert.Contains($"cargada {nativa}", m.LeerRegistro()); // la misma sesión del agente, recuperada por la otra cuenta
        var entradas = await m.En(() => m.Gestor.Viva(id)!.Hilo.Entradas);
        Assert.Contains(entradas, e => e.Tipo == "relevo" && e.Texto!.StartsWith("Sigue en «Claude B»"));
        Assert.Contains(entradas, e => e is { Tipo: "aviso", Nivel: "limite" });
        Assert.Equal(nativa, await m.En(() => m.Gestor.Viva(id)!.SesionNativa));
        // Dos turnos de 0,25 $; la cuenta B recupera la sesión, cuyo total ya incluye el de A, y no lo cuenta otra vez
        double Costes() => new Repositorio(m.Bd).Usos(DateTime.Today).Where(u => u.SesionId == id).Sum(u => u.Coste ?? 0);
        await Esperar(async () => await m.En(Costes) >= 0.5, "uso del segundo turno");
        Assert.Equal(0.5, await m.En(Costes), 3);

        // La cuenta A queda enfriando y el estado lo dice
        var estado = await m.En(() => JsonSerializer.SerializeToElement(m.Gestor.Estado(), Json.Opciones));
        var cuentaA = estado.GetProperty("cuentas").EnumerateArray().Single(c => c.GetProperty("alias").GetString() == "Claude A");
        Assert.Contains("límite", cuentaA.GetProperty("sinMargen").GetString());
    }

    [Fact]
    public async Task A_otro_proveedor_pasa_con_traspaso_y_resumen_vivo()
    {
        await using var m = await Montar(("Claude A", "Claude"), ("Codex C", "Codex"));
        var id = await Nueva(m, "hola");
        var carpetaSesion = await m.En(() => m.Gestor.Viva(id)!.Hilo.Carpeta);
        await Esperar(() => Task.FromResult(File.Exists(Path.Combine(carpetaSesion, ResumenVivo.NombreFichero))), "resumen vivo");

        await m.En(() => m.Gestor.Enviar(id, "limite", []));
        await EsperarRespuesta(m, id, "Recibido: Continúa donde lo dejaste. · con traspaso");
        Assert.Equal((await m.Cuenta("Codex C")).Id, (await m.Sesion(id)).CuentaId);
    }

    [Fact]
    public async Task Por_consumo_releva_al_terminar_el_turno_sin_cortarlo()
    {
        await using var m = await Montar(("Claude A", "Claude"), ("Claude B", "Claude"));
        var id = await Nueva(m, "hola");
        await m.En(() => m.Gestor.Enviar(id, "escribe lento", []));
        await Esperar(async () => await m.En(() => m.Gestor.Viva(id)!.Estado == EstadoSesion.Trabajando), "trabajando");

        m.Consumo["Claude A"] = new EstadoCuenta { Pct5h = 97 };
        await m.En(() => m.Gestor.ActualizarConsumo(todas: true));
        await m.En(() => m.Gestor.RevisarRelevos());
        Assert.True(await m.En(() => m.Gestor.HayRelevo(id))); // espera: nunca se corta un turno
        Assert.Equal((await m.Cuenta("Claude A")).Id, (await m.Sesion(id)).CuentaId);

        await m.En(() => m.Gestor.Interrumpir(id));
        await Esperar(async () => (await m.Sesion(id)).CuentaId == (await m.Cuenta("Claude B")).Id, "relevo tras el turno");
        await Esperar(async () => await m.En(() => m.Gestor.Viva(id)?.Estado == EstadoSesion.Esperando), "cuenta nueva lista");
        // Relevo por consumo: no se le dice «continúa» (el turno no se cortó)
        Assert.DoesNotContain("Continúa donde lo dejaste", m.LeerRegistro());
    }

    [Fact]
    public async Task Sin_ninguna_cuenta_con_margen_avisa_y_no_cambia()
    {
        await using var m = await Montar(("Solo", "Claude"));
        var id = await Nueva(m, "hola");
        await m.En(() => m.Gestor.Enviar(id, "limite", []));
        await Esperar(() => Task.FromResult(m.Mensajes.Any(x => x.GetProperty("tipo").GetString() == "relevo" &&
            x.GetProperty("relevo").ValueKind == JsonValueKind.Object && x.GetProperty("relevo").TryGetProperty("sinDestino", out _))), "aviso sin destino");
        Assert.Equal((await m.Cuenta("Solo")).Id, (await m.Sesion(id)).CuentaId);
    }

    [Fact]
    public async Task Con_cuenta_atras_se_puede_cancelar()
    {
        await using var m = await Montar(("Claude A", "Claude"), ("Claude B", "Claude"));
        var e = await m.Equipo();
        await m.En(() => { m.Gestor.GuardarEquipo(e.Id, e.Nombre, "Prioridad", true, 60, e.Miembros); return true; });
        var id = await Nueva(m, "hola");
        await m.En(() => m.Gestor.Enviar(id, "limite", []));
        await Esperar(async () => await m.En(() => m.Gestor.HayRelevo(id)), "relevo propuesto");
        await m.En(() => { m.Gestor.CancelarRelevo(id); return true; });
        await m.En(() => m.Gestor.RevisarRelevos());
        Assert.False(await m.En(() => m.Gestor.HayRelevo(id)));
        Assert.Equal((await m.Cuenta("Claude A")).Id, (await m.Sesion(id)).CuentaId);
    }

    [Fact]
    public async Task Escribir_con_la_cuenta_agotada_releva_antes_de_mandar()
    {
        await using var m = await Montar(("Claude A", "Claude"), ("Claude B", "Claude"));
        var e = await m.Equipo();
        await m.En(() => { m.Gestor.GuardarEquipo(e.Id, e.Nombre, "Prioridad", true, 600, e.Miembros); return true; });
        var id = await Nueva(m, "hola");
        m.Consumo["Claude A"] = new EstadoCuenta { PctSemana = 99 };
        await m.En(() => m.Gestor.ActualizarConsumo(todas: true));
        await m.En(() => m.Gestor.Enviar(id, "sigue con esto", []));
        await EsperarRespuesta(m, id, "Recibido: sigue con esto");
        Assert.Equal((await m.Cuenta("Claude B")).Id, (await m.Sesion(id)).CuentaId);
        Assert.False(await m.En(() => m.Gestor.HayRelevo(id)));
    }

    [Fact]
    public async Task Una_sesion_con_cuenta_fija_no_se_releva()
    {
        await using var m = await Montar(("Claude A", "Claude"), ("Claude B", "Claude"));
        var a = await m.Cuenta("Claude A");
        var id = await m.En(() => m.Gestor.NuevaSesion(m.Proyecto, "cuenta:" + a.Id, "hola", []));
        await EsperarRespuesta(m, id, "Recibido: hola");
        await m.En(() => m.Gestor.Enviar(id, "limite", []));
        await Esperar(async () => await m.En(() => m.Gestor.Viva(id)!.Hilo.Entradas.Any(x => x.Nivel == "limite")), "aviso de límite");
        await m.En(() => m.Gestor.RevisarRelevos());
        Assert.False(await m.En(() => m.Gestor.HayRelevo(id)));
        Assert.Equal(a.Id, (await m.Sesion(id)).CuentaId);
    }

    [Fact]
    public async Task Lo_que_estaba_en_cola_al_llegar_al_limite_pasa_a_la_cuenta_nueva()
    {
        await using var m = await Montar(("Claude A", "Claude"), ("Claude B", "Claude"));
        var id = await Nueva(m, "hola");
        await m.En(() => m.Gestor.Enviar(id, "limite tarde", []));
        await Esperar(async () => await m.En(() => m.Gestor.Viva(id)!.Estado == EstadoSesion.Trabajando), "trabajando");
        await m.En(() => m.Gestor.Enviar(id, "segundo", []));

        await EsperarRespuesta(m, id, "Recibido: segundo");
        var entradas = await m.En(() => m.Gestor.Viva(id)!.Hilo.Entradas);
        // Primero «Continúa…» y después lo que esperaba, los dos con la cuenta nueva; a la agotada no le llega nada más
        var mios = entradas.Where(e => e.Tipo == "usuario").Select(e => (e.Texto, e.Cuenta)).ToList();
        Assert.Equal([("limite tarde", "Claude A"), ("Continúa donde lo dejaste.", "Claude B"), ("segundo", "Claude B")], mios[1..]);
        Assert.Equal((await m.Cuenta("Claude B")).Id, (await m.Sesion(id)).CuentaId);
    }

    [Fact]
    public async Task Si_el_agente_se_cae_al_volver_a_abrir_la_sesion_arranca_otra_vez()
    {
        await using var m = await Montar(("Claude A", "Claude"));
        var id = await Nueva(m, "hola");
        await m.En(() => m.Gestor.Enviar(id, "muere", []));
        await Esperar(async () => await m.En(() => m.Gestor.Viva(id)!.Estado == EstadoSesion.Error), "agente caído");

        await m.En(() => m.Gestor.AbrirSesion(id));
        Assert.Equal(EstadoSesion.Esperando, await m.En(() => m.Gestor.Viva(id)!.Estado));
        await m.En(() => m.Gestor.Enviar(id, "sigo", []));
        await EsperarRespuesta(m, id, "Recibido: sigo");

        // Y escribir con el agente caído también lo vuelve a arrancar
        await m.En(() => m.Gestor.Enviar(id, "muere", []));
        await Esperar(async () => await m.En(() => m.Gestor.Viva(id)!.Estado == EstadoSesion.Error), "agente caído otra vez");
        await m.En(() => m.Gestor.Enviar(id, "otra vez", []));
        await EsperarRespuesta(m, id, "Recibido: otra vez");
    }

    [Fact]
    public async Task Ocupadas_dice_que_sesiones_se_cortarian_al_salir()
    {
        await using var m = await Montar(("Claude A", "Claude"));
        var id = await Nueva(m, "hola");
        Assert.Empty(await m.En(() => m.Gestor.Ocupadas()));

        await m.En(() => m.Gestor.Enviar(id, "escribe lento", []));
        await Esperar(async () => await m.En(() => m.Gestor.Viva(id)!.Estado == EstadoSesion.Trabajando), "trabajando");
        Assert.Equal(["hola"], await m.En(() => m.Gestor.Ocupadas()));

        await m.En(() => m.Gestor.Interrumpir(id));
        await Esperar(async () => await m.En(() => m.Gestor.Viva(id)!.Estado == EstadoSesion.Esperando), "turno terminado");
        Assert.Empty(await m.En(() => m.Gestor.Ocupadas()));
    }

    [Fact]
    public async Task Las_sesiones_nuevas_del_equipo_saltan_la_cuenta_enfriando()
    {
        await using var m = await Montar(("Claude A", "Claude"), ("Claude B", "Claude"));
        var a = await m.Cuenta("Claude A");
        await m.En(() => { m.Gestor.Enfriar(a.Id, "limit reached, try again in 2 hours"); return true; });
        var id = await Nueva(m, "hola");
        Assert.Equal((await m.Cuenta("Claude B")).Id, (await m.Sesion(id)).CuentaId);
    }
}

internal static class AgenteFalso
{
    public static string Dll()
    {
        var aqui = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var config = aqui.Parent!.Name;
        var dll = Path.GetFullPath(Path.Combine(aqui.FullName, "..", "..", "..", "..", "MultiAgente.FalsoAgente", "bin", config, "net10.0", "MultiAgente.FalsoAgente.dll"));
        Assert.True(File.Exists(dll), $"Falta el agente falso: {dll}");
        return dll;
    }
}
