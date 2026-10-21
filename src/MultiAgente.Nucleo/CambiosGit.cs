using System.Diagnostics;
using System.Text;

namespace MultiAgente.Nucleo;

/// <summary>Un fichero cambiado respecto al último commit. Estado: M (cambiado), A (añadido), D (borrado), ? (nuevo, sin git).</summary>
public sealed record FicheroCambiado(string Ruta, string Estado, int? Mas, int? Menos, bool Binario);

/// <summary>Los cambios sin confirmar de una carpeta que está dentro de un repositorio git.</summary>
public sealed record CambiosCarpeta(string Raiz, string? Rama, IReadOnlyList<FicheroCambiado> Ficheros, bool Recortado);

/// <summary>Las diferencias de un fichero, en formato unificado de git.</summary>
public sealed record DiferenciasFichero(string Ruta, string Texto, bool Binario, bool Recortado);

/// <summary>
/// Visor de cambios: lo que ha cambiado en la carpeta de una sesión desde el último commit (lo hecho por cualquier agente o
/// a mano). Solo lee: nunca toca el índice (<c>GIT_OPTIONAL_LOCKS=0</c>, para no molestar al git que use el agente).
/// Rutas relativas a la carpeta de la sesión.
/// </summary>
public static class CambiosGit
{
    /// <summary>Árbol vacío de git: la base cuando el repositorio aún no tiene commits.</summary>
    private const string ArbolVacio = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private const int MaxFicheros = 400;
    private const int MaxTexto = 400_000;

    /// <summary>Cambios de la carpeta; null si no está en un repositorio git (o no hay git).</summary>
    public static async Task<CambiosCarpeta?> Leer(string carpeta)
    {
        var raiz = await Git(carpeta, "rev-parse", "--show-toplevel");
        if (raiz.Codigo != 0) return null;
        var rama = await Git(carpeta, "rev-parse", "--abbrev-ref", "HEAD");
        var bas = await Base(carpeta);

        var estados = Partes((await Git(carpeta, "diff", "--name-status", "-z", "--relative", "--no-renames", bas, "--", ".")).Salida);
        var cuentas = Partes((await Git(carpeta, "diff", "--numstat", "-z", "--relative", "--no-renames", bas, "--", ".")).Salida);
        var nuevos = Partes((await Git(carpeta, "ls-files", "--others", "--exclude-standard", "-z", "--", ".")).Salida);

        // numstat -z: «añadidas\tquitadas\truta» por elemento («-\t-\truta» si es binario)
        var lineas = new Dictionary<string, (int? Mas, int? Menos)>(StringComparer.Ordinal);
        foreach (var c in cuentas)
        {
            var t = c.Split('\t', 3);
            if (t.Length == 3) lineas[t[2]] = (int.TryParse(t[0], out var a) ? a : null, int.TryParse(t[1], out var b) ? b : null);
        }
        var ficheros = new List<FicheroCambiado>();
        for (var i = 0; i + 1 < estados.Count; i += 2)
        {
            var ruta = estados[i + 1];
            var (mas, menos) = lineas.GetValueOrDefault(ruta);
            ficheros.Add(new FicheroCambiado(ruta, estados[i][..1], mas, menos, mas is null && menos is null && lineas.ContainsKey(ruta)));
        }
        foreach (var ruta in nuevos)
        {
            var (texto, binario) = LeerTexto(Path.Combine(carpeta, ruta));
            ficheros.Add(new FicheroCambiado(ruta, "?", binario ? null : ContarLineas(texto), binario ? null : 0, binario));
        }
        ficheros.Sort((a, b) => string.Compare(a.Ruta, b.Ruta, StringComparison.OrdinalIgnoreCase));
        var recortado = ficheros.Count > MaxFicheros;
        return new CambiosCarpeta(raiz.Salida.Trim().Replace('/', '\\'), rama.Codigo == 0 ? rama.Salida.Trim() : null,
                                  recortado ? ficheros[..MaxFicheros] : ficheros, recortado);
    }

    /// <summary>Diferencias de un fichero de la carpeta respecto al último commit (todo añadido si git no lo conoce).</summary>
    public static async Task<DiferenciasFichero> Diferencias(string carpeta, string ruta)
    {
        var completa = Path.GetFullPath(Path.Combine(carpeta, ruta));
        var dentro = Path.TrimEndingDirectorySeparator(Path.GetFullPath(carpeta)) + Path.DirectorySeparatorChar;
        if (!completa.StartsWith(dentro, StringComparison.OrdinalIgnoreCase))
            throw new ExcepcionUsuario("Ese fichero no está en la carpeta de la sesión.");

        var conocido = (await Git(carpeta, "ls-files", "-z", "--error-unmatch", "--", ruta)).Codigo == 0
                       || (await Git(carpeta, "diff", "--name-only", await Base(carpeta), "--", ruta)).Salida.Length > 0;
        if (!conocido)
        {
            var (texto, binario) = LeerTexto(completa);
            if (binario) return new DiferenciasFichero(ruta, "", true, false);
            var lineas = texto.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            var sb = new StringBuilder($"@@ -0,0 +1,{lineas.Length} @@\n");
            foreach (var l in lineas) sb.Append('+').Append(l).Append('\n');
            return Recortar(ruta, sb.ToString());
        }
        var d = await Git(carpeta, "diff", "--no-color", "--no-ext-diff", "--relative", "--no-renames", await Base(carpeta), "--", ruta);
        if (d.Salida.Contains("\nBinary files ") || d.Salida.StartsWith("Binary files ")) return new DiferenciasFichero(ruta, "", true, false);
        // Sin la cabecera (diff --git, index, ---, +++): la interfaz ya sabe qué fichero es
        var i = d.Salida.IndexOf("\n@@", StringComparison.Ordinal);
        return Recortar(ruta, i < 0 ? "" : d.Salida[(i + 1)..]);
    }

    private static DiferenciasFichero Recortar(string ruta, string texto) =>
        texto.Length <= MaxTexto ? new(ruta, texto, false, false) : new(ruta, texto[..texto.LastIndexOf('\n', MaxTexto)], false, true);

    private static async Task<string> Base(string carpeta) =>
        (await Git(carpeta, "rev-parse", "--verify", "-q", "HEAD")).Codigo == 0 ? "HEAD" : ArbolVacio;

    private static List<string> Partes(string salida) => [.. salida.Split('\0', StringSplitOptions.RemoveEmptyEntries)];

    private static int ContarLineas(string texto) =>
        texto.Length == 0 ? 0 : texto.Count(c => c == '\n') + (texto.EndsWith('\n') ? 0 : 1);

    /// <summary>Texto de un fichero (hasta 2 MB) y si parece binario.</summary>
    private static (string Texto, bool Binario) LeerTexto(string ruta)
    {
        try
        {
            var info = new FileInfo(ruta);
            if (!info.Exists || info.Length > 2_000_000) return ("", true);
            var bytes = File.ReadAllBytes(ruta);
            if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0) return ("", true);
            return (Encoding.UTF8.GetString(bytes).TrimStart('﻿'), false);
        }
        catch (IOException) { return ("", true); }
        catch (UnauthorizedAccessException) { return ("", true); }
    }

    private static async Task<(int Codigo, string Salida)> Git(string carpeta, params string[] argumentos)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = carpeta, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in (string[])["-c", "core.quotepath=false", "--literal-pathspecs", .. argumentos]) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        Process p;
        try { p = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception) { return (-1, ""); } // sin git en el equipo
        using (p)
        {
            var salida = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            using var plazo = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await p.WaitForExitAsync(plazo.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                Registro.Aviso($"git {string.Join(' ', argumentos)} tarda demasiado en {carpeta}");
                return (-1, "");
            }
            return (p.ExitCode, await salida);
        }
    }
}
