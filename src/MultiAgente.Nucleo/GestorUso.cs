namespace MultiAgente.Nucleo;

/// <summary>Estadísticas sencillas y límites de consumo (umbrales y presupuesto mensual por cuenta).</summary>
public sealed partial class Gestor
{
    /// <summary>Gasto del mes en curso de cada cuenta, a partir del uso guardado (coste que informan los agentes).</summary>
    private void CargarGastos()
    {
        var ahora = DateTime.Now;
        _mesGastos = ahora.ToString("yyyyMM");
        var usos = _repo.Usos(new DateTime(ahora.Year, ahora.Month, 1));
        foreach (var c in _repo.Cuentas())
        {
            var suyos = usos.Where(u => u.CuentaId == c.Id && u.Coste is not null).ToList();
            var gasto = suyos.Count == 0 ? (double?)null : suyos.Sum(u => u.Coste!.Value);
            _estados[c.Id] = (_estados.GetValueOrDefault(c.Id) ?? new EstadoCuenta()) with { GastoMes = gasto };
            if (suyos.LastOrDefault(u => u.Moneda is not null)?.Moneda is { } m) _monedas[c.Id] = m;
        }
    }

    /// <summary>Guarda el uso de un turno, suma el gasto del mes y vigila el presupuesto de la cuenta.</summary>
    private void AnotarUso(SesionAgente a, UsoTurno uso)
    {
        _repo.GuardarUso(uso);
        GuardarCosteContado(a);
        if (uso.Coste is not { } coste || coste <= 0) return;
        if (uso.Moneda is not null) _monedas[a.Cuenta.Id] = uso.Moneda;
        var previo = _estados.GetValueOrDefault(a.Cuenta.Id) ?? new EstadoCuenta();
        var gasto = (previo.GastoMes ?? 0) + coste;
        _estados[a.Cuenta.Id] = previo with { GastoMes = gasto };
        var cuenta = _repo.Cuentas().FirstOrDefault(c => c.Id == a.Cuenta.Id);
        if (cuenta?.Presupuesto is not { } presupuesto || presupuesto <= 0) return;
        var moneda = _monedas.GetValueOrDefault(cuenta.Id) ?? "USD";
        if (gasto >= presupuesto)
        {
            AvisarUnaVez($"presupuesto100:{cuenta.Id}:{_mesGastos}",
                $"«{cuenta.Alias}» ha gastado su presupuesto del mes ({gasto:0.00} de {presupuesto:0.00} {moneda}). No se usará más este mes salvo que lo amplíes.");
            EnviarEstado();
            RevisarCuentasAgotadas();
        }
        else if (gasto >= presupuesto * 0.8)
        {
            AvisarUnaVez($"presupuesto80:{cuenta.Id}:{_mesGastos}",
                $"«{cuenta.Alias}» lleva gastado el {gasto / presupuesto:P0} de su presupuesto del mes ({gasto:0.00} de {presupuesto:0.00} {moneda}).");
            EnviarEstado();
        }
    }

    private void AvisarUnaVez(string clave, string texto)
    {
        if (_repo.LeerAjuste("aviso:" + clave) is not null) return;
        _repo.GuardarAjuste("aviso:" + clave, DateTime.Now.ToString("o"));
        Registro.Info(texto);
        _enviar(new { tipo = "aviso", texto });
    }

    /// <summary>Umbrales de relevo y presupuesto mensual de una cuenta (null = el predeterminado / sin presupuesto).</summary>
    public void GuardarLimites(string cuentaId, double? umbral5h, double? umbralSemana, double? presupuesto)
    {
        foreach (var u in new[] { umbral5h, umbralSemana })
            if (u is < 10 or > 100) throw new ExcepcionUsuario("Los umbrales van del 10 al 100 %.");
        if (presupuesto is < 0) throw new ExcepcionUsuario("El presupuesto no puede ser negativo.");
        var c = Cuenta(cuentaId);
        c.Umbral5h = umbral5h is { } a && a != Enrutador.Umbral5h ? a : null;
        c.UmbralSemana = umbralSemana is { } b && b != Enrutador.UmbralSemana ? b : null;
        c.Presupuesto = presupuesto is > 0 ? presupuesto : null;
        _repo.GuardarCuenta(c);
        Registro.Info($"Límites de {c.Alias}: 5 h {c.Umbral5h}, semana {c.UmbralSemana}, presupuesto {c.Presupuesto}");
        EnviarEstado();
        RevisarCuentasAgotadas();
    }

    /// <summary>Estadísticas de los últimos <paramref name="dias"/> días (1 = hoy): por cuenta, por sesión y por día.</summary>
    public object Estadisticas(int dias)
    {
        dias = Math.Clamp(dias, 1, 365);
        var desde = DateTime.Today.AddDays(1 - dias);
        var usos = _repo.Usos(desde);
        var cuentas = _repo.Cuentas().ToDictionary(c => c.Id);
        var sesiones = _repo.Sesiones().Concat(_repo.Sesiones(archivadas: true)).ToDictionary(s => s.Id);
        object Suma(IEnumerable<UsoTurno> g)
        {
            var l = g.ToList();
            var conCoste = l.Where(u => u.Coste is not null).ToList();
            return new
            {
                turnos = l.Count, tokens = l.Sum(u => u.Total), entrada = l.Sum(u => u.Entrada), salida = l.Sum(u => u.Salida),
                coste = conCoste.Count == 0 ? (double?)null : conCoste.Sum(u => u.Coste!.Value),
                moneda = l.LastOrDefault(u => u.Moneda is not null)?.Moneda,
            };
        }
        return new
        {
            tipo = "estadisticas",
            dias,
            total = Suma(usos),
            porCuenta = usos.GroupBy(u => u.CuentaId ?? "").OrderByDescending(g => g.Sum(u => u.Total)).Select(g => new
            {
                cuenta = cuentas.GetValueOrDefault(g.Key)?.Alias ?? "(cuenta quitada)",
                proveedor = cuentas.GetValueOrDefault(g.Key)?.Proveedor,
                catalogo = cuentas.GetValueOrDefault(g.Key)?.Catalogo,
                datos = Suma(g),
            }),
            porSesion = usos.GroupBy(u => u.SesionId).OrderByDescending(g => g.Sum(u => u.Total)).ThenByDescending(g => g.Count()).Take(10).Select(g => new
            {
                id = g.Key,
                titulo = sesiones.GetValueOrDefault(g.Key)?.Titulo ?? "(sesión sin título)",
                cuentas = g.Select(u => cuentas.GetValueOrDefault(u.CuentaId ?? "")?.Alias).OfType<string>().Distinct(),
                datos = Suma(g),
            }),
            porDia = Enumerable.Range(0, dias).Select(i => desde.AddDays(i)).Select(d => new
            {
                dia = d.ToString("yyyy-MM-dd"),
                datos = Suma(usos.Where(u => u.Momento.Date == d)),
            }),
        };
    }
}
