namespace MultiAgente.Nucleo;

/// <summary>Proyectos para ordenar las sesiones, nombre y sitio de cada sesión, visor de cambios y espera de logins.</summary>
public sealed partial class Gestor
{
    // ================================================================= proyectos

    /// <summary>Crea o cambia un proyecto: un nombre y la carpeta donde empiezan sus sesiones nuevas.</summary>
    public Proyecto GuardarProyecto(string? id, string nombre, string ruta)
    {
        nombre = nombre.Trim();
        if (nombre.Length == 0) throw new ExcepcionUsuario("Ponle un nombre al proyecto.");
        if (string.IsNullOrWhiteSpace(ruta) || !Directory.Exists(ruta)) throw new ExcepcionUsuario("Elige una carpeta que exista.");
        var proyectos = _repo.Proyectos();
        var p = id is null ? null : proyectos.FirstOrDefault(x => x.Id == id);
        if (id is not null && p is null) throw new ExcepcionUsuario("Ese proyecto ya no existe.");
        p ??= new Proyecto { Orden = proyectos.Count == 0 ? 0 : proyectos.Max(x => x.Orden) + 1 };
        p.Nombre = nombre.Length <= 60 ? nombre : nombre[..60];
        p.Ruta = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ruta));
        _repo.GuardarProyecto(p);
        Registro.Info($"Proyecto «{p.Nombre}» en {p.Ruta}");
        EnviarEstado();
        return p;
    }

    /// <summary>Quita un proyecto. Sus sesiones no se borran: quedan en «Sin proyecto».</summary>
    public void BorrarProyecto(string id)
    {
        _repo.BorrarProyecto(id);
        foreach (var a in _vivas.Values.Where(a => a.Sesion.ProyectoId == id)) a.Sesion.ProyectoId = null;
        EnviarEstado();
    }

    /// <summary>
    /// Pasa una sesión a otro proyecto (o a ninguno). Solo cambia dónde se ve: su carpeta de trabajo sigue igual. Si estaba
    /// archivada, se restaura en ese proyecto.
    /// </summary>
    public void MoverSesion(string sesionId, string? proyectoId)
    {
        if (proyectoId is not null && _repo.Proyectos().All(p => p.Id != proyectoId))
            throw new ExcepcionUsuario("Ese proyecto ya no existe.");
        CambiarSesion(sesionId, s => { s.ProyectoId = proyectoId; s.Archivada = false; });
    }

    /// <summary>Cambia el nombre de una sesión (el agente ya no lo propondrá).</summary>
    public void RenombrarSesion(string sesionId, string titulo)
    {
        titulo = titulo.Trim();
        if (titulo.Length == 0) throw new ExcepcionUsuario("Escribe un nombre.");
        CambiarSesion(sesionId, s => s.Titulo = titulo.Length <= 80 ? titulo : titulo[..80]);
    }

    /// <summary>Cambia una sesión en la base y, si está abierta, también en su agente (que la vuelve a guardar en cada turno).</summary>
    private void CambiarSesion(string sesionId, Action<Sesion> cambio)
    {
        var s = _repo.Sesion(sesionId) ?? throw new ExcepcionUsuario("Esa sesión ya no existe.");
        cambio(s);
        _repo.GuardarSesion(s);
        if (_vivas.TryGetValue(sesionId, out var viva)) cambio(viva.Sesion);
        EnviarEstado();
    }

    // ================================================================= visor de cambios

    /// <summary>Lo que ha cambiado en la carpeta de la sesión desde el último commit (null si no es un repositorio git).</summary>
    public Task<CambiosCarpeta?> Cambios(string sesionId) => CambiosGit.Leer(CarpetaDeSesion(sesionId));

    /// <summary>Diferencias de un fichero de la carpeta de la sesión.</summary>
    public Task<DiferenciasFichero> DiferenciasDe(string sesionId, string ruta) => CambiosGit.Diferencias(CarpetaDeSesion(sesionId), ruta);

    private string CarpetaDeSesion(string sesionId)
    {
        var s = _repo.Sesion(sesionId) ?? throw new ExcepcionUsuario("Esa sesión ya no existe.");
        if (!Directory.Exists(s.Carpeta)) throw new ExcepcionUsuario($"La carpeta de la sesión ya no existe: {s.Carpeta}");
        return s.Carpeta;
    }

    // ================================================================= logins

    /// <summary>
    /// Cuentas con la ventana de «Autorizar…» abierta: en cuanto tienen su sesión iniciada se avisa a la interfaz (sin
    /// esperar a que el usuario cambie de pantalla). Se deja de mirar a los 15 minutos.
    /// </summary>
    private void RevisarLogins()
    {
        if (_esperandoLogin.Count == 0) return;
        var ahora = DateTime.Now;
        var cuentas = _repo.Cuentas().ToDictionary(c => c.Id);
        var listas = false;
        foreach (var (id, hasta) in _esperandoLogin.ToList())
        {
            if (!cuentas.TryGetValue(id, out var c) || hasta < ahora) { _esperandoLogin.Remove(id); continue; }
            if (!SinFallo(() => _op.Autorizada(c, _rutas, _boveda))) continue;
            _esperandoLogin.Remove(id);
            _consultado.Remove(id);
            Registro.Info($"La cuenta {c.Alias} ya tiene la sesión iniciada");
            _enviar(new { tipo = "aviso", texto = $"«{c.Alias}» ya está autorizada." });
            listas = true;
        }
        if (listas) EnviarEstado();
    }
}
