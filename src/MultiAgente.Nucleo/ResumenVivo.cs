using System.Diagnostics;
using System.Text;
using MultiAgente.Nucleo.Acp;

namespace MultiAgente.Nucleo;

/// <summary>
/// Resumen vivo de una sesión (<c>datos\sesiones\&lt;id&gt;\resumen.md</c>): objetivo, hecho, pendiente, decisiones y
/// ficheros. Se pone al día tras los turnos para que un relevo a otro proveedor tenga el contexto listo, aunque la cuenta
/// que se agota ya no pueda resumir nada. Se hace con una llamada breve a Claude (Haiku o su equivalente en una API
/// compatible), sin herramientas, MCP ni ajustes del usuario.
/// </summary>
public static class ResumenVivo
{
    public const string NombreFichero = "resumen.md";

    private const string Sistema =
        "Resumes conversaciones de trabajo entre una persona y un agente de programación para que otro agente pueda " +
        "continuarlas sin perder el hilo. Responde solo con el resumen, en castellano, en Markdown, con estas secciones: " +
        "## Objetivo, ## Hecho, ## Pendiente, ## Decisiones y normas, ## Ficheros. Sé concreto (rutas, nombres, órdenes) " +
        "y breve: como mucho 350 palabras. No inventes nada que no esté en la conversación.";

    public static string? Leer(string carpetaSesion)
    {
        var f = Path.Combine(carpetaSesion, NombreFichero);
        try { return File.Exists(f) ? File.ReadAllText(f) : null; }
        catch (IOException) { return null; }
    }

    public static DateTime? Fecha(string carpetaSesion)
    {
        var f = Path.Combine(carpetaSesion, NombreFichero);
        return File.Exists(f) ? File.GetLastWriteTime(f) : null;
    }

    /// <summary>Lo que se le pide al modelo: el resumen anterior (si lo hay) y la conversación reciente.</summary>
    public static string Peticion(IReadOnlyList<Entrada> entradas, string? anterior)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(anterior))
            sb.Append("Resumen anterior (ponlo al día con lo nuevo):\n").Append(anterior.Trim()).Append("\n\n");
        sb.Append("Conversación:\n").Append(Traspaso.Conversacion(entradas, 24000));
        return sb.ToString();
    }

    /// <summary>
    /// Pide el resumen con <c>claude -p</c> y la cuenta indicada (su entorno). Devuelve null si falla o tarda demasiado.
    /// </summary>
    public static async Task<string?> Generar(OrdenAgente claude, IReadOnlyDictionary<string, string?> entorno, string peticion,
                                              string carpetaTrabajo, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(carpetaTrabajo);
            var psi = new ProcessStartInfo(claude.Ejecutable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = carpetaTrabajo,
            };
            foreach (var a in claude.Argumentos) psi.ArgumentList.Add(a);
            foreach (var a in new[]
                     {
                         "-p", "--model", "haiku", "--effort", "low", "--no-session-persistence", "--output-format", "text",
                         "--tools", "", "--strict-mcp-config", "--disable-slash-commands", "--setting-sources", "project",
                         "--system-prompt", Sistema,
                     })
                psi.ArgumentList.Add(a);
            foreach (var (k, v) in entorno)
                if (v is null) psi.Environment.Remove(k); else psi.Environment[k] = v;
            using var p = Process.Start(psi)!;
            await p.StandardInput.WriteAsync(peticion);
            p.StandardInput.Close();
            var salida = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TimeSpan.FromSeconds(120));
            try { await p.WaitForExitAsync(limite.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch (Exception) { }
                Registro.Aviso("Resumen vivo: Claude no respondió a tiempo");
                return null;
            }
            var texto = (await salida).Trim();
            if (p.ExitCode != 0 || texto.Length < 20)
            {
                Registro.Aviso($"Resumen vivo: código {p.ExitCode}");
                return null;
            }
            return texto;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Registro.Error("Resumen vivo", ex);
            return null;
        }
    }

    public static void Guardar(string carpetaSesion, string resumen)
    {
        Directory.CreateDirectory(carpetaSesion);
        var f = Path.Combine(carpetaSesion, NombreFichero);
        File.WriteAllText(f + ".tmp", resumen, new UTF8Encoding(false));
        File.Move(f + ".tmp", f, overwrite: true);
    }
}
