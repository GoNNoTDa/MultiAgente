using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MultiAgente.Nucleo;

/// <summary>Cómo pedir a un proveedor su lista de modelos.</summary>
public sealed record ListaModelos(string Url, string Auth, string Lista, string Id, string? Nombre);

/// <summary>Un proveedor de API del catálogo (<c>catalogo\proveedores.json</c>, editable sin recompilar).</summary>
public sealed record EntradaCatalogo
{
    public string Id { get; init; } = "";
    public string Nombre { get; init; } = "";
    /// <summary>Agente que lo usa: Claude (API compatible con Anthropic) o Codex (API compatible con OpenAI Responses).</summary>
    public string Agente { get; init; } = "Claude";
    /// <summary>null = la API del propio proveedor del agente (Anthropic u OpenAI).</summary>
    public string? BaseUrl { get; init; }
    public string? ClaveUrl { get; init; }
    public bool SinClave { get; init; }
    /// <summary>Valor que se manda como clave cuando el servicio no la pide (Ollama, LM Studio).</summary>
    public string? ClaveFija { get; init; }
    public ListaModelos? Listar { get; init; }
    public List<string> Modelos { get; init; } = [];
    /// <summary>Solo funcionan los modelos de la lista (p. ej. NVIDIA con Codex: los demás no tienen la API Responses).</summary>
    public bool SoloModelos { get; init; }
    /// <summary>
    /// El proveedor solo tiene la API de OpenAI (Chat Completions): Claude Code le habla a través del traductor local de
    /// MultiAgente (<see cref="Traductor"/>).
    /// </summary>
    public bool Traducir { get; init; }
    public Dictionary<string, string> Entorno { get; init; } = [];
    public string? Notas { get; init; }
    public string? Confianza { get; init; }
    public List<string> Fuentes { get; init; } = [];
}

/// <summary>Catálogo de proveedores por API y lo necesario para usarlos con Claude Code o con Codex.</summary>
public sealed class CatalogoProveedores
{
    private sealed record Fichero(string? Version, List<EntradaCatalogo> Proveedores);

    private static readonly JsonSerializerOptions Lectura = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public CatalogoProveedores(IReadOnlyList<EntradaCatalogo> proveedores, string? version = null)
    {
        Proveedores = proveedores;
        Version = version;
    }

    public string? Version { get; }
    public IReadOnlyList<EntradaCatalogo> Proveedores { get; }

    public EntradaCatalogo? Buscar(string? id) => Proveedores.FirstOrDefault(p => p.Id == id);

    /// <summary>Lee el catálogo; si falta o está dañado, devuelve uno vacío (y lo apunta en el registro).</summary>
    public static CatalogoProveedores Cargar(string fichero)
    {
        try
        {
            var f = JsonSerializer.Deserialize<Fichero>(File.ReadAllText(fichero), Lectura);
            return new CatalogoProveedores(f?.Proveedores ?? [], f?.Version);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Registro.Error($"No se pudo leer el catálogo de proveedores {fichero}", ex);
            return new CatalogoProveedores([]);
        }
    }

    /// <summary>
    /// Pide al proveedor su lista de modelos con la clave. Devuelve los identificadores (y el nombre si lo da),
    /// ordenados. Lanza <see cref="ExcepcionUsuario"/> con un mensaje llano si falla.
    /// </summary>
    public static async Task<IReadOnlyList<(string Id, string? Nombre)>> ListarModelos(
        ListaModelos lista, string? baseUrl, string? clave, HttpMessageHandler? manejador = null, CancellationToken ct = default)
    {
        var url = lista.Url.Replace("{base}", (baseUrl ?? "").TrimEnd('/'));
        using var http = manejador is null ? new HttpClient() : new HttpClient(manejador, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(20);
        using var pet = new HttpRequestMessage(HttpMethod.Get, url);
        switch (lista.Auth)
        {
            case "x-api-key":
                pet.Headers.Add("x-api-key", clave ?? "");
                pet.Headers.Add("anthropic-version", "2023-06-01");
                break;
            case "bearer":
                pet.Headers.Authorization = new AuthenticationHeaderValue("Bearer", clave ?? "");
                break;
        }
        HttpResponseMessage r;
        try { r = await http.SendAsync(pet, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ExcepcionUsuario($"No se pudo conectar con {new Uri(url).Host}. Comprueba la dirección y la conexión.", ex);
        }
        using (r)
        {
            if (r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ExcepcionUsuario("El proveedor no acepta la clave de API (no es válida o no tiene permiso).");
            if (!r.IsSuccessStatusCode)
                throw new ExcepcionUsuario($"El proveedor no ha dado la lista de modelos (error {(int)r.StatusCode}).");
            var json = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
            var items = (lista.Lista == "" ? json : json?[lista.Lista]) as JsonArray ?? [];
            return items
                .Select(i => (Id: i?[lista.Id]?.ToString(), Nombre: lista.Nombre is null ? null : i?[lista.Nombre]?.ToString()))
                .Where(m => !string.IsNullOrEmpty(m.Id))
                .Select(m => (m.Id!, m.Nombre))
                .DistinctBy(m => m.Item1)
                .OrderBy(m => m.Item1, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// <c>config.toml</c> de Codex para una cuenta con API compatible: se escribe en la carpeta de la cuenta
    /// (<c>CODEX_HOME</c>), nunca en la del usuario. La clave va por la variable <c>MULTIAGENTE_API_KEY</c>.
    /// </summary>
    public static string ConfigCodex(string nombre, string baseUrl, string? modelo, bool sinClave)
    {
        static string T(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("# Generado por MultiAgente para esta cuenta: se rehace al arrancar (los cambios a mano se pierden).");
        if (!string.IsNullOrWhiteSpace(modelo)) sb.AppendLine($"model = {T(modelo)}");
        sb.AppendLine("model_provider = \"multiagente\"");
        sb.AppendLine();
        sb.AppendLine("[model_providers.multiagente]");
        sb.AppendLine($"name = {T(nombre)}");
        sb.AppendLine($"base_url = {T(baseUrl)}");
        if (!sinClave) sb.AppendLine("env_key = \"MULTIAGENTE_API_KEY\"");
        sb.AppendLine("wire_api = \"responses\"");
        return sb.ToString();
    }
}
