using System.IO.Compression;

namespace MultiAgente.Nucleo;

/// <summary>Resultado de <see cref="Traslado.Crear"/>.</summary>
/// <param name="Omitidos">Ficheros que no se han podido leer (abiertos por otro programa): van al registro.</param>
public sealed record ResultadoTraslado(string Zip, long Bytes, int Ficheros, IReadOnlyList<string> Omitidos);

/// <summary>
/// «Llevar a otro PC»: un .zip con la carpeta de MultiAgente (programa y datos) para descomprimir en otro equipo y seguir.
/// Lleva proyectos, sesiones con sus conversaciones, cuentas (sin su inicio de sesión), equipos, extensiones y ajustes.
/// No lleva credenciales (inicios de sesión de cada CLI ni la bóveda de claves: en el otro PC se vuelve a autorizar cada
/// cuenta), ni la caché del navegador, ni los registros. Las uniones NTFS no se copian: se rehacen al arrancar.
/// </summary>
public static class Traslado
{
    /// <summary>Ficheros de inicio de sesión de las CLIs dentro de la carpeta de cada cuenta.</summary>
    private static readonly HashSet<string> Credenciales = new(StringComparer.OrdinalIgnoreCase)
    {
        ".credentials.json", "auth.json", "oauth_creds.json", "google_accounts.json",
    };

    /// <param name="carpetaDestino">Dónde se deja el .zip (fuera de la carpeta de MultiAgente).</param>
    /// <param name="copiarBase">Copia coherente de la base de datos en el fichero que se le pasa (la base está abierta).</param>
    public static async Task<ResultadoTraslado> Crear(Rutas rutas, string carpetaDestino, Action<string> copiarBase)
    {
        if (string.IsNullOrWhiteSpace(carpetaDestino) || !Directory.Exists(carpetaDestino))
            throw new ExcepcionUsuario("Elige una carpeta que exista para dejar el fichero.");
        carpetaDestino = Path.GetFullPath(carpetaDestino);
        if (Dentro(rutas.Raiz, carpetaDestino))
            throw new ExcepcionUsuario("Elige una carpeta fuera de la de MultiAgente (por ejemplo, el Escritorio o un USB).");

        var zip = Path.Combine(carpetaDestino, $"MultiAgente-{DateTime.Now:yyyy-MM-dd-HHmm}.zip");
        var parcial = zip + ".parcial";
        var baseCopia = Path.Combine(Path.GetTempPath(), $"multiagente-{Guid.NewGuid():N}.db");
        try
        {
            copiarBase(baseCopia);
            var (ficheros, omitidos) = await Task.Run(() => Escribir(rutas, parcial, baseCopia));
            File.Move(parcial, zip, overwrite: true);
            Registro.Info($"Traslado: {zip} ({ficheros} ficheros, {omitidos.Count} omitidos)");
            foreach (var o in omitidos) Registro.Aviso($"Traslado: no se pudo leer {o}");
            return new ResultadoTraslado(zip, new FileInfo(zip).Length, ficheros, omitidos);
        }
        catch (IOException ex)
        {
            throw new ExcepcionUsuario($"No se pudo crear el fichero en {carpetaDestino}: {ex.Message}", ex);
        }
        finally
        {
            File.Delete(parcial);
            File.Delete(baseCopia);
        }
    }

    private static (int Ficheros, List<string> Omitidos) Escribir(Rutas rutas, string zip, string baseCopia)
    {
        var omitidos = new List<string>();
        var ficheros = 0;
        using var salida = new FileStream(zip, FileMode.Create, FileAccess.Write);
        using var archivo = new ZipArchive(salida, ZipArchiveMode.Create);
        foreach (var f in Ficheros(rutas))
        {
            var nombre = "MultiAgente/" + Path.GetRelativePath(rutas.Raiz, f).Replace('\\', '/');
            try
            {
                // Puede estar abierto (una conversación que se está escribiendo): se lee lo que haya
                using var entrada = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var destino = archivo.CreateEntry(nombre, CompressionLevel.Optimal).Open();
                entrada.CopyTo(destino);
                ficheros++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { omitidos.Add(f); }
        }
        using (var destino = archivo.CreateEntry("MultiAgente/datos/multiagente.db", CompressionLevel.Optimal).Open())
        using (var entrada = File.OpenRead(baseCopia))
            entrada.CopyTo(destino);
        return (ficheros + 1, omitidos);
    }

    /// <summary>Lo que va en el .zip (salvo la base, que se copia aparte).</summary>
    internal static IEnumerable<string> Ficheros(Rutas rutas)
    {
        var fuera = new[] { rutas.WebView, rutas.Logs };
        var deDatos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            rutas.BaseDatos, rutas.BaseDatos + "-wal", rutas.BaseDatos + "-shm", rutas.BaseDatos + "-journal",
            rutas.Boveda, Path.ChangeExtension(rutas.Boveda, ".clave"),
        };
        var pendientes = new Stack<string>([rutas.Raiz]);
        while (pendientes.Count > 0)
        {
            var carpeta = pendientes.Pop();
            foreach (var d in Directory.EnumerateDirectories(carpeta))
            {
                // Uniones NTFS (conversaciones compartidas entre cuentas): se rehacen al arrancar en el otro PC
                if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (fuera.Any(x => string.Equals(Path.TrimEndingDirectorySeparator(d), x, StringComparison.OrdinalIgnoreCase))) continue;
                pendientes.Push(d);
            }
            foreach (var f in Directory.EnumerateFiles(carpeta))
            {
                if (deDatos.Contains(f) || new FileInfo(f).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                // Inicios de sesión y claves de proceso de Claude Code (sessions\*.key)
                if (Dentro(rutas.Cuentas, f) && (Credenciales.Contains(Path.GetFileName(f)) || f.EndsWith(".key", StringComparison.OrdinalIgnoreCase))) continue;
                yield return f;
            }
        }
    }

    private static bool Dentro(string carpeta, string ruta)
    {
        var r = Path.GetRelativePath(carpeta, ruta);
        return r == "." || (!r.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(r));
    }
}
