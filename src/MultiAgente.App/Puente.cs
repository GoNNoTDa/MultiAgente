using System.Diagnostics;
using System.Text.Json;
using MultiAgente.Nucleo;

namespace MultiAgente.App;

/// <summary>
/// Mensajes entre la interfaz web y la aplicación. La interfaz manda <c>{ "tipo": "...", ... }</c>; la aplicación
/// responde con <c>estado</c>, <c>conversacion</c>, <c>entrada</c>, <c>agente</c>, <c>adjuntos</c>, <c>aviso</c>…
/// Llega y responde en el hilo de la ventana.
/// </summary>
internal sealed class Puente(VentanaPrincipal ventana, Rutas rutas, Gestor servicio)
{
    public static readonly JsonSerializerOptions Json = Nucleo.Json.Opciones;

    private bool _primeraVez = true;

    private static readonly HashSet<string> Ejecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp",
        ".lnk", ".scr", ".pif", ".reg", ".hta", ".cpl", ".jar", ".appref-ms",
    };

    public async void Recibir(string json, IReadOnlyList<string> soltados)
    {
        string? tipo = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            tipo = Texto(m, "tipo");
            await Atender(tipo, m, soltados);
        }
        catch (ExcepcionUsuario ex)
        {
            Registro.Aviso($"{tipo}: {ex.Message}");
            ventana.Enviar(new { tipo = "aviso", texto = ex.Message, origen = tipo });
        }
        catch (Exception ex)
        {
            Registro.Error($"Al atender «{tipo}» de la interfaz", ex);
            ventana.Enviar(new { tipo = "aviso", texto = "Algo ha fallado. El detalle está en el registro (datos\\logs).", origen = tipo });
        }
    }

    private async Task Atender(string? tipo, JsonElement m, IReadOnlyList<string> soltados)
    {
        switch (tipo)
        {
            case "listo":
            case "recomprobar":
                ventana.Enviar(servicio.Estado(recomprobar: tipo == "recomprobar"));
                if (tipo == "listo" && _primeraVez) { _primeraVez = false; ventana.InterfazLista(); }
                break;
            case "instalarRequisitos":
                InstalarRequisitos();
                break;
            case "abrirCarpeta":
                Abrir(Texto(m, "que") switch { "datos" => rutas.Datos, "logs" => rutas.Logs, _ => rutas.Raiz });
                break;
            case "abrirRuta":
                AbrirRuta(Texto(m, "ruta"));
                break;
            case "abrirEnlace":
                if (Uri.TryCreate(Texto(m, "url"), UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
                    Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
                break;

            // ----- cuentas
            case "cuentaNueva":
                await servicio.ComprobarModelo(Texto(m, "catalogo"), Texto(m, "endpoint"), Texto(m, "clave"), Texto(m, "modelo"));
                servicio.CrearCuenta(Texto(m, "proveedor") ?? "", Texto(m, "tipoCuenta") ?? "Login", Texto(m, "alias") ?? "",
                                     Texto(m, "clave"), Texto(m, "catalogo"), Texto(m, "endpoint"), Texto(m, "modelo"));
                ventana.Enviar(new { tipo = "cuentaCreada" });
                break;
            case "cuentaClave":
                servicio.CambiarClave(Texto(m, "id") ?? "", Texto(m, "clave") ?? "");
                break;
            case "cuentaModelo":
            {
                var texto = await servicio.CambiarModelo(Texto(m, "id") ?? "", Texto(m, "modelo") ?? "");
                ventana.Enviar(new { tipo = "cuentaModeloGuardado", id = Texto(m, "id"), texto });
                break;
            }
            case "modelosProbar":
            {
                var (funcionan, probados) = await servicio.ProbarModelosDe(Texto(m, "id") ?? "");
                ventana.Enviar(new { tipo = "modelosProbados", id = Texto(m, "id"), funcionan, probados });
                break;
            }
            case "cuentaQuitar":
                await servicio.QuitarCuenta(Texto(m, "id") ?? "");
                break;
            case "cuentaAutorizar":
                servicio.Autorizar(Texto(m, "id") ?? "");
                break;
            case "modelos":
            {
                var lista = await servicio.ListarModelos(Texto(m, "catalogo"), Texto(m, "endpoint"), Texto(m, "clave"), Texto(m, "cuentaId"));
                ventana.Enviar(new { tipo = "modelos", para = Texto(m, "para"), modelos = lista.Select(x => new { id = x.Id, nombre = x.Nombre }) });
                break;
            }

            // ----- carpetas y adjuntos
            case "elegirCarpeta":
            {
                using var d = new FolderBrowserDialog { Description = Texto(m, "titulo") ?? "Carpeta de trabajo de la sesión", UseDescriptionForTitle = true, ShowNewFolderButton = true };
                if (Texto(m, "inicial") is { } ini && Directory.Exists(ini)) d.InitialDirectory = ini;
                if (d.ShowDialog(ventana) == DialogResult.OK)
                    ventana.Enviar(new { tipo = "carpeta", para = Texto(m, "para"), ruta = d.SelectedPath });
                break;
            }
            case "elegirFicheros":
            {
                using var d = new OpenFileDialog { Multiselect = true, Title = Texto(m, "titulo") ?? "Adjuntar ficheros" };
                if (d.ShowDialog(ventana) == DialogResult.OK)
                    EnviarAdjuntos(d.FileNames.Select(Gestor.DeRuta));
                break;
            }
            case "soltar":
                EnviarAdjuntos(soltados.Where(r => File.Exists(r) || Directory.Exists(r)).Select(Gestor.DeRuta));
                break;
            case "pegar":
                EnviarAdjuntos([servicio.GuardarPegado(Texto(m, "sesionId"), Texto(m, "nombre") ?? "", Texto(m, "mime") ?? "image/png", Texto(m, "base64") ?? "")]);
                break;

            // ----- sesiones
            case "sesionNueva":
            {
                var id = await servicio.NuevaSesion(Texto(m, "carpeta") ?? "", Texto(m, "quien") ?? "", Texto(m, "texto") ?? "", Adjuntos(m),
                                                    Texto(m, "proyectoId"));
                ventana.Enviar(new { tipo = "sesionAbierta", id });
                break;
            }
            case "abrirSesion":
                await servicio.AbrirSesion(Texto(m, "id") ?? "");
                break;
            case "enviar":
                await servicio.Enviar(Texto(m, "sesionId") ?? "", Texto(m, "texto") ?? "", Adjuntos(m));
                break;
            case "interrumpir":
                await servicio.Interrumpir(Texto(m, "sesionId") ?? "");
                break;
            case "permiso":
                servicio.ResponderPermiso(Texto(m, "sesionId") ?? "", Texto(m, "peticion") ?? "", Texto(m, "opcion"));
                break;
            case "opcion":
                await servicio.CambiarOpcion(Texto(m, "sesionId") ?? "", Texto(m, "id") ?? "", Texto(m, "valor") ?? "");
                break;
            case "cambiarCuenta":
                await servicio.CambiarCuenta(Texto(m, "sesionId") ?? "", Texto(m, "quien") ?? "");
                break;
            case "archivar":
                await servicio.Archivar(Texto(m, "sesionId") ?? "");
                break;
            case "desarchivar":
                servicio.Desarchivar(Texto(m, "sesionId") ?? "");
                break;
            case "sesionRenombrar":
                servicio.RenombrarSesion(Texto(m, "sesionId") ?? "", Texto(m, "titulo") ?? "");
                break;
            case "sesionMover":
                servicio.MoverSesion(Texto(m, "sesionId") ?? "", Texto(m, "proyectoId"));
                break;

            // ----- proyectos
            case "proyectoGuardar":
            {
                var p = servicio.GuardarProyecto(Texto(m, "id"), Texto(m, "nombre") ?? "", Texto(m, "ruta") ?? "");
                ventana.Enviar(new { tipo = "proyectoGuardado", id = p.Id, nuevo = Texto(m, "id") is null });
                break;
            }
            case "proyectoBorrar":
                servicio.BorrarProyecto(Texto(m, "id") ?? "");
                break;

            // ----- importar conversaciones previas
            case "historiales":
                ventana.Enviar(await servicio.HistorialesPrevios());
                break;
            case "historialesImportar":
            {
                var (importadas, fallos) = await servicio.ImportarHistoriales(Lista(m, "claves"));
                ventana.Enviar(new { tipo = "historialesImportados", importadas, fallos });
                break;
            }

            // ----- visor de cambios
            case "cambios":
            {
                var sesionId = Texto(m, "sesionId") ?? "";
                var c = await servicio.Cambios(sesionId);
                ventana.Enviar(new { tipo = "cambios", sesionId, git = c is not null, raiz = c?.Raiz, rama = c?.Rama,
                                     ficheros = c?.Ficheros ?? [], recortado = c?.Recortado ?? false });
                break;
            }
            case "cambioFichero":
            {
                var sesionId = Texto(m, "sesionId") ?? "";
                var d = await servicio.DiferenciasDe(sesionId, Texto(m, "ruta") ?? "");
                ventana.Enviar(new { tipo = "cambioFichero", sesionId, d.Ruta, d.Texto, d.Binario, d.Recortado });
                break;
            }

            // ----- extensiones compartidas
            case "extensiones":
                ventana.Enviar(servicio.EstadoExtensiones());
                break;
            case "mcpGuardar":
            {
                var s = m.TryGetProperty("servidor", out var sv) ? sv.Deserialize<ServidorMcpDef>(Json) : null;
                if (s is null) throw new ExcepcionUsuario("Faltan los datos del servidor.");
                if (string.IsNullOrEmpty(s.Id)) s.Id = Guid.NewGuid().ToString("N");
                servicio.GuardarMcp(s);
                ventana.Enviar(new { tipo = "mcpGuardado" });
                break;
            }
            case "mcpActivar":
                servicio.ActivarMcp(Texto(m, "id") ?? "", m.TryGetProperty("activo", out var ac) && ac.ValueKind == JsonValueKind.True);
                break;
            case "mcpBorrar":
                servicio.BorrarMcp(Texto(m, "id") ?? "");
                break;
            case "mcpImportables":
                ventana.Enviar(new { tipo = "mcpImportables", servidores = servicio.McpImportables()
                    .Select(s => new { s.Nombre, s.Tipo, s.Comando, s.Argumentos, s.Url, s.Origen }) });
                break;
            case "mcpImportar":
                servicio.ImportarMcpDe(Lista(m, "nombres"));
                break;
            case "extImportables":
                ventana.Enviar(new { tipo = "extImportables", clave = Texto(m, "clave"), elementos = servicio.Importables(Texto(m, "clave") ?? "") });
                break;
            case "extImportar":
                servicio.ImportarExtensiones(Texto(m, "clave") ?? "", Lista(m, "rutas"));
                break;
            case "extAnadir":
            {
                var clave = Texto(m, "clave") ?? "";
                if (clave == "skills")
                {
                    using var d = new FolderBrowserDialog { Description = Texto(m, "titulo") ?? "Carpeta de la skill (con su SKILL.md)", UseDescriptionForTitle = true };
                    if (d.ShowDialog(ventana) == DialogResult.OK) servicio.ImportarExtensiones(clave, [d.SelectedPath]);
                }
                else
                {
                    using var d = new OpenFileDialog
                    {
                        Multiselect = true, Title = Texto(m, "titulo") ?? "Añadir ficheros",
                        // la barra separa las partes del filtro: no puede venir en el nombre
                        Filter = $"{(Texto(m, "filtro") ?? "Órdenes y agentes").Replace("|", "")} (*.md;*.toml)|*.md;*.toml",
                    };
                    if (d.ShowDialog(ventana) == DialogResult.OK) servicio.ImportarExtensiones(clave, d.FileNames);
                }
                break;
            }
            case "skillNueva":
                servicio.NuevaSkill(Texto(m, "nombre") ?? "", Texto(m, "descripcion") ?? "");
                break;
            case "extQuitar":
                servicio.QuitarExtension(Texto(m, "ruta") ?? "");
                break;
            case "pluginActivar":
                servicio.ActivarPlugin(Texto(m, "id") ?? "", m.TryGetProperty("activo", out var pa) && pa.ValueKind == JsonValueKind.True);
                break;
            case "instruccionesGuardar":
                servicio.GuardarInstrucciones(Texto(m, "texto") ?? "");
                ventana.Enviar(new { tipo = "instruccionesGuardadas" });
                break;
            case "instruccionesUsuario":
                ventana.Enviar(new { tipo = "instruccionesUsuario", lista = servicio.InstruccionesDelUsuario() });
                break;

            // ----- estadísticas, consumo y límites
            case "consumoActualizar":
                await servicio.ActualizarConsumo(todas: true);
                ventana.Enviar(servicio.Estado());
                break;
            case "estadisticas":
                ventana.Enviar(servicio.Estadisticas(m.TryGetProperty("dias", out var di) && di.TryGetInt32(out var nd) ? nd : 7));
                break;
            case "cuentaLimites":
            {
                double? Num(string n) => m.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
                servicio.GuardarLimites(Texto(m, "id") ?? "", Num("umbral5h"), Num("umbralSemana"), Num("presupuesto"));
                ventana.Enviar(new { tipo = "limitesGuardados" });
                break;
            }

            // ----- equipos y relevo
            case "equipoGuardar":
            {
                var miembros = m.TryGetProperty("miembros", out var mi) && mi.ValueKind == JsonValueKind.Array
                    ? mi.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList() : [];
                var atras = m.TryGetProperty("cuentaAtras", out var ca) && ca.TryGetInt32(out var n) ? n : 60;
                var otro = !m.TryGetProperty("permiteOtroProveedor", out var po) || po.ValueKind != JsonValueKind.False;
                servicio.GuardarEquipo(Texto(m, "id"), Texto(m, "nombre") ?? "", Texto(m, "regla") ?? "Prioridad", otro, atras, miembros);
                break;
            }
            case "equipoBorrar":
                servicio.BorrarEquipo(Texto(m, "id") ?? "");
                break;
            case "relevoYa":
                await servicio.RelevoYa(Texto(m, "sesionId") ?? "");
                break;
            case "relevoCancelar":
                servicio.CancelarRelevo(Texto(m, "sesionId") ?? "");
                break;
            case "ajuste":
                servicio.GuardarAjuste(Texto(m, "clave") ?? "", Texto(m, "valor"));
                break;
            default:
                Registro.Aviso($"Mensaje desconocido de la interfaz: {tipo}");
                ventana.Enviar(new { tipo = "aviso", texto = "Algo ha fallado. El detalle está en el registro (datos\\logs).", origen = tipo });
                break;
        }
    }

    private static List<string> Lista(JsonElement m, string nombre) =>
        m.TryGetProperty(nombre, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList() : [];

    private static string? Texto(JsonElement m, string nombre) =>
        m.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Adjuntos que manda la interfaz: solo rutas que existen.</summary>
    private static List<Adjunto> Adjuntos(JsonElement m)
    {
        var lista = new List<Adjunto>();
        if (!m.TryGetProperty("adjuntos", out var a) || a.ValueKind != JsonValueKind.Array) return lista;
        foreach (var x in a.EnumerateArray())
        {
            var ruta = Texto(x, "ruta");
            if (ruta is null || !(File.Exists(ruta) || Directory.Exists(ruta))) continue;
            var d = Gestor.DeRuta(ruta);
            lista.Add(d with { Nombre = Texto(x, "nombre") ?? d.Nombre });
        }
        return lista;
    }

    private void EnviarAdjuntos(IEnumerable<Adjunto> adjuntos) =>
        ventana.Enviar(new { tipo = "adjuntos", adjuntos = adjuntos.Select(a => new { a.Ruta, a.Nombre, a.Tipo, a.EsImagen, url = UrlVista(a) }) });

    /// <summary>Dirección para ver la miniatura: las de datos\adjuntos por su host; las demás imágenes pequeñas, en línea.</summary>
    private string? UrlVista(Adjunto a)
    {
        if (!a.EsImagen) return null;
        var rel = Path.GetRelativePath(rutas.Adjuntos, a.Ruta);
        if (!rel.StartsWith("..") && !Path.IsPathRooted(rel))
            return $"https://{VentanaPrincipal.HostAdjuntos}/{string.Join('/', rel.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString))}";
        try
        {
            var f = new FileInfo(a.Ruta);
            return f.Length <= 3 * 1024 * 1024 ? $"data:{a.Tipo};base64,{Convert.ToBase64String(File.ReadAllBytes(a.Ruta))}" : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>Abre una ruta de la conversación: carpetas y documentos se abren; los ejecutables solo se señalan en su carpeta.</summary>
    private static void AbrirRuta(string? ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta)) return;
        if (Directory.Exists(ruta)) { Abrir(ruta); return; }
        if (!File.Exists(ruta)) throw new ExcepcionUsuario($"No existe {ruta}.");
        if (Ejecutables.Contains(Path.GetExtension(ruta)))
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", ruta }, UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo(ruta) { UseShellExecute = true });
    }

    private static void Abrir(string carpeta) =>
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { carpeta }, UseShellExecute = true });

    private void InstalarRequisitos()
    {
        var script = Path.Combine(rutas.Herramientas, "Instalar-Requisitos.ps1");
        if (!File.Exists(script)) throw new ExcepcionUsuario("No está el instalador de requisitos (tools\\Instalar-Requisitos.ps1).");
        Registro.Info("Se abre la instalación de requisitos");
        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoExit", "-ExecutionPolicy", "Bypass", "-File", script },
            UseShellExecute = true,
            WorkingDirectory = rutas.Raiz,
        });
    }
}
