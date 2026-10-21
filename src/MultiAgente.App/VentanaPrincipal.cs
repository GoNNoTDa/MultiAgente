using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using MultiAgente.Datos;
using MultiAgente.Nucleo;

namespace MultiAgente.App;

/// <summary>
/// La única ventana: un WebView2 a pantalla completa con la interfaz de <c>ui\</c>. Todo lo visible es HTML; esta
/// clase solo aloja el navegador, guarda la posición y pasa mensajes al <see cref="Puente"/>.
/// </summary>
public partial class VentanaPrincipal : Form
{
    /// <summary>Nombre de host con el que se sirve la carpeta ui\ (nunca sale a la red).</summary>
    public const string Host = "app.multiagente";

    /// <summary>Host con el que se ven las capturas y adjuntos guardados en datosdjuntos.</summary>
    public const string HostAdjuntos = "adjuntos.multiagente";

    private readonly Rutas _rutas;
    private readonly BaseDatos _bd;
    private readonly string? _captura;
    private readonly bool _fuera;
    private Puente? _puente;
    private Gestor? _servicio;
    private CoreWebView2Environment? _entorno;
    private bool _cerrando;
    private int _cerrada;

    /// <param name="captura">Guardar una captura PNG en este fichero y cerrar (pruebas).</param>
    /// <param name="fueraDePantalla">Abrir fuera de la pantalla y sin botón en la barra de tareas (pruebas).</param>
    public VentanaPrincipal(Rutas rutas, BaseDatos bd, string? captura = null, bool fueraDePantalla = false)
    {
        _rutas = rutas;
        _bd = bd;
        _captura = captura;
        _fuera = fueraDePantalla || captura is not null;
        InitializeComponent();
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        var oscuro = TemaWindows.Oscuro();
        BackColor = oscuro ? Color.FromArgb(0x1e, 0x1d, 0x1b) : Color.FromArgb(0xfb, 0xfa, 0xf8);
        web.DefaultBackgroundColor = BackColor;
        if (!_fuera) RestaurarPosicion();
        else
        {
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Bounds = new Rectangle(-32000, -32000, 1280, 800);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TemaWindows.BarraTitulo(Handle, TemaWindows.Oscuro());
    }

    private async void VentanaPrincipal_Load(object? sender, EventArgs e)
    {
        try
        {
            // Perfil del navegador dentro de la carpeta portátil (datos\webview)
            _entorno = await CoreWebView2Environment.CreateAsync(null, _rutas.WebView);
            await web.EnsureCoreWebView2Async(_entorno);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            Registro.Error("Falta el runtime de WebView2", ex);
            MessageBox.Show("Para abrir MultiAgente hace falta WebView2 de Microsoft, que no está en este equipo.\n\n" +
                            "Instálalo desde https://developer.microsoft.com/microsoft-edge/webview2/ y vuelve a abrir MultiAgente.",
                            "MultiAgente", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
            return;
        }

        var core = web.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(Host, _rutas.Ui, CoreWebView2HostResourceAccessKind.Deny);
        Directory.CreateDirectory(_rutas.Adjuntos);
        core.SetVirtualHostNameToFolderMapping(HostAdjuntos, _rutas.Adjuntos, CoreWebView2HostResourceAccessKind.Allow);
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = Debugger.IsAttached;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.IsZoomControlEnabled = false;
        core.NavigationStarting += (_, a) =>
        {
            if (Uri.TryCreate(a.Uri, UriKind.Absolute, out var uri) && uri.Host == Host) return;
            a.Cancel = true;
            AbrirFuera(a.Uri);
        };
        core.NewWindowRequested += (_, a) => { a.Handled = true; AbrirFuera(a.Uri); };

        _servicio = new Gestor(_rutas, new Repositorio(_bd), Enviar, a => { if (!IsDisposed) BeginInvoke(a); }, Program.Version);
        _puente = new Puente(this, _rutas, _servicio);
        core.WebMessageReceived += (_, a) =>
        {
            // Ficheros soltados en la ventana: llegan con su ruta real como objetos adicionales
            var soltados = new List<string>();
            if (a.AdditionalObjects is { } objetos)
                foreach (var o in objetos)
                    if (o is CoreWebView2File f && !string.IsNullOrEmpty(f.Path)) soltados.Add(f.Path);
            _puente.Recibir(a.WebMessageAsJson, soltados);
        };
        core.Navigate($"https://{Host}/index.html");
    }

    /// <summary>Manda un mensaje a la interfaz (desde el hilo de la ventana).</summary>
    internal void Enviar(object mensaje)
    {
        if (IsDisposed || web.CoreWebView2 is null) return;
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(mensaje, Puente.Json));
    }

    /// <summary>La interfaz ya ha pintado su primer estado.</summary>
    internal async void InterfazLista()
    {
        if (_captura is null) return;
        try
        {
            await Task.Delay(800);
            await using (var f = File.Create(_captura))
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, f);
            Registro.Info($"Captura guardada en {_captura}");
        }
        catch (Exception ex) { Registro.Error("No se pudo hacer la captura", ex); }
        Close();
    }

    /// <summary>Otra copia de la misma carpeta pide mostrarse (llega desde un hilo de fondo).</summary>
    internal void MostrarDesdeOtraCopia()
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            Registro.Info("Otra copia de esta carpeta pide mostrar la ventana");
            if (_fuera) return;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
        });
    }

    /// <summary>Otra copia de la misma carpeta pide cerrar (<c>--cerrar</c>; llega desde un hilo de fondo).</summary>
    internal void CerrarDesdeOtraCopia()
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            Registro.Info("Otra copia de esta carpeta pide cerrar MultiAgente");
            Close();
        });
    }

    private static void AbrirFuera(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        else
            Registro.Aviso($"Navegación bloqueada: {url}");
    }

    private sealed record Posicion(int X, int Y, int Ancho, int Alto, bool Maximizada);

    private void RestaurarPosicion()
    {
        try
        {
            var json = _bd.LeerAjuste("ventana");
            if (json is null) return;
            var p = JsonSerializer.Deserialize<Posicion>(json);
            if (p is null) return;
            var r = new Rectangle(p.X, p.Y, Math.Max(p.Ancho, MinimumSize.Width), Math.Max(p.Alto, MinimumSize.Height));
            // Solo si se ve en alguna pantalla (en otro PC puede no existir ese monitor)
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) return;
            StartPosition = FormStartPosition.Manual;
            Bounds = r;
            if (p.Maximizada) WindowState = FormWindowState.Maximized;
        }
        catch (JsonException) { }
    }

    private void VentanaPrincipal_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_cerrando) return; // segunda vuelta: el navegador ya ha soltado datos\webview
        if (!_fuera)
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _bd.GuardarAjuste("ventana", JsonSerializer.Serialize(new Posicion(r.X, r.Y, r.Width, r.Height, WindowState == FormWindowState.Maximized)));
        }

        // Se cierran los agentes y se espera a que terminen los procesos del navegador (siguen unos segundos con
        // datos\webview abierto) para que, al cerrar MultiAgente, la carpeta se pueda copiar o mover en seguida.
        e.Cancel = true;
        _cerrando = true;
        Hide();
        _ = CerrarTodo();
    }

    private async Task CerrarTodo()
    {
        if (_servicio is not null)
        {
            try { await _servicio.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (Exception ex) { Registro.Error("Al cerrar los agentes", ex); }
        }
        if (_entorno is null || IsDisposed) { CerrarYa(); return; }
        _entorno.BrowserProcessExited += (_, _) => CerrarYa();
        web.Dispose();
        _ = Task.Delay(5000).ContinueWith(_ => CerrarYa(), TaskScheduler.Default);
    }

    private void CerrarYa()
    {
        if (Interlocked.Exchange(ref _cerrada, 1) == 1 || IsDisposed) return;
        try { BeginInvoke(Close); }
        catch (InvalidOperationException) { }
    }
}
