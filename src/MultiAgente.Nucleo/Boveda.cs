using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiAgente.Nucleo;

/// <summary>
/// Claves de API cifradas (AES-GCM) en <c>datos\boveda.bin</c>. La clave de cifrado está en <c>datos\boveda.clave</c>
/// (decisión D1): viaja con la carpeta, igual que los inicios de sesión de las CLIs. Protege de lecturas casuales y de
/// que las claves acaben en la base de datos o en el registro; no de quien tenga la carpeta entera.
/// </summary>
public sealed class Boveda
{
    private readonly string _fichero;
    private readonly string _ficheroClave;
    private readonly object _cerrojo = new();

    public Boveda(string fichero)
    {
        _fichero = fichero;
        _ficheroClave = Path.ChangeExtension(fichero, ".clave");
    }

    public void Guardar(string referencia, string secreto)
    {
        lock (_cerrojo)
        {
            var d = Leer();
            d[referencia] = secreto;
            Escribir(d);
        }
    }

    public string? Obtener(string referencia)
    {
        lock (_cerrojo) return Leer().GetValueOrDefault(referencia);
    }

    public bool Contiene(string referencia) => Obtener(referencia) is not null;

    public void Borrar(string referencia)
    {
        lock (_cerrojo)
        {
            var d = Leer();
            if (d.Remove(referencia)) Escribir(d);
        }
    }

    private byte[] Clave()
    {
        if (File.Exists(_ficheroClave))
        {
            var k = File.ReadAllBytes(_ficheroClave);
            if (k.Length == 32) return k;
            throw new ExcepcionUsuario("El fichero de la bóveda de claves (datos\\boveda.clave) está dañado.");
        }
        var nueva = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(_ficheroClave)!);
        EscribirAtomico(_ficheroClave, nueva);
        return nueva;
    }

    private Dictionary<string, string> Leer()
    {
        if (!File.Exists(_fichero)) return new();
        var datos = File.ReadAllBytes(_fichero);
        if (datos.Length < 28) throw new ExcepcionUsuario("La bóveda de claves (datos\\boveda.bin) está dañada.");
        var nonce = datos[..12];
        var etiqueta = datos[12..28];
        var cifrado = datos[28..];
        var claro = new byte[cifrado.Length];
        try
        {
            using var aes = new AesGcm(Clave(), 16);
            aes.Decrypt(nonce, cifrado, etiqueta, claro);
        }
        catch (CryptographicException ex)
        {
            throw new ExcepcionUsuario("No se pueden leer las claves guardadas: la bóveda no corresponde a su fichero de clave.", ex);
        }
        return JsonSerializer.Deserialize<Dictionary<string, string>>(claro) ?? new();
    }

    private void Escribir(Dictionary<string, string> d)
    {
        var claro = JsonSerializer.SerializeToUtf8Bytes(d);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var etiqueta = new byte[16];
        var cifrado = new byte[claro.Length];
        using (var aes = new AesGcm(Clave(), 16)) aes.Encrypt(nonce, claro, cifrado, etiqueta);
        CryptographicOperations.ZeroMemory(claro);
        EscribirAtomico(_fichero, [.. nonce, .. etiqueta, .. cifrado]);
    }

    private static void EscribirAtomico(string fichero, byte[] datos)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fichero)!);
        var tmp = fichero + ".tmp";
        File.WriteAllBytes(tmp, datos);
        File.Move(tmp, fichero, overwrite: true);
    }
}
