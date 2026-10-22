using MultiAgente.Nucleo;

namespace MultiAgente.Tests;

/// <summary>Las rutas que pulsa el usuario vienen del agente: solo se abre lo seguro y nunca una ruta de red.</summary>
public class AperturaSeguraTests
{
    [Theory]
    [InlineData(@"C:\proyecto\README.md", ComoAbrir.Abrir)]
    [InlineData(@"C:\proyecto\informe.PDF", ComoAbrir.Abrir)]
    [InlineData(@"C:\proyecto\src\Program.cs", ComoAbrir.Abrir)]
    [InlineData(@"C:\proyecto\captura.png", ComoAbrir.Abrir)]
    [InlineData(@"C:\proyecto\setup.exe", ComoAbrir.Senalar)]
    [InlineData(@"C:\proyecto\informe.url", ComoAbrir.Senalar)]
    [InlineData(@"C:\proyecto\ayuda.chm", ComoAbrir.Senalar)]
    [InlineData(@"C:\proyecto\presupuesto.docm", ComoAbrir.Senalar)]
    [InlineData(@"C:\proyecto\x.settingcontent-ms", ComoAbrir.Senalar)]
    [InlineData(@"C:\proyecto\script.py", ComoAbrir.Senalar)]
    [InlineData(@"C:\proyecto\script.js", ComoAbrir.Senalar)]
    [InlineData(@"C:\proyecto\Makefile", ComoAbrir.Senalar)]
    [InlineData(@"\\servidor\compartida\informe.pdf", ComoAbrir.Rechazar)]
    [InlineData("//servidor/compartida/informe.pdf", ComoAbrir.Rechazar)]
    [InlineData(@"\\?\UNC\servidor\compartida\informe.pdf", ComoAbrir.Rechazar)]
    [InlineData(@"\\.\pipe\algo", ComoAbrir.Rechazar)]
    [InlineData(@"\\?\C:\proyecto\README.md", ComoAbrir.Abrir)]
    public void Decide_que_hacer_con_un_fichero(string ruta, ComoAbrir esperado) =>
        Assert.Equal(esperado, AperturaSegura.Decidir(ruta, esCarpeta: false));

    [Fact]
    public void Una_carpeta_local_se_abre_y_una_de_red_no()
    {
        Assert.Equal(ComoAbrir.Abrir, AperturaSegura.Decidir(@"C:\proyecto", esCarpeta: true));
        Assert.Equal(ComoAbrir.Rechazar, AperturaSegura.Decidir(@"\\servidor\compartida", esCarpeta: true));
    }
}
