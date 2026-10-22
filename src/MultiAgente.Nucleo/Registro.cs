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

    /// <summary>Claves con forma reconocible (prefijo del proveedor): todo lo que casa es secreto; el grupo 1 se conserva.</summary>
    private static readonly (string Patron, string Sustituto)[] Formas =
    [
        (@"sk-ant-[A-Za-z0-9_\-]{10,}", "sk-ant-•••"),
        (@"\bsk-or-v1-[A-Za-z0-9]{16,}", "sk-or-v1-•••"),             // OpenRouter
        (@"\bsk-(?:proj-)?[A-Za-z0-9_\-]{16,}", "sk-•••"),
        (@"\bAIza[0-9A-Za-z_\-]{30,}", "AIza•••"),
        (@"\bya29\.[0-9A-Za-z_\-\.]{10,}", "ya29.•••"),
        (@"\bgsk_[A-Za-z0-9]{20,}", "gsk_•••"),                       // Groq
        (@"\bnvapi-[A-Za-z0-9_\-]{20,}", "nvapi-•••"),                // NVIDIA
        (@"\bfw_[A-Za-z0-9]{16,}", "fw_•••"),                         // Fireworks
        (@"\bxai-[A-Za-z0-9]{20,}", "xai-•••"),                       // xAI
        (@"\bcsk-[A-Za-z0-9]{20,}", "csk-•••"),                       // Cerebras
        (@"\bvck_[A-Za-z0-9]{20,}", "vck_•••"),                       // Vercel
        (@"\btgp_v1_[A-Za-z0-9_\-]{20,}", "tgp_v1_•••"),              // Together
        (@"\bhf_[A-Za-z0-9]{20,}", "hf_•••"),                         // Hugging Face
        (@"\bgithub_pat_[A-Za-z0-9_]{20,}", "github_pat_•••"),
        (@"\b(gh[pousr]_)[A-Za-z0-9]{20,}", "$1•••"),                 // GitHub
        (@"\bglpat-[A-Za-z0-9_\-]{20,}", "glpat-•••"),                // GitLab
        (@"\b(xox[abprs]-)[A-Za-z0-9\-]{10,}", "$1•••"),              // Slack
        (@"\bAKIA[0-9A-Z]{16}\b", "AKIA•••"),                         // AWS
        (@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", "eyJ•••"), // JWT
    ];

    /// <summary>Cualquiera de las <see cref="Formas"/> (para buscar claves sueltas, p. ej. en argumentos de un MCP).</summary>
    public static readonly Regex FormaDeClave = new(string.Join("|", Formas.Select(f => "(?:" + f.Patron + ")")), RegexOptions.Compiled);

    private static readonly (Regex Patron, string Sustituto)[] Secretos =
    [
        .. Formas.Select(f => (new Regex(f.Patron, RegexOptions.Compiled), f.Sustituto)),
        (new Regex(@"(?i)(\bbearer\s+)[A-Za-z0-9._\-~+/=]{12,}", RegexOptions.Compiled), "$1•••"),
        // Basic: base64 con algún dígito o signo, para no tapar «Basic configuration»
        (new Regex(@"(\bBasic\s+)(?=[A-Za-z0-9+/]*[0-9+/=])[A-Za-z0-9+/]{8,}={0,2}", RegexOptions.Compiled), "$1•••"),
        (new Regex(@"(?i)(""?\bauthorization""?\s*[:=]\s*""?(?:(?:basic|bearer|token|digest)\s+)?)(?!(?:basic|bearer|token|digest)\b)[^""\s,}•]{6,}", RegexOptions.Compiled), "$1•••"),
        (new Regex(@"(?i)(""?(?:access[_\-]?token|refresh[_\-]?token|id[_\-]?token|api[_\-]?key|apikey|client[_\-]?secret|password|token)""?\s*[:=]\s*""?)[^""\s,}]{6,}",
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
