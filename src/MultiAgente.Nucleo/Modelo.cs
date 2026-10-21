namespace MultiAgente.Nucleo;

/// <summary>Cómo entra una cuenta: con el inicio de sesión de la CLI o con una clave de API.</summary>
public enum TipoCuenta { Login, Api }

/// <summary>Una cuenta de un proveedor. Nunca lleva la clave: solo la referencia a la bóveda.</summary>
public sealed class Cuenta
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Alias { get; set; } = "";
    /// <summary>Id del proveedor (<see cref="Proveedores"/>).</summary>
    public string Proveedor { get; set; } = "";
    public TipoCuenta Tipo { get; set; }
    /// <summary>Carpeta de la cuenta, relativa a la raíz portátil (<c>datos\cuentas\&lt;alias&gt;</c>).</summary>
    public string Carpeta { get; set; } = "";
    /// <summary>Entrada de la bóveda con la clave de API (cuentas <see cref="TipoCuenta.Api"/>).</summary>
    public string? RefClave { get; set; }
    /// <summary>
    /// API compatible: dirección a la que se manda el agente en lugar de la del proveedor (p. ej. GLM de Z.ai con el
    /// agente de Claude Code: <c>https://api.z.ai/api/anthropic</c>). null = la del proveedor.
    /// </summary>
    public string? Endpoint { get; set; }
    /// <summary>Modelo que se fija para la cuenta (API compatible). null = el que elija el agente.</summary>
    public string? Modelo { get; set; }
    /// <summary>Proveedor del catálogo (<see cref="CatalogoProveedores"/>) del que salen la dirección y las variables recomendadas.</summary>
    public string? Catalogo { get; set; }
    /// <summary>Consumo del bloque de 5 horas a partir del cual se releva (null = el predeterminado, 92 %).</summary>
    public double? Umbral5h { get; set; }
    /// <summary>Consumo semanal a partir del cual se releva (null = el predeterminado, 95 %).</summary>
    public double? UmbralSemana { get; set; }
    /// <summary>Presupuesto mensual (en la moneda que informe el agente, normalmente USD). null = sin límite.</summary>
    public double? Presupuesto { get; set; }
    public int Orden { get; set; }
    public DateTime Creada { get; set; } = DateTime.Now;
}

/// <summary>Una carpeta de trabajo con nombre.</summary>
public sealed class Proyecto
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Nombre { get; set; } = "";
    public string Ruta { get; set; } = "";
    public int Orden { get; set; }
}

/// <summary>Estado de una sesión para la interfaz.</summary>
public enum EstadoSesion { Cerrada, Arrancando, Esperando, Trabajando, PidePermiso, Error }

/// <summary>Una conversación de trabajo de MultiAgente sobre una carpeta (puede pasar por varias cuentas).</summary>
public sealed class Sesion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ProyectoId { get; set; }
    public string? Titulo { get; set; }
    public string Carpeta { get; set; } = "";
    /// <summary>Cuenta con la que trabaja ahora (la del tramo actual).</summary>
    public string? CuentaId { get; set; }
    /// <summary>Equipo que reparte y releva (modo «Automático»); null = cuenta fija.</summary>
    public string? EquipoId { get; set; }
    public string? Modelo { get; set; }
    public string? Esfuerzo { get; set; }
    public string? ModoPermisos { get; set; }
    public EstadoSesion Estado { get; set; }
    public DateTime Creada { get; set; } = DateTime.Now;
    public DateTime UltimoUso { get; set; } = DateTime.Now;
    public bool Archivada { get; set; }
}

/// <summary>Un tramo de la sesión con una cuenta y su sesión nativa del agente.</summary>
public sealed class Tramo
{
    public string SesionId { get; set; } = "";
    public int Orden { get; set; }
    public string? CuentaId { get; set; }
    public string? SesionNativa { get; set; }
    public DateTime Desde { get; set; } = DateTime.Now;
    public DateTime? Hasta { get; set; }
    public string? Motivo { get; set; }
}

/// <summary>Uso de un turno: tokens y coste que ha informado el agente (lo que no informe queda a cero o null).</summary>
public sealed record UsoTurno
{
    public string SesionId { get; init; } = "";
    public string? CuentaId { get; init; }
    public DateTime Momento { get; init; } = DateTime.Now;
    public long Entrada { get; init; }
    public long Salida { get; init; }
    public long Total { get; init; }
    public double? Coste { get; init; }
    public string? Moneda { get; init; }
}

/// <summary>Error que se enseña tal cual al usuario (lenguaje llano).</summary>
public sealed class ExcepcionUsuario(string mensaje, Exception? causa = null) : Exception(mensaje, causa);
