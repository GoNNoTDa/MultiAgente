namespace MultiAgente.Nucleo;

/// <summary>Extensiones compartidas: servidores MCP, skills, órdenes, agentes, plugins e instrucciones.</summary>
public sealed partial class Gestor
{
    /// <summary>Tipos de elemento compartido por carpeta: (clave, proveedor, carpeta común relativa).</summary>
    private static readonly (string Clave, string Proveedor, string Sub, string Titulo)[] Carpetas =
    [
        ("ordenes-claude", "Claude", "commands", "Órdenes de Claude Code (/orden)"),
        ("agentes-claude", "Claude", "agents", "Subagentes de Claude Code"),
        ("estilos-claude", "Claude", "output-styles", "Estilos de respuesta de Claude Code"),
        ("prompts-codex", "Codex", "prompts", "Prompts de Codex (/prompts:nombre)"),
        ("ordenes-gemini", "Gemini", Path.Combine(".gemini", "commands"), "Órdenes de Gemini (.toml)"),
    ];

    private string CarpetaDe(string clave)
    {
        if (clave == "skills") return Extensiones.Skills(_rutas);
        var c = Carpetas.FirstOrDefault(x => x.Clave == clave);
        if (c.Clave is null) throw new ExcepcionUsuario("Tipo de extensión desconocido.");
        return CarpetasCompartidas.Comun(_rutas, c.Proveedor, c.Sub);
    }

    /// <summary>Todo lo compartido, para la vista «Extensiones».</summary>
    public object EstadoExtensiones()
    {
        var mcp = CatalogoMcp.Cargar(_repo);
        return new
        {
            tipo = "extensiones",
            mcp = mcp.Select(s => new
            {
                s.Id, s.Nombre, s.Tipo, s.Comando, s.Argumentos, s.Url, s.Activo, s.Origen, s.Agentes,
                valores = s.Valores.Select(v => new
                {
                    v.Nombre, valor = v.Secreto ? null : v.Valor, v.Secreto,
                    guardado = v.Secreto && _boveda.Contiene(CatalogoMcp.RefSecreto(s, v.Nombre)),
                }),
                problema = Problema(s),
            }),
            skills = new { carpeta = Extensiones.Skills(_rutas), elementos = Extensiones.ListarSkills(Extensiones.Skills(_rutas)) },
            carpetas = Carpetas.Select(c =>
            {
                var dir = CarpetasCompartidas.Comun(_rutas, c.Proveedor, c.Sub);
                return new { c.Clave, c.Proveedor, c.Titulo, carpeta = dir, elementos = Extensiones.ListarFicheros(dir) };
            }),
            plugins = Extensiones.PluginsClaude(_rutas),
            carpetaPlugins = Path.Combine(_rutas.Comun, "Claude", "plugins"),
            instrucciones = File.Exists(Extensiones.Instrucciones(_rutas)) ? File.ReadAllText(Extensiones.Instrucciones(_rutas)) : "",
        };
    }

    private string? Problema(ServidorMcpDef s)
    {
        try
        {
            CatalogoMcp.Definicion(s, _boveda);
            var faltan = s.Valores.Where(v => v.Secreto && !_boveda.Contiene(CatalogoMcp.RefSecreto(s, v.Nombre))).Select(v => v.Nombre).ToList();
            if (CatalogoMcp.Marcas(s).Any(m => !_boveda.Contiene(CatalogoMcp.RefMarca(s, m)))) faltan.Add("un secreto de los argumentos o la dirección");
            return faltan.Count > 0 ? "falta el valor de " + string.Join(", ", faltan) : null;
        }
        catch (ExcepcionUsuario ex) { return ex.Message; }
    }

    private void EnviarExtensiones() => _enviar(EstadoExtensiones());

    // ---------------------------------------------------------------- MCP

    /// <summary>
    /// Guarda un servidor (nuevo o cambiado). Los valores secretos que lleguen vacíos conservan el que ya había en la
    /// bóveda; los que lleguen con texto lo sustituyen.
    /// </summary>
    public void GuardarMcp(ServidorMcpDef s)
    {
        s.Nombre = s.Nombre.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(s.Nombre, @"^[A-Za-z0-9_.\-]{1,60}$"))
            throw new ExcepcionUsuario("El nombre del servidor MCP solo puede llevar letras, números, puntos, guiones y _.");
        if (s.Tipo is not ("stdio" or "http" or "sse")) throw new ExcepcionUsuario("Tipo de servidor MCP desconocido.");
        if (s.Tipo == "stdio" && string.IsNullOrWhiteSpace(s.Comando)) throw new ExcepcionUsuario("Escribe la orden que arranca el servidor (p. ej. npx).");
        if (s.Tipo != "stdio" && !Uri.TryCreate(s.Url, UriKind.Absolute, out _)) throw new ExcepcionUsuario("Escribe la dirección del servidor (https://…).");
        var lista = CatalogoMcp.Cargar(_repo);
        if (lista.Any(x => x.Id != s.Id && x.Nombre.Equals(s.Nombre, StringComparison.OrdinalIgnoreCase)))
            throw new ExcepcionUsuario($"Ya hay un servidor MCP que se llama «{s.Nombre}».");
        s.Valores = s.Valores.Where(v => !string.IsNullOrWhiteSpace(v.Nombre)).ToList();
        CatalogoMcp.GuardarSecretos(s, _boveda);
        var previo = lista.FindIndex(x => x.Id == s.Id);
        if (previo >= 0)
        {
            // Secretos que ya no están: fuera de la bóveda
            foreach (var v in lista[previo].Valores.Where(v => v.Secreto && !s.Valores.Any(n => n.Secreto && n.Nombre == v.Nombre)))
                _boveda.Borrar(CatalogoMcp.RefSecreto(s, v.Nombre));
            // Lo mismo con los secretos de los argumentos y la dirección cuya marca ya no está
            var marcas = CatalogoMcp.Marcas(s);
            foreach (var m in CatalogoMcp.Marcas(lista[previo]).Where(m => !marcas.Contains(m)))
                _boveda.Borrar(CatalogoMcp.RefMarca(s, m));
            lista[previo] = s;
        }
        else lista.Add(s);
        CatalogoMcp.Guardar(_repo, lista);
        Registro.Info($"MCP guardado: {s.Nombre} ({s.Tipo})");
        EnviarExtensiones();
    }

    public void ActivarMcp(string id, bool activo)
    {
        var lista = CatalogoMcp.Cargar(_repo);
        var s = lista.FirstOrDefault(x => x.Id == id) ?? throw new ExcepcionUsuario("Ese servidor MCP ya no existe.");
        s.Activo = activo;
        CatalogoMcp.Guardar(_repo, lista);
        EnviarExtensiones();
    }

    public void BorrarMcp(string id)
    {
        var lista = CatalogoMcp.Cargar(_repo);
        var s = lista.FirstOrDefault(x => x.Id == id);
        if (s is null) return;
        CatalogoMcp.BorrarSecretos(s, _boveda);
        lista.Remove(s);
        CatalogoMcp.Guardar(_repo, lista);
        EnviarExtensiones();
    }

    /// <summary>Servidores MCP que el usuario tiene en Claude Code, Codex y Gemini (los que no están ya en MultiAgente).</summary>
    public List<ServidorMcpDef> McpImportables()
    {
        var ya = CatalogoMcp.Cargar(_repo).Select(s => s.Nombre).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ImportarMcp.Buscar(_op.PerfilUsuario).Where(s => !ya.Contains(s.Nombre)).GroupBy(s => s.Nombre).Select(g => g.First()).ToList();
    }

    /// <summary>Trae los servidores elegidos (por nombre) de la configuración del usuario.</summary>
    public int ImportarMcpDe(IReadOnlyCollection<string> nombres)
    {
        var lista = CatalogoMcp.Cargar(_repo);
        var n = 0;
        foreach (var s in McpImportables().Where(s => nombres.Contains(s.Nombre)))
        {
            lista.Add(CatalogoMcp.GuardarSecretos(s, _boveda));
            n++;
        }
        CatalogoMcp.Guardar(_repo, lista);
        Registro.Info($"MCP importados: {n}");
        EnviarExtensiones();
        return n;
    }

    // ---------------------------------------------------------------- skills, órdenes, agentes

    /// <summary>Lo que el usuario tiene en su configuración de siempre y se puede traer (solo lectura).</summary>
    public List<ElementoCompartido> Importables(string clave)
    {
        var perfil = _op.PerfilUsuario;
        var origenes = clave switch
        {
            "skills" => new[] { Path.Combine(perfil, ".claude", "skills"), Path.Combine(perfil, ".codex", "skills"),
                                Path.Combine(perfil, ".gemini", "skills"), Path.Combine(perfil, ".agents", "skills") },
            "ordenes-claude" => [Path.Combine(perfil, ".claude", "commands")],
            "agentes-claude" => [Path.Combine(perfil, ".claude", "agents")],
            "estilos-claude" => [Path.Combine(perfil, ".claude", "output-styles")],
            "prompts-codex" => [Path.Combine(perfil, ".codex", "prompts")],
            "ordenes-gemini" => [Path.Combine(perfil, ".gemini", "commands")],
            _ => throw new ExcepcionUsuario("Tipo de extensión desconocido."),
        };
        var destino = CarpetaDe(clave);
        var ya = clave == "skills"
            ? Extensiones.ListarSkills(destino).Select(e => Path.GetFileName(e.Ruta)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : Extensiones.ListarFicheros(destino).Select(e => Path.GetFileName(e.Ruta)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var r = new List<ElementoCompartido>();
        foreach (var o in origenes.Where(Directory.Exists))
        {
            var lista = clave == "skills" ? Extensiones.ListarSkills(o)
                : Directory.GetFiles(o).Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
                           .Select(f => new ElementoCompartido(Path.GetFileNameWithoutExtension(f), Extensiones.Cabecera(File.ReadAllText(f)).GetValueOrDefault("description"), f)).ToList();
            r.AddRange(lista.Where(e => !ya.Contains(Path.GetFileName(e.Ruta)) && r.All(x => Path.GetFileName(x.Ruta) != Path.GetFileName(e.Ruta))));
        }
        return r;
    }

    /// <summary>Copia a la carpeta compartida las rutas elegidas (de la configuración del usuario o de donde sea).</summary>
    public int ImportarExtensiones(string clave, IEnumerable<string> rutas)
    {
        var destino = CarpetaDe(clave);
        var n = 0;
        foreach (var r in rutas)
        {
            if (clave == "skills" && Directory.Exists(r) && !File.Exists(Path.Combine(r, "SKILL.md")))
                throw new ExcepcionUsuario($"«{Path.GetFileName(r)}» no es una skill: falta su SKILL.md.");
            Extensiones.Copiar(r, destino);
            n++;
        }
        Registro.Info($"Importados {n} elementos en {clave}");
        EnviarExtensiones();
        return n;
    }

    public void NuevaSkill(string nombre, string descripcion)
    {
        Extensiones.NuevaSkill(_rutas, nombre, descripcion);
        EnviarExtensiones();
    }

    public void QuitarExtension(string ruta)
    {
        Extensiones.Quitar(_rutas, ruta);
        EnviarExtensiones();
    }

    public void ActivarPlugin(string id, bool activo)
    {
        Extensiones.ActivarPlugin(_rutas, id, activo);
        EnviarExtensiones();
    }

    // ---------------------------------------------------------------- instrucciones comunes

    public void GuardarInstrucciones(string texto)
    {
        Extensiones.GuardarInstrucciones(_rutas, texto);
        // Se aplican a las cuentas ya preparadas al momento (y a las demás al abrir su próxima sesión)
        foreach (var c in _repo.Cuentas())
        {
            var carpeta = _rutas.Resolver(c.Carpeta);
            if (Directory.Exists(carpeta)) Extensiones.Sincronizar(_rutas, c.Proveedor, carpeta);
        }
        Registro.Info("Instrucciones comunes guardadas");
        EnviarExtensiones();
    }

    /// <summary>Las instrucciones globales que el usuario ya tiene (CLAUDE.md, AGENTS.md, GEMINI.md), para copiarlas.</summary>
    public List<object> InstruccionesDelUsuario()
    {
        var p = _op.PerfilUsuario;
        var r = new List<object>();
        foreach (var (nombre, f) in new[] { ("Claude Code (~/.claude/CLAUDE.md)", Path.Combine(p, ".claude", "CLAUDE.md")),
                                             ("Codex (~/.codex/AGENTS.md)", Path.Combine(p, ".codex", "AGENTS.md")),
                                             ("Gemini (~/.gemini/GEMINI.md)", Path.Combine(p, ".gemini", "GEMINI.md")) })
            if (File.Exists(f)) r.Add(new { nombre, texto = Extensiones.AplicarBloque(File.ReadAllText(f), "") });
        return r;
    }
}
