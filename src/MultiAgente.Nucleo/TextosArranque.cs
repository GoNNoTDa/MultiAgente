using System.Globalization;

namespace MultiAgente.Nucleo;

/// <summary>
/// Mensajes que se ven antes de que la interfaz esté abierta (o cuando falla del todo), en el idioma elegido en ella: la
/// interfaz lo guarda en datos\idioma.txt al abrirse; si aún no hay, el de Windows (catalán, castellano o, si no, inglés).
/// </summary>
public static class TextosArranque
{
    public static readonly IReadOnlyList<string> Idiomas = ["es", "en", "ca"];

    internal static readonly Dictionary<string, (string Es, string En, string Ca)> Textos = new()
    {
        ["sinPermiso"] = (
            "MultiAgente no puede escribir en su carpeta:\n{0}\n\nCópiala a un sitio donde tengas permiso de escritura (por ejemplo, tu carpeta de usuario o C:\\MultiAgente).",
            "MultiAgente can't write to its folder:\n{0}\n\nCopy it somewhere you have write permission (for example, your user folder or C:\\MultiAgente).",
            "MultiAgente no pot escriure a la seva carpeta:\n{0}\n\nCopia-la a un lloc on tinguis permís d'escriptura (per exemple, la teva carpeta d'usuari o C:\\MultiAgente)."),
        ["sinDatos"] = (
            "No se pudo abrir los datos de MultiAgente ({0}).\n\n{1}",
            "MultiAgente's data couldn't be opened ({0}).\n\n{1}",
            "No s'han pogut obrir les dades de MultiAgente ({0}).\n\n{1}"),
        ["fallo"] = (
            "Algo ha fallado en MultiAgente. El detalle está en el registro (datos\\logs).",
            "Something went wrong in MultiAgente. The details are in the log (datos\\logs).",
            "Alguna cosa ha fallat a MultiAgente. El detall és al registre (datos\\logs)."),
        ["sinWebView2"] = (
            "Para abrir MultiAgente hace falta WebView2 de Microsoft, que no está en este equipo.\n\nInstálalo desde https://developer.microsoft.com/microsoft-edge/webview2/ y vuelve a abrir MultiAgente.",
            "MultiAgente needs Microsoft WebView2, which is not on this PC.\n\nInstall it from https://developer.microsoft.com/microsoft-edge/webview2/ and open MultiAgente again.",
            "Per obrir MultiAgente cal el WebView2 de Microsoft, que no és en aquest ordinador.\n\nInstal·la'l des de https://developer.microsoft.com/microsoft-edge/webview2/ i torna a obrir MultiAgente."),
    };

    /// <summary>El idioma guardado por la interfaz o, si no hay (o no se puede leer), el de Windows.</summary>
    public static string Idioma(Rutas rutas)
    {
        try
        {
            if (File.Exists(rutas.Idioma) && File.ReadAllText(rutas.Idioma).Trim() is var g && Idiomas.Contains(g)) return g;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* el de Windows */ }
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch { "ca" => "ca", "es" => "es", _ => "en" };
    }

    /// <summary>Guarda el idioma de la interfaz (solo si cambia; sin permiso de escritura, no pasa nada).</summary>
    public static void GuardarIdioma(Rutas rutas, string? idioma)
    {
        if (idioma is null || !Idiomas.Contains(idioma)) return;
        try
        {
            if (!File.Exists(rutas.Idioma) || File.ReadAllText(rutas.Idioma).Trim() != idioma) File.WriteAllText(rutas.Idioma, idioma);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Registro.Aviso($"No se pudo guardar el idioma en {rutas.Idioma}: {ex.Message}");
        }
    }

    public static string Texto(Rutas rutas, string clave, params object[] datos)
    {
        var (es, en, ca) = Textos[clave];
        return string.Format(Idioma(rutas) switch { "en" => en, "ca" => ca, _ => es }, datos);
    }
}
