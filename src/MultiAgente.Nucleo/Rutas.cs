namespace MultiAgente.Nucleo;

/// <summary>
/// Rutas de la carpeta portátil. Todo lo de MultiAgente vive bajo <see cref="Raiz"/> (la carpeta del ejecutable):
/// nada en el registro de Windows ni en %APPDATA% / %LOCALAPPDATA%.
/// </summary>
public sealed class Rutas
{
    public Rutas(string raiz)
    {
        Raiz = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raiz));
    }

    /// <summary>Las de la aplicación en marcha: la carpeta del ejecutable (también en la publicación de un solo fichero).</summary>
    public static Rutas Actual { get; } = new(AppContext.BaseDirectory);

    public string Raiz { get; }
    public string Datos => Path.Combine(Raiz, "datos");
    public string BaseDatos => Path.Combine(Datos, "multiagente.db");
    public string Logs => Path.Combine(Datos, "logs");
    public string Sesiones => Path.Combine(Datos, "sesiones");
    public string Cuentas => Path.Combine(Datos, "cuentas");
    public string Comun => Path.Combine(Datos, "comun");
    public string Boveda => Path.Combine(Datos, "boveda.bin");
    public string WebView => Path.Combine(Datos, "webview");
    public string Ui => Path.Combine(Raiz, "ui");
    public string Herramientas => Path.Combine(Raiz, "tools");
    public string Catalogo => Path.Combine(Raiz, "catalogo", "proveedores.json");
    public string Adjuntos => Path.Combine(Datos, "adjuntos");

    /// <summary>Crea las carpetas de datos que falten.</summary>
    public void CrearCarpetas()
    {
        foreach (var c in new[] { Datos, Logs, Sesiones, Cuentas, Comun })
            Directory.CreateDirectory(c);
    }

    /// <summary>
    /// Ruta para guardar: relativa a la raíz si está dentro de la carpeta portátil (así sigue valiendo al copiarla a
    /// otro sitio); absoluta si está fuera (p. ej. la carpeta de un proyecto).
    /// </summary>
    public string ParaGuardar(string ruta)
    {
        var completa = Path.GetFullPath(ruta);
        var relativa = Path.GetRelativePath(Raiz, completa);
        var fuera = relativa == ".." || relativa.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || Path.IsPathRooted(relativa);
        return fuera ? completa : relativa;
    }

    /// <summary>Inversa de <see cref="ParaGuardar"/>.</summary>
    public string Resolver(string guardada) =>
        Path.IsPathRooted(guardada) ? Path.GetFullPath(guardada) : Path.GetFullPath(Path.Combine(Raiz, guardada));
}
