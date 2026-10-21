using MultiAgente.Datos;

namespace MultiAgente.Tests;

public class BaseDatosTests
{
    [Fact]
    public void Crea_el_esquema_completo()
    {
        using var c = new CarpetaPrueba("bd");
        using var bd = BaseDatos.Abrir(Path.Combine(c.Ruta, "datos", "multiagente.db"));
        Assert.Equal(BaseDatos.VersionEsquema, bd.Version);
        Assert.Equal(
            new[] { "Ajuste", "Consumo", "Cuenta", "Equipo", "EquipoCuenta", "Proyecto", "Sesion", "Tramo", "UsoTurno" },
            bd.Tablas());
    }

    [Fact]
    public void Abrir_otra_vez_no_repite_el_esquema_y_conserva_los_datos()
    {
        using var c = new CarpetaPrueba("bd");
        var f = Path.Combine(c.Ruta, "multiagente.db");
        using (var bd = BaseDatos.Abrir(f)) bd.GuardarAjuste("tema", "oscuro");
        using (var bd = BaseDatos.Abrir(f))
        {
            Assert.Equal(BaseDatos.VersionEsquema, bd.Version);
            Assert.Equal("oscuro", bd.LeerAjuste("tema"));
        }
    }

    [Fact]
    public void Ajustes_se_guardan_cambian_y_se_borran()
    {
        using var c = new CarpetaPrueba("bd");
        using var bd = BaseDatos.Abrir(Path.Combine(c.Ruta, "multiagente.db"));
        Assert.Null(bd.LeerAjuste("ventana"));
        bd.GuardarAjuste("ventana", "{\"x\":1}");
        bd.GuardarAjuste("ventana", "{\"x\":2}");
        Assert.Equal("{\"x\":2}", bd.LeerAjuste("ventana"));
        bd.GuardarAjuste("ventana", null);
        Assert.Null(bd.LeerAjuste("ventana"));
    }

    [Fact]
    public void Al_cerrar_suelta_el_fichero()
    {
        using var c = new CarpetaPrueba("bd");
        var f = Path.Combine(c.Ruta, "multiagente.db");
        using (var bd = BaseDatos.Abrir(f)) bd.GuardarAjuste("a", "b");
        File.Move(f, f + ".copia"); // fallaría si siguiera abierto
        Assert.True(File.Exists(f + ".copia"));
    }
}
