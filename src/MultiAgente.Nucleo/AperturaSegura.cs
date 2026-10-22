namespace MultiAgente.Nucleo;

/// <summary>Qué se hace con una ruta que sale en la conversación al pulsarla.</summary>
public enum ComoAbrir
{
    /// <summary>Con su programa (documentos, imágenes, código…).</summary>
    Abrir,
    /// <summary>Solo se señala en su carpeta: podría ejecutarse algo.</summary>
    Senalar,
    /// <summary>No se toca: ruta de red (Windows mandaría tus credenciales a ese servidor).</summary>
    Rechazar,
}

/// <summary>
/// Las rutas vienen del agente, que puede haber leído algo malicioso: solo se abre lo que es seguro abrir (lista blanca);
/// el resto se señala en el Explorador para que el usuario decida.
/// </summary>
public static class AperturaSegura
{
    private static readonly HashSet<string> Seguras = new(StringComparer.OrdinalIgnoreCase)
    {
        // Texto y datos
        ".txt", ".md", ".markdown", ".log", ".json", ".jsonl", ".xml", ".yaml", ".yml", ".toml", ".ini", ".csv", ".tsv",
        ".diff", ".patch",
        // Documentos (sin macros)
        ".pdf", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp",
        // Imágenes, audio y vídeo
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".svg", ".mp3", ".wav", ".mp4", ".webm",
        // Código que no se ejecuta al abrirlo (.js, .py, .ps1… sí lo harían)
        ".cs", ".csproj", ".sln", ".ts", ".tsx", ".jsx", ".css", ".scss", ".html", ".htm", ".java", ".c", ".cpp", ".h",
        ".hpp", ".go", ".rs", ".php", ".sql", ".kt", ".swift", ".dart", ".vue",
    };

    /// <summary>Decide sin tocar el disco (comprobar si existe ya conectaría con un servidor de red).</summary>
    public static ComoAbrir Decidir(string ruta, bool esCarpeta)
    {
        if (EsDeRed(ruta)) return ComoAbrir.Rechazar;
        if (esCarpeta) return ComoAbrir.Abrir;
        return Seguras.Contains(Path.GetExtension(ruta)) ? ComoAbrir.Abrir : ComoAbrir.Senalar;
    }

    /// <summary>\\servidor\…, //servidor/… o \\?\UNC\…</summary>
    public static bool EsDeRed(string ruta)
    {
        var r = ruta.Trim().Replace('/', '\\');
        return r.StartsWith(@"\\", StringComparison.Ordinal) && !r.StartsWith(@"\\?\", StringComparison.Ordinal)
               || r.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
    }
}
