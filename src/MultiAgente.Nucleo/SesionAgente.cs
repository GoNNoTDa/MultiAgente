using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MultiAgente.Nucleo.Acp;

namespace MultiAgente.Nucleo;

/// <summary>Petición de permiso del agente, a la espera de que el usuario pulse una opción.</summary>
/// <param name="Id">Identificador para la respuesta.</param>
/// <param name="Titulo">Qué quiere hacer el agente.</param>
/// <param name="Clase">Tipo de herramienta (edit, execute…).</param>
/// <param name="Opciones">Opciones ACP: optionId, name, kind (allow_once, allow_always, reject_once, reject_always).</param>
/// <param name="Herramienta">La llamada a la herramienta tal cual (para enseñar el detalle).</param>
public sealed record PeticionPermiso(string Id, string Titulo, string? Clase, JsonArray Opciones, JsonNode? Herramienta);

/// <summary>Un servidor MCP que MultiAgente pasa a cada sesión (formato ACP: stdio o http).</summary>
public sealed record ServidorMcp(string Nombre, JsonObject Definicion);

/// <summary>
/// Una sesión viva con un agente por ACP: arranca el agente con la carpeta de la cuenta, crea o recupera su sesión,
/// manda los mensajes (en cola si está trabajando), recoge lo que hace en la conversación canónica (<see cref="Hilo"/>)
/// y pasa al usuario las peticiones de permiso. Los eventos llegan desde hilos de fondo.
/// </summary>
public sealed class SesionAgente : IAsyncDisposable
{
    private readonly OrdenAgente _orden;
    private readonly IReadOnlyDictionary<string, string?> _entorno;
    private readonly IReadOnlyList<ServidorMcp> _mcp;
    private readonly object _cerrojo = new();
    private readonly ConcurrentQueue<(string Texto, IReadOnlyList<Adjunto> Adjuntos)> _cola = new();
    private readonly List<Entrada> _turno = [];
    private ConexionAcp? _conexion;
    private TaskCompletionSource<string?>? _respuestaPermiso;
    private bool _procesando;
    private bool _cargando;
    private bool _traspasoPendiente;
    // Tras un límite, la cola espera al relevo en vez de seguir mandando a la cuenta agotada
    private volatile bool _pausada;
    private readonly HashSet<string> _mcpAvisados = [];
    private bool _admiteCarga;
    private bool _mcpHttp;
    private bool _mcpSse;
    private volatile bool _cerrandoAdrede;

    public SesionAgente(Sesion sesion, Cuenta cuenta, Proveedor proveedor, OrdenAgente orden,
                        IReadOnlyDictionary<string, string?> entorno, Hilo hilo, string? sesionNativa,
                        IReadOnlyList<ServidorMcp>? mcp = null)
    {
        Sesion = sesion;
        Cuenta = cuenta;
        Proveedor = proveedor;
        Hilo = hilo;
        SesionNativa = sesionNativa;
        _orden = orden;
        _entorno = entorno;
        _mcp = mcp ?? [];
    }

    public Sesion Sesion { get; }
    public Cuenta Cuenta { get; }
    public Proveedor Proveedor { get; }
    public Hilo Hilo { get; }
    public string? SesionNativa { get; private set; }
    public EstadoSesion Estado { get; private set; } = EstadoSesion.Cerrada;
    public bool AdmiteImagenes { get; private set; }
    /// <summary>Opciones de la sesión (ACP «configOptions»: modo, modelo, esfuerzo…), normalizadas.</summary>
    public JsonArray Opciones { get; private set; } = [];
    /// <summary>Órdenes «/» que ofrece el agente.</summary>
    public JsonArray Comandos { get; private set; } = [];
    public PeticionPermiso? PermisoPendiente { get; private set; }
    public IReadOnlyList<string> Cola => _cola.Select(m => m.Texto).ToList();
    public string? UltimoError { get; private set; }
    /// <summary>Cuándo empezó el turno en curso (null si no está trabajando): para enseñar cuánto lleva.</summary>
    public DateTime? TurnoDesde { get; private set; }

    /// <summary>Cambió el estado, las opciones, la cola o el permiso pendiente.</summary>
    public event Action<SesionAgente>? Cambiado;
    /// <summary>Entrada nueva o actualizada (copia).</summary>
    public event Action<SesionAgente, Entrada>? EntradaCambiada;
    /// <summary>El agente ha creado su sesión nativa (hay que guardarla en el tramo).</summary>
    public event Action<SesionAgente, string>? SesionNativaCreada;
    /// <summary>El agente propone un título para la sesión.</summary>
    public event Action<SesionAgente, string>? TituloPropuesto;
    /// <summary>La cuenta ha llegado a su límite de uso (el turno se ha cortado). Lleva el mensaje del proveedor.</summary>
    public event Action<SesionAgente, string>? LimiteAlcanzado;
    /// <summary>Ha terminado un turno (con o sin error).</summary>
    public event Action<SesionAgente>? TurnoTerminado;
    /// <summary>Uso de un turno (tokens y coste que ha informado el agente).</summary>
    public event Action<SesionAgente, UsoTurno>? UsoDelTurno;

    /// <summary>Tokens en el contexto y tamaño de la ventana (si el agente lo informa).</summary>
    public (long Usado, long Tamano)? Contexto { get; private set; }

    // Coste acumulado que da el agente (de su sesión nativa): el de cada turno es la diferencia. Los tokens llegan ya
    // por turno (esquema ACP: «Token usage for this turn»).
    private double? _accCoste;
    private string? _moneda;
    private long _turnoEntrada, _turnoSalida, _turnoTotal;
    private double? _turnoCoste;

    /// <summary>De qué cuenta viene la conversación si este agente la recibe por traspaso (relevo a otro proveedor).</summary>
    public string? TraspasoDe { get; set; }

    /// <summary>
    /// Coste acumulado de la sesión nativa que ya se contó antes (en otro arranque o con otra cuenta). Al recuperarla,
    /// Claude Code vuelve a informar el total desde el principio: sin esto, se sumaría otra vez.
    /// </summary>
    public double? CosteYaContado { get; init; }

    /// <summary>Último coste acumulado que ha informado el agente (para guardarlo como <see cref="CosteYaContado"/>).</summary>
    public double? CosteAcumulado { get { lock (_cerrojo) return _accCoste; } }

    /// <summary>Arranca el agente y deja la sesión lista. Lanza <see cref="ExcepcionUsuario"/> con un mensaje llano.</summary>
    public async Task Arrancar(CancellationToken ct = default)
    {
        PonerEstado(EstadoSesion.Arrancando);
        try
        {
            _conexion = ConexionAcp.Iniciar(_orden, Sesion.Carpeta, _entorno, $"{Proveedor.Id}·{Cuenta.Alias}");
            _conexion.Notificacion += AlNotificar;
            _conexion.AtenderPeticion = AlPedir;
            _conexion.Cerrada += _ =>
            {
                if (_cerrandoAdrede || Estado == EstadoSesion.Cerrada) return;
                UltimoError = "El agente se ha cerrado.";
                PonerEstado(EstadoSesion.Error);
            };

            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TimeSpan.FromSeconds(90));
            var ini = await _conexion.Pedir("initialize", new JsonObject
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new JsonObject
                {
                    ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                    ["terminal"] = false,
                },
                ["clientInfo"] = new JsonObject { ["name"] = "MultiAgente", ["version"] = "0.2" },
            }, limite.Token);
            var capacidades = ini?["agentCapabilities"];
            AdmiteImagenes = capacidades?["promptCapabilities"]?["image"]?.GetValue<bool>() ?? false;
            _admiteCarga = capacidades?["loadSession"]?.GetValue<bool>() ?? false;
            _mcpHttp = capacidades?["mcpCapabilities"]?["http"]?.GetValue<bool>() ?? false;
            _mcpSse = capacidades?["mcpCapabilities"]?["sse"]?.GetValue<bool>() ?? false;

            if (Cuenta.Tipo == TipoCuenta.Api && Proveedor.MetodoAutenticacionClave is { } metodo &&
                (ini?["authMethods"] as JsonArray ?? []).Any(m => m?["id"]?.GetValue<string>() == metodo))
                await _conexion.Pedir("authenticate", new JsonObject { ["methodId"] = metodo }, limite.Token);

            if (SesionNativa is not null && _admiteCarga)
            {
                try
                {
                    _cargando = true;
                    var r = await _conexion.Pedir("session/load", ParametrosSesion(SesionNativa), limite.Token);
                    LeerSesion(r);
                    lock (_cerrojo) _accCoste = CosteYaContado;
                }
                catch (ErrorAcp ex) when (_conexion.Viva)
                {
                    Registro.Aviso($"{Proveedor.Id}·{Cuenta.Alias}: no se pudo recuperar la sesión {SesionNativa}: {ex.Message}");
                    await NuevaSesionNativa(limite.Token);
                    _traspasoPendiente = Hilo.Cuantas > 0;
                }
                finally { _cargando = false; }
            }
            else
            {
                await NuevaSesionNativa(limite.Token);
                _traspasoPendiente = Hilo.Cuantas > 0;
            }
        }
        catch (ErrorAcp ex)
        {
            var mensaje = MensajeLlano(ex);
            await CerrarConexion();
            UltimoError = mensaje;
            PonerEstado(EstadoSesion.Error);
            throw new ExcepcionUsuario(mensaje, ex);
        }
        catch (OperationCanceledException ex)
        {
            var mensaje = $"{Proveedor.Nombre} no ha respondido a tiempo al arrancar.";
            await CerrarConexion();
            UltimoError = mensaje;
            PonerEstado(EstadoSesion.Error);
            throw new ExcepcionUsuario(mensaje, ex);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            UltimoError = $"No se pudo arrancar {Proveedor.Nombre}: {ex.Message}";
            PonerEstado(EstadoSesion.Error);
            throw new ExcepcionUsuario(UltimoError, ex);
        }

        UltimoError = null;
        TomarUsoDelTurno(); // lo que haya informado al recuperar la sesión no es de ningún turno
        PonerEstado(EstadoSesion.Esperando);
        await AplicarPreferencias();
        Lanzar();
    }

    private JsonObject ParametrosSesion(string? id)
    {
        var p = new JsonObject
        {
            ["cwd"] = Sesion.Carpeta,
            ["mcpServers"] = new JsonArray(McpAdmitidos().Select(m => (JsonNode)m.Definicion.DeepClone()).ToArray()),
        };
        if (id is not null) p["sessionId"] = id;
        return p;
    }

    /// <summary>Los servidores MCP cuyo transporte admite el agente (stdio siempre; http y sse si lo declara).</summary>
    private IEnumerable<ServidorMcp> McpAdmitidos() => _mcp.Where(m =>
    {
        var tipo = m.Definicion["type"]?.GetValue<string>();
        var ok = tipo switch { "http" => _mcpHttp, "sse" => _mcpSse, _ => true };
        if (!ok) Registro.Info($"{Proveedor.Id}: el MCP {m.Nombre} ({tipo}) no se pasa: el agente no lo admite");
        return ok;
    });

    private async Task NuevaSesionNativa(CancellationToken ct)
    {
        var r = await _conexion!.Pedir("session/new", ParametrosSesion(null), ct);
        SesionNativa = r?["sessionId"]?.GetValue<string>() ?? throw new ErrorAcp(-1, "El agente no devolvió la sesión.");
        LeerSesion(r);
        SesionNativaCreada?.Invoke(this, SesionNativa);
    }

    /// <summary>Lee modos, modelos y opciones de la respuesta de session/new o session/load.</summary>
    private void LeerSesion(JsonNode? r)
    {
        var opciones = new JsonArray();
        if (r?["configOptions"] is JsonArray co)
            foreach (var o in co) opciones.Add(o?.DeepClone());
        // Agentes que dan modos y modelos por separado: se convierten a opciones
        if (!opciones.Any(o => o?["category"]?.GetValue<string>() == "mode") && r?["modes"] is JsonObject modos)
            opciones.Add(new JsonObject
            {
                ["id"] = "mode", ["name"] = "Modo", ["category"] = "mode", ["via"] = "modes",
                ["currentValue"] = modos["currentModeId"]?.DeepClone(),
                ["options"] = new JsonArray((modos["availableModes"] as JsonArray ?? []).Select(m => (JsonNode)new JsonObject
                {
                    ["value"] = m?["id"]?.DeepClone(), ["name"] = m?["name"]?.DeepClone(), ["description"] = m?["description"]?.DeepClone(),
                }).ToArray()),
            });
        if (!opciones.Any(o => o?["category"]?.GetValue<string>() == "model") && r?["models"] is JsonObject modelos)
            opciones.Add(new JsonObject
            {
                ["id"] = "model", ["name"] = "Modelo", ["category"] = "model", ["via"] = "models",
                ["currentValue"] = modelos["currentModelId"]?.DeepClone(),
                ["options"] = new JsonArray((modelos["availableModels"] as JsonArray ?? []).Select(m => (JsonNode)new JsonObject
                {
                    ["value"] = m?["modelId"]?.DeepClone(), ["name"] = m?["name"]?.DeepClone(), ["description"] = m?["description"]?.DeepClone(),
                }).ToArray()),
            });
        lock (_cerrojo) Opciones = opciones;
    }

    /// <summary>Vuelve a poner el modo, el modelo y el esfuerzo que tenía la sesión (al reabrirla o tras un relevo).</summary>
    private async Task AplicarPreferencias()
    {
        foreach (var (categoria, valor) in new[] { ("mode", Sesion.ModoPermisos), ("model", Sesion.Modelo), ("thought_level", Sesion.Esfuerzo) })
        {
            if (valor is null) continue;
            JsonNode? o;
            lock (_cerrojo) o = Opciones.FirstOrDefault(x => x?["category"]?.GetValue<string>() == categoria);
            if (o is null || o["currentValue"]?.ToString() == valor) continue;
            if (!(o["options"] as JsonArray ?? []).Any(x => x?["value"]?.ToString() == valor)) continue;
            try { await CambiarOpcion(o["id"]!.GetValue<string>(), valor); }
            catch (ExcepcionUsuario ex) { Registro.Aviso($"No se pudo volver a poner {categoria}={valor}: {ex.Message}"); }
        }
    }

    /// <summary>Cambia una opción (modo de permisos, modelo, esfuerzo…).</summary>
    public async Task CambiarOpcion(string id, string valor)
    {
        if (_conexion is null || SesionNativa is null) return;
        JsonNode? o;
        lock (_cerrojo) o = Opciones.FirstOrDefault(x => x?["id"]?.GetValue<string>() == id);
        if (o is null) return;
        try
        {
            var via = o["via"]?.GetValue<string>();
            JsonNode? r;
            if (via == "modes")
                r = await _conexion.Pedir("session/set_mode", new JsonObject { ["sessionId"] = SesionNativa, ["modeId"] = valor });
            else if (via == "models")
                r = await _conexion.Pedir("session/set_model", new JsonObject { ["sessionId"] = SesionNativa, ["modelId"] = valor });
            else
                r = await _conexion.Pedir("session/set_config_option", new JsonObject { ["sessionId"] = SesionNativa, ["configId"] = id, ["value"] = valor });

            lock (_cerrojo)
            {
                if (r?["configOptions"] is JsonArray nuevas && via is null) LeerSesion(new JsonObject { ["configOptions"] = nuevas.DeepClone() });
                else o["currentValue"] = valor;
            }
            switch (o["category"]?.GetValue<string>())
            {
                case "mode": Sesion.ModoPermisos = valor; break;
                case "model": Sesion.Modelo = valor; break;
                case "thought_level": Sesion.Esfuerzo = valor; break;
            }
            Cambiado?.Invoke(this);
        }
        catch (ErrorAcp ex)
        {
            throw new ExcepcionUsuario($"{Proveedor.Nombre} no ha aceptado el cambio: {ex.Message}", ex);
        }
    }

    /// <summary>Manda un mensaje. Si el agente está trabajando, queda en cola y se manda al terminar.</summary>
    public void Enviar(string texto, IReadOnlyList<Adjunto>? adjuntos = null)
    {
        _cola.Enqueue((texto, adjuntos ?? []));
        _pausada = false; // el usuario escribe: decide él, aunque la cuenta esté agotada
        Cambiado?.Invoke(this);
        Lanzar();
    }

    /// <summary>Saca los mensajes que esperaban en la cola (para pasarlos a la cuenta del relevo).</summary>
    public List<(string Texto, IReadOnlyList<Adjunto> Adjuntos)> SacarCola()
    {
        var sacados = new List<(string, IReadOnlyList<Adjunto>)>();
        while (_cola.TryDequeue(out var m)) sacados.Add(m);
        if (sacados.Count > 0) Cambiado?.Invoke(this);
        return sacados;
    }

    /// <summary>Sigue mandando la cola con esta cuenta (el usuario ha cancelado el relevo).</summary>
    public void Reanudar()
    {
        _pausada = false;
        Lanzar();
    }

    private void Lanzar()
    {
        lock (_cerrojo)
        {
            if (_procesando || _pausada || _cola.IsEmpty || Estado is not (EstadoSesion.Esperando)) return;
            _procesando = true;
        }
        _ = Task.Run(Procesar);
    }

    private async Task Procesar()
    {
        try
        {
            string? limiteDelTurno = null;
            while (_conexion is { Viva: true } && _cola.TryDequeue(out var m))
            {
                var yo = new Entrada { Tipo = "usuario", Texto = m.Texto, Adjuntos = m.Adjuntos.Count > 0 ? [.. m.Adjuntos] : null, Cuenta = Cuenta.Alias };
                Hilo.Guardar(yo);
                EntradaCambiada?.Invoke(this, yo.Clonar());
                var bloques = Bloques(m.Texto, m.Adjuntos);
                TurnoDesde = DateTime.Now;
                PonerEstado(EstadoSesion.Trabajando);
                try
                {
                    var r = await _conexion.Pedir("session/prompt", new JsonObject { ["sessionId"] = SesionNativa, ["prompt"] = bloques });
                    if (r?["usage"] is JsonObject uso) AcumularTokens(uso);
                    switch (r?["stopReason"]?.GetValue<string>())
                    {
                        case "cancelled": Avisar("Interrumpido.", "interrumpido"); break;
                        case "refusal": Avisar($"{Proveedor.Nombre} se ha negado a seguir con esta petición.", "error"); break;
                        case "max_tokens": Avisar("La respuesta se ha cortado por longitud.", "info"); break;
                        case "max_turn_requests": Avisar("El agente ha llegado al máximo de pasos en este turno.", "info"); break;
                    }
                }
                catch (ErrorAcp ex)
                {
                    UltimoError = MensajeLlano(ex);
                    var limite = EsLimite(ex);
                    Avisar(UltimoError, limite ? "limite" : "error");
                    if (limite) limiteDelTurno = ex.Message + " " + ex.Datos?.ToJsonString();
                }
                finally
                {
                    limiteDelTurno ??= LimiteEnLaRespuesta();
                    if (limiteDelTurno is not null && !_turno.Any(e => e.Nivel == "limite"))
                        Avisar($"La cuenta «{Cuenta.Alias}» ha llegado a su límite de uso.", "limite");
                    if (limiteDelTurno is not null) _pausada = true;
                    CerrarTurno();
                    TurnoDesde = null;
                    if (Estado is EstadoSesion.Trabajando or EstadoSesion.PidePermiso) PonerEstado(EstadoSesion.Esperando);
                }
                try
                {
                    var usoTurno = TomarUsoDelTurno();
                    if (usoTurno is not null) UsoDelTurno?.Invoke(this, usoTurno);
                    if (limiteDelTurno is not null) LimiteAlcanzado?.Invoke(this, limiteDelTurno);
                    TurnoTerminado?.Invoke(this);
                }
                catch (Exception ex) { Registro.Error("Al avisar del fin del turno", ex); }
                if (_pausada) break;
                limiteDelTurno = null;
            }
        }
        finally
        {
            lock (_cerrojo) _procesando = false;
            if (!_cola.IsEmpty && Estado == EstadoSesion.Esperando) Lanzar();
        }
    }

    private JsonArray Bloques(string texto, IReadOnlyList<Adjunto> adjuntos)
    {
        var bloques = new JsonArray();
        if (_traspasoPendiente)
        {
            _traspasoPendiente = false;
            var anteriores = Hilo.Entradas;
            bloques.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = Traspaso.Componer(anteriores.Take(Math.Max(0, anteriores.Count - 1)).ToList(),
                                             resumen: ResumenVivo.Leer(Hilo.Carpeta), deDonde: TraspasoDe),
            });
        }
        bloques.Add(new JsonObject { ["type"] = "text", ["text"] = texto });
        foreach (var a in adjuntos)
        {
            if (a.EsImagen && AdmiteImagenes && File.Exists(a.Ruta) && new FileInfo(a.Ruta).Length < 20 * 1024 * 1024)
                bloques.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["mimeType"] = a.Tipo,
                    ["data"] = Convert.ToBase64String(File.ReadAllBytes(a.Ruta)),
                    ["uri"] = new Uri(a.Ruta).AbsoluteUri,
                });
            else
                bloques.Add(new JsonObject { ["type"] = "resource_link", ["uri"] = new Uri(a.Ruta).AbsoluteUri, ["name"] = a.Nombre, ["mimeType"] = a.Tipo });
        }
        return bloques;
    }

    /// <summary>Interrumpe el turno en curso (y rechaza el permiso pendiente).</summary>
    public async Task Interrumpir()
    {
        _respuestaPermiso?.TrySetResult(null);
        if (_conexion is null || SesionNativa is null || Estado is not (EstadoSesion.Trabajando or EstadoSesion.PidePermiso)) return;
        await _conexion.Notificar("session/cancel", new JsonObject { ["sessionId"] = SesionNativa });
    }

    /// <summary>Respuesta del usuario a la petición de permiso (null = rechazar/cancelar).</summary>
    public void ResponderPermiso(string peticion, string? opcion)
    {
        if (PermisoPendiente?.Id == peticion) _respuestaPermiso?.TrySetResult(opcion);
    }

    private async Task<JsonNode?> AlPedir(string metodo, JsonNode? p)
    {
        if (metodo != "session/request_permission")
            throw new ErrorAcp(-32601, $"MultiAgente no ofrece {metodo}");

        var herramienta = p?["toolCall"];
        var peticion = new PeticionPermiso(
            Guid.NewGuid().ToString("N"),
            herramienta?["title"]?.GetValue<string>() ?? "El agente pide permiso",
            herramienta?["kind"]?.GetValue<string>(),
            p?["options"]?.DeepClone() as JsonArray ?? [],
            herramienta?.DeepClone());
        if (herramienta?["toolCallId"]?.GetValue<string>() is { } idHerramienta) AplicarHerramienta(herramienta, idHerramienta, nueva: false);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_cerrojo)
        {
            _respuestaPermiso?.TrySetResult(null);
            _respuestaPermiso = tcs;
            PermisoPendiente = peticion;
        }
        PonerEstado(EstadoSesion.PidePermiso);
        var opcion = await tcs.Task;
        lock (_cerrojo)
        {
            if (ReferenceEquals(_respuestaPermiso, tcs)) { _respuestaPermiso = null; PermisoPendiente = null; }
        }
        if (Estado == EstadoSesion.PidePermiso) PonerEstado(EstadoSesion.Trabajando);
        return opcion is null
            ? new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } }
            : new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = opcion } };
    }

    private void AlNotificar(string metodo, JsonNode? p)
    {
        if (metodo != "session/update") return;
        var u = p?["update"];
        var tipo = u?["sessionUpdate"]?.GetValue<string>();
        if (u is null || tipo is null) return;
        switch (tipo)
        {
            case "available_commands_update":
                lock (_cerrojo) Comandos = u["availableCommands"]?.DeepClone() as JsonArray ?? [];
                Cambiado?.Invoke(this);
                return;
            case "current_mode_update":
                lock (_cerrojo)
                    foreach (var o in Opciones.Where(o => o?["category"]?.GetValue<string>() == "mode"))
                        o!["currentValue"] = u["currentModeId"]?.DeepClone();
                Cambiado?.Invoke(this);
                return;
            case "config_option_update":
                if (u["configOptions"] is JsonArray co) LeerSesion(new JsonObject { ["configOptions"] = co.DeepClone() });
                Cambiado?.Invoke(this);
                return;
            case "session_info_update":
                if (u["title"]?.GetValue<string>() is { Length: > 0 } titulo) TituloPropuesto?.Invoke(this, titulo);
                return;
            case "usage_update":
                AlUso(u);
                Cambiado?.Invoke(this);
                return;
        }
        if (_cargando) return; // historial que reenvía el agente al recuperar la sesión: ya lo tenemos

        switch (tipo)
        {
            case "agent_message_chunk": AnadirTexto("agente", u); break;
            case "agent_thought_chunk": AnadirTexto("pensamiento", u); break;
            case "tool_call": AplicarHerramienta(u, u["toolCallId"]?.GetValue<string>(), nueva: true); break;
            case "tool_call_update": AplicarHerramienta(u, u["toolCallId"]?.GetValue<string>(), nueva: false); break;
            case "plan": AplicarPlan(u["entries"]); break;
        }
    }

    private static long Numero(JsonNode? n) => n is JsonValue v && long.TryParse(v.ToString(), out var x) ? x : 0;

    /// <summary>usage_update: contexto (used/size) y coste acumulado de la sesión.</summary>
    private void AlUso(JsonNode u)
    {
        lock (_cerrojo)
        {
            if (u["used"] is not null || u["size"] is not null) Contexto = (Numero(u["used"]), Numero(u["size"]));
            if (u["cost"] is JsonObject c && c["amount"] is JsonValue a && double.TryParse(a.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var total))
            {
                _moneda = c["currency"]?.ToString() ?? _moneda;
                // Si baja, el agente empezó una sesión nueva (cuenta desde cero)
                var delta = _accCoste is { } previo && total >= previo ? total - previo : total;
                _accCoste = total;
                _turnoCoste = (_turnoCoste ?? 0) + delta;
            }
        }
    }

    /// <summary>usage de la respuesta del turno: los tokens de este turno.</summary>
    private void AcumularTokens(JsonObject u)
    {
        lock (_cerrojo)
        {
            var entrada = Numero(u["inputTokens"]) + Numero(u["cachedReadTokens"]) + Numero(u["cachedWriteTokens"]);
            var salida = Numero(u["outputTokens"]) + Numero(u["thoughtTokens"]);
            var total = Numero(u["totalTokens"]);
            if (total == 0) total = entrada + salida;
            _turnoEntrada += entrada;
            _turnoSalida += salida;
            _turnoTotal += total;
        }
    }

    private UsoTurno? TomarUsoDelTurno()
    {
        lock (_cerrojo)
        {
            var u = new UsoTurno
            {
                SesionId = Sesion.Id, CuentaId = Cuenta.Id, Entrada = _turnoEntrada, Salida = _turnoSalida,
                Total = _turnoTotal, Coste = _turnoCoste, Moneda = _moneda,
            };
            _turnoEntrada = _turnoSalida = _turnoTotal = 0;
            _turnoCoste = null;
            return u;
        }
    }

    private void AnadirTexto(string tipo, JsonNode u)
    {
        var contenido = u["content"];
        if (contenido?["type"]?.GetValue<string>() != "text") return;
        var texto = contenido["text"]?.GetValue<string>();
        if (string.IsNullOrEmpty(texto)) return;
        // Lo que escribe un subagente no es la respuesta: va a su herramienta (lo que dice ahora; su informe llega al final)
        if (u["_meta"]?["claudeCode"]?["parentToolUseId"]?.GetValue<string>() is { } padre)
        {
            if (tipo == "agente") TextoDeSubagente(padre, texto);
            return;
        }
        Entrada e;
        lock (_cerrojo)
        {
            var ultima = _turno.LastOrDefault();
            if (ultima?.Tipo == tipo) e = ultima;
            else
            {
                e = new Entrada { Tipo = tipo, Texto = "", Cuenta = Cuenta.Alias };
                _turno.Add(e);
            }
            e.Texto += texto;
            if (tipo == "agente") Limpiar(e);
            e = e.Clonar();
        }
        EntradaCambiada?.Invoke(this, e);
    }

    private void TextoDeSubagente(string padre, string texto)
    {
        Entrada e;
        lock (_cerrojo)
        {
            var sub = _turno.FirstOrDefault(x => x.Tipo == "herramienta" && x.Id == padre);
            if (sub is null) return;
            sub.Texto = (sub.Texto ?? "") + texto;
            if (sub.Texto.Length > 4000) sub.Texto = sub.Texto[^4000..]; // basta con lo último que ha dicho
            e = sub.Clonar();
        }
        EntradaCambiada?.Invoke(this, e);
    }

    /// <summary>Aviso que Codex repite en cada turno con modelos que no son de OpenAI: no aporta nada en la conversación.</summary>
    private static readonly System.Text.RegularExpressions.Regex AvisoMetadatos = new(
        @"Warning: Model metadata for `[^`]*` not found\. Defaulting to fallback metadata; this can degrade performance and cause issues\.\s*");

    /// <summary>Error HTTP del proveedor que el agente devuelve como texto («unexpected status 404 Not Found: …, url: …»).</summary>
    private static readonly System.Text.RegularExpressions.Regex ErrorProveedor = new(
        @"^\s*unexpected status (\d{3})[^\n]*?(?:url: (\S+))?\s*$", System.Text.RegularExpressions.RegexOptions.Singleline);

    private string ModeloNoDisponible() =>
        $"El proveedor no tiene el modelo {(Cuenta.Modelo is { } m ? $"«{m}»" : "elegido")} disponible para la cuenta «{Cuenta.Alias}». " +
        "Cámbialo en Cuentas y proveedores › «Modelo…».";

    /// <summary>Quita el ruido del agente y pasa a lenguaje llano los errores del proveedor (no los de límite: van al relevo).</summary>
    private void Limpiar(Entrada e)
    {
        if (e.Texto!.StartsWith("There's an issue with the selected model", StringComparison.Ordinal) && e.Texto.Contains("may not have access"))
        {
            e.Tipo = "aviso";
            e.Nivel = "error";
            e.Texto = ModeloNoDisponible();
            return;
        }
        if (e.Texto!.Contains("Warning: Model metadata", StringComparison.Ordinal))
            e.Texto = AvisoMetadatos.Replace(e.Texto, "");
        var m = ErrorProveedor.Match(e.Texto);
        if (!m.Success) return;
        var codigo = int.Parse(m.Groups[1].Value);
        var url = m.Groups[2].Success ? m.Groups[2].Value : "";
        var modelo = Cuenta.Modelo is { } mo ? $"«{mo}»" : "este modelo";
        var texto = codigo switch
        {
            404 when url.EndsWith("/responses", StringComparison.Ordinal) =>
                $"El proveedor no admite {modelo} con Codex (no tiene la API que usa Codex para ese modelo). Elige otro modelo para la cuenta «{Cuenta.Alias}» o usa este modelo con otro proveedor.",
            404 => $"El proveedor no encuentra {modelo} (error 404). Revisa el modelo y la dirección de la cuenta «{Cuenta.Alias}».",
            401 or 403 => $"El proveedor rechaza la clave de la cuenta «{Cuenta.Alias}» (error {codigo}). Cámbiala en Cuentas y proveedores.",
            429 => null,
            >= 500 => $"El proveedor está fallando ahora mismo (error {codigo}). Prueba otra vez en un rato o pasa la sesión a otra cuenta.",
            _ => $"El proveedor ha devuelto un error {codigo}.",
        };
        if (texto is null) return;
        Registro.Aviso($"{Proveedor.Id}·{Cuenta.Alias}: {e.Texto.Trim()}");
        e.Tipo = "aviso";
        e.Nivel = "error";
        e.Texto = texto;
    }

    private void AplicarHerramienta(JsonNode u, string? id, bool nueva)
    {
        if (id is null) return;
        if (ArranqueMcp(u)) return;
        // Claude Code compacta la conversación (llena el contexto): llega como herramienta «compact» con los tokens de antes
        // y de después; se guarda aparte para enseñarlo como un hito de la conversación, no como una acción más
        var compacta = u["_meta"]?["claudeCode"]?["toolName"]?.GetValue<string>() == "compact";
        Entrada e;
        lock (_cerrojo)
        {
            var existente = _turno.FirstOrDefault(x => x.Tipo is "herramienta" or "compactacion" && x.Id == id);
            if (existente is null)
            {
                existente = Hilo.Entradas.FirstOrDefault(x => x.Tipo is "herramienta" or "compactacion" && x.Id == id)
                            ?? new Entrada { Id = id, Tipo = "herramienta", Estado = "pending", Cuenta = Cuenta.Alias };
                _turno.Add(existente);
            }
            e = existente;
            if (compacta) e.Tipo = "compactacion";
            if (compacta && u["rawOutput"] is JsonObject datos) e.Datos = datos.DeepClone();
            if (u["_meta"]?["claudeCode"] is JsonObject cc)
            {
                if (cc["toolName"]?.GetValue<string>() is { } nombre) e.Herramienta = nombre;
                if (cc["parentToolUseId"]?.GetValue<string>() is { } padre) e.Padre = padre;
            }
            if (u["title"]?.GetValue<string>() is { } t) e.Titulo = t;
            if (u["kind"]?.GetValue<string>() is { } k) e.Clase = k;
            if (u["status"]?.GetValue<string>() is { } s) e.Estado = s;
            if (u["content"] is JsonArray c && (c.Count > 0 || nueva)) e.Contenido = c.DeepClone();
            if (u["locations"] is JsonArray l && l.Count > 0) e.Ubicaciones = l.DeepClone();
            if (u["rawInput"] is { } ri) e.Datos = ri.DeepClone();
            e = e.Clonar();
        }
        EntradaCambiada?.Invoke(this, e);
    }

    /// <summary>
    /// Codex informa del arranque de cada servidor MCP como si fuera una herramienta («mcp__x__startup»), en cada turno. No
    /// es trabajo del agente: si falla, se avisa una vez por sesión; si va bien, no se enseña.
    /// </summary>
    private bool ArranqueMcp(JsonNode u)
    {
        var titulo = u["title"]?.GetValue<string>();
        if (titulo is null || !titulo.StartsWith("mcp__", StringComparison.Ordinal) || !titulo.EndsWith("__startup", StringComparison.Ordinal)) return false;
        var servidor = titulo["mcp__".Length..^"__startup".Length];
        if (u["status"]?.GetValue<string>() == "failed" && _mcpAvisados.Add(servidor))
        {
            var detalle = (u["content"] as JsonArray ?? []).Select(x => x?["content"]?["text"]?.GetValue<string>()).FirstOrDefault(x => x is not null) ?? "";
            var motivo = detalle.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ? "pide iniciar sesión en él desde " + Proveedor.Nombre : "no ha podido arrancar";
            Registro.Aviso($"{Proveedor.Id}·{Cuenta.Alias}: MCP {servidor}: {detalle}");
            Avisar($"El servidor MCP «{servidor}» no está disponible en {Proveedor.Nombre}: {motivo}. Si no lo necesitas aquí, en Extensiones puedes dejarlo solo para los agentes que lo usan.", "info");
        }
        return true;
    }

    private void AplicarPlan(JsonNode? pasos)
    {
        Entrada e;
        lock (_cerrojo)
        {
            var plan = _turno.FirstOrDefault(x => x.Tipo == "plan");
            if (plan is null)
            {
                plan = new Entrada { Tipo = "plan", Cuenta = Cuenta.Alias };
                _turno.Add(plan);
            }
            plan.Pasos = pasos?.DeepClone();
            e = plan.Clonar();
        }
        EntradaCambiada?.Invoke(this, e);
    }

    private void Avisar(string texto, string nivel)
    {
        var e = new Entrada { Tipo = "aviso", Texto = texto, Nivel = nivel, Cuenta = Cuenta.Alias };
        lock (_cerrojo) _turno.Add(e);
        EntradaCambiada?.Invoke(this, e.Clonar());
    }

    private void CerrarTurno()
    {
        List<Entrada> turno;
        lock (_cerrojo)
        {
            turno = [.. _turno];
            _turno.Clear();
        }
        foreach (var e in turno.Where(e => e.Tipo != "agente" || !string.IsNullOrWhiteSpace(e.Texto))) Hilo.Guardar(e);
    }

    private void PonerEstado(EstadoSesion estado)
    {
        if (Estado == estado) return;
        Estado = estado;
        Sesion.Estado = estado;
        Cambiado?.Invoke(this);
    }

    /// <summary>
    /// Algunos agentes no dan error al llegar al límite: contestan con un texto corto («Claude AI usage limit
    /// reached…», «You've hit your usage limit…»). Si la última respuesta del turno es así, se trata como límite.
    /// </summary>
    private string? LimiteEnLaRespuesta()
    {
        string? texto;
        bool conHerramientas;
        lock (_cerrojo)
        {
            texto = _turno.LastOrDefault(e => e.Tipo == "agente")?.Texto;
            conHerramientas = _turno.Any(e => e.Tipo == "herramienta");
        }
        // Un turno que ha trabajado con herramientas es una respuesta de verdad, aunque hable de límites
        return texto is not null && !conHerramientas && EsRespuestaDeLimite(texto) ? texto : null;
    }

    // Frases con las que los agentes y los proveedores avisan del límite de uso (no «limit» ni «rate» sueltos)
    private const string FrasesLimite =
        @"\busage[ _-]?limit|\blimit (reached|exceeded)|\bhit your (usage )?limit|\brate[ _-]?limit(ed| reached| exceeded|_error)" +
        @"|\bquota (exceeded|exhausted)|\bexceeded your (current )?quota|\bresource[ _-]?exhausted|\bout of credits" +
        @"|\binsufficient (credit|balance)|\btoo many requests\b|\b429\b";

    // Errores de tamaño del contexto: dicen «limit» pero no se arreglan cambiando de cuenta
    private static readonly System.Text.RegularExpressions.Regex ErrorDeContexto = new(
        @"context|prompt is too long|token limit|max_tokens", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex ErrorDeLimite = new(
        FrasesLimite + @"|\brate[ _-]?limit\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex RespuestaDeLimite = new(
        FrasesLimite, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>¿El error del agente es de límite de uso (y no de contexto ni otra cosa)?</summary>
    internal static bool EsErrorDeLimite(string t) => ErrorDeLimite.IsMatch(t) && !ErrorDeContexto.IsMatch(t);

    /// <summary>¿La respuesta corta del agente es su aviso de límite? (Solo cuenta si lo dice al principio.)</summary>
    internal static bool EsRespuestaDeLimite(string t)
    {
        t = t.Trim();
        if (t.Length > 400 || ErrorDeContexto.IsMatch(t)) return false;
        var m = RespuestaDeLimite.Match(t);
        return m.Success && m.Index <= 40;
    }

    private static bool EsLimite(ErrorAcp ex) => EsErrorDeLimite(ex.Message + " " + ex.Datos?.ToJsonString());

    private string MensajeLlano(ErrorAcp ex)
    {
        var t = (ex.Message + " " + ex.Datos?.ToJsonString()).ToLowerInvariant();
        if (t.Contains("auth") || t.Contains("not logged in") || t.Contains("api key") || t.Contains("login") || t.Contains("401"))
            return Cuenta.Tipo == TipoCuenta.Api
                ? $"La clave de API de la cuenta «{Cuenta.Alias}» no es válida o falta. Revísala en Cuentas."
                : $"La cuenta «{Cuenta.Alias}» no tiene la sesión iniciada. Ve a Cuentas y pulsa «Autorizar…».";
        if (EsLimite(ex))
            return $"La cuenta «{Cuenta.Alias}» ha llegado a su límite de uso. ({ex.Message})";
        if (t.Contains("issue with the selected model")) return ModeloNoDisponible();
        if (ex.Codigo == -1)
            return $"{Proveedor.Nombre} se ha cerrado inesperadamente. Vuelve a abrir la sesión para seguir.";
        return $"{Proveedor.Nombre}: {ex.Message}";
    }

    private async Task CerrarConexion()
    {
        if (_conexion is null) return;
        var c = _conexion;
        _conexion = null;
        _cerrandoAdrede = true;
        await c.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _respuestaPermiso?.TrySetResult(null);
        CerrarTurno();
        Estado = EstadoSesion.Cerrada;
        Sesion.Estado = EstadoSesion.Cerrada;
        await CerrarConexion();
    }
}
