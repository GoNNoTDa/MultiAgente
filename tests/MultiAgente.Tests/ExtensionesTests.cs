using System.Text.Json;
using MultiAgente.Datos;
using MultiAgente.Nucleo;
using MultiAgente.Nucleo.Acp;

namespace MultiAgente.Tests;

public class McpTests
{
    [Fact]
    public void Importa_los_mcp_de_claude_gemini_y_codex()
    {
        using var c = new CarpetaPrueba("mcp");
        c.Fichero(".claude.json", """
            { "oauthAccount": {}, "mcpServers": {
                "github": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-github"], "env": { "GITHUB_TOKEN": "ghp_secreto", "MODO": "ro" } },
                "docs": { "type": "http", "url": "https://mcp.ejemplo.com/mcp", "headers": { "Authorization": "Bearer abc" } } } }
            """);
        c.Fichero(@".gemini\settings.json", """
            { "selectedAuthType": "gemini-api-key", "mcpServers": {
                "viejo": { "url": "https://sse.ejemplo.com/sse" }, "nuevo": { "httpUrl": "https://http.ejemplo.com/mcp" } } }
            """);
        c.Fichero(@".codex\config.toml", """
            model = "gpt-6"
            [mcp_servers.playwright]
            command = "npx"
            args = ["@playwright/mcp@latest", "--headless"]
            env = { "DEBUG" = "pw" }

            [mcp_servers.base.env]
            DB_PASSWORD = "secreta"

            [mcp_servers.base]
            command = "uvx"
            args = ['mcp-base']

            [otra_cosa]
            command = "no"
            """);

        var todos = ImportarMcp.Buscar(c.Ruta).ToDictionary(s => s.Nombre);
        Assert.Equal(["base", "docs", "github", "nuevo", "playwright", "viejo"], todos.Keys.Order());
        var gh = todos["github"];
        Assert.Equal(("stdio", "npx"), (gh.Tipo, gh.Comando));
        Assert.Equal(["-y", "@modelcontextprotocol/server-github"], gh.Argumentos);
        Assert.True(gh.Valores.Single(v => v.Nombre == "GITHUB_TOKEN").Secreto);
        Assert.False(gh.Valores.Single(v => v.Nombre == "MODO").Secreto);
        Assert.Equal(("http", "https://mcp.ejemplo.com/mcp"), (todos["docs"].Tipo, todos["docs"].Url));
        Assert.True(todos["docs"].Valores.Single().Secreto);
        Assert.Equal("sse", todos["viejo"].Tipo);
        Assert.Equal("http", todos["nuevo"].Tipo);
        Assert.Equal(["@playwright/mcp@latest", "--headless"], todos["playwright"].Argumentos);
        Assert.Equal("pw", todos["playwright"].Valores.Single().Valor);
        Assert.Equal("uvx", todos["base"].Comando);
        Assert.True(todos["base"].Valores.Single(v => v.Nombre == "DB_PASSWORD").Secreto);
        Assert.Contains("Codex", todos["base"].Origen);
    }

    [Fact]
    public void Definicion_acp_con_ruta_absoluta_secretos_y_variables()
    {
        using var c = new CarpetaPrueba("mcp");
        var boveda = new Boveda(Path.Combine(c.Ruta, "boveda.bin"));
        var bin = Path.Combine(c.Ruta, "bin");
        c.Fichero(@"bin\npx.cmd", "@echo off");
        c.Fichero(@"bin\servidor.exe", "");
        Environment.SetEnvironmentVariable("MULTIAGENTE_PRUEBA_RUTA", @"D:\datos");

        var s = new ServidorMcpDef
        {
            Nombre = "github", Comando = "npx", Argumentos = ["-y", "srv", "${MULTIAGENTE_PRUEBA_RUTA}"],
            Valores = [new() { Nombre = "GITHUB_TOKEN", Valor = "ghp_123" }, new() { Nombre = "MODO", Valor = "ro" }],
        };
        CatalogoMcp.GuardarSecretos(s, boveda);
        Assert.Null(s.Valores[0].Valor); // el secreto ya no está en el servidor…
        Assert.True(s.Valores[0].Secreto);
        Assert.Equal("ghp_123", boveda.Obtener(CatalogoMcp.RefSecreto(s, "GITHUB_TOKEN"))); // …sino en la bóveda

        var d = CatalogoMcp.Definicion(s, boveda, bin);
        Assert.EndsWith("cmd.exe", d["command"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/d", "/c", Path.Combine(bin, "npx.cmd"), "-y", "srv", @"D:\datos"], d["args"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("ghp_123", d["env"]![0]!["value"]!.GetValue<string>());
        Assert.Null(d["type"]);

        var exe = CatalogoMcp.Definicion(new ServidorMcpDef { Nombre = "x", Comando = "servidor" }, boveda, bin);
        Assert.Equal(Path.Combine(bin, "servidor.exe"), exe["command"]!.GetValue<string>());

        var http = CatalogoMcp.Definicion(new ServidorMcpDef { Nombre = "h", Tipo = "http", Url = "https://x/mcp",
                                                              Valores = [new() { Nombre = "X-Equipo", Valor = "uno" }] }, boveda);
        Assert.Equal("http", http["type"]!.GetValue<string>());
        Assert.Equal("X-Equipo", http["headers"]![0]!["name"]!.GetValue<string>());

        Assert.Throws<ExcepcionUsuario>(() => CatalogoMcp.Definicion(new ServidorMcpDef { Nombre = "y", Comando = "no-existe-123" }, boveda, bin));
    }
}

public class ExtensionesTests
{
    [Fact]
    public void El_bloque_de_instrucciones_comunes_se_pone_y_se_quita_sin_tocar_lo_demas()
    {
        var doc = "# Mis normas\n\nUsa tabuladores.\n";
        var con = Extensiones.AplicarBloque(doc, "Responde en castellano.");
        Assert.StartsWith(Extensiones.InicioBloque, con);
        Assert.Contains("Responde en castellano.", con);
        Assert.Contains("Usa tabuladores.", con);
        Assert.Equal(con, Extensiones.AplicarBloque(con, "Responde en castellano.")); // no se duplica
        var cambiado = Extensiones.AplicarBloque(con, "Responde en inglés.");
        Assert.DoesNotContain("castellano", cambiado);
        Assert.Equal(doc, Extensiones.AplicarBloque(cambiado, ""));
    }

    [Fact]
    public void Las_instrucciones_de_un_proveedor_son_iguales_en_todas_sus_cuentas_y_gana_el_cambio_mas_reciente()
    {
        using var c = new CarpetaPrueba("ext");
        var rutas = new Rutas(c.Ruta);
        var a = Path.Combine(rutas.Cuentas, "a");
        var b = Path.Combine(rutas.Cuentas, "b");
        Extensiones.GuardarInstrucciones(rutas, "Comunes para todos.");
        Directory.CreateDirectory(a);
        File.WriteAllText(Path.Combine(a, "CLAUDE.md"), "Recuerda: el proyecto usa .NET 10.\n");
        Extensiones.Sincronizar(rutas, "Claude", a);
        Extensiones.Sincronizar(rutas, "Claude", b);
        var enB = File.ReadAllText(Path.Combine(b, "CLAUDE.md"));
        Assert.Contains("Comunes para todos.", enB);
        Assert.Contains(".NET 10", enB);
        Assert.Equal(File.ReadAllText(Path.Combine(a, "CLAUDE.md")), enB);

        // El agente de la cuenta B recuerda algo: pasa a las demás
        Thread.Sleep(1100);
        File.AppendAllText(Path.Combine(b, "CLAUDE.md"), "Recuerda: despliegue los viernes no.\n");
        Extensiones.Sincronizar(rutas, "Claude", b);
        Extensiones.Sincronizar(rutas, "Claude", a);
        Assert.Contains("viernes", File.ReadAllText(Path.Combine(a, "CLAUDE.md")));

        // Codex y Gemini reciben las comunes en su fichero
        var codex = Path.Combine(rutas.Cuentas, "codex");
        Extensiones.Sincronizar(rutas, "Codex", codex);
        Assert.Contains("Comunes para todos.", File.ReadAllText(Path.Combine(codex, "AGENTS.md")));
        var gemini = Path.Combine(rutas.Cuentas, "gemini");
        Extensiones.Sincronizar(rutas, "Gemini", gemini);
        Assert.Contains("Comunes para todos.", File.ReadAllText(Path.Combine(gemini, ".gemini", "GEMINI.md")));
    }

    [Fact]
    public void Una_sola_carpeta_de_skills_para_claude_codex_y_gemini()
    {
        using var c = new CarpetaPrueba("ext");
        var rutas = new Rutas(c.Ruta);
        foreach (var (prov, carpeta, sub) in new[] { ("Claude", "c1", "skills"), ("Codex", "x1", "skills"), ("Gemini", "g1", @".gemini\skills") })
        {
            var cuenta = Path.Combine(rutas.Cuentas, carpeta);
            Assert.True(CarpetasCompartidas.Preparar(rutas, prov, cuenta));
            Assert.Equal(Extensiones.Skills(rutas), CarpetasCompartidas.Destino(Path.Combine(cuenta, sub)));
        }
        Assert.Equal(Path.Combine(rutas.Comun, "Claude", "commands"), CarpetasCompartidas.Destino(Path.Combine(rutas.Cuentas, "c1", "commands")));
    }

    [Fact]
    public void Lista_skills_ordenes_y_plugins()
    {
        using var c = new CarpetaPrueba("ext");
        var rutas = new Rutas(c.Ruta);
        var skill = Extensiones.NuevaSkill(rutas, "Revisar código", "Revisa los cambios antes de un commit");
        var s = Assert.Single(Extensiones.ListarSkills(Extensiones.Skills(rutas)));
        Assert.Equal(("revisar-codigo", "Revisa los cambios antes de un commit"), (s.Nombre, s.Descripcion));
        Assert.Throws<ExcepcionUsuario>(() => Extensiones.NuevaSkill(rutas, "revisar código", "otra"));

        c.Fichero(@"datos\comun\Claude\commands\git\commit.md", "---\ndescription: Hace un commit\n---\nHaz commit");
        c.Fichero(@"datos\comun\Gemini\.gemini\commands\probar.toml", "description = \"Pasa las pruebas\"\nprompt = \"...\"");
        Assert.Equal(("git:commit", "Hace un commit"), Extensiones.ListarFicheros(Path.Combine(rutas.Comun, "Claude", "commands")).Select(e => (e.Nombre, e.Descripcion)).Single());
        Assert.Equal("Pasa las pruebas", Extensiones.ListarFicheros(Path.Combine(rutas.Comun, "Gemini", ".gemini", "commands")).Single().Descripcion);

        c.Fichero(@"datos\comun\Claude\plugins\installed_plugins.json", """
            { "version": 2, "plugins": { "revisor@ejemplo": [ { "scope": "user", "version": "1.2.0" } ], "otro@tienda": [ {} ] } }
            """);
        Extensiones.ActivarPlugin(rutas, "otro@tienda", false);
        var plugins = Extensiones.PluginsClaude(rutas);
        Assert.Equal([("otro", false), ("revisor", true)], plugins.Select(p => (p.Nombre, p.Activo)));
        Assert.Equal("1.2.0", plugins.Single(p => p.Nombre == "revisor").Version);

        Extensiones.Quitar(rutas, skill);
        Assert.Empty(Extensiones.ListarSkills(Extensiones.Skills(rutas)));
        Assert.Single(Directory.GetDirectories(Path.Combine(rutas.Datos, "papelera")));
        Assert.Throws<ExcepcionUsuario>(() => Extensiones.Quitar(rutas, c.Ruta));
    }
}

public class GestorExtensionesTests
{
    /// <summary>Lee el registro del agente falso sin bloquearlo (lo está escribiendo).</summary>
    private static string Leer(string f)
    {
        if (!File.Exists(f)) return "";
        using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public async Task Los_mcp_llegan_a_la_sesion_segun_lo_que_admite_el_agente_y_se_importan_de_tu_configuracion()
    {
        using var c = new CarpetaPrueba("gext");
        var rutas = new Rutas(Path.Combine(c.Ruta, "MultiAgente"));
        rutas.CrearCarpetas();
        Directory.CreateDirectory(Path.Combine(c.Ruta, "proyecto"));
        var perfil = Path.Combine(c.Ruta, "perfil");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(perfil).FullName, ".claude.json"),
            """{ "mcpServers": { "local": { "command": "dotnet", "args": ["--info"], "env": { "API_KEY": "k-123" } }, "remoto": { "type": "http", "url": "https://x/mcp" } } }""");
        Directory.CreateDirectory(Path.Combine(perfil, ".claude", "skills", "mi-skill"));
        File.WriteAllText(Path.Combine(perfil, ".claude", "skills", "mi-skill", "SKILL.md"), "---\nname: mi-skill\ndescription: prueba\n---\n");
        using var bd = BaseDatos.Abrir(rutas.BaseDatos);
        using var bucle = new BucleUnico();
        var registro = Path.Combine(c.Ruta, "falso.log");
        var mensajes = new List<string>();
        var gestor = await bucle.Ejecutar(() => new Gestor(rutas, new Repositorio(bd), m => { lock (mensajes) mensajes.Add(JsonSerializer.Serialize(m, Json.Opciones)); },
            bucle.Poner, "prueba", new OpcionesGestor
            {
                ComprobarRequisitos = () => [], OrdenAgente = (_, _) => new OrdenAgente("dotnet", [AgenteFalso.Dll()]),
                Autorizada = (_, _, _) => true, Temporizadores = false, PerfilUsuario = perfil,
                EntornoExtra = new Dictionary<string, string?> { ["FALSO_REGISTRO"] = registro },
            }));
        try
        {
            var importables = await bucle.Ejecutar(() => gestor.McpImportables());
            Assert.Equal(["local", "remoto"], importables.Select(s => s.Nombre).Order());
            Assert.Equal(2, await bucle.Ejecutar(() => gestor.ImportarMcpDe(["local", "remoto"])));
            Assert.Empty(await bucle.Ejecutar(() => gestor.McpImportables())); // ya están
            var guardado = new Repositorio(bd).LeerAjuste("mcp")!;
            Assert.DoesNotContain("k-123", guardado); // la clave, a la bóveda

            Assert.Equal(1, await bucle.Ejecutar(() => gestor.ImportarExtensiones("skills", gestor.Importables("skills").Select(e => e.Ruta))));
            await bucle.Ejecutar(() => { gestor.GuardarInstrucciones("Responde siempre en castellano."); return true; });

            await bucle.Ejecutar(() => { gestor.CrearCuenta("Claude", "Api", "Cuenta", "k", null, null, null); return true; });
            var cuenta = new Repositorio(bd).Cuentas().Single();
            var id = await bucle.Ejecutar(() => gestor.NuevaSesion(Path.Combine(c.Ruta, "proyecto"), "cuenta:" + cuenta.Id, "hola", []));
            var limite = DateTime.UtcNow.AddSeconds(20);
            while (!Leer(registro).Contains("prompt"))
            {
                Assert.True(DateTime.UtcNow < limite, "el agente no recibió el mensaje. Registro: " + Leer(registro)
                    + " · Mensajes: " + string.Join(" | ", mensajes.TakeLast(4).Select(x => x.Length > 300 ? x[..300] : x)));
                await Task.Delay(100);
            }
            // El agente falso no declara MCP por http: solo le llega el stdio
            Assert.Contains("mcp=1", Leer(registro));
            // La cuenta ve la skill compartida y las instrucciones comunes
            var carpeta = rutas.Resolver(cuenta.Carpeta);
            Assert.True(File.Exists(Path.Combine(carpeta, "skills", "mi-skill", "SKILL.md")));
            Assert.Contains("Responde siempre en castellano.", File.ReadAllText(Path.Combine(carpeta, "CLAUDE.md")));
        }
        finally
        {
            await bucle.Ejecutar(async () => await gestor.DisposeAsync());
        }
    }
}

