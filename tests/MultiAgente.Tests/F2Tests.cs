using MultiAgente.Datos;
using MultiAgente.Nucleo;

namespace MultiAgente.Tests;

public class BovedaTests
{
    [Fact]
    public void Guarda_lee_y_borra_sin_dejar_la_clave_en_claro()
    {
        using var c = new CarpetaPrueba("boveda");
        var f = Path.Combine(c.Ruta, "boveda.bin");
        var b = new Boveda(f);
        b.Guardar("cuenta1", "sk-secreto-de-prueba-123");
        b.Guardar("cuenta2", "otra");
        Assert.Equal("sk-secreto-de-prueba-123", new Boveda(f).Obtener("cuenta1"));
        Assert.DoesNotContain("sk-secreto", File.ReadAllText(f));
        Assert.True(File.Exists(Path.Combine(c.Ruta, "boveda.clave")));
        b.Borrar("cuenta1");
        Assert.Null(b.Obtener("cuenta1"));
        Assert.Equal("otra", b.Obtener("cuenta2"));
    }

    [Fact]
    public void Con_otra_clave_de_cifrado_avisa_en_lenguaje_llano()
    {
        using var c = new CarpetaPrueba("boveda");
        var f = Path.Combine(c.Ruta, "boveda.bin");
        new Boveda(f).Guardar("a", "b");
        File.WriteAllBytes(Path.Combine(c.Ruta, "boveda.clave"), new byte[32]);
        var ex = Assert.Throws<ExcepcionUsuario>(() => new Boveda(f).Obtener("a"));
        Assert.Contains("claves guardadas", ex.Message);
    }
}

public class RepositorioTests
{
    [Fact]
    public void Cuentas_proyectos_sesiones_y_tramos()
    {
        using var c = new CarpetaPrueba("repo");
        using var bd = BaseDatos.Abrir(Path.Combine(c.Ruta, "m.db"));
        var r = new Repositorio(bd);

        var cuenta = new Cuenta { Alias = "GLM", Proveedor = "Claude", Tipo = TipoCuenta.Api, Carpeta = @"datos\cuentas\glm",
                                  RefClave = "x", Endpoint = "https://api.z.ai/api/anthropic", Modelo = "glm-5" };
        r.GuardarCuenta(cuenta);
        var leida = Assert.Single(r.Cuentas());
        Assert.Equal(TipoCuenta.Api, leida.Tipo);
        Assert.Equal("https://api.z.ai/api/anthropic", leida.Endpoint);
        Assert.Equal("glm-5", leida.Modelo);

        var p = new Proyecto { Nombre = "Web", Ruta = @"C:\proyectos\web" };
        r.GuardarProyecto(p);
        Assert.Equal(p.Id, r.ProyectoDe(@"c:\proyectos\web\src")?.Id);
        Assert.Null(r.ProyectoDe(@"C:\proyectos\webnueva"));

        var s = new Sesion { ProyectoId = p.Id, Carpeta = p.Ruta, CuentaId = cuenta.Id, Titulo = "Arreglar el alta", ModoPermisos = "plan" };
        r.GuardarSesion(s);
        s.Archivada = true;
        r.GuardarSesion(s);
        Assert.Empty(r.Sesiones());
        Assert.Equal("plan", Assert.Single(r.Sesiones(archivadas: true)).ModoPermisos);

        r.GuardarTramo(new Tramo { SesionId = s.Id, Orden = 1, CuentaId = cuenta.Id, SesionNativa = "n1" });
        r.GuardarTramo(new Tramo { SesionId = s.Id, Orden = 2, CuentaId = cuenta.Id, SesionNativa = "n2" });
        Assert.Equal("n2", r.UltimoTramo(s.Id)?.SesionNativa);

        r.BorrarCuenta(cuenta.Id);
        Assert.Null(r.Sesion(s.Id)!.CuentaId); // ON DELETE SET NULL
    }

    [Fact]
    public void Una_base_del_esquema_1_se_pone_al_dia()
    {
        using var c = new CarpetaPrueba("repo");
        var f = Path.Combine(c.Ruta, "m.db");
        using (var bd = BaseDatos.Abrir(f)) Assert.Equal(BaseDatos.VersionEsquema, bd.Version);
        Assert.True(BaseDatos.VersionEsquema >= 2);
    }
}

public class ProveedoresTests
{
    [Fact]
    public void El_entorno_aisla_la_cuenta_y_no_hereda_claves_ni_sesiones_ajenas()
    {
        var e = Proveedores.Entorno(Proveedores.Claude, @"D:\ma\datos\cuentas\c1", null,
                                    ["PATH", "CLAUDE_CODE_SESSION_ID", "ANTHROPIC_BASE_URL", "OPENAI_API_KEY"]);
        Assert.Equal(@"D:\ma\datos\cuentas\c1", e["CLAUDE_CONFIG_DIR"]);
        Assert.True(e.ContainsKey("CLAUDE_CODE_SESSION_ID") && e["CLAUDE_CODE_SESSION_ID"] is null);
        Assert.True(e.ContainsKey("ANTHROPIC_BASE_URL") && e["ANTHROPIC_BASE_URL"] is null);
        Assert.True(e.ContainsKey("OPENAI_API_KEY") && e["OPENAI_API_KEY"] is null);
        Assert.False(e.ContainsKey("PATH"));
    }

    [Fact]
    public void Cuenta_de_api_y_api_compatible()
    {
        var api = Proveedores.Entorno(Proveedores.Codex, @"C:\c", "sk-x", []);
        Assert.Equal("sk-x", api["OPENAI_API_KEY"]);
        Assert.Equal(@"C:\c", api["CODEX_HOME"]);

        var glm = Proveedores.Entorno(Proveedores.Claude, @"C:\g", "clave-glm", [], "https://api.z.ai/api/anthropic", "glm-5");
        Assert.Equal("https://api.z.ai/api/anthropic", glm["ANTHROPIC_BASE_URL"]);
        Assert.Equal("clave-glm", glm["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("", glm["ANTHROPIC_API_KEY"]);
        Assert.Equal("glm-5", glm["ANTHROPIC_MODEL"]);

        var kimi = Proveedores.Entorno(Proveedores.Claude, @"C:\k", "clave", [], "https://api.moonshot.ai/anthropic", null,
            new Dictionary<string, string> { ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = "kimi-k2.7-code", ["API_TIMEOUT_MS"] = "1000" });
        Assert.Equal("kimi-k2.7-code", kimi["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
        Assert.Equal("1000", kimi["API_TIMEOUT_MS"]);
        Assert.False(kimi.ContainsKey("ANTHROPIC_MODEL"));

        var codex = Proveedores.Entorno(Proveedores.Codex, @"C:\x", "sk-or", [], "https://openrouter.ai/api/v1");
        Assert.Equal("sk-or", codex["MULTIAGENTE_API_KEY"]);
    }

    [Fact]
    public void Autorizada_segun_el_fichero_de_login_o_la_boveda()
    {
        using var c = new CarpetaPrueba("prov");
        var rutas = new Rutas(c.Ruta);
        var boveda = new Boveda(rutas.Boveda);
        var login = new Cuenta { Proveedor = "Codex", Tipo = TipoCuenta.Login, Carpeta = @"datos\cuentas\codex1" };
        Assert.False(Proveedores.Autorizada(login, rutas, boveda));
        c.Fichero(@"datos\cuentas\codex1\auth.json", "{}");
        Assert.True(Proveedores.Autorizada(login, rutas, boveda));

        var api = new Cuenta { Proveedor = "Gemini", Tipo = TipoCuenta.Api, RefClave = "g1" };
        Assert.False(Proveedores.Autorizada(api, rutas, boveda));
        boveda.Guardar("g1", "AIza-algo");
        Assert.True(Proveedores.Autorizada(api, rutas, boveda));
    }

    [Fact]
    public void La_orden_de_un_lanzador_de_npm_es_node_con_su_script()
    {
        using var c = new CarpetaPrueba("orden");
        c.Fichero("node.exe");
        c.Fichero("codex-acp.cmd", "@echo off");
        var js = c.Fichero(@"node_modules\@agentclientprotocol\codex-acp\dist\index.js", "");
        c.Fichero(@"node_modules\@agentclientprotocol\codex-acp\package.json", """{ "version": "2.2.2", "bin": { "codex-acp": "dist/index.js" } }""");
        c.Fichero("claude.cmd", "@echo off");
        var exe = c.Fichero(@"node_modules\@anthropic-ai\claude-code\bin\claude.exe", "");
        c.Fichero(@"node_modules\@anthropic-ai\claude-code\package.json", """{ "version": "2.1.0", "bin": { "claude": "bin/claude.exe" } }""");

        var req = Requisitos.Comprobar(c.Ruta);
        var o = Requisitos.Orden("codex-acp", req)!;
        Assert.Equal(Path.Combine(c.Ruta, "node.exe"), o.Ejecutable);
        Assert.Equal([js], o.Argumentos);

        var claude = Requisitos.Orden("claude", req)!;
        Assert.Equal(exe, claude.Ejecutable);
        Assert.Empty(claude.Argumentos);

        Assert.Null(Requisitos.Orden("gemini", req));
    }
}

public class HiloTests
{
    [Fact]
    public void Una_entrada_actualizada_se_lee_en_su_sitio_con_lo_ultimo()
    {
        using var c = new CarpetaPrueba("hilo");
        var h = new Hilo(c.Ruta);
        var herramienta = new Entrada { Tipo = "herramienta", Titulo = "Leer", Estado = "pending" };
        h.Guardar(new Entrada { Tipo = "usuario", Texto = "uno" });
        h.Guardar(herramienta);
        h.Guardar(new Entrada { Tipo = "agente", Texto = "dos" });
        herramienta.Estado = "completed";
        h.Guardar(herramienta);

        var r = new Hilo(c.Ruta).Entradas;
        Assert.Equal(["usuario", "herramienta", "agente"], r.Select(e => e.Tipo));
        Assert.Equal("completed", r[1].Estado);
    }

    [Fact]
    public void El_traspaso_lleva_lo_ultimo_de_la_conversacion()
    {
        var t = Traspaso.Componer([
            new Entrada { Tipo = "usuario", Texto = "Arregla el login" },
            new Entrada { Tipo = "herramienta", Titulo = "Editar Login.cs", Estado = "completed" },
            new Entrada { Tipo = "agente", Texto = "Arreglado" },
        ]);
        Assert.Contains("[Usuario] Arregla el login", t);
        Assert.Contains("[Herramienta] Editar Login.cs (completed)", t);
        Assert.Contains("[Agente] Arreglado", t);
        Assert.True(t.IndexOf("Arregla", StringComparison.Ordinal) < t.IndexOf("Arreglado", StringComparison.Ordinal));
    }
}
