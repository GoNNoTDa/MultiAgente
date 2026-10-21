using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MultiAgente.App;

/// <summary>Tema claro u oscuro de Windows (solo se lee) y barra de título a juego.</summary>
internal static class TemaWindows
{
    public static bool Oscuro()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return false; }
    }

    public static void BarraTitulo(IntPtr ventana, bool oscuro)
    {
        var valor = oscuro ? 1 : 0;
        try { _ = DwmSetWindowAttribute(ventana, DWMWA_USE_IMMERSIVE_DARK_MODE, ref valor, sizeof(int)); }
        catch (DllNotFoundException) { }
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int atributo, ref int valor, int tamano);
}
