namespace MultiAgente.Tests;

/// <summary>Carpeta temporal que se borra al terminar la prueba.</summary>
internal sealed class CarpetaPrueba : IDisposable
{
    public CarpetaPrueba(string tema = "prueba")
    {
        Ruta = Path.Combine(Path.GetTempPath(), $"multiagente_{tema}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Ruta);
    }

    public string Ruta { get; }

    public string Fichero(string relativa, string contenido = "")
    {
        var f = Path.Combine(Ruta, relativa);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, contenido);
        return f;
    }

    public void Dispose()
    {
        try { Directory.Delete(Ruta, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
