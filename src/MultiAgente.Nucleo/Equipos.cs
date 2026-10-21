namespace MultiAgente.Nucleo;

/// <summary>Cómo reparte un equipo las sesiones nuevas (y a quién pasa el relevo).</summary>
public enum ReglaReparto
{
    /// <summary>La primera cuenta con margen, en el orden del equipo.</summary>
    Prioridad,
    /// <summary>Cada sesión nueva empieza en la siguiente cuenta.</summary>
    Rotacion,
    /// <summary>La de más margen (menos consumo).</summary>
    MenosUsada,
    /// <summary>Primero las de suscripción; las de clave de API, de reserva.</summary>
    SuscripcionPrimero,
}

/// <summary>Cuentas intercambiables, en orden, con una regla de reparto.</summary>
public sealed class Equipo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Nombre { get; set; } = "";
    public ReglaReparto Regla { get; set; } = ReglaReparto.Prioridad;
    /// <summary>Si puede pasar una sesión a una cuenta de otro proveedor (con traspaso) cuando no queda ninguna del mismo.</summary>
    public bool PermiteOtroProveedor { get; set; } = true;
    /// <summary>Segundos de la cuenta atrás antes de un relevo automático (0 = sin esperar).</summary>
    public int CuentaAtras { get; set; } = 60;
    public bool Predeterminado { get; set; }
    /// <summary>Cuentas del equipo, en orden.</summary>
    public List<string> Miembros { get; set; } = [];
}

/// <summary>Lo que se sabe del uso de una cuenta: consumo (si el proveedor lo da) y enfriamiento tras un límite.</summary>
public sealed record EstadoCuenta
{
    /// <summary>Uso del bloque de 5 horas (0-100), si se conoce.</summary>
    public double? Pct5h { get; init; }
    /// <summary>Uso semanal (0-100), si se conoce.</summary>
    public double? PctSemana { get; init; }
    public DateTime? Reinicio5h { get; init; }
    public DateTime? ReinicioSemana { get; init; }
    /// <summary>La cuenta ha dado error de límite: no se usa hasta esta hora.</summary>
    public DateTime? EnfriandoHasta { get; init; }
    public string? Motivo { get; init; }
    /// <summary>Gasto del mes en curso (cuentas con coste informado por el agente).</summary>
    public double? GastoMes { get; init; }
    public DateTime Momento { get; init; } = DateTime.Now;

    public bool Enfriando(DateTime ahora) => EnfriandoHasta is { } h && h > ahora;
    /// <summary>El mayor de los consumos conocidos (lo que de verdad limita).</summary>
    public double? Peor => (Pct5h, PctSemana) switch
    {
        (null, null) => null,
        (var a, var b) => Math.Max(a ?? 0, b ?? 0),
    };
}

/// <summary>
/// Persistencia que necesita el <see cref="Gestor"/>. La implementa MultiAgente.Datos (SQLite); así el núcleo no depende
/// de la base de datos y se puede probar entero.
/// </summary>
public interface IRepositorio
{
    List<Cuenta> Cuentas();
    void GuardarCuenta(Cuenta c);
    void BorrarCuenta(string id);
    List<Proyecto> Proyectos();
    void GuardarProyecto(Proyecto p);
    void BorrarProyecto(string id);
    Proyecto? ProyectoDe(string carpeta);
    List<Sesion> Sesiones(bool archivadas = false);
    Sesion? Sesion(string id);
    void GuardarSesion(Sesion s);
    Tramo? UltimoTramo(string sesionId);
    void GuardarTramo(Tramo t);
    List<Equipo> Equipos();
    void GuardarEquipo(Equipo e);
    void BorrarEquipo(string id);
    string? LeerAjuste(string clave);
    void GuardarAjuste(string clave, string? valor);
    void GuardarUso(UsoTurno u);
    List<UsoTurno> Usos(DateTime desde);
}
