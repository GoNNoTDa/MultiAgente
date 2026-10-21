namespace MultiAgente.Nucleo;

/// <summary>Una cuenta vista por el enrutador: sus datos, si se puede usar y lo que se sabe de su uso.</summary>
/// <param name="Cuenta">La cuenta.</param>
/// <param name="Lista">Autorizada (sesión iniciada o clave) y con su agente instalado.</param>
/// <param name="Estado">Consumo y enfriamiento (null si no se sabe nada).</param>
public sealed record Candidata(Cuenta Cuenta, bool Lista, EstadoCuenta? Estado);

/// <summary>
/// Decide qué cuenta trabaja: al crear una sesión (según la regla del equipo) y al relevar (cuando la actual se agota).
/// Lógica pura: no lee disco ni red.
/// </summary>
public static class Enrutador
{
    /// <summary>Consumo del bloque de 5 h a partir del cual una cuenta se considera agotada.</summary>
    public const double Umbral5h = 92;
    /// <summary>Consumo semanal a partir del cual una cuenta se considera agotada.</summary>
    public const double UmbralSemana = 95;

    /// <summary>Motivo por el que una cuenta no tiene margen (null si lo tiene).</summary>
    public static string? SinMargen(Candidata c, DateTime ahora)
    {
        if (!c.Lista) return "no está autorizada o falta su agente";
        var e = c.Estado;
        if (e is null) return null;
        if (e.Enfriando(ahora)) return $"ha llegado a su límite (hasta las {e.EnfriandoHasta:HH:mm})";
        if (e.Pct5h >= (c.Cuenta.Umbral5h ?? Umbral5h)) return $"está al {e.Pct5h:0} % del bloque de 5 horas";
        if (e.PctSemana >= (c.Cuenta.UmbralSemana ?? UmbralSemana)) return $"está al {e.PctSemana:0} % de su límite semanal";
        if (c.Cuenta.Presupuesto is { } p && e.GastoMes >= p) return $"ha gastado su presupuesto del mes ({e.GastoMes:0.00} de {p:0.00})";
        return null;
    }

    public static bool TieneMargen(Candidata c, DateTime ahora) => SinMargen(c, ahora) is null;

    /// <summary>Cuentas del equipo, en su orden, ya filtradas por las que existen.</summary>
    private static List<Candidata> DelEquipo(Equipo equipo, IReadOnlyList<Candidata> todas) =>
        equipo.Miembros.Select(id => todas.FirstOrDefault(c => c.Cuenta.Id == id)).OfType<Candidata>().ToList();

    /// <summary>Ordena las cuentas con margen según la regla (sin la de rotación, que depende de la última usada).</summary>
    private static IEnumerable<Candidata> Ordenar(ReglaReparto regla, List<Candidata> conMargen) => regla switch
    {
        ReglaReparto.MenosUsada => conMargen.Select((c, i) => (c, i))
            .OrderBy(x => x.c.Estado?.Peor ?? 50).ThenBy(x => x.i).Select(x => x.c),
        ReglaReparto.SuscripcionPrimero => conMargen.Select((c, i) => (c, i))
            .OrderBy(x => x.c.Cuenta.Tipo == TipoCuenta.Login ? 0 : 1).ThenBy(x => x.i).Select(x => x.c),
        _ => conMargen,
    };

    /// <summary>
    /// Cuenta para una sesión nueva del equipo. <paramref name="ultimaUsada"/> es la cuenta con la que empezó la última
    /// sesión (para la rotación). null si ninguna tiene margen.
    /// </summary>
    public static Cuenta? ParaNueva(Equipo equipo, IReadOnlyList<Candidata> todas, DateTime ahora, string? ultimaUsada = null)
    {
        var miembros = DelEquipo(equipo, todas);
        if (equipo.Regla == ReglaReparto.Rotacion && miembros.Count > 0)
        {
            var i = miembros.FindIndex(c => c.Cuenta.Id == ultimaUsada);
            for (var k = 1; k <= miembros.Count; k++)
            {
                var c = miembros[(i + k + miembros.Count) % miembros.Count];
                if (TieneMargen(c, ahora)) return c.Cuenta;
            }
            return null;
        }
        return Ordenar(equipo.Regla, miembros.Where(c => TieneMargen(c, ahora)).ToList()).FirstOrDefault()?.Cuenta;
    }

    /// <summary>
    /// A quién pasa una sesión cuya cuenta <paramref name="actual"/> se ha agotado: primero el mismo proveedor (con la
    /// misma dirección de API: la conversación sigue tal cual), después otro proveedor si el equipo lo permite (con
    /// traspaso). La rotación se trata como prioridad (se sigue el orden desde la actual). null si nadie tiene margen.
    /// </summary>
    public static Cuenta? ParaRelevo(Equipo equipo, IReadOnlyList<Candidata> todas, Cuenta actual, DateTime ahora)
    {
        var miembros = DelEquipo(equipo, todas);
        var conMargen = miembros.Where(c => c.Cuenta.Id != actual.Id && TieneMargen(c, ahora)).ToList();
        if (equipo.Regla == ReglaReparto.Rotacion)
        {
            // Desde la siguiente a la actual, dando la vuelta
            var i = miembros.FindIndex(c => c.Cuenta.Id == actual.Id);
            conMargen = conMargen.OrderBy(c => (miembros.IndexOf(c) - i + miembros.Count) % miembros.Count).ToList();
        }
        var ordenadas = Ordenar(equipo.Regla == ReglaReparto.Rotacion ? ReglaReparto.Prioridad : equipo.Regla, conMargen).ToList();
        var mismo = ordenadas.FirstOrDefault(c => MismaConversacion(c.Cuenta, actual));
        if (mismo is not null) return mismo.Cuenta;
        return equipo.PermiteOtroProveedor ? ordenadas.FirstOrDefault()?.Cuenta : null;
    }

    /// <summary>
    /// Las dos cuentas pueden seguir la misma sesión del agente (mismo proveedor y misma API): el relevo conserva
    /// todo. Si no, hace falta traspaso.
    /// </summary>
    public static bool MismaConversacion(Cuenta a, Cuenta b) =>
        a.Proveedor == b.Proveedor && string.Equals(a.Endpoint ?? "", b.Endpoint ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Hora hasta la que se deja descansar una cuenta tras un error de límite: la que diga el mensaje si se reconoce
    /// («resets at 3pm», «try again in 2 hours», una fecha ISO…), la de reinicio conocida del consumo o, si no, una hora.
    /// </summary>
    public static DateTime HastaCuando(string? mensaje, EstadoCuenta? estado, DateTime ahora)
    {
        var t = (mensaje ?? "").ToLowerInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(t, @"(?:in|en)\s+(\d+)\s*(second|sec|minute|min|hour|hr|hora|minuto|segundo)");
        if (m.Success)
        {
            var n = int.Parse(m.Groups[1].Value);
            var u = m.Groups[2].Value;
            return u.StartsWith("h") ? ahora.AddHours(n) : u.StartsWith("m") ? ahora.AddMinutes(n) : ahora.AddSeconds(n);
        }
        m = System.Text.RegularExpressions.Regex.Match(t, @"(\d{4}-\d{2}-\d{2}t\d{2}:\d{2}(:\d{2})?(\.\d+)?(z|[+-]\d{2}:?\d{2})?)");
        if (m.Success && DateTime.TryParse(m.Groups[1].Value.ToUpperInvariant(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var iso))
            return iso.ToLocalTime() > ahora ? iso.ToLocalTime() : ahora.AddHours(1);
        m = System.Text.RegularExpressions.Regex.Match(t, @"resets?\s+(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)");
        if (m.Success)
        {
            var h = int.Parse(m.Groups[1].Value) % 12 + (m.Groups[3].Value == "pm" ? 12 : 0);
            var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            var hora = ahora.Date.AddHours(h).AddMinutes(min);
            return hora > ahora ? hora : hora.AddDays(1);
        }
        var conocido = new[] { estado?.Reinicio5h, estado?.ReinicioSemana }.Where(x => x > ahora).Min();
        return conocido ?? ahora.AddHours(1);
    }
}
