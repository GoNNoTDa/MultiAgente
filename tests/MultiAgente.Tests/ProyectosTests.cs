using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MultiAgente.Datos;
using MultiAgente.Nucleo;
using MultiAgente.Nucleo.Acp;

namespace MultiAgente.Tests;

public class ProyectosTests
{
    [Fact]
    public void El_esquema_4_conserva_el_proyecto_de_las_sesiones_y_deja_repetir_carpeta()
    {
        using var c = new CarpetaPrueba("esq4");
        var f = Path.Combine(c.Ruta, "multiagente.db");
        using (var bd = BaseDatos.Abrir(f))
        {
            var repo = new Repositorio(bd);
            repo.GuardarProyecto(new Proyecto { Id = "p1", Nombre = "web", Ruta = @"C:\web" });
            repo.GuardarSesion(new Sesion { Id = "s1", ProyectoId = "p1", Carpeta = @"C:\web" });
        }
        // Como si la base fuera de antes del esquema 4: se vuelve a aplicar al abrir
        using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f, Pooling = false }.ToString()))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 3;";
            cmd.ExecuteNonQuery();
        }
        using (var bd = BaseDatos.Abrir(f))
        {
            var repo = new Repositorio(bd);
            Assert.Equal(BaseDatos.VersionEsquema, bd.Version);
            Assert.Equal("p1", repo.Sesion("s1")!.ProyectoId);
            repo.GuardarProyecto(new Proyecto { Id = "p2", Nombre = "ideas", Ruta = @"C:\web" });
            Assert.Equal(2, repo.Proyectos().Count);

            repo.BorrarProyecto("p1");
            Assert.Null(repo.Sesion("s1")!.ProyectoId);
            Assert.Equal("ideas", Assert.Single(repo.Proyectos()).Nombre);
        }
    }

    [Fact]
    public async Task Proyectos_sesiones_movidas_renombradas_y_proyecto_quitado()
    {
        using var c = new CarpetaPrueba("proy");
        var rutas = new Rutas(Path.Combine(c.Ruta, "MultiAgente"));
        rutas.CrearCarpetas();
        var carpeta = Path.Combine(c.Ruta, "web");
        Directory.CreateDirectory(carpeta);
        using var bd = BaseDatos.Abrir(rutas.BaseDatos);
        using var bucle = new BucleUnico();
        var gestor = await bucle.Ejecutar(() => new Gestor(rutas, new Repositorio(bd), _ => { }, bucle.Poner, "prueba",
            new OpcionesGestor
            {
                ComprobarRequisitos = () => [], OrdenAgente = (_, _) => new OrdenAgente("dotnet", [AgenteFalso.Dll()]),
                Autorizada = (_, _, _) => true, Temporizadores = false, GenerarResumen = _ => Task.FromResult<string?>(null),
            }));
        try
        {
            var repo = new Repositorio(bd);
            var (web, ideas) = await bucle.Ejecutar(() =>
            {
                gestor.CrearCuenta("Claude", "Api", "Prueba", "k1", null, null, null);
                return (gestor.GuardarProyecto(null, "Web", carpeta), gestor.GuardarProyecto(null, "Ideas", carpeta));
            });
            Assert.Throws<ExcepcionUsuario>(() => { gestor.GuardarProyecto(null, "  ", carpeta); });
            Assert.Throws<ExcepcionUsuario>(() => { gestor.GuardarProyecto(null, "X", Path.Combine(c.Ruta, "no-existe")); });

            // Dos proyectos en la misma carpeta: la sesión va al elegido (sin carpeta: la del proyecto)
            var cuenta = repo.Cuentas().Single();
            var id = await bucle.Ejecutar(() => gestor.NuevaSesion("", "cuenta:" + cuenta.Id, "hola", [], ideas.Id));
            Assert.Equal(ideas.Id, repo.Sesion(id)!.ProyectoId);
            Assert.Equal(carpeta, repo.Sesion(id)!.Carpeta);

            // Con el agente vivo: el cambio no se pierde cuando el agente vuelve a guardar la sesión en el siguiente turno
            await bucle.Ejecutar(() => { gestor.MoverSesion(id, web.Id); gestor.RenombrarSesion(id, "  Alta de clientes "); return true; });
            await bucle.Ejecutar(() => gestor.Enviar(id, "otra cosa", []));
            var limite = DateTime.UtcNow.AddSeconds(25);
            while (await bucle.Ejecutar(() => gestor.Viva(id)!.Hilo.Entradas.Count(e => e.Tipo == "agente") < 2))
            {
                Assert.True(DateTime.UtcNow < limite, "No respondió al segundo mensaje");
                await Task.Delay(100);
            }
            Assert.Equal(web.Id, repo.Sesion(id)!.ProyectoId);
            Assert.Equal("Alta de clientes", repo.Sesion(id)!.Titulo);

            await bucle.Ejecutar(() => { gestor.BorrarProyecto(web.Id); return true; });
            Assert.Null(repo.Sesion(id)!.ProyectoId);
            Assert.Equal("Ideas", Assert.Single(repo.Proyectos()).Nombre);
        }
        finally
        {
            await bucle.Ejecutar(async () => await gestor.DisposeAsync());
        }
    }
}

public class CambiosGitTests
{
    private static void Git(string carpeta, params string[] argumentos)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = carpeta, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-c", "user.name=Prueba", "-c", "user.email=prueba@ejemplo.invalid", "-c", "core.autocrlf=false", .. argumentos])
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', argumentos)}: {error}");
    }

    [Fact]
    public async Task Ve_ficheros_cambiados_nuevos_y_borrados_con_sus_diferencias()
    {
        using var c = new CarpetaPrueba("git");
        var repo = Path.Combine(c.Ruta, "repo");
        var sub = Path.Combine(repo, "src");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "Validación.cs"), "uno\ndos\ntres\n");
        File.WriteAllText(Path.Combine(sub, "viejo.txt"), "a\nb\n");
        File.WriteAllText(Path.Combine(repo, "fuera.txt"), "x\n");
        Git(repo, "init", "-q");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "inicio");

        File.WriteAllText(Path.Combine(sub, "Validación.cs"), "uno\nDOS\ntres\ncuatro\n");
        File.Delete(Path.Combine(sub, "viejo.txt"));
        File.WriteAllText(Path.Combine(sub, "nuevo.md"), "# hola\n\nmundo\n");
        File.WriteAllBytes(Path.Combine(sub, "imagen.bin"), [1, 0, 2, 0]);
        File.WriteAllText(Path.Combine(repo, "fuera.txt"), "y\n"); // fuera de la carpeta de la sesión: no sale

        var cambios = await CambiosGit.Leer(sub);
        Assert.NotNull(cambios);
        Assert.Equal(Path.GetFullPath(repo).TrimEnd('\\'), cambios!.Raiz, ignoreCase: true);
        Assert.Equal(["imagen.bin", "nuevo.md", "Validación.cs", "viejo.txt"], cambios.Ficheros.Select(f => f.Ruta));
        var v = cambios.Ficheros.Single(f => f.Ruta == "Validación.cs");
        Assert.Equal(("M", 2, 1), (v.Estado, v.Mas, v.Menos));
        Assert.Equal(("D", 0, 2), cambios.Ficheros.Single(f => f.Ruta == "viejo.txt") is var d ? (d.Estado, d.Mas, d.Menos) : default);
        Assert.Equal(("?", 3), cambios.Ficheros.Single(f => f.Ruta == "nuevo.md") is var n ? (n.Estado, n.Mas) : default);
        Assert.True(cambios.Ficheros.Single(f => f.Ruta == "imagen.bin").Binario);

        var dif = await CambiosGit.Diferencias(sub, "Validación.cs");
        Assert.StartsWith("@@ -1,3 +1,4 @@", dif.Texto);
        Assert.Contains("-dos\n+DOS\n", dif.Texto);
        Assert.Contains("+cuatro", dif.Texto);
        var nuevo = await CambiosGit.Diferencias(sub, "nuevo.md");
        Assert.Equal("@@ -0,0 +1,3 @@\n+# hola\n+\n+mundo\n", nuevo.Texto);
        Assert.True((await CambiosGit.Diferencias(sub, "imagen.bin")).Binario);
        await Assert.ThrowsAsync<ExcepcionUsuario>(() => CambiosGit.Diferencias(sub, @"..\fuera.txt"));

        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "todo");
        Assert.Empty((await CambiosGit.Leer(sub))!.Ficheros);
    }

    [Fact]
    public async Task Sin_repositorio_no_hay_cambios_de_git_y_sin_commits_todo_es_nuevo()
    {
        using var c = new CarpetaPrueba("sin-git");
        Assert.Null(await CambiosGit.Leer(c.Ruta));

        var repo = Path.Combine(c.Ruta, "recien");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "1\n2\n");
        var cambios = await CambiosGit.Leer(repo);
        var a = Assert.Single(cambios!.Ficheros);
        Assert.Equal(("a.txt", "?", 2), (a.Ruta, a.Estado, a.Mas));
    }
}
