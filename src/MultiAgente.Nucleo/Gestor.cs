using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MultiAgente.Nucleo.Acp;

namespace MultiAgente.Nucleo;

/// <summary>Lo que el <see cref="Gestor"/> toma del sistema; las pruebas lo sustituyen.</summary>
public sealed class OpcionesGestor
{
    public Func<IReadOnlyList<Requisito>> ComprobarRequisitos { get; init; } = () => Requisitos.Comprobar();
    /// <summary>Orden ACP de un proveedor con los requisitos encontrados (null = no instalado).</summary>
    public Func<Proveedor, IReadOnlyList<Requisito>, OrdenAgente?> OrdenAgente { get; init; } = (p, r) => Proveedores.OrdenAcp(p, r);
    public Func<Cuenta, Rutas, Boveda, bool> Autorizada { get; init; } = Proveedores.Autorizada;
    /// <summary>Consumo de una cuenta (null = no se sabe). Por defecto, solo las suscripciones de Claude lo dan.</summary>
    public Func<Cuenta, string, Task<EstadoCuenta?>> Consumo { get; init; } = (c, carpeta) =>
        c.Proveedor == "Claude" && c.Tipo == TipoCuenta.Login ? ConsumoClaude.Obtener(carpeta) : Task.FromResult<EstadoCuenta?>(null);
    /// <summary>Genera el resumen vivo; null = con <c>claude -p</c> y una cuenta de Claude con margen.</summary>
    public Func<string, Task<string?>>? GenerarResumen { get; init; }
    /// <summary>Variables añadidas al entorno de cada agente (pruebas).</summary>
    public IReadOnlyDictionary<string, string?>? EntornoExtra { get; init; }
    /// <summary>Temporizadores de consumo y cuenta atrás (las pruebas los llaman a mano).</summary>
    public bool Temporizadores { get; init; } = true;
    /// <summary>Compartir las conversaciones y extensiones entre cuentas de un proveedor con uniones NTFS.</summary>
    public bool CompartirCarpetas { get; init; } = true;
    /// <summary>Carpeta de usuario de la que se importan MCP, skills e instrucciones (solo lectura).</summary>
    public string PerfilUsuario { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

/// <summary>
/// El «cerebro»: cuentas, equipos, proyectos, sesiones y agentes vivos, consumo, enfriamiento, relevo y resumen vivo.
/// Se usa desde un solo hilo (el de la ventana): lo que llega de los agentes y de los temporizadores se pasa a ese hilo
/// con <c>enUi</c> antes de tocar nada. Lo que la interfaz tiene que saber sale por <c>enviar</c>.
/// </summary>
public sealed partial class Gestor : IAsyncDisposable
{
    private readonly Rutas _rutas;
    private readonly IRepositorio _repo;
    private readonly Boveda _boveda;
    private readonly Action<object> _enviar;
    private readonly Action<Action> _enUi;
    private readonly string _version;
    private readonly OpcionesGestor _op;
    private readonly Dictionary<string, SesionAgente> _vivas = [];
    private readonly Dictionary<string, EstadoCuenta> _estados = [];
    private readonly Dictionary<string, DateTime> _consultado = [];
    private readonly Dictionary<string, RelevoPendiente> _relevos = [];
    /// <summary>Mensajes que esperan a que arranque la cuenta del relevo («Continúa…» y la cola de la cuenta agotada).</summary>
    private readonly Dictionary<string, List<(string Texto, IReadOnlyList<Adjunto> Adjuntos)>> _aMandar = [];
    private readonly HashSet<string> _resumiendo = [];
    private readonly Dictionary<string, string> _monedas = [];
    private readonly Dictionary<string, DateTime> _esperandoLogin = [];
    private string _mesGastos = "";
    private IReadOnlyList<Requisito> _requisitos;
    private CatalogoProveedores _catalogo;
    private Timer? _reloj;
    private Traductor? _traductor;
    private int _vueltas;

    /// <summary>Un relevo propuesto, a la espera de su cuenta atrás y de que termine el turno.</summary>
    private sealed record RelevoPendiente(string SesionId, string Origen, string Destino, string Motivo, DateTime Hasta, bool Continuar, int Intento);

    public Gestor(Rutas rutas, IRepositorio repo, Action<object> enviar, Action<Action> enUi, string version, OpcionesGestor? opciones = null)
    {
        _rutas = rutas;
        _repo = repo;
        _boveda = new Boveda(rutas.Boveda);
        _enviar = enviar;
        _enUi = enUi;
        _version = version;
        _op = opciones ?? new OpcionesGestor();
        _requisitos = _op.ComprobarRequisitos();
        _catalogo = CatalogoProveedores.Cargar(rutas.Catalogo);
        CargarEnfriamientos();
        CargarGastos();
        AsegurarEquipoPrincipal();
        if (_op.Temporizadores)
            _reloj = new Timer(_ => _enUi(() => _ = Vuelta()), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
    }

    public string Adjuntos => _rutas.Adjuntos;
    public bool ResumenVivoActivo => _repo.LeerAjuste("resumenVivo") != "no";

    // ================================================================= estado para la interfaz

    public object Estado(bool recomprobar = false)
    {
        if (recomprobar)
        {
            _requisitos = _op.ComprobarRequisitos();
            _catalogo = CatalogoProveedores.Cargar(_rutas.Catalogo);
        }
        var ahora = DateTime.Now;
        var cuentas = _repo.Cuentas();
        var porId = cuentas.ToDictionary(c => c.Id);
        var candidatas = Candidatas(cuentas).ToDictionary(c => c.Cuenta.Id);
        var sesiones = _repo.Sesiones();
        var proyectos = _repo.Proyectos();
        object Sesion(Sesion s)
        {
            var viva = _vivas.GetValueOrDefault(s.Id);
            var cuenta = s.CuentaId is null ? null : porId.GetValueOrDefault(s.CuentaId);
            return new
            {
                s.Id, s.ProyectoId, titulo = s.Titulo ?? "Sesión nueva", s.Carpeta, estado = viva?.Estado ?? EstadoSesion.Cerrada,
                proveedor = cuenta?.Proveedor, cuenta = cuenta?.Alias, cuentaId = cuenta?.Id, s.EquipoId, s.UltimoUso,
            };
        }
        return new
        {
            tipo = "estado",
            version = _version,
            raiz = _rutas.Raiz,
            requisitos = _requisitos.Select(r => new { r.Id, r.Nombre, r.Proveedor, r.Paquete, r.Encontrado, r.Version }),
            proveedores = Proveedores.Todos.Select(p => new { p.Id, p.Nombre, listo = Instalado(p.Id), p.DondeClave }),
            catalogo = new
            {
                version = _catalogo.Version,
                proveedores = _catalogo.Proveedores.Select(e => new
                {
                    e.Id, e.Nombre, e.Agente, e.BaseUrl, e.ClaveUrl, e.SinClave, e.Modelos, e.SoloModelos, e.Traducir, listar = e.Listar is not null,
                    e.Notas, e.Confianza, fuente = e.Fuentes.FirstOrDefault(),
                }),
            },
            puedeInstalar = File.Exists(Path.Combine(_rutas.Herramientas, "Instalar-Requisitos.ps1")),
            resumenVivo = ResumenVivoActivo,
            cuentas = cuentas.Select(c =>
            {
                var e = _estados.GetValueOrDefault(c.Id)?.AHora(ahora);
                return new
                {
                    c.Id, c.Alias, c.Proveedor, tipo = c.Tipo.ToString(), c.Endpoint, c.Modelo, c.Catalogo,
                    catalogoNombre = _catalogo.Buscar(c.Catalogo)?.Nombre,
                    autorizada = SinFallo(() => _op.Autorizada(c, _rutas, _boveda)),
                    problema = ProblemaDeCuenta(c),
                    listo = Instalado(c.Proveedor),
                    consumo = e is null ? null : new
                    {
                        pct5h = e.Pct5h, pctSemana = e.PctSemana, reinicio5h = e.Reinicio5h, reinicioSemana = e.ReinicioSemana,
                        enfriandoHasta = e.Enfriando(ahora) ? e.EnfriandoHasta : null, motivo = e.Enfriando(ahora) ? e.Motivo : null,
                        momento = e.Pct5h is null && e.PctSemana is null ? (DateTime?)null : e.Momento,
                    },
                    sinMargen = Enrutador.SinMargen(candidatas[c.Id], ahora),
                    limites = new { umbral5h = c.Umbral5h ?? Enrutador.Umbral5h, umbralSemana = c.UmbralSemana ?? Enrutador.UmbralSemana, presupuesto = c.Presupuesto },
                    gastoMes = e?.GastoMes, moneda = _monedas.GetValueOrDefault(c.Id),
                };
            }),
            equipos = _repo.Equipos().Select(e => new
            {
                e.Id, e.Nombre, regla = e.Regla.ToString(), e.PermiteOtroProveedor, e.CuentaAtras, e.Predeterminado, e.Miembros,
            }),
            proyectos = proyectos.Select(p => new { p.Id, p.Nombre, p.Ruta, sesiones = sesiones.Where(s => s.ProyectoId == p.Id).Select(Sesion) }),
            sueltas = sesiones.Where(s => s.ProyectoId is null || proyectos.All(p => p.Id != s.ProyectoId)).Select(Sesion),
            archivadas = _repo.Sesiones(archivadas: true).Take(100).Select(Sesion),
        };
    }

    /// <summary>Lo que impide que la cuenta funcione aunque esté autorizada (p. ej. un modelo que su proveedor no admite).</summary>
    private string? ProblemaDeCuenta(Cuenta c) =>
        _catalogo.Buscar(c.Catalogo) is { SoloModelos: true } e && !e.Modelos.Contains(c.Modelo ?? "")
            ? $"«{c.Modelo}» no funciona con {e.Nombre}. Quita la cuenta y créala con uno de sus modelos."
            : null;

    private bool Instalado(string proveedor) =>
        Proveedores.Buscar(proveedor) is { } p && _op.OrdenAgente(p, _requisitos) is not null;

    private static bool SinFallo(Func<bool> f)
    {
        try { return f(); } catch (ExcepcionUsuario) { return false; }
    }

    private void EnviarEstado() => _enviar(Estado());

    private List<Candidata> Candidatas(List<Cuenta>? cuentas = null) =>
        (cuentas ?? _repo.Cuentas()).Select(c => new Candidata(c,
            Instalado(c.Proveedor) && SinFallo(() => _op.Autorizada(c, _rutas, _boveda)) && ProblemaDeCuenta(c) is null,
            _estados.GetValueOrDefault(c.Id)?.AHora(DateTime.Now))).ToList();

    // ================================================================= cuentas

    public void CrearCuenta(string proveedor, string tipo, string alias, string? clave, string? catalogoId, string? endpoint, string? modelo)
    {
        var entrada = _catalogo.Buscar(catalogoId);
        if (entrada is not null)
        {
            proveedor = entrada.Agente;
            tipo = "Api";
            endpoint = string.IsNullOrWhiteSpace(endpoint) ? entrada.BaseUrl : endpoint;
            if (entrada.SinClave && string.IsNullOrWhiteSpace(clave)) clave = entrada.ClaveFija;
            if (endpoint?.Contains('{') == true)
                throw new ExcepcionUsuario("Completa la dirección de la API (cambia lo que va entre llaves por tus datos).");
            if (entrada.SoloModelos && !entrada.Modelos.Contains(modelo?.Trim() ?? ""))
                throw new ExcepcionUsuario($"Con {entrada.Nombre} solo funcionan estos modelos: {string.Join(", ", entrada.Modelos)}.");
        }
        var p = Proveedores.Buscar(proveedor) ?? throw new ExcepcionUsuario("Elige un proveedor.");
        alias = alias.Trim();
        if (alias.Length is < 2 or > 40) throw new ExcepcionUsuario("El nombre de la cuenta debe tener entre 2 y 40 caracteres.");
        if (_repo.Cuentas().Any(c => c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)))
            throw new ExcepcionUsuario($"Ya hay una cuenta que se llama «{alias}».");
        var t = tipo == "Api" ? TipoCuenta.Api : TipoCuenta.Login;
        endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim();
        if (endpoint is not null)
        {
            if (p == Proveedores.Gemini) throw new ExcepcionUsuario("Las API compatibles se usan con el agente de Claude Code o con el de Codex.");
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http"))
                throw new ExcepcionUsuario("La dirección de la API no es válida (debe empezar por https://).");
            t = TipoCuenta.Api;
        }
        if (t == TipoCuenta.Api && string.IsNullOrWhiteSpace(clave)) throw new ExcepcionUsuario("Falta la clave de API.");

        var cuenta = new Cuenta
        {
            Alias = alias, Proveedor = p.Id, Tipo = t, Endpoint = endpoint, Catalogo = entrada?.Id,
            Modelo = string.IsNullOrWhiteSpace(modelo) ? null : modelo.Trim(),
            Orden = _repo.Cuentas().Count,
        };
        var carpeta = Path.Combine(_rutas.Cuentas, NombreCarpeta(alias));
        for (var i = 2; Directory.Exists(carpeta); i++) carpeta = Path.Combine(_rutas.Cuentas, $"{NombreCarpeta(alias)}-{i}");
        Directory.CreateDirectory(carpeta);
        cuenta.Carpeta = _rutas.ParaGuardar(carpeta);
        if (t == TipoCuenta.Api)
        {
            cuenta.RefClave = cuenta.Id;
            _boveda.Guardar(cuenta.RefClave, clave!.Trim());
        }
        _repo.GuardarCuenta(cuenta);
        // Toda cuenta nueva entra al final del equipo predeterminado
        AsegurarEquipoPrincipal();
        var principal = _repo.Equipos().FirstOrDefault(e => e.Predeterminado);
        if (principal is not null && !principal.Miembros.Contains(cuenta.Id))
        {
            principal.Miembros.Add(cuenta.Id);
            _repo.GuardarEquipo(principal);
        }
        Registro.Info($"Cuenta nueva: {cuenta.Alias} ({p.Id}, {t}{(endpoint is null ? "" : ", " + endpoint)})");
        EnviarEstado();
    }

    private static string NombreCarpeta(string alias)
    {
        var s = Regex.Replace(alias.Normalize(NormalizationForm.FormD), @"\p{Mn}", "").ToLowerInvariant();
        s = Regex.Replace(s, "[^a-z0-9]+", "-").Trim('-');
        return s.Length == 0 ? "cuenta" : s;
    }

    public void CambiarClave(string id, string clave)
    {
        var c = Cuenta(id);
        if (c.Tipo != TipoCuenta.Api || string.IsNullOrWhiteSpace(clave)) throw new ExcepcionUsuario("Escribe la clave nueva.");
        c.RefClave ??= c.Id;
        _boveda.Guardar(c.RefClave, clave.Trim());
        _repo.GuardarCuenta(c);
        QuitarEnfriamiento(c.Id);
        EnviarEstado();
    }

    public async Task QuitarCuenta(string id)
    {
        var c = Cuenta(id);
        foreach (var (sid, viva) in _vivas.Where(v => v.Value.Cuenta.Id == id).ToList())
        {
            _vivas.Remove(sid);
            await viva.DisposeAsync();
        }
        if (c.RefClave is not null) _boveda.Borrar(c.RefClave);
        _repo.BorrarCuenta(id);
        _estados.Remove(id);
        var carpeta = _rutas.Resolver(c.Carpeta);
        if (carpeta.StartsWith(_rutas.Cuentas, StringComparison.OrdinalIgnoreCase) && Directory.Exists(carpeta))
        {
            try
            {
                // Primero las uniones (sin seguirlas: lo compartido es de las demás cuentas)
                foreach (var sub in CarpetasCompartidas.De(c.Proveedor))
                {
                    var enlace = Path.Combine(carpeta, sub);
                    if (CarpetasCompartidas.Destino(enlace) is not null) Directory.Delete(enlace);
                }
                Directory.Delete(carpeta, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Registro.Aviso($"No se pudo borrar la carpeta de la cuenta {c.Alias}: {ex.Message}");
            }
        }
        Registro.Info($"Cuenta quitada: {c.Alias}");
        EnviarEstado();
    }

    /// <summary>Abre una ventana de consola con la CLI del proveedor para iniciar sesión con esa cuenta.</summary>
    public void Autorizar(string id)
    {
        var c = Cuenta(id);
        var p = Proveedores.Buscar(c.Proveedor)!;
        var orden = Requisitos.Orden(p.RequisitoLogin, _requisitos)
                    ?? throw new ExcepcionUsuario($"Falta {p.Nombre} en este equipo. Instálalo primero (pantalla de inicio).");
        var carpeta = _rutas.Resolver(c.Carpeta);
        Directory.CreateDirectory(carpeta);
        var cmd = Path.Combine(carpeta, "autorizar.cmd");
        static string Cita(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var paso = p == Proveedores.Gemini
            ? "echo Elige «Login with Google» y sigue los pasos del navegador. Despues escribe /quit."
            : "echo Sigue los pasos del navegador con el correo de esta cuenta.";
        File.WriteAllText(cmd, string.Join("\r\n",
            "@echo off",
            "chcp 65001 > nul",
            $"title Autorizar {c.Alias} ({p.Nombre})",
            paso,
            "echo.",
            $"{Cita(orden.Ejecutable)} {string.Join(" ", orden.Argumentos.Concat(p.ArgumentosLogin).Select(Cita))}",
            "echo.",
            "echo Ya puedes cerrar esta ventana y volver a MultiAgente.",
            "pause > nul"), new UTF8Encoding(false));
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = carpeta,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(cmd);
        foreach (var (k, v) in Proveedores.Entorno(p, carpeta, null))
            if (v is null) psi.Environment.Remove(k); else psi.Environment[k] = v;
        Process.Start(psi);
        QuitarEnfriamiento(c.Id);
        _esperandoLogin[c.Id] = DateTime.Now.AddMinutes(15);
        Registro.Info($"Autorizar la cuenta {c.Alias} ({p.Id})");
    }

    /// <summary>
    /// Antes de dar de alta (o cambiar) una cuenta que va por el traductor, prueba el modelo: hay proveedores (NVIDIA) que
    /// listan modelos que luego no sirven a todas las cuentas. Sin modelo elegido, no se puede usar.
    /// </summary>
    public async Task ComprobarModelo(string? catalogoId, string? endpoint, string? clave, string? modelo)
    {
        var entrada = _catalogo.Buscar(catalogoId);
        if (entrada is not { Traducir: true }) return;
        if (string.IsNullOrWhiteSpace(modelo)) throw new ExcepcionUsuario("Elige un modelo (pulsa «Actualizar lista» para ver los del proveedor).");
        var url = string.IsNullOrWhiteSpace(endpoint) ? entrada.BaseUrl : endpoint;
        if (url is null || url.Contains('{')) return;
        if (await Traductor.ProbarModelo(url, clave, modelo.Trim()) is { } problema) throw new ExcepcionUsuario(problema);
    }

    /// <summary>Lo que no es un modelo de chat (embeddings, reordenar, seguridad, imagen…): no se prueba.</summary>
    private static readonly System.Text.RegularExpressions.Regex NoEsChat = new(
        @"embed|rerank|retriev|guard|safety|reward|parse|clip|vila|deplot|kosmos|fuyu|neva|detector|calibration|translate|starcoder|codegemma|recurrentgemma|llama2|whisper|tts|asr|diffusion|flux|sdxl|stable",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Prueba con la clave de la cuenta todos los modelos de chat que lista el proveedor (de 4 en 4, con una petición mínima
    /// cada uno) y devuelve los que de verdad le sirve: NVIDIA lista muchos que no da a todas las cuentas.
    /// </summary>
    public async Task<(List<string> Funcionan, int Probados)> ProbarModelosDe(string id)
    {
        var c = Cuenta(id);
        var entrada = _catalogo.Buscar(c.Catalogo);
        if (entrada is not { Traducir: true } || c.Endpoint is null) throw new ExcepcionUsuario("Esta prueba es para las cuentas que van por el traductor (NVIDIA, Groq…).");
        var clave = c.RefClave is null ? null : _boveda.Obtener(c.RefClave);
        var modelos = (await ListarModelos(c.Catalogo, c.Endpoint, clave, null)).Select(m => m.Id).Where(m => !NoEsChat.IsMatch(m)).ToList();
        var funcionan = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var turnos = new SemaphoreSlim(4);
        await Task.WhenAll(modelos.Select(async m =>
        {
            await turnos.WaitAsync();
            try
            {
                for (var intento = 0; intento < 3; intento++)
                {
                    var (codigo, _) = await Traductor.Probar(c.Endpoint, clave, m);
                    if (codigo == 200) { funcionan.Add(m); return; }
                    if (codigo is not (429 or 0 or >= 500)) return;
                    await Task.Delay(2500 * (intento + 1));
                }
            }
            finally { turnos.Release(); }
        }));
        Registro.Info($"Modelos que funcionan con {c.Alias}: {funcionan.Count} de {modelos.Count}: {string.Join(", ", funcionan.Order())}");
        return ([.. funcionan.Order(StringComparer.OrdinalIgnoreCase)], modelos.Count);
    }

    /// <summary>
    /// Cambia el modelo de una cuenta de API; sus sesiones abiertas se reabren con él (la conversación sigue). Se pueden dar
    /// varios separados por comas: se prueban y se queda el primero que funcione. Devuelve lo que ha pasado.
    /// </summary>
    public async Task<string> CambiarModelo(string id, string modelo)
    {
        var c = Cuenta(id);
        var varios = modelo.Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var aviso = "";
        if (varios.Length > 1)
        {
            var clave = c.RefClave is null ? null : _boveda.Obtener(c.RefClave);
            var ok = new List<string>();
            foreach (var m in varios)
                if (c.Endpoint is null || await Traductor.ProbarModelo(c.Endpoint, clave, m) is null) ok.Add(m);
            if (ok.Count == 0) throw new ExcepcionUsuario($"Ninguno de esos modelos está disponible para tu cuenta: {string.Join(", ", varios)}.");
            aviso = ok.Count == varios.Length ? "" : $" No funcionan: {string.Join(", ", varios.Except(ok))}.";
            modelo = ok[0];
        }
        modelo = modelo.Trim();
        if (c.Tipo != TipoCuenta.Api || c.Catalogo is null) throw new ExcepcionUsuario("Esta cuenta usa el modelo que elijas en cada sesión.");
        var entrada = _catalogo.Buscar(c.Catalogo);
        if (entrada?.SoloModelos == true && !entrada.Modelos.Contains(modelo))
            throw new ExcepcionUsuario($"Con {entrada.Nombre} solo funcionan estos modelos: {string.Join(", ", entrada.Modelos)}.");
        if (modelo.Length == 0) throw new ExcepcionUsuario("Escribe el modelo.");
        await ComprobarModelo(c.Catalogo, c.Endpoint, c.RefClave is null ? null : _boveda.Obtener(c.RefClave), modelo);
        c.Modelo = modelo;
        _repo.GuardarCuenta(c);
        Registro.Info($"Modelo de {c.Alias}: {modelo}");
        EnviarEstado();
        foreach (var viva in _vivas.Values.Where(v => v.Cuenta.Id == id).ToList())
        {
            _vivas.Remove(viva.Sesion.Id);
            await viva.DisposeAsync();
            await AbrirSesion(viva.Sesion.Id);
        }
        return $"Modelo de «{c.Alias}»: {modelo}.{aviso}";
    }

    private Cuenta Cuenta(string id) =>
        _repo.Cuentas().FirstOrDefault(c => c.Id == id) ?? throw new ExcepcionUsuario("Esa cuenta ya no existe.");

    /// <summary>
    /// Pide al proveedor del catálogo su lista de modelos. La clave es la que se está escribiendo en el alta o, si se da
    /// <paramref name="cuentaId"/>, la de esa cuenta. Sin forma de listarlos, devuelve la lista del catálogo.
    /// </summary>
    public async Task<IReadOnlyList<(string Id, string? Nombre)>> ListarModelos(string? catalogoId, string? endpoint, string? clave, string? cuentaId)
    {
        if (cuentaId is not null)
        {
            var c = Cuenta(cuentaId);
            catalogoId ??= c.Catalogo;
            endpoint ??= c.Endpoint;
            if (string.IsNullOrEmpty(clave) && c.RefClave is not null) clave = _boveda.Obtener(c.RefClave);
        }
        var e = _catalogo.Buscar(catalogoId) ?? throw new ExcepcionUsuario("Ese proveedor no está en el catálogo.");
        if (e.Listar is null) return e.Modelos.Select(m => (m, (string?)null)).ToList();
        if (string.IsNullOrWhiteSpace(clave) && e.SinClave) clave = e.ClaveFija;
        if (string.IsNullOrWhiteSpace(clave) && e.Listar.Auth != "ninguna") throw new ExcepcionUsuario("Escribe primero la clave de API.");
        var lista = await CatalogoProveedores.ListarModelos(e.Listar, endpoint ?? e.BaseUrl, clave);
        Registro.Info($"Modelos de {e.Id}: {lista.Count}");
        return lista.Count > 0 ? lista : e.Modelos.Select(m => (m, (string?)null)).ToList();
    }

    // ================================================================= equipos

    /// <summary>Con cuentas y sin equipos, crea el «Principal» con todas (el que usa «Automático» por defecto).</summary>
    private void AsegurarEquipoPrincipal()
    {
        var equipos = _repo.Equipos();
        if (equipos.Count > 0)
        {
            if (!equipos.Any(e => e.Predeterminado))
            {
                equipos[0].Predeterminado = true;
                _repo.GuardarEquipo(equipos[0]);
            }
            return;
        }
        var cuentas = _repo.Cuentas();
        if (cuentas.Count == 0) return;
        _repo.GuardarEquipo(new Equipo { Nombre = "Principal", Predeterminado = true, Miembros = cuentas.Select(c => c.Id).ToList() });
    }

    public void GuardarEquipo(string? id, string nombre, string regla, bool permiteOtroProveedor, int cuentaAtras, IReadOnlyList<string> miembros)
    {
        nombre = nombre.Trim();
        if (nombre.Length is < 2 or > 40) throw new ExcepcionUsuario("El nombre del equipo debe tener entre 2 y 40 caracteres.");
        var equipos = _repo.Equipos();
        if (equipos.Any(e => e.Id != id && e.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase)))
            throw new ExcepcionUsuario($"Ya hay un equipo que se llama «{nombre}».");
        var e = equipos.FirstOrDefault(x => x.Id == id) ?? new Equipo { Predeterminado = equipos.Count == 0 };
        e.Nombre = nombre;
        e.Regla = Enum.TryParse<ReglaReparto>(regla, out var r) ? r : ReglaReparto.Prioridad;
        e.PermiteOtroProveedor = permiteOtroProveedor;
        e.CuentaAtras = Math.Clamp(cuentaAtras, 0, 600);
        e.Miembros = miembros.Distinct().ToList();
        _repo.GuardarEquipo(e);
        EnviarEstado();
    }

    public void BorrarEquipo(string id)
    {
        var equipos = _repo.Equipos();
        var e = equipos.FirstOrDefault(x => x.Id == id) ?? throw new ExcepcionUsuario("Ese equipo ya no existe.");
        if (e.Predeterminado) throw new ExcepcionUsuario("El equipo predeterminado no se puede quitar.");
        foreach (var s in _repo.Sesiones().Where(s => s.EquipoId == id))
        {
            s.EquipoId = equipos.FirstOrDefault(x => x.Predeterminado)?.Id;
            _repo.GuardarSesion(s);
        }
        _repo.BorrarEquipo(id);
        EnviarEstado();
    }

    public void GuardarAjuste(string clave, string? valor)
    {
        if (clave is not ("resumenVivo")) throw new ExcepcionUsuario("Ajuste desconocido.");
        _repo.GuardarAjuste(clave, valor);
        EnviarEstado();
    }

    // ================================================================= consumo y enfriamiento

    private void CargarEnfriamientos()
    {
        try
        {
            var json = _repo.LeerAjuste("enfriamientos");
            if (json is null) return;
            foreach (var (id, e) in JsonSerializer.Deserialize<Dictionary<string, EstadoCuenta>>(json, Json.Opciones) ?? [])
                if (e.Enfriando(DateTime.Now)) _estados[id] = e;
        }
        catch (JsonException) { }
    }

    private void GuardarEnfriamientos()
    {
        var ahora = DateTime.Now;
        var d = _estados.Where(x => x.Value.Enfriando(ahora))
                        .ToDictionary(x => x.Key, x => new EstadoCuenta { EnfriandoHasta = x.Value.EnfriandoHasta, Motivo = x.Value.Motivo });
        _repo.GuardarAjuste("enfriamientos", d.Count == 0 ? null : JsonSerializer.Serialize(d, Json.Opciones));
    }

    /// <summary>La cuenta ha dado error de límite: no se usa hasta la hora de reinicio.</summary>
    public void Enfriar(string cuentaId, string? mensaje)
    {
        var anterior = _estados.GetValueOrDefault(cuentaId);
        var hasta = Enrutador.HastaCuando(mensaje, anterior, DateTime.Now);
        _estados[cuentaId] = (anterior ?? new EstadoCuenta()) with { EnfriandoHasta = hasta, Motivo = "límite de uso", Momento = DateTime.Now };
        GuardarEnfriamientos();
        Registro.Info($"Cuenta {cuentaId} enfriando hasta {hasta:HH:mm}");
        EnviarEstado();
    }

    private void QuitarEnfriamiento(string cuentaId)
    {
        if (_estados.TryGetValue(cuentaId, out var e) && e.EnfriandoHasta is not null)
        {
            _estados[cuentaId] = e with { EnfriandoHasta = null, Motivo = null };
            GuardarEnfriamientos();
        }
    }

    /// <summary>Consulta el consumo de las cuentas que lo dan (cada 3 minutos; cada minuto si alguna pasa del 80 %).</summary>
    public async Task ActualizarConsumo(bool todas = false)
    {
        var ahora = DateTime.Now;
        var cambio = false;
        foreach (var c in _repo.Cuentas())
        {
            var previo = _estados.GetValueOrDefault(c.Id);
            var cada = previo?.AHora(ahora).Peor > 80 ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(3);
            if (!todas && _consultado.TryGetValue(c.Id, out var ultima) && ahora - ultima < cada) continue;
            _consultado[c.Id] = ahora;
            EstadoCuenta? nuevo;
            try { nuevo = await _op.Consumo(c, _rutas.Resolver(c.Carpeta)); }
            catch (Exception ex) { Registro.Error($"Consumo de {c.Alias}", ex); continue; }
            if (nuevo is null) continue;
            // El enfriamiento por un error de límite se conserva hasta su hora, diga lo que diga el consumo
            _estados[c.Id] = nuevo with { EnfriandoHasta = previo?.EnfriandoHasta, Motivo = previo?.Motivo, GastoMes = previo?.GastoMes };
            cambio = true;
        }
        if (!cambio) return;
        EnviarEstado();
        RevisarCuentasAgotadas();
    }

    /// <summary>Sesiones vivas en modo automático cuya cuenta se ha quedado sin margen: se propone el relevo.</summary>
    private void RevisarCuentasAgotadas()
    {
        var ahora = DateTime.Now;
        var candidatas = Candidatas();
        foreach (var a in _vivas.Values.ToList())
        {
            if (a.Sesion.EquipoId is null || _relevos.ContainsKey(a.Sesion.Id)) continue;
            var c = candidatas.FirstOrDefault(x => x.Cuenta.Id == a.Cuenta.Id);
            if (c is null || !c.Lista) continue;
            if (Enrutador.SinMargen(c, ahora) is { } motivo)
                ProponerRelevo(a.Sesion.Id, $"«{a.Cuenta.Alias}» {motivo}", continuar: false);
        }
    }

    // ================================================================= relevo

    /// <summary>
    /// Propone pasar la sesión a otra cuenta del equipo: cuenta atrás (la del equipo) y, cuando termine el turno, relevo.
    /// <paramref name="continuar"/>: el límite cortó el turno, así que la cuenta nueva recibe «Continúa donde lo dejaste».
    /// </summary>
    public void ProponerRelevo(string sesionId, string motivo, bool continuar, int intento = 1)
    {
        if (_relevos.ContainsKey(sesionId)) return;
        var s = _repo.Sesion(sesionId);
        if (s?.EquipoId is null || s.CuentaId is null) return;
        var equipo = _repo.Equipos().FirstOrDefault(e => e.Id == s.EquipoId);
        var actual = _repo.Cuentas().FirstOrDefault(c => c.Id == s.CuentaId);
        if (equipo is null || actual is null) return;
        var destino = Enrutador.ParaRelevo(equipo, Candidatas(), actual, DateTime.Now);
        if (destino is null)
        {
            Registro.Aviso($"Sesión {sesionId}: {motivo} y ninguna cuenta del equipo {equipo.Nombre} tiene margen");
            _enviar(new { tipo = "relevo", sesionId, relevo = new { de = actual.Alias, motivo, sinDestino = true } });
            return;
        }
        var p = new RelevoPendiente(sesionId, actual.Id, destino.Id, motivo, DateTime.Now.AddSeconds(equipo.CuentaAtras), continuar, intento);
        _relevos[sesionId] = p;
        Registro.Info($"Sesión {sesionId}: relevo propuesto de {actual.Alias} a {destino.Alias} ({motivo})");
        EnviarRelevo(p);
        if (equipo.CuentaAtras <= 0) _ = RevisarRelevos();
    }

    private void EnviarRelevo(RelevoPendiente p)
    {
        var cuentas = _repo.Cuentas();
        _enviar(new
        {
            tipo = "relevo", sesionId = p.SesionId,
            relevo = new
            {
                de = cuentas.FirstOrDefault(c => c.Id == p.Origen)?.Alias,
                a = cuentas.FirstOrDefault(c => c.Id == p.Destino)?.Alias,
                p.Motivo, hasta = p.Hasta, segundos = Math.Max(0, (int)Math.Ceiling((p.Hasta - DateTime.Now).TotalSeconds)),
                esperaTurno = _vivas.GetValueOrDefault(p.SesionId)?.Estado is EstadoSesion.Trabajando or EstadoSesion.PidePermiso,
            },
        });
    }

    public void CancelarRelevo(string sesionId)
    {
        if (!_relevos.Remove(sesionId)) return;
        Registro.Info($"Sesión {sesionId}: relevo cancelado por el usuario");
        _enviar(new { tipo = "relevo", sesionId, relevo = (object?)null });
        _vivas.GetValueOrDefault(sesionId)?.Reanudar(); // lo que había en cola sigue con esta cuenta
    }

    public Task RelevoYa(string sesionId)
    {
        if (_relevos.TryGetValue(sesionId, out var p)) _relevos[sesionId] = p with { Hasta = DateTime.Now };
        return RevisarRelevos();
    }

    /// <summary>Hace los relevos cuya cuenta atrás ha terminado, si su sesión no está a mitad de un turno.</summary>
    public async Task RevisarRelevos()
    {
        foreach (var p in _relevos.Values.ToList())
        {
            if (DateTime.Now < p.Hasta) continue;
            var viva = _vivas.GetValueOrDefault(p.SesionId);
            if (viva?.Estado is EstadoSesion.Trabajando or EstadoSesion.PidePermiso or EstadoSesion.Arrancando) continue; // nunca se corta un turno
            _relevos.Remove(p.SesionId);
            _enviar(new { tipo = "relevo", sesionId = p.SesionId, relevo = (object?)null });
            var destino = _repo.Cuentas().FirstOrDefault(c => c.Id == p.Destino);
            if (destino is null) continue;
            try
            {
                await Relevar(p.SesionId, destino, p.Motivo, p.Continuar);
            }
            catch (ExcepcionUsuario ex)
            {
                // La cuenta destino tampoco vale (sin sesión, sin clave…): descansa un rato y se prueba con la siguiente
                Registro.Aviso($"Relevo a {destino.Alias} fallido: {ex.Message}");
                _estados[destino.Id] = (_estados.GetValueOrDefault(destino.Id) ?? new EstadoCuenta()) with
                    { EnfriandoHasta = DateTime.Now.AddMinutes(10), Motivo = "no se pudo usar" };
                _enviar(new { tipo = "aviso", texto = $"No se pudo seguir con «{destino.Alias}»: {ex.Message}" });
                if (p.Intento < 3) ProponerRelevo(p.SesionId, p.Motivo, p.Continuar, p.Intento + 1);
            }
        }
    }

    /// <summary>
    /// Pasa la sesión a otra cuenta. Mismo proveedor (y misma API): recupera la misma sesión del agente desde las
    /// carpetas compartidas. Otro proveedor: sesión nueva con traspaso (resumen vivo + últimos mensajes).
    /// </summary>
    public async Task Relevar(string sesionId, Cuenta destino, string motivo, bool continuar)
    {
        var s = _repo.Sesion(sesionId) ?? throw new ExcepcionUsuario("Esa sesión ya no existe.");
        // Lo que tiene que recibir la cuenta nueva, en orden; si no arranca, espera al siguiente intento
        var aMandar = _aMandar.GetValueOrDefault(sesionId) ?? [];
        const string continua = "Continúa donde lo dejaste.";
        if (continuar && !aMandar.Any(m => m.Texto == continua)) aMandar.Insert(0, (continua, []));
        if (_vivas.Remove(sesionId, out var viva))
        {
            aMandar.AddRange(viva.SacarCola());
            await viva.DisposeAsync();
        }
        if (aMandar.Count > 0) _aMandar[sesionId] = aMandar;
        var origen = s.CuentaId is null ? null : _repo.Cuentas().FirstOrDefault(c => c.Id == s.CuentaId);
        var hilo = new Hilo(Path.Combine(_rutas.Sesiones, s.Id));
        var marca = new Entrada
        {
            Tipo = "relevo", Cuenta = destino.Alias, Nivel = origen?.Alias,
            Texto = $"Sigue en «{destino.Alias}»" + (string.IsNullOrWhiteSpace(motivo) ? "" : $": {motivo}"),
        };
        hilo.Guardar(marca);
        _enviar(new { tipo = "entrada", sesionId, entrada = marca });
        s.CuentaId = destino.Id;
        _repo.GuardarSesion(s);
        Registro.Info($"Sesión {sesionId}: relevo de {origen?.Alias} a {destino.Alias} ({motivo})");
        EnviarEstado();
        await AbrirSesion(sesionId);
    }

    // ================================================================= sesiones

    /// <summary>
    /// Crea una sesión en una carpeta (y su proyecto si no lo tiene), la abre y manda el primer mensaje.
    /// <paramref name="quien"/>: <c>equipo:&lt;id&gt;</c> (automático: el equipo elige y releva) o <c>cuenta:&lt;id&gt;</c>.
    /// </summary>
    public async Task<string> NuevaSesion(string carpeta, string quien, string texto, IReadOnlyList<Adjunto> adjuntos, string? proyectoId = null)
    {
        var elegido = proyectoId is null ? null : _repo.Proyectos().FirstOrDefault(p => p.Id == proyectoId);
        if (string.IsNullOrWhiteSpace(carpeta)) carpeta = elegido?.Ruta ?? "";
        if (string.IsNullOrWhiteSpace(carpeta) || !Directory.Exists(carpeta))
            throw new ExcepcionUsuario("Elige una carpeta de trabajo que exista.");
        carpeta = Path.TrimEndingDirectorySeparator(Path.GetFullPath(carpeta));
        var (equipo, cuenta) = Elegir(quien);
        var proyecto = elegido ?? _repo.ProyectoDe(carpeta);
        if (proyecto is null)
        {
            proyecto = new Proyecto { Nombre = new DirectoryInfo(carpeta).Name, Ruta = carpeta, Orden = _repo.Proyectos().Count };
            _repo.GuardarProyecto(proyecto);
        }
        var s = new Sesion { ProyectoId = proyecto.Id, Carpeta = carpeta, CuentaId = cuenta.Id, EquipoId = equipo?.Id, Titulo = Titulo(texto) };
        _repo.GuardarSesion(s);
        Registro.Info($"Sesión nueva {s.Id} en {carpeta} con {cuenta.Alias}{(equipo is null ? "" : $" (equipo {equipo.Nombre})")}");
        EnviarEstado();
        await AbrirSesion(s.Id);
        await Enviar(s.Id, texto, adjuntos);
        return s.Id;
    }

    /// <summary>Equipo (si es automático) y cuenta con la que empieza.</summary>
    private (Equipo? Equipo, Cuenta Cuenta) Elegir(string quien)
    {
        var partes = quien.Split(':', 2);
        if (partes.Length == 2 && partes[0] == "equipo")
        {
            var equipo = _repo.Equipos().FirstOrDefault(e => e.Id == partes[1]) ?? throw new ExcepcionUsuario("Ese equipo ya no existe.");
            var clave = "rotacion:" + equipo.Id;
            var cuenta = Enrutador.ParaNueva(equipo, Candidatas(), DateTime.Now, _repo.LeerAjuste(clave))
                         ?? throw new ExcepcionUsuario($"Ninguna cuenta del equipo «{equipo.Nombre}» tiene margen ahora (agotadas, enfriando o sin autorizar).");
            _repo.GuardarAjuste(clave, cuenta.Id);
            return (equipo, cuenta);
        }
        return (null, Cuenta(partes.Length == 2 ? partes[1] : quien));
    }

    private static string? Titulo(string texto)
    {
        var linea = texto.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(linea)) return null;
        return linea.Length <= 60 ? linea : linea[..57].TrimEnd() + "…";
    }

    /// <summary>Manda a la interfaz la conversación guardada y arranca el agente si no está vivo.</summary>
    public async Task AbrirSesion(string id)
    {
        var s = _repo.Sesion(id) ?? throw new ExcepcionUsuario("Esa sesión ya no existe.");
        var hilo = new Hilo(Path.Combine(_rutas.Sesiones, s.Id));
        if (_vivas.TryGetValue(id, out var viva))
        {
            if (viva.Estado != EstadoSesion.Error)
            {
                _enviar(new { tipo = "conversacion", sesionId = id, entradas = viva.Conversacion(), agente = Agente(viva) });
                if (_relevos.TryGetValue(id, out var r)) EnviarRelevo(r);
                return;
            }
            // El agente se cayó o no llegó a arrancar (p. ej., faltaba autorizar la cuenta): se vuelve a arrancar
            _vivas.Remove(id);
            await viva.DisposeAsync();
        }
        _enviar(new { tipo = "conversacion", sesionId = id, entradas = hilo.Entradas, agente = (object?)null });

        var cuenta = s.CuentaId is null ? null : _repo.Cuentas().FirstOrDefault(c => c.Id == s.CuentaId);
        if (cuenta is null) throw new ExcepcionUsuario("La cuenta de esta sesión ya no existe. Elige otra en el chip de la cuenta.");
        await Arrancar(s, cuenta, hilo);
        if (_aMandar.Remove(id, out var aMandar) && _vivas.TryGetValue(id, out var nueva))
            foreach (var (texto, adjuntos) in aMandar) nueva.Enviar(texto, adjuntos);
    }

    private async Task Arrancar(Sesion s, Cuenta cuenta, Hilo hilo)
    {
        var p = Proveedores.Buscar(cuenta.Proveedor)!;
        var orden = _op.OrdenAgente(p, _requisitos)
                    ?? throw new ExcepcionUsuario($"Falta {p.Nombre} (o su adaptador) en este equipo. Instálalo desde la pantalla de inicio.");
        var clave = cuenta.Tipo == TipoCuenta.Api && cuenta.RefClave is not null ? _boveda.Obtener(cuenta.RefClave) : null;
        var carpetaCuenta = _rutas.Resolver(cuenta.Carpeta);
        Directory.CreateDirectory(carpetaCuenta);
        if (_op.CompartirCarpetas) CarpetasCompartidas.Preparar(_rutas, p.Id, carpetaCuenta);
        var entrada = _catalogo.Buscar(cuenta.Catalogo);
        var endpoint = cuenta.Endpoint;
        if (entrada?.Traducir == true && endpoint is not null && p == Proveedores.Claude)
        {
            // Proveedor que solo habla la API de OpenAI: Claude Code va al traductor local con un pase; la clave se queda aquí
            _traductor ??= new Traductor();
            clave = _traductor.Registrar(cuenta.Id, new DestinoTraductor(endpoint, clave, cuenta.Modelo));
            endpoint = _traductor.Url;
        }
        var entorno = Proveedores.Entorno(p, carpetaCuenta, clave, endpoint: endpoint, modelo: cuenta.Modelo, extra: entrada?.Entorno);
        foreach (var (k, v) in _op.EntornoExtra ?? new Dictionary<string, string?>()) entorno[k] = v;
        if (p == Proveedores.Codex && cuenta.Endpoint is not null)
            File.WriteAllText(Path.Combine(carpetaCuenta, "config.toml"),
                CatalogoProveedores.ConfigCodex(entrada?.Nombre ?? cuenta.Alias, cuenta.Endpoint, cuenta.Modelo, entrada?.SinClave ?? false),
                new UTF8Encoding(false));

        // Sesión del agente que se recupera: la de esta cuenta o, tras un relevo dentro del mismo proveedor (y la misma
        // API), la de la cuenta anterior (las conversaciones están en la carpeta compartida). Si no, traspaso.
        var tramo = _repo.UltimoTramo(s.Id);
        var anterior = tramo?.CuentaId is null ? null : _repo.Cuentas().FirstOrDefault(c => c.Id == tramo.CuentaId);
        string? nativa = null;
        string? traspasoDe = null;
        if (tramo is not null && (tramo.CuentaId == cuenta.Id || (anterior is not null && Enrutador.MismaConversacion(anterior, cuenta))))
            nativa = tramo.SesionNativa;
        else if (anterior is not null)
            traspasoDe = $"«{anterior.Alias}» ({Proveedores.Buscar(anterior.Proveedor)?.Nombre ?? anterior.Proveedor})";

        var mcp = CatalogoMcp.ParaSesion(CatalogoMcp.Cargar(_repo), _boveda, proveedor: p.Id);
        var a = new SesionAgente(s, cuenta, p, orden, entorno, hilo, nativa, mcp) { TraspasoDe = traspasoDe, CosteYaContado = CosteYaContado(nativa) };
        _vivas[s.Id] = a;
        a.Cambiado += x => _enUi(() =>
        {
            if (!_vivas.ContainsValue(x)) return;
            _enviar(new { tipo = "agente", sesionId = x.Sesion.Id, agente = Agente(x) });
            if (_relevos.TryGetValue(x.Sesion.Id, out var r)) EnviarRelevo(r);
        });
        a.EntradaCambiada += (x, e) => _enUi(() => _enviar(new { tipo = "entrada", sesionId = x.Sesion.Id, entrada = e }));
        a.SesionNativaCreada += (x, idNativa) => _enUi(() => GuardarTramo(x, idNativa));
        a.TituloPropuesto += (x, titulo) => _enUi(() =>
        {
            if (x.Sesion.Titulo is not null) return;
            x.Sesion.Titulo = titulo;
            _repo.GuardarSesion(x.Sesion);
            EnviarEstado();
        });
        a.LimiteAlcanzado += (x, mensaje) => _enUi(() =>
        {
            Enfriar(x.Cuenta.Id, mensaje);
            ProponerRelevo(x.Sesion.Id, $"«{x.Cuenta.Alias}» ha llegado a su límite de uso", continuar: true);
        });
        a.UsoDelTurno += (x, uso) => _enUi(() => AnotarUso(x, uso));
        a.TurnoTerminado += x => _enUi(() =>
        {
            if (!_vivas.ContainsValue(x)) return; // cerrada mientras tanto (p. ej. al salir): ni relevo ni resumen
            _ = RevisarRelevos();
            _ = ActualizarResumen(x);
        });
        try
        {
            await a.Arrancar();
            if (a.SesionNativa is not null) GuardarTramo(a, a.SesionNativa);
        }
        catch (ExcepcionUsuario)
        {
            _enviar(new { tipo = "agente", sesionId = s.Id, agente = Agente(a) });
            throw;
        }
    }

    // El coste acumulado de cada sesión nativa ya contado, para no volver a sumarlo al recuperarla
    private double? CosteYaContado(string? nativa) =>
        nativa is not null && double.TryParse(_repo.LeerAjuste("coste:" + nativa), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : null;

    private void GuardarCosteContado(SesionAgente a)
    {
        if (a.SesionNativa is { } nativa && a.CosteAcumulado is { } c)
            _repo.GuardarAjuste("coste:" + nativa, c.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void GuardarTramo(SesionAgente a, string nativa)
    {
        var ultimo = _repo.UltimoTramo(a.Sesion.Id);
        if (ultimo is not null && ultimo.CuentaId == a.Cuenta.Id)
        {
            if (ultimo.SesionNativa == nativa) return;
            ultimo.SesionNativa = nativa;
            _repo.GuardarTramo(ultimo);
            return;
        }
        if (ultimo is not null)
        {
            ultimo.Hasta = DateTime.Now;
            _repo.GuardarTramo(ultimo);
        }
        _repo.GuardarTramo(new Tramo { SesionId = a.Sesion.Id, Orden = (ultimo?.Orden ?? 0) + 1, CuentaId = a.Cuenta.Id, SesionNativa = nativa });
    }

    private static object Agente(SesionAgente a) => new
    {
        estado = a.Estado,
        opciones = a.Opciones,
        comandos = a.Comandos,
        cola = a.Cola,
        permiso = a.PermisoPendiente,
        error = a.UltimoError,
        admiteImagenes = a.AdmiteImagenes,
        contexto = a.Contexto is { } k ? new { usado = k.Usado, tamano = k.Tamano } : null,
        turnoDesde = a.TurnoDesde,
        cuenta = new { a.Cuenta.Id, a.Cuenta.Alias, a.Cuenta.Proveedor, tipo = a.Cuenta.Tipo.ToString() },
    };

    /// <summary>
    /// Manda un mensaje. En modo automático, si la cuenta actual ya no tiene margen, primero se releva (sin cuenta atrás:
    /// lo ha pedido el usuario al escribir).
    /// </summary>
    public async Task Enviar(string sesionId, string texto, IReadOnlyList<Adjunto> adjuntos)
    {
        if (string.IsNullOrWhiteSpace(texto) && adjuntos.Count == 0) return;
        var a = _vivas.GetValueOrDefault(sesionId) ?? throw new ExcepcionUsuario("La sesión no está abierta. Ábrela otra vez.");
        if (a.Sesion.EquipoId is not null && a.Estado is EstadoSesion.Esperando or EstadoSesion.Error)
        {
            var c = Candidatas().FirstOrDefault(x => x.Cuenta.Id == a.Cuenta.Id);
            var equipo = _repo.Equipos().FirstOrDefault(e => e.Id == a.Sesion.EquipoId);
            if (c is not null && equipo is not null && Enrutador.SinMargen(c, DateTime.Now) is { } motivo &&
                Enrutador.ParaRelevo(equipo, Candidatas(), a.Cuenta, DateTime.Now) is { } destino)
            {
                _relevos.Remove(sesionId);
                await Relevar(sesionId, destino, $"«{a.Cuenta.Alias}» {motivo}", continuar: false);
                a = _vivas.GetValueOrDefault(sesionId) ?? throw new ExcepcionUsuario("No se pudo seguir con otra cuenta.");
            }
        }
        if (a.Estado == EstadoSesion.Error)
        {
            await AbrirSesion(sesionId); // lo vuelve a arrancar; si no puede, avisa con el motivo
            a = _vivas.GetValueOrDefault(sesionId) ?? throw new ExcepcionUsuario("El agente no está disponible.");
        }
        a.Sesion.UltimoUso = DateTime.Now;
        a.Sesion.Titulo ??= Titulo(texto);
        _repo.GuardarSesion(a.Sesion);
        a.Enviar(texto, adjuntos);
    }

    public Task Interrumpir(string sesionId) => _vivas.GetValueOrDefault(sesionId)?.Interrumpir() ?? Task.CompletedTask;

    public void ResponderPermiso(string sesionId, string peticion, string? opcion) =>
        _vivas.GetValueOrDefault(sesionId)?.ResponderPermiso(peticion, opcion);

    public async Task CambiarOpcion(string sesionId, string opcion, string valor)
    {
        if (_vivas.GetValueOrDefault(sesionId) is not { } a) return;
        await a.CambiarOpcion(opcion, valor);
        _repo.GuardarSesion(a.Sesion);
    }

    /// <summary>
    /// Cambia quién trabaja en la sesión: <c>equipo:&lt;id&gt;</c> (automático; sigue con la cuenta actual si es del
    /// equipo y tiene margen) o <c>cuenta:&lt;id&gt;</c> (fija; la conversación sigue con la misma sesión o con traspaso).
    /// </summary>
    public async Task CambiarCuenta(string sesionId, string quien)
    {
        var s = _repo.Sesion(sesionId) ?? throw new ExcepcionUsuario("Esa sesión ya no existe.");
        var viva = _vivas.GetValueOrDefault(sesionId);
        if (viva?.Estado is EstadoSesion.Trabajando or EstadoSesion.PidePermiso)
            throw new ExcepcionUsuario("Espera a que termine el turno (o interrúmpelo) para cambiar de cuenta.");
        _relevos.Remove(sesionId);
        var partes = quien.Split(':', 2);
        if (partes.Length == 2 && partes[0] == "equipo")
        {
            var equipo = _repo.Equipos().FirstOrDefault(e => e.Id == partes[1]) ?? throw new ExcepcionUsuario("Ese equipo ya no existe.");
            s.EquipoId = equipo.Id;
            _repo.GuardarSesion(s);
            if (viva is not null) viva.Sesion.EquipoId = equipo.Id;
            var candidatas = Candidatas();
            var actual = candidatas.FirstOrDefault(c => c.Cuenta.Id == s.CuentaId);
            if (actual is null || !equipo.Miembros.Contains(actual.Cuenta.Id) || !Enrutador.TieneMargen(actual, DateTime.Now))
            {
                var destino = Enrutador.ParaNueva(equipo, candidatas, DateTime.Now)
                              ?? throw new ExcepcionUsuario($"Ninguna cuenta del equipo «{equipo.Nombre}» tiene margen ahora.");
                await Relevar(sesionId, destino, $"pasa al equipo «{equipo.Nombre}»", continuar: false);
                return;
            }
            EnviarEstado();
            if (viva is null) await AbrirSesion(sesionId);
            return;
        }
        var cuenta = Cuenta(partes.Length == 2 ? partes[1] : quien);
        s.EquipoId = null;
        _repo.GuardarSesion(s);
        if (cuenta.Id == s.CuentaId)
        {
            if (viva is not null) viva.Sesion.EquipoId = null;
            EnviarEstado();
            if (viva is null) await AbrirSesion(sesionId);
            return;
        }
        await Relevar(sesionId, cuenta, "cambio de cuenta", continuar: false);
    }

    public async Task Archivar(string sesionId)
    {
        _relevos.Remove(sesionId);
        _aMandar.Remove(sesionId);
        if (_vivas.Remove(sesionId, out var viva)) await viva.DisposeAsync();
        var s = _repo.Sesion(sesionId);
        if (s is null) return;
        s.Archivada = true;
        _repo.GuardarSesion(s);
        EnviarEstado();
    }

    /// <summary>Vuelve a poner en la lista una sesión archivada (la conversación no se había borrado).</summary>
    public void Desarchivar(string sesionId)
    {
        var s = _repo.Sesion(sesionId) ?? throw new ExcepcionUsuario("Esa sesión ya no existe.");
        s.Archivada = false;
        _repo.GuardarSesion(s);
        EnviarEstado();
    }

    /// <summary>Títulos de las sesiones a mitad de un turno o pidiendo permiso: al salir se cortarían.</summary>
    public IReadOnlyList<string> Ocupadas() => _vivas.Values
        .Where(a => a.Estado is EstadoSesion.Trabajando or EstadoSesion.PidePermiso)
        .Select(a => a.Sesion.Titulo ?? "Sesión nueva").ToList();

    /// <summary>Estado de un agente vivo (pruebas y diagnóstico).</summary>
    public SesionAgente? Viva(string sesionId) => _vivas.GetValueOrDefault(sesionId);

    /// <summary>Hay un relevo esperando (pruebas y diagnóstico).</summary>
    public bool HayRelevo(string sesionId) => _relevos.ContainsKey(sesionId);

    // ================================================================= resumen vivo

    /// <summary>
    /// Pone al día el resumen de la sesión tras un turno, si puede relevarse (equipo con más de una cuenta) y ha pasado
    /// algo desde el último: como mucho uno cada 2 minutos salvo que haya mucho nuevo.
    /// </summary>
    public async Task ActualizarResumen(SesionAgente a, bool forzar = false)
    {
        if (!ResumenVivoActivo || a.Sesion.EquipoId is null || !_resumiendo.Add(a.Sesion.Id)) return;
        try
        {
            var equipo = _repo.Equipos().FirstOrDefault(e => e.Id == a.Sesion.EquipoId);
            if (equipo is null || equipo.Miembros.Count < 2) return;
            var carpeta = a.Hilo.Carpeta;
            var entradas = a.Hilo.Entradas;
            var fecha = ResumenVivo.Fecha(carpeta);
            var nuevas = fecha is null ? entradas.Count : entradas.Count(e => e.Hora > fecha);
            if (!forzar && (nuevas == 0 || (fecha > DateTime.Now.AddMinutes(-2) && nuevas < 8))) return;
            var peticion = ResumenVivo.Peticion(entradas, ResumenVivo.Leer(carpeta));
            var texto = await (_op.GenerarResumen?.Invoke(peticion) ?? GenerarConClaude(peticion));
            if (texto is null) return;
            ResumenVivo.Guardar(carpeta, texto);
            Registro.Info($"Resumen vivo de {a.Sesion.Id} al día ({texto.Length} caracteres)");
        }
        finally { _resumiendo.Remove(a.Sesion.Id); }
    }

    /// <summary>Resumen con <c>claude -p</c> y la primera cuenta de Claude con margen (suscripción antes que API).</summary>
    private Task<string?> GenerarConClaude(string peticion)
    {
        var claude = Requisitos.Orden("claude", _requisitos);
        var ahora = DateTime.Now;
        var cuenta = Candidatas().Where(c => c.Cuenta.Proveedor == "Claude" && Enrutador.TieneMargen(c, ahora))
                                 .OrderBy(c => c.Cuenta.Tipo == TipoCuenta.Login ? 0 : 1).Select(c => c.Cuenta).FirstOrDefault();
        if (claude is null || cuenta is null) return Task.FromResult<string?>(null);
        var clave = cuenta.Tipo == TipoCuenta.Api && cuenta.RefClave is not null ? _boveda.Obtener(cuenta.RefClave) : null;
        var entorno = Proveedores.Entorno(Proveedores.Claude, _rutas.Resolver(cuenta.Carpeta), clave, endpoint: cuenta.Endpoint,
                                          extra: _catalogo.Buscar(cuenta.Catalogo)?.Entorno);
        return ResumenVivo.Generar(claude, entorno, peticion, Path.Combine(_rutas.Datos, "llamadas"));
    }

    // ================================================================= adjuntos

    /// <summary>Guarda una imagen pegada (base64) en los adjuntos y la devuelve como adjunto.</summary>
    public Adjunto GuardarPegado(string? sesionId, string nombre, string tipo, string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        if (bytes.Length > 20 * 1024 * 1024) throw new ExcepcionUsuario("La imagen es demasiado grande (más de 20 MB).");
        var carpeta = Path.Combine(Adjuntos, sesionId ?? "nuevas");
        Directory.CreateDirectory(carpeta);
        var ext = tipo switch { "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", _ => ".png" };
        var fichero = Path.Combine(carpeta, $"captura_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}");
        File.WriteAllBytes(fichero, bytes);
        return new Adjunto(fichero, string.IsNullOrWhiteSpace(nombre) ? Path.GetFileName(fichero) : nombre, tipo, true);
    }

    public static Adjunto DeRuta(string ruta)
    {
        var ext = Path.GetExtension(ruta).ToLowerInvariant();
        var tipo = ext switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
            ".pdf" => "application/pdf", ".md" => "text/markdown", ".txt" or ".log" => "text/plain", ".json" => "application/json",
            ".html" or ".htm" => "text/html", ".csv" => "text/csv",
            _ => Directory.Exists(ruta) ? "inode/directory" : "application/octet-stream",
        };
        return new Adjunto(ruta, Path.GetFileName(Path.TrimEndingDirectorySeparator(ruta)), tipo, tipo.StartsWith("image/"));
    }

    // ================================================================= temporizadores

    /// <summary>Cada segundo: cuenta atrás de los relevos; cada 30 s, consumo de las cuentas.</summary>
    private async Task Vuelta()
    {
        try
        {
            foreach (var p in _relevos.Values.ToList()) EnviarRelevo(p);
            if (_vueltas % 2 == 0) RevisarLogins();
            await RevisarRelevos();
            if (_vueltas++ % 30 == 0)
            {
                if (_mesGastos != DateTime.Now.ToString("yyyyMM")) { CargarGastos(); EnviarEstado(); }
                await ActualizarConsumo();
            }
        }
        catch (Exception ex) { Registro.Error("Vuelta del gestor", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_reloj is not null) await _reloj.DisposeAsync();
        // Todos a la vez: cada uno espera hasta 3 s a que su agente termine, y la ventana no espera más de 8
        var vivas = _vivas.Values.ToList();
        _vivas.Clear();
        await Task.WhenAll(vivas.Select(async a =>
        {
            try { await a.DisposeAsync(); }
            catch (Exception ex) { Registro.Error("Al cerrar un agente", ex); }
        }));
        if (_traductor is not null) await _traductor.DisposeAsync();
    }
}
