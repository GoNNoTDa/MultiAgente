using MultiAgente.Nucleo;

namespace MultiAgente.Tests;

public class EnrutadorTests
{
    private static readonly DateTime Ahora = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Local);

    private static Cuenta C(string id, string prov = "Claude", TipoCuenta tipo = TipoCuenta.Login, string? endpoint = null) =>
        new() { Id = id, Alias = id, Proveedor = prov, Tipo = tipo, Endpoint = endpoint };

    private static Candidata Ok(Cuenta c, double? pct = null) => new(c, true, pct is null ? null : new EstadoCuenta { Pct5h = pct });
    private static Candidata Enfriando(Cuenta c) => new(c, true, new EstadoCuenta { EnfriandoHasta = Ahora.AddHours(1) });

    private static Equipo E(ReglaReparto regla, params string[] miembros) =>
        new() { Nombre = "e", Regla = regla, Miembros = [.. miembros] };

    [Fact]
    public void Sin_margen_por_cada_motivo()
    {
        Assert.Null(Enrutador.SinMargen(Ok(C("a"), 50), Ahora));
        Assert.Null(Enrutador.SinMargen(Ok(C("a")), Ahora)); // sin datos de consumo: se usa
        Assert.Contains("autorizada", Enrutador.SinMargen(new Candidata(C("a"), false, null), Ahora));
        Assert.Contains("5 horas", Enrutador.SinMargen(Ok(C("a"), 93), Ahora));
        Assert.Contains("semanal", Enrutador.SinMargen(new Candidata(C("a"), true, new EstadoCuenta { PctSemana = 96 }), Ahora));
        Assert.Contains("límite", Enrutador.SinMargen(Enfriando(C("a")), Ahora));
        Assert.Null(Enrutador.SinMargen(new Candidata(C("a"), true, new EstadoCuenta { EnfriandoHasta = Ahora.AddMinutes(-1) }), Ahora));
    }

    [Fact]
    public void Prioridad_salta_las_que_no_tienen_margen()
    {
        var todas = new[] { Enfriando(C("a")), Ok(C("b"), 95), new Candidata(C("c"), false, null), Ok(C("d")), Ok(C("e")) };
        Assert.Equal("d", Enrutador.ParaNueva(E(ReglaReparto.Prioridad, "a", "b", "c", "d", "e"), todas, Ahora)?.Id);
        Assert.Null(Enrutador.ParaNueva(E(ReglaReparto.Prioridad, "a", "b", "c"), todas, Ahora));
        Assert.Equal("e", Enrutador.ParaNueva(E(ReglaReparto.Prioridad, "x", "e", "d"), todas, Ahora)?.Id); // cuenta que ya no existe
    }

    [Fact]
    public void Rotacion_empieza_en_la_siguiente_a_la_ultima_y_da_la_vuelta()
    {
        var todas = new[] { Ok(C("a")), Ok(C("b")), Enfriando(C("c")) };
        var e = E(ReglaReparto.Rotacion, "a", "b", "c");
        Assert.Equal("a", Enrutador.ParaNueva(e, todas, Ahora)?.Id);
        Assert.Equal("b", Enrutador.ParaNueva(e, todas, Ahora, ultimaUsada: "a")?.Id);
        Assert.Equal("a", Enrutador.ParaNueva(e, todas, Ahora, ultimaUsada: "b")?.Id); // c enfriando: da la vuelta
    }

    [Fact]
    public void Menos_usada_y_suscripcion_primero()
    {
        var todas = new[] { Ok(C("a"), 70), Ok(C("b"), 20), Ok(C("c")), Ok(C("k", tipo: TipoCuenta.Api)) };
        Assert.Equal("b", Enrutador.ParaNueva(E(ReglaReparto.MenosUsada, "a", "b", "c"), todas, Ahora)?.Id);
        Assert.Equal("c", Enrutador.ParaNueva(E(ReglaReparto.MenosUsada, "a", "c"), todas, Ahora)?.Id); // sin datos cuenta como 50
        Assert.Equal("a", Enrutador.ParaNueva(E(ReglaReparto.SuscripcionPrimero, "k", "a", "b"), todas, Ahora)?.Id);
        Assert.Equal("k", Enrutador.ParaNueva(E(ReglaReparto.Prioridad, "k", "a", "b"), todas, Ahora)?.Id);
    }

    [Fact]
    public void El_relevo_prefiere_el_mismo_proveedor_y_la_misma_api()
    {
        var claude1 = C("claude1");
        var glm = C("glm", tipo: TipoCuenta.Api, endpoint: "https://api.z.ai/api/anthropic");
        var codex = C("codex", prov: "Codex");
        var claude2 = C("claude2");
        var todas = new[] { Ok(claude1), Ok(glm), Ok(codex), Ok(claude2) };
        var e = E(ReglaReparto.Prioridad, "claude1", "glm", "codex", "claude2");

        Assert.Equal("claude2", Enrutador.ParaRelevo(e, todas, claude1, Ahora)?.Id); // misma conversación antes que el orden
        var sinClaude2 = new[] { Ok(claude1), Ok(glm), Ok(codex), Enfriando(claude2) };
        Assert.Equal("glm", Enrutador.ParaRelevo(e, sinClaude2, claude1, Ahora)?.Id); // otro: el primero con margen
        e.PermiteOtroProveedor = false;
        Assert.Null(Enrutador.ParaRelevo(e, sinClaude2, claude1, Ahora));
        Assert.False(Enrutador.MismaConversacion(claude1, glm));
        Assert.True(Enrutador.MismaConversacion(claude1, claude2));
    }

    [Fact]
    public void El_relevo_por_rotacion_sigue_desde_la_actual()
    {
        var todas = new[] { Ok(C("a", "Codex")), Ok(C("b", "Gemini")), Ok(C("c", "Gemini")), Ok(C("d", "Codex")) };
        var e = E(ReglaReparto.Rotacion, "a", "b", "c", "d");
        Assert.Equal("d", Enrutador.ParaRelevo(e, todas, todas[0].Cuenta, Ahora)?.Id); // mismo proveedor
        Assert.Equal("c", Enrutador.ParaRelevo(e, todas, todas[1].Cuenta, Ahora)?.Id);
        Assert.Equal("b", Enrutador.ParaRelevo(e, todas.Take(3).ToArray(), todas[2].Cuenta, Ahora)?.Id);
        Assert.Equal("a", Enrutador.ParaRelevo(e, [todas[0], todas[1], todas[3]], todas[3].Cuenta, Ahora)?.Id); // mismo proveedor, dando la vuelta
    }

    [Theory]
    [InlineData("Usage limit reached. Try again in 2 hours.", 120)]
    [InlineData("rate limited, retry in 30 minutes", 30)]
    [InlineData("You've hit your limit · resets 3pm", 180)]
    [InlineData("quota exhausted until 2026-10-20T14:30:00Z", -1)]
    [InlineData("algo raro", 60)]
    public void Hasta_cuando_descansa_una_cuenta(string mensaje, int minutos)
    {
        var hasta = Enrutador.HastaCuando(mensaje, null, Ahora);
        if (minutos >= 0) Assert.Equal(Ahora.AddMinutes(minutos), hasta);
        else Assert.Equal(new DateTime(2026, 10, 20, 14, 30, 0, DateTimeKind.Utc).ToLocalTime(), hasta);
    }

    [Fact]
    public void Sin_hora_en_el_mensaje_usa_la_del_consumo()
    {
        var estado = new EstadoCuenta { Reinicio5h = Ahora.AddMinutes(42), ReinicioSemana = Ahora.AddDays(3) };
        Assert.Equal(Ahora.AddMinutes(42), Enrutador.HastaCuando("limit reached", estado, Ahora));
    }
}

public class ConsumoTests
{
    [Fact]
    public void Lee_el_consumo_del_servicio_de_uso()
    {
        var e = ConsumoClaude.Interpretar("""
            { "five_hour": { "utilization": 93.5, "resets_at": "2026-10-20T15:00:00+00:00" },
              "seven_day": { "utilization": 41, "resets_at": "2026-10-25T08:00:00Z" }, "extra_usage": null }
            """);
        Assert.Equal(93.5, e.Pct5h);
        Assert.Equal(41, e.PctSemana);
        Assert.Equal(new DateTime(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc).ToLocalTime(), e.Reinicio5h);
        Assert.Equal(93.5, e.Peor);
        Assert.Null(ConsumoClaude.Interpretar("{}").Peor);
    }

    [Fact]
    public void El_token_se_lee_solo_si_no_ha_caducado()
    {
        using var c = new CarpetaPrueba("consumo");
        Assert.Null(ConsumoClaude.Token(c.Ruta));
        var futuro = DateTimeOffset.Now.AddHours(1).ToUnixTimeMilliseconds();
        c.Fichero(".credentials.json", $$"""{ "claudeAiOauth": { "accessToken": "tok-1", "expiresAt": {{futuro}} } }""");
        Assert.Equal("tok-1", ConsumoClaude.Token(c.Ruta));
        var pasado = DateTimeOffset.Now.AddHours(-1).ToUnixTimeMilliseconds();
        c.Fichero(".credentials.json", $$"""{ "claudeAiOauth": { "accessToken": "tok-1", "expiresAt": {{pasado}} } }""");
        Assert.Null(ConsumoClaude.Token(c.Ruta));
    }
}

public class CarpetasCompartidasTests
{
    [Fact]
    public void Enlaza_junta_lo_que_hubiera_y_se_repara_al_mover_la_carpeta()
    {
        using var c = new CarpetaPrueba("compartidas");
        var rutas = new Rutas(Path.Combine(c.Ruta, "MultiAgente"));
        var cuentaA = Path.Combine(rutas.Cuentas, "a");
        var cuentaB = Path.Combine(rutas.Cuentas, "b");
        // La cuenta B ya tenía conversaciones propias (carpeta real)
        Directory.CreateDirectory(Path.Combine(cuentaB, "projects", "C--proyecto"));
        File.WriteAllText(Path.Combine(cuentaB, "projects", "C--proyecto", "s1.jsonl"), "{}");

        Assert.True(CarpetasCompartidas.Preparar(rutas, "Claude", cuentaA));
        Assert.True(CarpetasCompartidas.Preparar(rutas, "Claude", cuentaB));
        var comun = Path.Combine(rutas.Comun, "Claude", "projects");
        Assert.Equal(comun, CarpetasCompartidas.Destino(Path.Combine(cuentaA, "projects")));
        Assert.Equal(comun, CarpetasCompartidas.Destino(Path.Combine(cuentaB, "projects")));
        Assert.True(File.Exists(Path.Combine(cuentaA, "projects", "C--proyecto", "s1.jsonl"))); // A ve lo que empezó B

        // Mover la carpeta portátil: las uniones apuntan a la ruta vieja y se rehacen
        var movida = Path.Combine(c.Ruta, "Movida");
        Directory.Move(rutas.Raiz, movida);
        var rutas2 = new Rutas(movida);
        var cuentaA2 = Path.Combine(rutas2.Cuentas, "a");
        Assert.True(CarpetasCompartidas.Preparar(rutas2, "Claude", cuentaA2));
        Assert.Equal(Path.Combine(rutas2.Comun, "Claude", "projects"), CarpetasCompartidas.Destino(Path.Combine(cuentaA2, "projects")));
        Assert.True(File.Exists(Path.Combine(cuentaA2, "projects", "C--proyecto", "s1.jsonl")));
    }

    [Fact]
    public void Gemini_comparte_su_carpeta_de_conversaciones()
    {
        using var c = new CarpetaPrueba("compartidas");
        var rutas = new Rutas(c.Ruta);
        var cuenta = Path.Combine(rutas.Cuentas, "g");
        Assert.True(CarpetasCompartidas.Preparar(rutas, "Gemini", cuenta));
        Assert.NotNull(CarpetasCompartidas.Destino(Path.Combine(cuenta, ".gemini", "tmp")));
    }
}
