using Microsoft.Data.Sqlite;
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

    [Fact]
    public void Una_base_antigua_con_datos_se_pone_al_dia_sin_perder_nada()
    {
        using var c = new CarpetaPrueba("esq1");
        var f = Path.Combine(c.Ruta, "multiagente.db");
        // Base tal como la dejaba la primera versión: solo el paso 1 del esquema (se lee el de verdad, que nunca cambia)
        var pasos = (string[])typeof(BaseDatos).Assembly.GetType("MultiAgente.Datos.Esquema")!.GetField("Pasos")!.GetValue(null)!;
        using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f, Pooling = false }.ToString()))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = ON;\n" + pasos[0] + """

                PRAGMA user_version = 1;
                INSERT INTO Ajuste VALUES ('idioma', 'es');
                INSERT INTO Proyecto VALUES ('p1', 'web', 'C:\web', NULL, 0, 0), ('p2', 'api', 'C:\api', NULL, 1, 1);
                INSERT INTO Cuenta (Id, Alias, Proveedor, Tipo, Carpeta, RefClave, Umbrales, Presupuesto, Orden, Creada) VALUES
                    ('c1', 'Personal', 'Claude', 'Login', 'datos\cuentas\personal', NULL, '{"5h":80}', NULL, 0, '2026-01-05T10:00:00.0000000Z'),
                    ('c2', 'Trabajo', 'Codex', 'Api', 'datos\cuentas\trabajo', 'cuenta:c2', NULL, 25.5, 1, '2026-01-06T11:30:00.0000000Z');
                INSERT INTO Equipo VALUES ('e1', 'Todas', 'Rotacion', 0, 30, 1);
                INSERT INTO EquipoCuenta VALUES ('e1', 'c2', 0), ('e1', 'c1', 1);
                INSERT INTO Sesion (Id, ProyectoId, Titulo, Carpeta, EquipoId, CuentaFija, Modelo, Esfuerzo, ModoPermisos, Estado, Creada, UltimoUso, Archivada) VALUES
                    ('s1', 'p1', 'Arreglar el login', 'C:\web', 'e1', NULL, 'opus', 'alto', 'plan', 'Cerrada', '2026-01-07T09:00:00.0000000Z', '2026-01-08T09:00:00.0000000Z', 0),
                    ('s2', 'p2', 'Vieja', 'C:\api', NULL, 'c1', NULL, NULL, NULL, 'Cerrada', '2026-01-02T09:00:00.0000000Z', '2026-01-03T09:00:00.0000000Z', 1);
                INSERT INTO Tramo VALUES
                    ('s1', 1, 'c1', 'nativa-1', '2026-01-07T09:00:00.0000000Z', '2026-01-07T12:00:00.0000000Z', NULL),
                    ('s1', 2, 'c2', 'nativa-2', '2026-01-07T12:00:00.0000000Z', NULL, 'límite');
                INSERT INTO Consumo VALUES ('c1', '2026-01-07T12:00:00.0000000Z', '5h', 97.5, '2026-01-07T15:00:00.0000000Z', NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        using var bd = BaseDatos.Abrir(f);
        var repo = new Repositorio(bd);
        Assert.Equal(BaseDatos.VersionEsquema, bd.Version);
        Assert.Contains("UsoTurno", bd.Tablas());
        Assert.Equal("es", repo.LeerAjuste("idioma"));

        // Cuentas: los datos de antes siguen; las columnas nuevas, vacías (sin endpoint, modelo ni umbrales propios)
        var cuentas = repo.Cuentas();
        Assert.Equal(["c1", "c2"], cuentas.Select(x => x.Id));
        var (c1, c2) = (cuentas[0], cuentas[1]);
        Assert.Equal(("Personal", "Claude", TipoCuenta.Login, @"datos\cuentas\personal", (string?)null), (c1.Alias, c1.Proveedor, c1.Tipo, c1.Carpeta, c1.RefClave));
        Assert.Equal(("Trabajo", "Codex", TipoCuenta.Api, "cuenta:c2", (double?)25.5), (c2.Alias, c2.Proveedor, c2.Tipo, c2.RefClave, c2.Presupuesto));
        Assert.Equal(new DateTime(2026, 1, 6, 11, 30, 0, DateTimeKind.Utc), c2.Creada.ToUniversalTime());
        Assert.All(cuentas, x => Assert.True(x.Endpoint is null && x.Modelo is null && x.Catalogo is null && x.Umbral5h is null && x.UmbralSemana is null));

        // Proyectos (el oculto sigue oculto) y sesiones con su proyecto, equipo y cuenta fija (el paso 4 rehace Proyecto)
        Assert.Equal(("p1", "web", @"C:\web"), repo.Proyectos().Select(p => (p.Id, p.Nombre, p.Ruta)).Single());
        Assert.Equal(1L, Escalar(bd, "SELECT Oculto FROM Proyecto WHERE Id = 'p2'"));
        var s1 = repo.Sesion("s1")!;
        Assert.Equal(("p1", "Arreglar el login", "e1", "opus", "alto", "plan", false), (s1.ProyectoId, s1.Titulo, s1.EquipoId, s1.Modelo, s1.Esfuerzo, s1.ModoPermisos, s1.Archivada));
        var s2 = Assert.Single(repo.Sesiones(archivadas: true));
        Assert.Equal(("s2", "p2", "c1"), (s2.Id, s2.ProyectoId, s2.CuentaId));
        var tramo = repo.UltimoTramo("s1")!;
        Assert.Equal((2, "c2", "nativa-2", "límite", (DateTime?)null), (tramo.Orden, tramo.CuentaId, tramo.SesionNativa, tramo.Motivo, tramo.Hasta));
        Assert.Equal(2L, Escalar(bd, "SELECT COUNT(*) FROM Tramo"));
        Assert.Equal(1L, Escalar(bd, "SELECT COUNT(*) FROM Consumo"));

        var e = Assert.Single(repo.Equipos());
        Assert.Equal(("Todas", ReglaReparto.Rotacion, false, 30, true), (e.Nombre, e.Regla, e.PermiteOtroProveedor, e.CuentaAtras, e.Predeterminado));
        Assert.Equal(["c2", "c1"], e.Miembros);

        // Sin claves ajenas rotas, y ya se puede repetir carpeta en otro proyecto (lo que trae el esquema 4)
        Assert.Null(Escalar(bd, "PRAGMA foreign_key_check"));
        repo.GuardarProyecto(new Proyecto { Id = "p3", Nombre = "web (ideas)", Ruta = @"C:\web" });
        Assert.Equal(2, repo.Proyectos().Count);
        // Y las tablas nuevas funcionan
        repo.GuardarUso(new UsoTurno { SesionId = "s1", CuentaId = "c2", Momento = DateTime.UtcNow, Entrada = 10, Salida = 5, Total = 15 });
        Assert.Single(repo.Usos(DateTime.UtcNow.AddMinutes(-1)));
    }

    private static object? Escalar(BaseDatos bd, string sql)
    {
        using var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = bd.Fichero, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
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
