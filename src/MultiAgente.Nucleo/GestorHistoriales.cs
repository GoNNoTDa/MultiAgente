using System.Text.Json;

namespace MultiAgente.Nucleo;

/// <summary>Importar conversaciones previas del equipo (Claude Code, Codex, Gemini CLI) como sesiones de MultiAgente.</summary>
public sealed partial class Gestor
{
    private static string ClaveImportada(HistorialPrevio h) => $"importada:{h.Origen}:{h.Id}";

    private static string NombreOrigen(string origen) => origen switch
    {
        "Claude" => "Claude Code", "Codex" => "Codex", "Gemini" => "Gemini CLI", _ => origen,
    };

    private bool YaImportada(HistorialPrevio h) => _repo.LeerAjuste(ClaveImportada(h)) is { } id && _repo.Sesion(id) is not null;

    /// <summary>Las conversaciones previas que hay en el equipo, de la más reciente a la más antigua.</summary>
    public async Task<object> HistorialesPrevios()
    {
        var lista = await Task.Run(() => Historiales.Buscar(_op.PerfilUsuario));
        return new
        {
            tipo = "historiales",
            lista = lista.Select(h => new
            {
                clave = h.Fichero, origen = h.Origen, nombreOrigen = NombreOrigen(h.Origen), h.Titulo, h.Carpeta, h.Fecha, h.Mensajes,
                existe = h.Carpeta is not null && Directory.Exists(h.Carpeta), importada = YaImportada(h),
            }),
        };
    }

    /// <summary>
    /// Importa las conversaciones elegidas (por su fichero): cada una pasa a ser una sesión en el proyecto de su carpeta,
    /// en automático con el equipo principal y empezando por una cuenta del mismo proveedor si la hay. Con esa cuenta el
    /// agente la retoma tal cual (Claude Code y Codex); con otra, recibe la conversación como traspaso.
    /// </summary>
    public async Task<(int Importadas, List<string> Fallos)> ImportarHistoriales(IReadOnlyList<string> ficheros)
    {
        var cuentas = _repo.Cuentas();
        if (cuentas.Count == 0) throw new ExcepcionUsuario("Añade antes una cuenta: las conversaciones importadas siguen con ella.");
        var equipo = _repo.Equipos().FirstOrDefault(e => e.Predeterminado);
        var miembros = equipo?.Miembros.Select(id => cuentas.FirstOrDefault(c => c.Id == id)).OfType<Cuenta>().ToList() ?? [];
        if (miembros.Count == 0) miembros = cuentas;

        // Solo ficheros que de verdad son historiales del equipo (no una ruta cualquiera que llegue de la interfaz)
        var pedidos = new HashSet<string>(ficheros, StringComparer.OrdinalIgnoreCase);
        var elegidos = (await Task.Run(() => Historiales.Buscar(_op.PerfilUsuario))).Where(h => pedidos.Contains(h.Fichero)).ToList();
        var fallos = new List<string>();
        var hechas = 0;
        foreach (var h in elegidos)
        {
            var nombre = h.Titulo ?? h.Id;
            try
            {
                if (YaImportada(h)) { fallos.Add($"«{nombre}»: ya estaba importada."); continue; }
                if (h.Carpeta is null || !Directory.Exists(h.Carpeta)) { fallos.Add($"«{nombre}»: su carpeta ya no existe ({h.Carpeta ?? "no se sabe cuál era"})."); continue; }
                var entradas = await Task.Run(() => Historiales.Leer(h));
                var carpeta = Path.TrimEndingDirectorySeparator(Path.GetFullPath(h.Carpeta));
                // Una cuenta del mismo proveedor (mejor del equipo y de suscripción): con ella el agente la retoma tal cual
                var mismo = miembros.Concat(cuentas).Where(c => c.Proveedor == h.Origen && c.Endpoint is null)
                    .OrderBy(c => c.Tipo == TipoCuenta.Login ? 0 : 1).FirstOrDefault();
                var cuenta = mismo ?? miembros[0];

                var proyecto = _repo.ProyectoDe(carpeta);
                if (proyecto is null)
                {
                    var proyectos = _repo.Proyectos();
                    proyecto = new Proyecto { Nombre = new DirectoryInfo(carpeta).Name, Ruta = carpeta, Orden = proyectos.Count == 0 ? 0 : proyectos.Max(p => p.Orden) + 1 };
                    _repo.GuardarProyecto(proyecto);
                }
                var s = new Sesion
                {
                    ProyectoId = proyecto.Id, Carpeta = carpeta, CuentaId = cuenta.Id, EquipoId = equipo?.Id,
                    Titulo = h.Titulo ?? "Conversación importada", Creada = entradas.FirstOrDefault()?.Hora ?? h.Fecha, UltimoUso = h.Fecha,
                };
                entradas.Insert(0, new Entrada
                {
                    Tipo = "aviso", Nivel = "info", Hora = s.Creada,
                    Texto = $"Conversación importada de {NombreOrigen(h.Origen)} (última actividad: {h.Fecha:dd/MM/yyyy HH:mm}).",
                });
                Hilo.Crear(Path.Combine(_rutas.Sesiones, s.Id), entradas);
                _repo.GuardarSesion(s);
                if (mismo is not null && Historiales.CopiarParaRetomar(h, _rutas, _op.PerfilUsuario) is { } nativa)
                    _repo.GuardarTramo(new Tramo { SesionId = s.Id, Orden = 1, CuentaId = cuenta.Id, SesionNativa = nativa, Desde = s.Creada, Motivo = "importada" });
                _repo.GuardarAjuste(ClaveImportada(h), s.Id);
                hechas++;
                Registro.Info($"Importada {h.Origen} {h.Id} ({entradas.Count} entradas) como sesión {s.Id} con {cuenta.Alias}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Registro.Error($"Al importar {h.Fichero}", ex);
                fallos.Add($"«{nombre}»: no se pudo leer.");
            }
        }
        EnviarEstado();
        return (hechas, fallos);
    }
}
