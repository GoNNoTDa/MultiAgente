using System.Diagnostics;

namespace MultiAgente.Nucleo;

/// <summary>
/// Lo que las cuentas de un mismo proveedor comparten (las conversaciones del agente), en
/// <c>datos\comun\&lt;proveedor&gt;</c> y enlazado con una unión de NTFS desde la carpeta de cada cuenta. Así una cuenta
/// puede recuperar la sesión que empezó otra (relevo sin perder nada). El inicio de sesión sigue siendo propio.
/// Las uniones guardan rutas absolutas y el Explorador las copia como carpetas normales: se comprueban y reparan antes
/// de cada uso (lo que haya dentro se junta en la carpeta común).
/// </summary>
public static class CarpetasCompartidas
{
    /// <summary>
    /// Subcarpetas compartidas por proveedor, relativas a la carpeta de la cuenta: las conversaciones (para el relevo) y
    /// las extensiones (skills, órdenes, agentes, plugins, estilos). Las skills (formato abierto SKILL.md) son una sola
    /// carpeta para los tres proveedores.
    /// </summary>
    public static IReadOnlyList<string> De(string proveedor) => proveedor switch
    {
        "Claude" => ["projects", "skills", "commands", "agents", "plugins", "output-styles"],
        "Codex" => ["sessions", "skills", "prompts"],
        "Gemini" => [Path.Combine(".gemini", "tmp"), Path.Combine(".gemini", "skills"), Path.Combine(".gemini", "commands"), Path.Combine(".gemini", "extensions")],
        _ => [],
    };

    /// <summary>Carpeta común a la que apunta una subcarpeta compartida.</summary>
    public static string Comun(Rutas rutas, string proveedor, string sub) =>
        Path.GetFileName(sub) == "skills" ? Extensiones.Skills(rutas) : Path.Combine(rutas.Comun, proveedor, sub);

    /// <summary>
    /// Deja enlazadas las carpetas compartidas de la cuenta y sincroniza sus ficheros compartidos (instrucciones,
    /// ajustes). Devuelve false si alguna carpeta no se pudo enlazar.
    /// </summary>
    public static bool Preparar(Rutas rutas, string proveedor, string carpetaCuenta)
    {
        var bien = true;
        foreach (var sub in De(proveedor))
        {
            var comun = Comun(rutas, proveedor, sub);
            var enlace = Path.Combine(carpetaCuenta, sub);
            try { Enlazar(enlace, comun); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                bien = false;
                Registro.Error($"No se pudo compartir {sub} de {carpetaCuenta}", ex);
            }
        }
        Extensiones.Sincronizar(rutas, proveedor, carpetaCuenta);
        return bien;
    }

    /// <summary>Destino de una unión (o enlace simbólico); null si es una carpeta normal o no existe.</summary>
    public static string? Destino(string carpeta)
    {
        var d = new DirectoryInfo(carpeta);
        if (!d.Exists || !d.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
        return d.LinkTarget is { } t ? Path.GetFullPath(t.StartsWith(@"\??\") ? t[4..] : t) : null;
    }

    /// <summary>Hace que <paramref name="enlace"/> sea una unión a <paramref name="destino"/>, juntando lo que hubiera.</summary>
    public static void Enlazar(string enlace, string destino)
    {
        Directory.CreateDirectory(destino);
        var actual = Destino(enlace);
        if (actual is not null && string.Equals(Path.TrimEndingDirectorySeparator(actual), Path.TrimEndingDirectorySeparator(Path.GetFullPath(destino)), StringComparison.OrdinalIgnoreCase))
            return;
        if (actual is not null)
            Directory.Delete(enlace); // unión a otra ruta (carpeta movida): se quita solo la unión
        else if (Directory.Exists(enlace))
        {
            Juntar(enlace, destino); // carpeta real (la creó el agente o se copió con el Explorador)
            Directory.Delete(enlace, recursive: true);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(enlace)!);
        CrearUnion(enlace, destino);
    }

    /// <summary>Mueve el contenido de <paramref name="origen"/> a <paramref name="destino"/> sin pisar: si choca, se queda con otro nombre.</summary>
    private static void Juntar(string origen, string destino)
    {
        foreach (var dir in Directory.GetDirectories(origen))
        {
            var d = Path.Combine(destino, Path.GetFileName(dir));
            if (Destino(dir) is not null) { Directory.Delete(dir); continue; }
            if (!Directory.Exists(d)) Directory.Move(dir, d);
            else Juntar(dir, d);
        }
        foreach (var f in Directory.GetFiles(origen))
        {
            var d = Path.Combine(destino, Path.GetFileName(f));
            if (!File.Exists(d)) File.Move(f, d);
            else if (new FileInfo(d).Length != new FileInfo(f).Length || File.GetLastWriteTimeUtc(d) != File.GetLastWriteTimeUtc(f))
                File.Move(f, Path.Combine(destino, $"{Path.GetFileNameWithoutExtension(f)} (copia {DateTime.Now:yyyyMMddHHmmss}){Path.GetExtension(f)}"));
        }
    }

    private static void CrearUnion(string enlace, string destino)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "/d", "/c", "mklink", "/J", enlace, destino }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit(10000);
        if (p.ExitCode != 0 || Destino(enlace) is null)
            throw new IOException($"mklink /J terminó con código {p.ExitCode}: {error.Trim()}");
    }
}
