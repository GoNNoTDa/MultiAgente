using System.Text.Json;
using MultiAgente.Datos;
using MultiAgente.Nucleo;
using MultiAgente.Nucleo.Acp;

namespace MultiAgente.Tests;

public class LimitesTests
{
    private static readonly DateTime Ahora = new(2026, 10, 20, 12, 0, 0);

    [Fact]
    public void Umbrales_propios_de_la_cuenta_y_presupuesto_del_mes()
    {
        var prudente = new Cuenta { Id = "a", Alias = "a", Umbral5h = 50, UmbralSemana = 70 };
        Assert.Contains("5 horas", Enrutador.SinMargen(new Candidata(prudente, true, new EstadoCuenta { Pct5h = 60 }), Ahora));
        Assert.Contains("semanal", Enrutador.SinMargen(new Candidata(prudente, true, new EstadoCuenta { PctSemana = 75 }), Ahora));
        var normal = new Cuenta { Id = "b", Alias = "b" };
        Assert.Null(Enrutador.SinMargen(new Candidata(normal, true, new EstadoCuenta { Pct5h = 60, PctSemana = 75 }), Ahora));

        var api = new Cuenta { Id = "c", Alias = "c", Tipo = TipoCuenta.Api, Presupuesto = 20 };
        Assert.Null(Enrutador.SinMargen(new Candidata(api, true, new EstadoCuenta { GastoMes = 19.5 }), Ahora));
        Assert.Contains("presupuesto", Enrutador.SinMargen(new Candidata(api, true, new EstadoCuenta { GastoMes = 20 }), Ahora));
    }

    [Fact]
    public void El_uso_se_guarda_y_se_lee_por_fecha()
    {
        using var c = new CarpetaPrueba("uso");
        using var bd = BaseDatos.Abrir(Path.Combine(c.Ruta, "m.db"));
        var r = new Repositorio(bd);
        r.GuardarUso(new UsoTurno { SesionId = "s", CuentaId = "a", Momento = DateTime.Now.AddDays(-10), Total = 5 });
        r.GuardarUso(new UsoTurno { SesionId = "s", CuentaId = "a", Momento = DateTime.Now, Entrada = 100, Salida = 20, Total = 120, Coste = 0.5, Moneda = "USD" });
        var hoy = Assert.Single(r.Usos(DateTime.Today));
        Assert.Equal((100L, 20L, 120L, 0.5, "USD"), (hoy.Entrada, hoy.Salida, hoy.Total, hoy.Coste!.Value, hoy.Moneda));
        Assert.Equal(2, r.Usos(DateTime.Today.AddDays(-30)).Count);

        var cuenta = new Cuenta { Alias = "x", Proveedor = "Claude", Carpeta = "c", Umbral5h = 80, Presupuesto = 12.5 };
        r.GuardarCuenta(cuenta);
        var leida = r.Cuentas().Single();
        Assert.Equal((80.0, (double?)null, 12.5), (leida.Umbral5h!.Value, leida.UmbralSemana, leida.Presupuesto!.Value));
    }
}

public class UsoAgenteTests
{
    [Fact]
    public async Task Cada_turno_da_su_parte_de_tokens_y_coste_y_el_contexto()
    {
        using var c = new CarpetaPrueba("uso");
        var trabajo = Directory.CreateDirectory(Path.Combine(c.Ruta, "p")).FullName;
        var cuenta = new Cuenta { Alias = "A", Proveedor = "Claude", Tipo = TipoCuenta.Api, Carpeta = Path.Combine(c.Ruta, "cuenta") };
        await using var a = new SesionAgente(new Sesion { Carpeta = trabajo }, cuenta, Proveedores.Claude,
            new OrdenAgente("dotnet", [AgenteFalso.Dll()]), Proveedores.Entorno(Proveedores.Claude, cuenta.Carpeta, "k"),
            new Hilo(Path.Combine(c.Ruta, "s")), null);
        var usos = new List<UsoTurno>();
        a.UsoDelTurno += (_, u) => { lock (usos) usos.Add(u); };
        await a.Arrancar();
        a.Enviar("uno");
        a.Enviar("dos");
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            lock (usos) if (usos.Count == 2) break;
            Assert.True(DateTime.UtcNow < limite, "no llegó el uso de los dos turnos");
            await Task.Delay(50);
        }
        // El agente da acumulados (110, 220 tokens; 0,25, 0,50 $): cada turno se queda con su parte
        Assert.All(usos, u => Assert.Equal((100L, 10L, 110L, 0.25, "USD"), (u.Entrada, u.Salida, u.Total, u.Coste!.Value, u.Moneda)));
        Assert.Equal((2000L, 200000L), a.Contexto);
    }
}

public class PresupuestoTests
{
    [Fact]
    public async Task Al_agotar_el_presupuesto_avisa_y_releva_y_las_estadisticas_lo_cuentan()
    {
        using var c = new CarpetaPrueba("presu");
        var rutas = new Rutas(Path.Combine(c.Ruta, "MultiAgente"));
        rutas.CrearCarpetas();
        Directory.CreateDirectory(Path.Combine(c.Ruta, "proyecto"));
        using var bd = BaseDatos.Abrir(rutas.BaseDatos);
        using var bucle = new BucleUnico();
        var mensajes = new List<JsonElement>();
        var gestor = await bucle.Ejecutar(() => new Gestor(rutas, new Repositorio(bd),
            m => { lock (mensajes) mensajes.Add(JsonSerializer.SerializeToElement(m, Json.Opciones)); }, bucle.Poner, "prueba",
            new OpcionesGestor
            {
                ComprobarRequisitos = () => [], OrdenAgente = (_, _) => new OrdenAgente("dotnet", [AgenteFalso.Dll()]),
                Autorizada = (_, _, _) => true, Temporizadores = false, GenerarResumen = _ => Task.FromResult<string?>(null),
            }));
        try
        {
            await bucle.Ejecutar(() =>
            {
                gestor.CrearCuenta("Claude", "Api", "Barata", "k1", null, null, null);
                gestor.CrearCuenta("Claude", "Api", "Reserva", "k2", null, null, null);
                return true;
            });
            var repo = new Repositorio(bd);
            var barata = repo.Cuentas().Single(x => x.Alias == "Barata");
            var equipo = repo.Equipos().Single();
            await bucle.Ejecutar(() =>
            {
                gestor.GuardarLimites(barata.Id, null, null, 1.0);
                gestor.GuardarEquipo(equipo.Id, equipo.Nombre, "Prioridad", true, 0, equipo.Miembros);
                return true;
            });
            var id = await bucle.Ejecutar(() => gestor.NuevaSesion(Path.Combine(c.Ruta, "proyecto"), "equipo:" + equipo.Id, "hola", []));

            async Task Esperar(Func<bool> condicion, string que)
            {
                var limite = DateTime.UtcNow.AddSeconds(25);
                while (!await bucle.Ejecutar(condicion))
                {
                    Assert.True(DateTime.UtcNow < limite, "No llegó a tiempo: " + que);
                    await Task.Delay(100);
                }
            }
            await Esperar(() => gestor.Viva(id)?.Estado == EstadoSesion.Esperando && gestor.Viva(id)!.Hilo.Entradas.Any(e => e.Tipo == "agente"), "primer turno");
            // 0,25 $ de 1 $: sin aviso. Un turno «caro» (5 $) agota el presupuesto: aviso y relevo a la reserva
            await bucle.Ejecutar(() => gestor.Enviar(id, "algo caro", []));
            await Esperar(() => repo.Sesion(id)!.CuentaId == repo.Cuentas().Single(x => x.Alias == "Reserva").Id, "relevo por presupuesto");
            lock (mensajes)
                Assert.Contains(mensajes, m => m.GetProperty("tipo").GetString() == "aviso" && m.GetProperty("texto").GetString()!.Contains("presupuesto del mes"));

            var e = await bucle.Ejecutar(() => JsonSerializer.SerializeToElement(gestor.Estadisticas(1), Json.Opciones));
            var total = e.GetProperty("total");
            Assert.Equal(2, total.GetProperty("turnos").GetInt32());
            Assert.Equal(5.25, total.GetProperty("coste").GetDouble(), 3);
            Assert.Equal("Barata", e.GetProperty("porCuenta")[0].GetProperty("cuenta").GetString());
            Assert.Equal("hola", e.GetProperty("porSesion")[0].GetProperty("titulo").GetString());
            Assert.Equal(1, e.GetProperty("porDia").GetArrayLength());

            var estado = await bucle.Ejecutar(() => JsonSerializer.SerializeToElement(gestor.Estado(), Json.Opciones));
            var cb = estado.GetProperty("cuentas").EnumerateArray().Single(x => x.GetProperty("alias").GetString() == "Barata");
            Assert.Equal(5.25, cb.GetProperty("gastoMes").GetDouble(), 3);
            Assert.Contains("presupuesto", cb.GetProperty("sinMargen").GetString());
        }
        finally
        {
            await bucle.Ejecutar(async () => await gestor.DisposeAsync());
        }
    }
}
