using System.Net;
using MultiAgente.Nucleo;

namespace MultiAgente.Tests;

public class CatalogoTests
{
    internal static string FicheroCatalogo()
    {
        var aqui = new DirectoryInfo(AppContext.BaseDirectory);
        while (aqui is not null && !File.Exists(Path.Combine(aqui.FullName, "MultiAgente.sln"))) aqui = aqui.Parent;
        Assert.NotNull(aqui);
        return Path.Combine(aqui!.FullName, "src", "MultiAgente.App", "catalogo", "proveedores.json");
    }

    [Fact]
    public void El_catalogo_publicado_es_valido()
    {
        var c = CatalogoProveedores.Cargar(FicheroCatalogo());
        Assert.True(c.Proveedores.Count >= 20, $"solo {c.Proveedores.Count} proveedores");
        Assert.NotNull(c.Version);
        Assert.Equal(c.Proveedores.Count, c.Proveedores.Select(p => p.Id).Distinct().Count());
        foreach (var p in c.Proveedores)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Nombre), p.Id);
            Assert.Contains(p.Agente, new[] { "Claude", "Codex" });
            // Claude Code añade /v1/messages: la dirección no debe llevar /v1 al final (salvo si va por el traductor: es la de
            // OpenAI, a la que el traductor añade /chat/completions)
            if (p.Agente == "Claude" && p.BaseUrl is not null && !p.Traducir) Assert.False(p.BaseUrl.TrimEnd('/').EndsWith("/v1"), p.Id);
            if (p.Traducir) Assert.Equal("Claude", p.Agente);
            if (p.Agente == "Codex") Assert.NotNull(p.BaseUrl);
            if (p.SinClave) Assert.NotNull(p.ClaveFija);
            Assert.True(p.Listar is not null || p.Modelos.Count > 0 || p.Id == "litellm", $"{p.Id}: sin modelos ni forma de listarlos");
        }
        Assert.Contains(c.Proveedores, p => p.Id == "zai" && p.BaseUrl == "https://api.z.ai/api/anthropic");
        Assert.Equal("glm-5.3-flash[1m]", c.Buscar("zai")!.Entorno["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
    }

    [Fact]
    public void Un_catalogo_danado_no_rompe_nada()
    {
        using var c = new CarpetaPrueba("catalogo");
        var f = c.Fichero("proveedores.json", "{ esto no es json");
        Assert.Empty(CatalogoProveedores.Cargar(f).Proveedores);
        Assert.Empty(CatalogoProveedores.Cargar(Path.Combine(c.Ruta, "no-existe.json")).Proveedores);
    }

    private sealed class Falso(HttpStatusCode codigo, string cuerpo) : HttpMessageHandler
    {
        public HttpRequestMessage? Peticion { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Peticion = request;
            return Task.FromResult(new HttpResponseMessage(codigo) { Content = new StringContent(cuerpo) });
        }
    }

    [Fact]
    public async Task Lista_los_modelos_con_cada_forma_de_autenticacion()
    {
        var anthropic = new Falso(HttpStatusCode.OK, """{ "data": [ { "id": "claude-sonnet-5-5", "display_name": "Sonnet 5.5" }, { "id": "claude-haiku-5-5", "display_name": "Haiku 5.5" } ] }""");
        var m = await CatalogoProveedores.ListarModelos(new ListaModelos("https://api.anthropic.com/v1/models", "x-api-key", "data", "id", "display_name"), null, "k1", anthropic);
        Assert.Equal(["claude-haiku-5-5", "claude-sonnet-5-5"], m.Select(x => x.Id));
        Assert.Equal("Sonnet 5.5", m[1].Nombre);
        Assert.Equal("k1", anthropic.Peticion!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", anthropic.Peticion.Headers.GetValues("anthropic-version").Single());

        var fireworks = new Falso(HttpStatusCode.OK, """{ "models": [ { "name": "glm-latest", "displayName": "GLM" } ] }""");
        m = await CatalogoProveedores.ListarModelos(new ListaModelos("{base}/v1/accounts/x/models", "bearer", "models", "name", "displayName"), "https://api.fw.ai/", "k2", fireworks);
        Assert.Equal("glm-latest", Assert.Single(m).Id);
        Assert.Equal("https://api.fw.ai/v1/accounts/x/models", fireworks.Peticion!.RequestUri!.ToString());
        Assert.Equal("Bearer k2", fireworks.Peticion.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Clave_mala_o_sin_conexion_da_un_mensaje_llano()
    {
        var ex = await Assert.ThrowsAsync<ExcepcionUsuario>(() => CatalogoProveedores.ListarModelos(
            new ListaModelos("https://x/models", "bearer", "data", "id", null), null, "mala", new Falso(HttpStatusCode.Unauthorized, "{}")));
        Assert.Contains("no acepta la clave", ex.Message);

        ex = await Assert.ThrowsAsync<ExcepcionUsuario>(() => CatalogoProveedores.ListarModelos(
            new ListaModelos("http://127.0.0.1:9/v1/models", "ninguna", "data", "id", null), null, null));
        Assert.Contains("No se pudo conectar", ex.Message);
    }

    [Fact]
    public void Config_de_codex_para_una_api_compatible()
    {
        var toml = CatalogoProveedores.ConfigCodex("Kimi", "https://api.moonshot.ai/v1", "kimi-k3", sinClave: false);
        Assert.Contains("model = \"kimi-k3\"", toml);
        Assert.Contains("model_provider = \"multiagente\"", toml);
        Assert.Contains("[model_providers.multiagente]", toml);
        Assert.Contains("base_url = \"https://api.moonshot.ai/v1\"", toml);
        Assert.Contains("env_key = \"MULTIAGENTE_API_KEY\"", toml);
        Assert.Contains("wire_api = \"responses\"", toml);

        var local = CatalogoProveedores.ConfigCodex("Ollama", "http://localhost:11434/v1", null, sinClave: true);
        Assert.DoesNotContain("env_key", local);
        Assert.DoesNotContain("model =", local);
    }
}
