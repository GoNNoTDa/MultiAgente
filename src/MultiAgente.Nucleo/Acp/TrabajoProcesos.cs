using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MultiAgente.Nucleo.Acp;

/// <summary>
/// Un «job» de Windows con un proceso y todo lo que lance (servidores MCP, órdenes que deja en marcha el agente…). Al
/// cerrarlo mueren todos; y si MultiAgente se cierra, se cuelga o lo matan, Windows lo cierra solo. Sin esto, al cerrar un
/// agente sus hijos se quedaban vivos. Solo para procesos de MultiAgente: lo que abre el usuario (autorizar, documentos)
/// no va aquí, que se cerraría con la aplicación.
/// </summary>
public sealed class TrabajoProcesos : IDisposable
{
    private IntPtr _job;

    private TrabajoProcesos(IntPtr job) => _job = job;

    /// <summary>Mete el proceso en un job nuevo. null si Windows no lo permite (se sigue sin él, como antes).</summary>
    public static TrabajoProcesos? Atar(Process proceso)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            Registro.Aviso($"No se pudo crear el job para el pid {proceso.Id} (error {Marshal.GetLastPInvokeError()})");
            return null;
        }
        var info = new LimiteExtendido { Basico = new LimiteBasico { Banderas = MatarAlCerrar } };
        if (!SetInformationJobObject(job, InformacionLimiteExtendido, ref info, (uint)Marshal.SizeOf<LimiteExtendido>()) ||
            !AssignProcessToJobObject(job, proceso.Handle))
        {
            Registro.Aviso($"No se pudo atar el pid {proceso.Id} a su job (error {Marshal.GetLastPInvokeError()})");
            CloseHandle(job);
            return null;
        }
        return new TrabajoProcesos(job);
    }

    /// <summary>Cierra el job: termina el proceso y todos sus descendientes que sigan vivos.</summary>
    public void Dispose()
    {
        var job = Interlocked.Exchange(ref _job, IntPtr.Zero);
        if (job != IntPtr.Zero) CloseHandle(job);
    }

    private const int InformacionLimiteExtendido = 9;       // JobObjectExtendedLimitInformation
    private const uint MatarAlCerrar = 0x2000;              // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE

    [StructLayout(LayoutKind.Sequential)]
    private struct LimiteBasico
    {
        public long TiempoUsuarioProceso, TiempoUsuarioJob;
        public uint Banderas;
        public UIntPtr MinimoMemoria, MaximoMemoria;
        public uint MaximoProcesos;
        public UIntPtr Afinidad;
        public uint Prioridad, Planificacion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ContadoresEs
    {
        public ulong Lecturas, Escrituras, Otras, BytesLeidos, BytesEscritos, BytesOtros;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LimiteExtendido
    {
        public LimiteBasico Basico;
        public ContadoresEs Es;
        public UIntPtr MemoriaProceso, MemoriaJob, PicoProceso, PicoJob;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr atributos, string? nombre);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int clase, ref LimiteExtendido info, uint largo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proceso);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
