using System.Text.Json;
using System.Text.Json.Nodes;
using MultiAgente.Datos;
using MultiAgente.Nucleo;
using MultiAgente.Nucleo.Acp;

namespace MultiAgente.Tests;

public class HistorialesTests
{
    private static string Linea(object o) => JsonSerializer.Serialize(o);

    /// <summary>Un perfil de usuario de mentira con una conversación de cada agente.</summary>
    private static (string Perfil, string Proyecto) Perfil(CarpetaPrueba c)
    {
        var perfil = Path.Combine(c.Ruta, "perfil");
        var proyecto = Path.Combine(c.Ruta, "web");
        Directory.CreateDirectory(proyecto);

        var claude = Path.Combine(perfil, ".claude", "projects", "C--web");
        Directory.CreateDirectory(Path.Combine(claude, "subagentes"));
        File.WriteAllLines(Path.Combine(claude, "aaaa-1111.jsonl"),
        [
            Linea(new { type = "user", cwd = proyecto, sessionId = "aaaa-1111", timestamp = "2026-10-01T10:00:00Z", message = new { role = "user", content = "Revisa el alta de clientes\ncon calma" } }),
            Linea(new { type = "user", isMeta = true, cwd = proyecto, message = new { role = "user", content = "Base directory for this skill: ..." } }),
            Linea(new { type = "assistant", timestamp = "2026-10-01T10:00:05Z", message = new { role = "assistant", content = new object[]
            {
                new { type = "thinking", thinking = "Miro el código" },
                new { type = "text", text = "Voy a cambiar la validación." },
                new { type = "tool_use", id = "t1", name = "Edit", input = new { file_path = @"C:\web\Alta.cs", old_string = "a\n", new_string = "b\n" } },
            } } }),
            Linea(new { type = "user", cwd = proyecto, message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t1", content = "ok" } } } }),
            Linea(new { type = "user", cwd = proyecto, message = new { role = "user", content = "<command-name>/compact</command-name>\n<command-args>breve</command-args>" } }),
            Linea(new { type = "user", cwd = proyecto, message = new { role = "user", content = "<local-command-stdout>Compacted</local-command-stdout>" } }),
            Linea(new { type = "user", isSidechain = true, cwd = proyecto, message = new { role = "user", content = "de un subagente" } }),
            Linea(new { type = "custom-title", customTitle = "Alta de clientes", sessionId = "aaaa-1111" }),
            "{\"type\":\"user\",\"mensaje a me", // línea a medias
        ]);
        File.WriteAllText(Path.Combine(claude, "subagentes", "agent-1.jsonl"), "{}"); // no es una sesión

        var codex = Path.Combine(perfil, ".codex", "sessions", "2026", "10", "02");
        Directory.CreateDirectory(codex);
        File.WriteAllLines(Path.Combine(codex, "rollout-2026-10-02T09-00-00-cccc-2222.jsonl"),
        [
            Linea(new { type = "session_meta", timestamp = "2026-10-02T09:00:00Z", payload = new { id = "cccc-2222", cwd = proyecto } }),
            Linea(new { type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "<environment_context>cwd</environment_context>" } } } }),
            Linea(new { type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "Haz el informe en PDF" } } } }),
            Linea(new { type = "response_item", payload = new { type = "function_call", name = "shell", call_id = "f1", arguments = "{\"command\":[\"dotnet\",\"build\"]}" } }),
            Linea(new { type = "response_item", payload = new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "[external_agent_tool_call: Read]\nfile: C:\\web\\a.cs\n[/external_agent_tool_call]" } } } }),
            Linea(new { type = "response_item", payload = new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "[external_agent_tool_result]\n1 hola" } } } }),
            Linea(new { type = "response_item", payload = new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "Hecho." } } } }),
        ]);

        var gemini = Path.Combine(perfil, ".gemini", "tmp", "abc");
        Directory.CreateDirectory(Path.Combine(gemini, "chats"));
        File.WriteAllText(Path.Combine(gemini, ".project_root"), Path.Combine(c.Ruta, "no-existe"));
        File.WriteAllText(Path.Combine(gemini, "chats", "session-1.json"), Linea(new
        {
            sessionId = "gggg-3333", lastUpdated = "2026-10-03T08:00:00Z",
            messages = new object[]
            {
                new { type = "user", timestamp = "2026-10-03T07:59:00Z", content = "¿Qué hace este script?" },
                new { type = "gemini", timestamp = "2026-10-03T08:00:00Z", content = "Copia ficheros.", thoughts = new[] { new { subject = "Leer", description = "Abro el script" } },
                      toolCalls = new[] { new { name = "read_file", displayName = "ReadFile", status = "success" } } },
            },
        }));
        return (perfil, proyecto);
    }

    [Fact]
    public void Encuentra_y_lee_las_conversaciones_de_los_tres_agentes()
    {
        using var c = new CarpetaPrueba("hist");
        var (perfil, proyecto) = Perfil(c);
        var lista = Historiales.Buscar(perfil);
        Assert.Equal(["Claude", "Codex", "Gemini"], lista.Select(h => h.Origen).Order());
        Assert.Equal("Gemini", lista[^1].Origen); // de la más reciente a la más antigua (Gemini guarda su fecha: 3 de octubre)

        var cl = lista.Single(h => h.Origen == "Claude");
        Assert.Equal(("aaaa-1111", "Alta de clientes", proyecto, 2), (cl.Id, cl.Titulo, cl.Carpeta, cl.Mensajes));
        var e = Historiales.Leer(cl);
        Assert.Equal(["usuario", "pensamiento", "agente", "herramienta", "usuario"], e.Select(x => x.Tipo));
        Assert.Equal("Revisa el alta de clientes\ncon calma", e[0].Texto);
        Assert.Equal("/compact breve", e[4].Texto);
        var edicion = e[3];
        Assert.Equal(("edit", "completed"), (edicion.Clase, edicion.Estado));
        Assert.Equal("diff", edicion.Contenido![0]!["type"]!.GetValue<string>());
        Assert.Equal("b\n", edicion.Contenido[0]!["newText"]!.GetValue<string>());

        var cx = lista.Single(h => h.Origen == "Codex");
        Assert.Equal(("cccc-2222", "Haz el informe en PDF", 1), (cx.Id, cx.Titulo, cx.Mensajes));
        var ex = Historiales.Leer(cx);
        Assert.Equal(["usuario", "herramienta", "herramienta", "agente"], ex.Select(x => x.Tipo));
        Assert.Equal("shell dotnet build", ex[1].Titulo);
        Assert.Equal(@"Read C:\web\a.cs", ex[2].Titulo);

        var ge = lista.Single(h => h.Origen == "Gemini");
        Assert.Equal(("gggg-3333", "¿Qué hace este script?"), (ge.Id, ge.Titulo));
        Assert.Equal(["usuario", "pensamiento", "herramienta", "agente"], Historiales.Leer(ge).Select(x => x.Tipo));
    }

    [Fact]
    public async Task Importa_las_elegidas_como_sesiones_que_el_mismo_agente_puede_retomar()
    {
        using var c = new CarpetaPrueba("imp");
        var (perfil, proyecto) = Perfil(c);
        var rutas = new Rutas(Path.Combine(c.Ruta, "MultiAgente"));
        rutas.CrearCarpetas();
        using var bd = BaseDatos.Abrir(rutas.BaseDatos);
        using var bucle = new BucleUnico();
        var gestor = await bucle.Ejecutar(() => new Gestor(rutas, new Repositorio(bd), _ => { }, bucle.Poner, "prueba",
            new OpcionesGestor
            {
                ComprobarRequisitos = () => [], OrdenAgente = (_, _) => new OrdenAgente("dotnet", [AgenteFalso.Dll()]),
                Autorizada = (_, _, _) => true, Temporizadores = false, PerfilUsuario = perfil, CompartirCarpetas = false,
            }));
        try
        {
            var repo = new Repositorio(bd);
            await Assert.ThrowsAsync<ExcepcionUsuario>(() => bucle.Ejecutar(() => gestor.ImportarHistoriales(["x"])));
            await bucle.Ejecutar(() =>
            {
                gestor.CrearCuenta("Codex", "Api", "Codex API", "k", null, null, null);
                gestor.CrearCuenta("Claude", "Api", "Claude API", "k", null, null, null);
                return true;
            });
            var lista = JsonSerializer.SerializeToElement(await bucle.Ejecutar(gestor.HistorialesPrevios), Json.Opciones).GetProperty("lista");
            Assert.Equal(3, lista.GetArrayLength());
            Assert.False(lista.EnumerateArray().Single(x => x.GetProperty("origen").GetString() == "Gemini").GetProperty("existe").GetBoolean());

            var claves = lista.EnumerateArray().Select(x => x.GetProperty("clave").GetString()!).Append(@"C:\Windows\win.ini").ToList();
            var (importadas, fallos) = await bucle.Ejecutar(() => gestor.ImportarHistoriales(claves));
            Assert.Equal(2, importadas);
            Assert.Contains(fallos, f => f.Contains("su carpeta ya no existe"));

            var sesiones = repo.Sesiones();
            var sc = sesiones.Single(s => s.Titulo == "Alta de clientes");
            Assert.Equal(proyecto, sc.Carpeta);
            Assert.Equal("Claude API", repo.Cuentas().Single(x => x.Id == sc.CuentaId).Alias); // la del mismo proveedor
            Assert.Equal("aaaa-1111", repo.UltimoTramo(sc.Id)!.SesionNativa);
            Assert.True(File.Exists(Path.Combine(rutas.Comun, "Claude", "projects", "C--web", "aaaa-1111.jsonl")));
            Assert.True(File.Exists(Path.Combine(rutas.Comun, "Codex", "sessions", "2026", "10", "02", "rollout-2026-10-02T09-00-00-cccc-2222.jsonl")));
            var hilo = new Hilo(Path.Combine(rutas.Sesiones, sc.Id));
            Assert.Equal("aviso", hilo.Entradas[0].Tipo);
            Assert.Equal(6, hilo.Cuantas);
            Assert.Equal(sesiones[0].ProyectoId, sesiones[1].ProyectoId); // misma carpeta, mismo proyecto

            // Otra vez: ya están
            var (otra, fallos2) = await bucle.Ejecutar(() => gestor.ImportarHistoriales(claves));
            Assert.Equal(0, otra);
            Assert.Equal(2, fallos2.Count(f => f.Contains("ya estaba importada")));
        }
        finally
        {
            await bucle.Ejecutar(async () => await gestor.DisposeAsync());
        }
    }
}
