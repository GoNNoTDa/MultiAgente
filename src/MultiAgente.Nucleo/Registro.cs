using System.Text;
using System.Text.RegularExpressions;

namespace MultiAgente.Nucleo;

/// <summary>
/// Registro técnico en <c>datos\logs\aaaammdd.log</c>. Todo lo que se escribe pasa por <see cref="Tapar"/>: nunca
/// quedan tokens ni claves en el registro. Se conservan 30 días.
/// </summary>
public static class Registro
{
    private static readonly object Cerrojo = new();
    private static string? _carpeta;

    private static readonly (Regex Patron, string Sustituto)[] Secretos =
    [
        (new Regex(@"sk-ant-[A-Za-z0-9_\-]{10,}", RegexOptions.Compiled), "sk-ant-•••"),
        (new Regex(@"\bsk-(?:proj-)?[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled), "sk-•••"),
        (new Regex(@"\bAIza[0-9A-Za-z_\-]{30,}", RegexOptions.Compiled), "AIza•••"),
        (new Regex(@"\bya29\.[0-9A-Za-z_\-\.]{10,}", RegexOptions.Compiled), "ya29.•••"),
        (new Regex(@"(?i)(\bbearer\s+)[A-Za-z0-9._\-~+/=]{12,}", RegexOptions.Compiled), "$1•••"),
        (new Regex(@"(?i)(""?(?:access_?token|refresh_?token|id_?token|api_?key|apikey|client_?secret|password|token)""?\s*[:=]\s*""?)[^""\s,}]{6,}",
            RegexOptions.Compiled), "$1•••"),
    ];

    /// <summary>Empieza a escribir en <paramref name="carpeta"/> y borra los registros de más de 30 días.</summary>
    public static void Iniciar(string carpeta)
    {
        Directory.CreateDirectory(carpeta);
        _carpeta = carpeta;
        try
        {
            foreach (var f in Directory.EnumerateFiles(carpeta, "*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-30)) File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Info(string mensaje) => Escribir("INFO", mensaje);
    public static void Aviso(string mensaje) => Escribir("AVISO", mensaje);
    public static void Error(string mensaje, Exception? ex = null) =>
        Escribir("ERROR", ex is null ? mensaje : $"{mensaje}: {ex}");

    /// <summary>Tapa tokens, claves y contraseñas que aparezcan en el texto.</summary>
    public static string Tapar(string texto)
    {
        foreach (var (patron, sustituto) in Secretos)
            texto = patron.Replace(texto, sustituto);
        return texto;
    }

    private static void Escribir(string nivel, string mensaje)
    {
        var carpeta = _carpeta;
        if (carpeta is null) return;
        var linea = $"{DateTime.Now:HH:mm:ss.fff} {nivel,-5} {Tapar(mensaje)}{Environment.NewLine}";
        lock (Cerrojo)
        {
            try { File.AppendAllText(Path.Combine(carpeta, $"{DateTime.Now:yyyyMMdd}.log"), linea, Encoding.UTF8); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
