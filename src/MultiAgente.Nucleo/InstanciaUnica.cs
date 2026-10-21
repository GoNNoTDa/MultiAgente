using System.Security.Cryptography;
using System.Text;

namespace MultiAgente.Nucleo;

/// <summary>
/// Una sola instancia por carpeta portátil (dos copias en el mismo PC conviven). La segunda que se abre pide a la
/// primera que se muestre (o que se cierre, con <c>--cerrar</c>) y termina.
/// </summary>
public sealed class InstanciaUnica : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _mostrar;
    private readonly EventWaitHandle _cerrar;
    private readonly Thread _vigia;
    private volatile bool _terminando;

    private InstanciaUnica(Mutex mutex, EventWaitHandle mostrar, EventWaitHandle cerrar)
    {
        _mutex = mutex;
        _mostrar = mostrar;
        _cerrar = cerrar;
        _vigia = new Thread(Vigilar) { IsBackground = true, Name = "InstanciaUnica" };
        _vigia.Start();
    }

    /// <summary>Otra copia de la misma carpeta pide mostrarse (llega en un hilo de fondo).</summary>
    public event Action? MostrarPedido;

    /// <summary>Otra copia de la misma carpeta pide cerrar esta (llega en un hilo de fondo).</summary>
    public event Action? CierrePedido;

    /// <summary>Nombre de los objetos de sincronización para una carpeta: igual sin importar mayúsculas ni la barra final.</summary>
    public static string Nombre(string raiz)
    {
        var normal = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raiz)).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normal)))[..16];
        return $"MultiAgente.{hash}";
    }

    /// <summary>
    /// Toma la instancia de la carpeta. Si ya hay otra abierta, le pide que se muestre (o que se cierre, si
    /// <paramref name="pedirCierre"/>) y devuelve null.
    /// </summary>
    public static InstanciaUnica? Tomar(string raiz, bool pedirCierre = false)
    {
        var nombre = Nombre(raiz);
        var mutex = new Mutex(true, $@"Local\{nombre}", out var nueva);
        if (!nueva)
        {
            mutex.Dispose();
            try
            {
                using var otra = EventWaitHandle.OpenExisting($@"Local\{nombre}.{(pedirCierre ? "Cerrar" : "Mostrar")}");
                otra.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return null;
        }
        return new InstanciaUnica(mutex,
            new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{nombre}.Mostrar"),
            new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{nombre}.Cerrar"));
    }

    private void Vigilar()
    {
        var esperas = new WaitHandle[] { _mostrar, _cerrar };
        while (true)
        {
            int cual;
            try { cual = WaitHandle.WaitAny(esperas); }
            catch (ObjectDisposedException) { return; }
            if (_terminando) return;
            try { (cual == 0 ? MostrarPedido : CierrePedido)?.Invoke(); }
            catch (Exception ex) { Registro.Error("Al atender la petición de otra copia", ex); }
        }
    }

    public void Dispose()
    {
        if (_terminando) return;
        _terminando = true;
        _mostrar.Set();
        _vigia.Join(1000);
        _mostrar.Dispose();
        _cerrar.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
