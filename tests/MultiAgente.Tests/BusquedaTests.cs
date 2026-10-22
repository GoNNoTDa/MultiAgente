using MultiAgente.Datos;
using MultiAgente.Nucleo;
using MultiAgente.Nucleo.Acp;

namespace MultiAgente.Tests;

/// <summary>Buscar en las conversaciones (también archivadas) y archivar todas las sesiones de un proyecto.</summary>
public class BusquedaTests
{
    private sealed class Montaje : IAsyncDisposable
    {
        public required CarpetaPrueba Carpeta { get; init; }
        public required Rutas Rutas { get; init; }
        public required BaseDatos Bd { get; init; }
        public required BucleUnico Bucle { get; init; }
        public required Gestor Gestor { get; init; }
        public Repositorio Repo => new(Bd);
        public string Trabajo => Path.Combine(Carpeta.Ruta, "web");

        /// <summary>Sesión con su conversación ya escrita (sin arrancar ningún agente).</summary>
        public Sesion Sesion(string titulo, string? proyectoId, bool archivada, DateTime uso, params (string Tipo, string Texto)[] entradas)
        {
            var s = new Sesion { Titulo = titulo, ProyectoId = proyectoId, Carpeta = Trabajo, Archivada = archivada, UltimoUso = uso };
            Repo.GuardarSesion(s);
            Hilo.Crear(Path.Combine(Rutas.Sesiones, s.Id), entradas.Select(e => new Entrada { Tipo = e.Tipo, Texto = e.Texto }));
            return s;
        }

        public async ValueTask DisposeAsync()
        {
            await Bucle.Ejecutar(async () => await Gestor.DisposeAsync());
            Bucle.Dispose();
            Bd.Dispose();
            Carpeta.Dispose();
        }
    }

    private static async Task<Montaje> Montar()
    {
        var c = new CarpetaPrueba("buscar");
        var rutas = new Rutas(Path.Combine(c.Ruta, "MultiAgente"));
        rutas.CrearCarpetas();
        Directory.CreateDirectory(Path.Combine(c.Ruta, "web"));
        var bd = BaseDatos.Abrir(rutas.BaseDatos);
        var bucle = new BucleUnico();
        var gestor = await bucle.Ejecutar(() => new Gestor(rutas, new Repositorio(bd), _ => { }, bucle.Poner, "prueba", new OpcionesGestor
        {
            ComprobarRequisitos = () => [], OrdenAgente = (_, _) => new OrdenAgente("dotnet", [AgenteFalso.Dll()]),
            Autorizada = (_, _, _) => true, Temporizadores = false, GenerarResumen = _ => Task.FromResult<string?>(null),
        }));
        return new Montaje { Carpeta = c, Rutas = rutas, Bd = bd, Bucle = bucle, Gestor = gestor };
    }

    [Fact]
    public async Task Encuentra_en_titulos_y_mensajes_tambien_archivadas_sin_mayusculas_ni_tildes()
    {
        await using var m = await Montar();
        var hoy = DateTime.Now;
        var web = await m.Bucle.Ejecutar(() => m.Gestor.GuardarProyecto(null, "Web", m.Trabajo));
        var factura = m.Sesion("Facturación", web.Id, archivada: false, hoy.AddDays(-2),
            ("usuario", "Revisa la validación del NIF"), ("agente", "La VALIDACION ya estaba hecha.\nHe añadido otra validación para el CIF."));
        var vieja = m.Sesion("Ideas sueltas", null, archivada: true, hoy.AddDays(-1),
            ("usuario", "apunta: validacion de correos"), ("herramienta", "validacion en un comando: no cuenta"));
        m.Sesion("Otra cosa", web.Id, archivada: false, hoy, ("usuario", "nada que ver"));

        var r = await m.Bucle.Ejecutar(() => m.Gestor.Buscar("Validación"));
        // La archivada es más reciente: sale primero; solo cuentan los mensajes del usuario y del agente
        Assert.Equal([vieja.Id, factura.Id], r.Select(x => x.SesionId));
        var a = r[0];
        Assert.True(a.Archivada);
        Assert.Null(a.Proyecto);
        Assert.Equal(1, a.Coincidencias);
        Assert.Equal("validacion", a.Fragmento.Substring(a.Marca[0], a.Marca[1]));
        var f = r[1];
        Assert.Equal(("Web", 2), (f.Proyecto, f.Coincidencias));
        Assert.Equal("validación", f.Fragmento.Substring(f.Marca[0], f.Marca[1]));
        Assert.Equal(m.Trabajo, f.Carpeta);
        Assert.NotNull(f.EntradaId);

        // Solo en el título
        var t = Assert.Single(await m.Bucle.Ejecutar(() => m.Gestor.Buscar("facturacion")));
        Assert.Equal((factura.Id, (string?)null, "Facturación"), (t.SesionId, t.EntradaId, t.Fragmento));
        Assert.Equal([0, 11], t.Marca);

        Assert.Empty(await m.Bucle.Ejecutar(() => m.Gestor.Buscar("x")));            // menos de dos letras
        Assert.Empty(await m.Bucle.Ejecutar(() => m.Gestor.Buscar("no aparece")));
        Assert.Single(await m.Bucle.Ejecutar(() => m.Gestor.Buscar("validación", maximo: 1)));
    }

    [Fact]
    public async Task Archivar_un_proyecto_deja_las_que_trabajan_y_se_puede_deshacer()
    {
        await using var m = await Montar();
        var (web, otro) = await m.Bucle.Ejecutar(() =>
        {
            m.Gestor.CrearCuenta("Claude", "Api", "Prueba", "k1", null, null, null);
            return (m.Gestor.GuardarProyecto(null, "Web", m.Trabajo), m.Gestor.GuardarProyecto(null, "Otro", m.Trabajo));
        });
        var s1 = m.Sesion("uno", web.Id, archivada: false, DateTime.Now);
        var s2 = m.Sesion("dos", web.Id, archivada: false, DateTime.Now);
        var fuera = m.Sesion("de otro proyecto", otro.Id, archivada: false, DateTime.Now);
        // Una que está trabajando (el agente falso tarda 10 s con «lento»): no se corta
        var cuenta = m.Repo.Cuentas().Single();
        var ocupada = await m.Bucle.Ejecutar(() => m.Gestor.NuevaSesion("", "cuenta:" + cuenta.Id, "escribe lento", [], web.Id));
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (await m.Bucle.Ejecutar(() => m.Gestor.Viva(ocupada)?.Estado != EstadoSesion.Trabajando))
        {
            Assert.True(DateTime.UtcNow < limite, "no se puso a trabajar");
            await Task.Delay(100);
        }

        var (archivadas, ocupadas) = await m.Bucle.Ejecutar(() => m.Gestor.ArchivarProyecto(web.Id));
        Assert.Equal(new[] { s1.Id, s2.Id }.Order(), archivadas.Order());
        Assert.Equal(1, ocupadas);
        Assert.True(m.Repo.Sesion(s1.Id)!.Archivada);
        Assert.False(m.Repo.Sesion(ocupada)!.Archivada);
        Assert.Equal(EstadoSesion.Trabajando, await m.Bucle.Ejecutar(() => m.Gestor.Viva(ocupada)!.Estado));
        Assert.False(m.Repo.Sesion(fuera.Id)!.Archivada);

        await m.Bucle.Ejecutar(() => { m.Gestor.Desarchivar(archivadas); return true; });
        Assert.False(m.Repo.Sesion(s1.Id)!.Archivada);
        Assert.False(m.Repo.Sesion(s2.Id)!.Archivada);
        Assert.Equal(web.Id, m.Repo.Sesion(s1.Id)!.ProyectoId);

        await Assert.ThrowsAsync<ExcepcionUsuario>(() => m.Bucle.Ejecutar(() => m.Gestor.ArchivarProyecto("no-existe")));

        // Que termine el turno antes de cerrar la base (si no, su consumo llegaría con la base ya cerrada)
        await m.Bucle.Ejecutar(() => m.Gestor.Interrumpir(ocupada));
        while (await m.Bucle.Ejecutar(() => m.Gestor.Viva(ocupada)!.Estado != EstadoSesion.Esperando)) await Task.Delay(100);
        await Task.Delay(300);
    }

    [Theory]
    [InlineData("corto", 0, 5, "corto", 0, 5)]
    [InlineData("una línea\ncon\n\nsaltos y validación al final", 24, 10, "una línea con saltos y validación al final", 23, 10)]
    public void El_fragmento_va_en_una_linea_y_marca_lo_encontrado(string texto, int inicio, int largo, string fragmento, int i, int n)
    {
        Assert.Equal(fragmento, Gestor.Recortar(texto, inicio, largo, out var marca));
        Assert.Equal([i, n], marca);
    }

    [Fact]
    public void Un_texto_largo_se_recorta_con_puntos_suspensivos_sin_cortar_palabras()
    {
        var texto = string.Join(' ', Enumerable.Repeat("palabra", 40)) + " AQUÍ " + string.Join(' ', Enumerable.Repeat("otra", 60));
        var inicio = texto.IndexOf("AQUÍ", StringComparison.Ordinal);
        var f = Gestor.Recortar(texto, inicio, 4, out var marca);
        Assert.StartsWith("…palabra", f);
        Assert.EndsWith("otra…", f);
        Assert.Equal("AQUÍ", f.Substring(marca[0], marca[1]));
        Assert.True(f.Length < 210);
    }
}
