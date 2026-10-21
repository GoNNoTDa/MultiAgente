using System.Globalization;
using System.Text.RegularExpressions;
using MultiAgente.Nucleo;

namespace MultiAgente.Tests;

public class TextosArranqueTests
{
    [Fact]
    public void Usa_el_idioma_que_guarda_la_interfaz_y_si_no_el_de_Windows()
    {
        using var c = new CarpetaPrueba("idioma");
        var rutas = new Rutas(c.Ruta);
        rutas.CrearCarpetas();
        var cultura = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("ca-ES");
            Assert.Equal("ca", TextosArranque.Idioma(rutas));
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            Assert.Equal("en", TextosArranque.Idioma(rutas));
            Assert.StartsWith("MultiAgente can't write", TextosArranque.Texto(rutas, "sinPermiso", c.Ruta));

            TextosArranque.GuardarIdioma(rutas, "es");
            Assert.Equal("es", TextosArranque.Idioma(rutas));
            Assert.Contains(c.Ruta, TextosArranque.Texto(rutas, "sinPermiso", c.Ruta));
            Assert.StartsWith("MultiAgente no puede escribir", TextosArranque.Texto(rutas, "sinPermiso", c.Ruta));

            TextosArranque.GuardarIdioma(rutas, "fr"); // no es de la interfaz: se ignora
            TextosArranque.GuardarIdioma(rutas, null);
            Assert.Equal("es", TextosArranque.Idioma(rutas));

            File.WriteAllText(rutas.Idioma, "basura");
            Assert.Equal("en", TextosArranque.Idioma(rutas));
        }
        finally { CultureInfo.CurrentUICulture = cultura; }
    }

    [Fact]
    public void Cada_mensaje_esta_en_los_tres_idiomas_con_los_mismos_datos()
    {
        static string Datos(string t) => string.Join(",", Regex.Matches(t, @"\{\d\}").Select(m => m.Value).Order());
        foreach (var (clave, (es, en, ca)) in TextosArranque.Textos)
        {
            Assert.False(string.IsNullOrWhiteSpace(en) || string.IsNullOrWhiteSpace(ca), clave);
            Assert.NotEqual(es, en);
            Assert.Equal(Datos(es), Datos(en));
            Assert.Equal(Datos(es), Datos(ca));
        }
    }
}
