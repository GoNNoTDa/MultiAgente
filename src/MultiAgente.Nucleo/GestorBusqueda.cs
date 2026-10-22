using System.Globalization;
using System.Text;

namespace MultiAgente.Nucleo;

/// <summary>Un resultado de la búsqueda: una sesión, cuántas veces aparece y el primer trozo donde sale.</summary>
/// <param name="EntradaId">Entrada de la conversación donde sale (null si solo sale en el título).</param>
/// <param name="Marca">Dónde está lo buscado dentro de <paramref name="Fragmento"/>: inicio y largo.</param>
/// <param name="Carpeta">Con la cuenta y el equipo, para abrirla aunque no salga en el lateral (archivadas antiguas).</param>
public sealed record ResultadoBusqueda(string SesionId, string Titulo, string? Proyecto, bool Archivada, DateTime UltimoUso,
                                       int Coincidencias, string Fragmento, int[] Marca, string? EntradaId,
                                       string Carpeta, string? CuentaId, string? EquipoId);

/// <summary>Buscar en las conversaciones (también las archivadas) y archivar todas las sesiones de un proyecto.</summary>
public sealed partial class Gestor
{
    // ================================================================= búsqueda

    private const CompareOptions SinMayusNiTildes = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
    private static readonly CompareInfo Comparar = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>
    /// Busca un texto en los títulos y en lo que han escrito el usuario y los agentes, sin distinguir mayúsculas ni tildes.
    /// Las sesiones más recientes primero. Lee los ficheros fuera del hilo de la ventana.
    /// </summary>
    public async Task<IReadOnlyList<ResultadoBusqueda>> Buscar(string texto, int maximo = 50)
    {
        texto = texto.Trim();
        if (texto.Length < 2) return [];
        var proyectos = _repo.Proyectos().ToDictionary(p => p.Id, p => p.Nombre);
        var sesiones = _repo.Sesiones().Concat(_repo.Sesiones(archivadas: true)).OrderByDescending(s => s.UltimoUso).ToList();
        var carpetas = _rutas.Sesiones;
        return await Task.Run(() =>
        {
            var resultados = new List<ResultadoBusqueda>();
            foreach (var s in sesiones)
            {
                if (BuscarEn(s, Path.Combine(carpetas, s.Id), texto) is not { } r) continue;
                resultados.Add(r with { Proyecto = s.ProyectoId is null ? null : proyectos.GetValueOrDefault(s.ProyectoId) });
                if (resultados.Count >= maximo) break;
            }
            return (IReadOnlyList<ResultadoBusqueda>)resultados;
        });
    }

    private static ResultadoBusqueda? BuscarEn(Sesion s, string carpeta, string texto)
    {
        var titulo = s.Titulo ?? "Sesión nueva";
        var enTitulo = Comparar.IndexOf(titulo, texto, SinMayusNiTildes) >= 0;
        var coincidencias = enTitulo ? 1 : 0;
        string? fragmento = null, entrada = null;
        int[] marca = [];

        var fichero = Path.Combine(carpeta, "hilo.jsonl");
        // Filtro rápido sobre el fichero tal cual (las tildes no se escapan); con comillas o barras no vale y se lee entero
        if (File.Exists(fichero) && (texto.IndexOfAny(['"', '\\']) >= 0 || Contiene(fichero, texto)))
        {
            foreach (var e in new Hilo(carpeta).Entradas)
            {
                if (e.Tipo is not ("usuario" or "agente") || string.IsNullOrEmpty(e.Texto)) continue;
                var i = Comparar.IndexOf(e.Texto, texto, SinMayusNiTildes, out var largo);
                if (i < 0) continue;
                coincidencias++;
                if (fragmento is not null) continue;
                fragmento = Recortar(e.Texto, i, largo, out marca);
                entrada = e.Id;
            }
        }
        if (coincidencias == 0) return null;
        if (fragmento is null)
        {
            var i = Comparar.IndexOf(titulo, texto, SinMayusNiTildes, out var largo);
            fragmento = titulo;
            marca = [i, largo];
        }
        return new ResultadoBusqueda(s.Id, titulo, null, s.Archivada, s.UltimoUso, coincidencias, fragmento, marca, entrada,
                                     s.Carpeta, s.CuentaId, s.EquipoId);
    }

    private static bool Contiene(string fichero, string texto)
    {
        try { return Comparar.IndexOf(File.ReadAllText(fichero, Encoding.UTF8), texto, SinMayusNiTildes) >= 0; }
        catch (IOException) { return false; }
    }

    /// <summary>Unos 60 caracteres antes y 120 después de lo encontrado, en una línea; devuelve dónde queda la marca.</summary>
    internal static string Recortar(string texto, int inicio, int largo, out int[] marca)
    {
        var desde = Math.Max(0, inicio - 60);
        var hasta = Math.Min(texto.Length, inicio + largo + 120);
        // Sin cortar palabras por la mitad (si hay un espacio cerca)
        if (desde > 0 && texto.IndexOf(' ', desde, inicio - desde) is var a and >= 0) desde = a + 1;
        if (hasta < texto.Length && texto.IndexOf(' ', hasta) is var b and >= 0 && b - hasta < 20) hasta = b;
        var antes = (desde > 0 ? "…" : "") + Linea(texto[desde..inicio]);
        var dentro = Linea(texto.Substring(inicio, largo));
        var despues = Linea(texto[(inicio + largo)..hasta]) + (hasta < texto.Length ? "…" : "");
        marca = [antes.Length, dentro.Length];
        return antes + dentro + despues;
    }

    private static string Linea(string t) => System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ");

    // ================================================================= archivar en bloque

    /// <summary>
    /// Archiva todas las sesiones de un proyecto, salvo las que están a mitad de un turno o pidiendo permiso (no se
    /// cortan). Devuelve las archivadas (para «Deshacer») y cuántas se han dejado.
    /// </summary>
    public async Task<(IReadOnlyList<string> Archivadas, int Ocupadas)> ArchivarProyecto(string proyectoId)
    {
        if (_repo.Proyectos().All(p => p.Id != proyectoId)) throw new ExcepcionUsuario("Ese proyecto ya no existe.");
        var archivadas = new List<string>();
        var ocupadas = 0;
        foreach (var s in _repo.Sesiones().Where(s => s.ProyectoId == proyectoId))
        {
            if (_vivas.GetValueOrDefault(s.Id)?.Estado is EstadoSesion.Trabajando or EstadoSesion.PidePermiso or EstadoSesion.Arrancando)
            {
                ocupadas++;
                continue;
            }
            _relevos.Remove(s.Id);
            _aMandar.Remove(s.Id);
            if (_vivas.Remove(s.Id, out var viva)) await viva.DisposeAsync();
            s.Archivada = true;
            _repo.GuardarSesion(s);
            archivadas.Add(s.Id);
        }
        Registro.Info($"Proyecto {proyectoId}: {archivadas.Count} sesiones archivadas ({ocupadas} trabajando, sin tocar)");
        EnviarEstado();
        return (archivadas, ocupadas);
    }

    /// <summary>Vuelve a poner en la lista varias sesiones archivadas (el «Deshacer» de archivar un proyecto).</summary>
    public void Desarchivar(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            if (_repo.Sesion(id) is { Archivada: true } s)
            {
                s.Archivada = false;
                _repo.GuardarSesion(s);
            }
        EnviarEstado();
    }
}
