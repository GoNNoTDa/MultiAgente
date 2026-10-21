using System.Reflection;
using System.Text.Json;
using MultiAgente.Datos;
using MultiAgente.Nucleo;

namespace MultiAgente.App;

internal static class Program
{
    /// <summary>Versión de la aplicación (la del ensamblado, sin el sufijo de compilación).</summary>
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    /// <summary>
    /// Opciones de la línea de órdenes (para pruebas):
    /// <c>--comprobar [salida.json]</c> prepara la carpeta, abre la base y termina sin ventana;
    /// <c>--captura &lt;fichero.png&gt;</c> abre la ventana fuera de la pantalla, guarda una captura y termina;
    /// <c>--fuera</c> abre la ventana fuera de la pantalla y sin botón en la barra de tareas;
    /// <c>--cerrar</c> pide al MultiAgente abierto desde esta carpeta que se cierre (como con la cruz).
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var rutas = Rutas.Actual;
        try
        {
            rutas.CrearCarpetas();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"MultiAgente no puede escribir en su carpeta:\n{rutas.Raiz}\n\nCópiala a un sitio donde tengas permiso de escritura (por ejemplo, tu carpeta de usuario o C:\\MultiAgente).",
                            "MultiAgente", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        Registro.Iniciar(rutas.Logs);
        Registro.Info($"MultiAgente {Version} arranca en {rutas.Raiz}");

        var comprobar = Array.IndexOf(args, "--comprobar");
        var captura = Array.IndexOf(args, "--captura");
        string? Argumento(int i) => i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Fallo(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Registro.Error("Error no controlado", e.ExceptionObject as Exception);

        var cerrar = args.Contains("--cerrar");
        using var instancia = InstanciaUnica.Tomar(rutas.Raiz, pedirCierre: cerrar);
        if (instancia is null)
        {
            Registro.Info($"Ya hay un MultiAgente abierto desde esta carpeta: se le pide que {(cerrar ? "se cierre" : "se muestre")}");
            return comprobar >= 0 ? 2 : 0;
        }
        if (cerrar) return 0; // no había ninguno abierto

        BaseDatos bd;
        try
        {
            bd = BaseDatos.Abrir(rutas.BaseDatos);
        }
        catch (Exception ex)
        {
            Registro.Error("No se pudo abrir la base de datos", ex);
            if (comprobar < 0)
                MessageBox.Show($"No se pudo abrir los datos de MultiAgente ({rutas.BaseDatos}).\n\n{ex.Message}",
                                "MultiAgente", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        using (bd)
        {
            if (comprobar >= 0)
                return Comprobar(rutas, bd, Argumento(comprobar));

            using var ventana = new VentanaPrincipal(rutas, bd, Argumento(captura), args.Contains("--fuera"));
            instancia.MostrarPedido += ventana.MostrarDesdeOtraCopia;
            instancia.CierrePedido += ventana.CerrarDesdeOtraCopia;
            Application.Run(ventana);
        }
        Registro.Info("MultiAgente se cierra");
        return 0;
    }

    private static int Comprobar(Rutas rutas, BaseDatos bd, string? salida)
    {
        var requisitos = Requisitos.Comprobar();
        var resultado = new
        {
            version = Version,
            raiz = rutas.Raiz,
            datos = rutas.Datos,
            baseDatos = bd.Fichero,
            esquema = bd.Version,
            tablas = bd.Tablas(),
            ui = File.Exists(Path.Combine(rutas.Ui, "index.html")),
            requisitos = requisitos.Select(r => new { r.Id, r.Encontrado, r.Version }),
        };
        var json = JsonSerializer.Serialize(resultado, new JsonSerializerOptions { WriteIndented = true });
        Registro.Info("Comprobación: " + json.ReplaceLineEndings(" "));
        if (salida is not null) File.WriteAllText(salida, json);
        return resultado.ui ? 0 : 3;
    }

    private static void Fallo(Exception ex)
    {
        Registro.Error("Error no controlado en la interfaz", ex);
        MessageBox.Show("Algo ha fallado en MultiAgente. El detalle está en el registro (datos\\logs).",
                        "MultiAgente", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
