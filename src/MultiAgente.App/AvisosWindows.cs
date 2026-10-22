using System.Runtime.InteropServices;

namespace MultiAgente.App;

/// <summary>
/// Avisos cuando MultiAgente no está delante: notificación de Windows (con un icono en la bandeja, que no escribe nada en el
/// registro ni hace falta instalar) y el botón de la barra de tareas parpadeando hasta que se vuelve a la ventana.
/// </summary>
internal sealed class AvisosWindows(Form ventana, Action<string> alPulsar) : IDisposable
{
    private NotifyIcon? _bandeja;
    private string? _sesion;

    public void Mostrar(string titulo, string texto, string? sesionId)
    {
        Parpadear();
        if (_bandeja is null)
        {
            _bandeja = new NotifyIcon { Icon = ventana.Icon, Text = "MultiAgente" };
            _bandeja.BalloonTipClicked += (_, _) => Abrir();
            _bandeja.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Abrir(); };
        }
        _sesion = sesionId;
        _bandeja.Visible = true;
        _bandeja.ShowBalloonTip(6000, Recortar(titulo, 60), Recortar(texto, 200), ToolTipIcon.None);
    }

    private void Abrir()
    {
        if (ventana.IsDisposed) return;
        if (ventana.WindowState == FormWindowState.Minimized) ventana.WindowState = FormWindowState.Normal;
        ventana.Activate();
        if (_sesion is not null) alPulsar(_sesion);
    }

    private static string Recortar(string t, int max) =>
        string.IsNullOrWhiteSpace(t) ? " " : t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";

    private void Parpadear()
    {
        var info = new FlashInfo
        {
            Tamano = (uint)Marshal.SizeOf<FlashInfo>(), Ventana = ventana.Handle,
            Banderas = FlashwTray | FlashwTimerNoFg, Veces = 0, Ritmo = 0,
        };
        try { FlashWindowEx(ref info); } catch (EntryPointNotFoundException) { }
    }

    public void Dispose()
    {
        if (_bandeja is null) return;
        _bandeja.Visible = false; // si no, el icono se queda en la bandeja hasta pasar el ratón
        _bandeja.Dispose();
        _bandeja = null;
    }

    private const uint FlashwTray = 0x2;          // FLASHW_TRAY: el botón de la barra de tareas
    private const uint FlashwTimerNoFg = 0xC;     // FLASHW_TIMERNOFG: hasta que la ventana vuelve delante

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Tamano;
        public IntPtr Ventana;
        public uint Banderas, Veces, Ritmo;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
