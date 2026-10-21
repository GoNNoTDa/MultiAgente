using System.Globalization;
using Microsoft.Data.Sqlite;
using MultiAgente.Nucleo;

namespace MultiAgente.Datos;

/// <summary>Lectura y escritura de cuentas, proyectos, sesiones y tramos. Nunca guarda claves ni tokens.</summary>
public sealed class Repositorio(BaseDatos bd) : IRepositorio
{
    private static string F(DateTime d) => d.ToString("o", CultureInfo.InvariantCulture);
    private static DateTime F(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // ---------- Cuentas ----------

    public List<Cuenta> Cuentas() => bd.Leer(
        "SELECT Id, Alias, Proveedor, Tipo, Carpeta, RefClave, Orden, Creada, Endpoint, Modelo, Catalogo, Umbral5h, UmbralSemana, Presupuesto FROM Cuenta ORDER BY Orden, Alias;",
        r => new Cuenta
        {
            Id = r.GetString(0), Alias = r.GetString(1), Proveedor = r.GetString(2),
            Tipo = Enum.Parse<TipoCuenta>(r.GetString(3)), Carpeta = r.GetString(4),
            RefClave = r.IsDBNull(5) ? null : r.GetString(5), Orden = r.GetInt32(6), Creada = F(r.GetString(7)),
            Endpoint = r.IsDBNull(8) ? null : r.GetString(8), Modelo = r.IsDBNull(9) ? null : r.GetString(9),
            Catalogo = r.IsDBNull(10) ? null : r.GetString(10),
            Umbral5h = r.IsDBNull(11) ? null : r.GetDouble(11), UmbralSemana = r.IsDBNull(12) ? null : r.GetDouble(12),
            Presupuesto = r.IsDBNull(13) ? null : r.GetDouble(13),
        });

    public void GuardarCuenta(Cuenta c) => bd.Ejecutar("""
        INSERT INTO Cuenta (Id, Alias, Proveedor, Tipo, Carpeta, RefClave, Orden, Creada, Endpoint, Modelo, Catalogo, Umbral5h, UmbralSemana, Presupuesto)
        VALUES ($id, $alias, $prov, $tipo, $carpeta, $ref, $orden, $creada, $endpoint, $modelo, $catalogo, $u5, $usem, $presu)
        ON CONFLICT(Id) DO UPDATE SET Alias = excluded.Alias, Proveedor = excluded.Proveedor, Tipo = excluded.Tipo,
            Carpeta = excluded.Carpeta, RefClave = excluded.RefClave, Orden = excluded.Orden,
            Endpoint = excluded.Endpoint, Modelo = excluded.Modelo, Catalogo = excluded.Catalogo,
            Umbral5h = excluded.Umbral5h, UmbralSemana = excluded.UmbralSemana, Presupuesto = excluded.Presupuesto;
        """,
        ("$id", c.Id), ("$alias", c.Alias), ("$prov", c.Proveedor), ("$tipo", c.Tipo.ToString()), ("$carpeta", c.Carpeta),
        ("$ref", c.RefClave), ("$orden", c.Orden), ("$creada", F(c.Creada)), ("$endpoint", c.Endpoint), ("$modelo", c.Modelo), ("$catalogo", c.Catalogo),
        ("$u5", c.Umbral5h), ("$usem", c.UmbralSemana), ("$presu", c.Presupuesto));

    public void BorrarCuenta(string id) => bd.Ejecutar("DELETE FROM Cuenta WHERE Id = $id;", ("$id", id));

    // ---------- Proyectos ----------

    public List<Proyecto> Proyectos() => bd.Leer(
        "SELECT Id, Nombre, Ruta, Orden FROM Proyecto WHERE Oculto = 0 ORDER BY Orden, Nombre;",
        r => new Proyecto { Id = r.GetString(0), Nombre = r.GetString(1), Ruta = r.GetString(2), Orden = r.GetInt32(3) });

    public void GuardarProyecto(Proyecto p) => bd.Ejecutar("""
        INSERT INTO Proyecto (Id, Nombre, Ruta, Orden) VALUES ($id, $nombre, $ruta, $orden)
        ON CONFLICT(Id) DO UPDATE SET Nombre = excluded.Nombre, Ruta = excluded.Ruta, Orden = excluded.Orden;
        """, ("$id", p.Id), ("$nombre", p.Nombre), ("$ruta", p.Ruta), ("$orden", p.Orden));

    /// <summary>Quita un proyecto; sus sesiones quedan sin proyecto.</summary>
    public void BorrarProyecto(string id)
    {
        bd.Ejecutar("UPDATE Sesion SET ProyectoId = NULL WHERE ProyectoId = $id;", ("$id", id));
        bd.Ejecutar("DELETE FROM Proyecto WHERE Id = $id;", ("$id", id));
    }

    /// <summary>El proyecto de una carpeta (o el que la contiene más de cerca); null si no hay ninguno.</summary>
    public Proyecto? ProyectoDe(string carpeta)
    {
        var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(carpeta));
        return Proyectos()
            .Where(p =>
            {
                var r = Path.TrimEndingDirectorySeparator(p.Ruta);
                return c.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                       c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(p => p.Ruta.Length)
            .FirstOrDefault();
    }

    // ---------- Sesiones ----------

    private const string ColumnasSesion =
        "Id, ProyectoId, Titulo, Carpeta, CuentaFija, Modelo, Esfuerzo, ModoPermisos, Creada, UltimoUso, Archivada, EquipoId";

    private static Sesion LeerSesion(SqliteDataReader r) => new()
    {
        Id = r.GetString(0), ProyectoId = r.IsDBNull(1) ? null : r.GetString(1), Titulo = r.IsDBNull(2) ? null : r.GetString(2),
        Carpeta = r.GetString(3), CuentaId = r.IsDBNull(4) ? null : r.GetString(4), Modelo = r.IsDBNull(5) ? null : r.GetString(5),
        Esfuerzo = r.IsDBNull(6) ? null : r.GetString(6), ModoPermisos = r.IsDBNull(7) ? null : r.GetString(7),
        Creada = F(r.GetString(8)), UltimoUso = F(r.GetString(9)), Archivada = r.GetInt64(10) != 0,
        EquipoId = r.IsDBNull(11) ? null : r.GetString(11),
    };

    public List<Sesion> Sesiones(bool archivadas = false) => bd.Leer(
        $"SELECT {ColumnasSesion} FROM Sesion WHERE Archivada = $a ORDER BY UltimoUso DESC;", LeerSesion, ("$a", archivadas ? 1 : 0));

    public Sesion? Sesion(string id) => bd.Leer($"SELECT {ColumnasSesion} FROM Sesion WHERE Id = $id;", LeerSesion, ("$id", id)).FirstOrDefault();

    public void GuardarSesion(Sesion s) => bd.Ejecutar("""
        INSERT INTO Sesion (Id, ProyectoId, Titulo, Carpeta, CuentaFija, Modelo, Esfuerzo, ModoPermisos, Estado, Creada, UltimoUso, Archivada, EquipoId)
        VALUES ($id, $proy, $titulo, $carpeta, $cuenta, $modelo, $esfuerzo, $modo, 'Cerrada', $creada, $uso, $arch, $equipo)
        ON CONFLICT(Id) DO UPDATE SET ProyectoId = excluded.ProyectoId, Titulo = excluded.Titulo, Carpeta = excluded.Carpeta,
            CuentaFija = excluded.CuentaFija, Modelo = excluded.Modelo, Esfuerzo = excluded.Esfuerzo,
            ModoPermisos = excluded.ModoPermisos, UltimoUso = excluded.UltimoUso, Archivada = excluded.Archivada,
            EquipoId = excluded.EquipoId;
        """,
        ("$id", s.Id), ("$proy", s.ProyectoId), ("$titulo", s.Titulo), ("$carpeta", s.Carpeta), ("$cuenta", s.CuentaId),
        ("$modelo", s.Modelo), ("$esfuerzo", s.Esfuerzo), ("$modo", s.ModoPermisos), ("$creada", F(s.Creada)),
        ("$uso", F(s.UltimoUso)), ("$arch", s.Archivada ? 1 : 0), ("$equipo", s.EquipoId));

    // ---------- Tramos ----------

    public Tramo? UltimoTramo(string sesionId) => bd.Leer(
        "SELECT SesionId, Orden, CuentaId, SesionNativa, Desde, Hasta, Motivo FROM Tramo WHERE SesionId = $s ORDER BY Orden DESC LIMIT 1;",
        r => new Tramo
        {
            SesionId = r.GetString(0), Orden = r.GetInt32(1), CuentaId = r.IsDBNull(2) ? null : r.GetString(2),
            SesionNativa = r.IsDBNull(3) ? null : r.GetString(3), Desde = F(r.GetString(4)),
            Hasta = r.IsDBNull(5) ? null : F(r.GetString(5)), Motivo = r.IsDBNull(6) ? null : r.GetString(6),
        }, ("$s", sesionId)).FirstOrDefault();

    public void GuardarTramo(Tramo t) => bd.Ejecutar("""
        INSERT INTO Tramo (SesionId, Orden, CuentaId, SesionNativa, Desde, Hasta, Motivo)
        VALUES ($s, $o, $c, $n, $desde, $hasta, $motivo)
        ON CONFLICT(SesionId, Orden) DO UPDATE SET CuentaId = excluded.CuentaId, SesionNativa = excluded.SesionNativa,
            Hasta = excluded.Hasta, Motivo = excluded.Motivo;
        """,
        ("$s", t.SesionId), ("$o", t.Orden), ("$c", t.CuentaId), ("$n", t.SesionNativa), ("$desde", F(t.Desde)),
        ("$hasta", t.Hasta is { } h ? F(h) : null), ("$motivo", t.Motivo));

    // ---------- Equipos ----------

    public List<Equipo> Equipos()
    {
        var equipos = bd.Leer(
            "SELECT Id, Nombre, Regla, PermiteOtroProveedor, CuentaAtras, Predeterminado FROM Equipo ORDER BY Predeterminado DESC, Nombre;",
            r => new Equipo
            {
                Id = r.GetString(0), Nombre = r.GetString(1),
                Regla = Enum.TryParse<ReglaReparto>(r.GetString(2), out var regla) ? regla : ReglaReparto.Prioridad,
                PermiteOtroProveedor = r.GetInt64(3) != 0, CuentaAtras = r.GetInt32(4), Predeterminado = r.GetInt64(5) != 0,
            });
        var miembros = bd.Leer("SELECT EquipoId, CuentaId FROM EquipoCuenta ORDER BY EquipoId, Orden;", r => (E: r.GetString(0), C: r.GetString(1)));
        foreach (var e in equipos) e.Miembros = miembros.Where(m => m.E == e.Id).Select(m => m.C).ToList();
        return equipos;
    }

    public void GuardarEquipo(Equipo e)
    {
        bd.Ejecutar("""
            INSERT INTO Equipo (Id, Nombre, Regla, PermiteOtroProveedor, CuentaAtras, Predeterminado)
            VALUES ($id, $nombre, $regla, $otro, $atras, $pred)
            ON CONFLICT(Id) DO UPDATE SET Nombre = excluded.Nombre, Regla = excluded.Regla,
                PermiteOtroProveedor = excluded.PermiteOtroProveedor, CuentaAtras = excluded.CuentaAtras,
                Predeterminado = excluded.Predeterminado;
            """, ("$id", e.Id), ("$nombre", e.Nombre), ("$regla", e.Regla.ToString()), ("$otro", e.PermiteOtroProveedor ? 1 : 0),
            ("$atras", e.CuentaAtras), ("$pred", e.Predeterminado ? 1 : 0));
        bd.Ejecutar("DELETE FROM EquipoCuenta WHERE EquipoId = $id;", ("$id", e.Id));
        var cuentas = Cuentas().Select(c => c.Id).ToHashSet();
        var orden = 0;
        foreach (var c in e.Miembros.Distinct().Where(cuentas.Contains))
            bd.Ejecutar("INSERT INTO EquipoCuenta (EquipoId, CuentaId, Orden) VALUES ($e, $c, $o);", ("$e", e.Id), ("$c", c), ("$o", orden++));
    }

    public void BorrarEquipo(string id) => bd.Ejecutar("DELETE FROM Equipo WHERE Id = $id;", ("$id", id));

    // ---------- Uso ----------

    public void GuardarUso(UsoTurno u) => bd.Ejecutar("""
        INSERT INTO UsoTurno (SesionId, CuentaId, Momento, Entrada, Salida, Total, Coste, Moneda)
        VALUES ($s, $c, $m, $e, $o, $t, $coste, $moneda);
        """, ("$s", u.SesionId), ("$c", u.CuentaId), ("$m", F(u.Momento)), ("$e", u.Entrada), ("$o", u.Salida), ("$t", u.Total),
        ("$coste", u.Coste), ("$moneda", u.Moneda));

    public List<UsoTurno> Usos(DateTime desde) => bd.Leer(
        "SELECT SesionId, CuentaId, Momento, Entrada, Salida, Total, Coste, Moneda FROM UsoTurno WHERE Momento >= $d ORDER BY Momento;",
        r => new UsoTurno
        {
            SesionId = r.GetString(0), CuentaId = r.IsDBNull(1) ? null : r.GetString(1), Momento = F(r.GetString(2)),
            Entrada = r.GetInt64(3), Salida = r.GetInt64(4), Total = r.GetInt64(5),
            Coste = r.IsDBNull(6) ? null : r.GetDouble(6), Moneda = r.IsDBNull(7) ? null : r.GetString(7),
        }, ("$d", F(desde)));

    // ---------- Ajustes ----------

    public string? LeerAjuste(string clave) => bd.LeerAjuste(clave);
    public void GuardarAjuste(string clave, string? valor) => bd.GuardarAjuste(clave, valor);
}
