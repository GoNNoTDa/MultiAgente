using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiAgente.Tests;

// La interfaz está en castellano, inglés y catalán: cada texto de app.js (trad('…')) y del catálogo tiene que estar en los
// dos diccionarios, con los mismos datos ({0}, {1}…) que el original.
public partial class IdiomasTests
{
    static string CarpetaUi() => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(CatalogoTests.FicheroCatalogo()))!, "ui");

    static Dictionary<string, string> Diccionario(string idioma)
    {
        var js = File.ReadAllText(Path.Combine(CarpetaUi(), "idiomas", idioma + ".js"));
        var inicio = js.IndexOf("= {") + 2;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(js[inicio..(js.LastIndexOf('}') + 1)])!;
    }

    static IEnumerable<string> TextosDeAppJs()
    {
        var js = File.ReadAllText(Path.Combine(CarpetaUi(), "app.js"));
        foreach (Match m in Trad().Matches(js))
            yield return Regex.Replace(m.Groups[1].Value, @"\\(.)", e => e.Groups[1].Value == "n" ? "\n" : e.Groups[1].Value);
    }

    static string Datos(string texto) => string.Join(",", Dato().Matches(texto).Select(m => m.Value).Order());

    [Theory]
    [InlineData("en")]
    [InlineData("ca")]
    public void Cada_texto_de_la_interfaz_esta_traducido(string idioma)
    {
        var d = Diccionario(idioma);
        var faltan = TextosDeAppJs().Distinct().Where(t => !d.ContainsKey(t)).ToList();
        Assert.True(faltan.Count == 0, $"Sin traducir al {idioma}: " + string.Join(" | ", faltan));
        var catalogo = MultiAgente.Nucleo.CatalogoProveedores.Cargar(CatalogoTests.FicheroCatalogo());
        var notas = catalogo.Proveedores.Select(p => p.Notas).Where(n => !string.IsNullOrEmpty(n)).Where(n => !d.ContainsKey(n!)).ToList();
        Assert.True(notas.Count == 0, $"Notas del catálogo sin traducir al {idioma}: " + string.Join(" | ", notas));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ca")]
    public void Las_traducciones_llevan_los_mismos_datos(string idioma)
    {
        var mal = Diccionario(idioma).Where(kv => Datos(kv.Key) != Datos(kv.Value)).Select(kv => kv.Key).ToList();
        Assert.True(mal.Count == 0, $"Datos distintos en {idioma}: " + string.Join(" | ", mal));
    }

    [GeneratedRegex(@"trad\('((?:[^'\\]|\\.)*)'")]
    private static partial Regex Trad();

    [GeneratedRegex(@"\{\d\}")]
    private static partial Regex Dato();
}
