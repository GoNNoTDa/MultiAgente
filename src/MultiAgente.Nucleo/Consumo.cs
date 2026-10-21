using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MultiAgente.Nucleo;

/// <summary>
/// Consumo de una cuenta de suscripción de Claude: el mismo dato que «/usage», del servicio de uso de Anthropic con el
/// token de la propia cuenta (se lee de su <c>.credentials.json</c> en memoria; nunca se guarda ni va al registro). Es un
/// servicio no documentado: si falla, el relevo sigue funcionando por los errores de límite.
/// </summary>
public static class ConsumoClaude
{
    public const string Url = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>Token de acceso de la cuenta, o null si no hay sesión iniciada o ha caducado.</summary>
    public static string? Token(string carpetaCuenta)
    {
        try
        {
            var f = Path.Combine(carpetaCuenta, ".credentials.json");
            if (!File.Exists(f)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var o)) return null;
            if (o.TryGetProperty("expiresAt", out var exp) && exp.TryGetInt64(out var ms) &&
                DateTimeOffset.FromUnixTimeMilliseconds(ms) < DateTimeOffset.Now) return null;
            return o.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Consulta el consumo. null si no se puede saber ahora (sin token, sin red, servicio caído…).</summary>
    public static async Task<EstadoCuenta?> Obtener(string carpetaCuenta, HttpMessageHandler? manejador = null, CancellationToken ct = default)
    {
        var token = Token(carpetaCuenta);
        if (token is null) return null;
        using var http = manejador is null ? new HttpClient() : new HttpClient(manejador, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(15);
        using var pet = new HttpRequestMessage(HttpMethod.Get, Url);
        pet.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        pet.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        try
        {
            using var r = await http.SendAsync(pet, ct);
            if (r.StatusCode == HttpStatusCode.TooManyRequests || !r.IsSuccessStatusCode)
            {
                Registro.Aviso($"Consumo de {Path.GetFileName(carpetaCuenta)}: HTTP {(int)r.StatusCode}");
                return null;
            }
            return Interpretar(await r.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Registro.Aviso($"Consumo de {Path.GetFileName(carpetaCuenta)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Lee la respuesta del servicio (tolera campos que falten).</summary>
    public static EstadoCuenta Interpretar(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        (double? Pct, DateTime? Reinicio) Ventana(string nombre)
        {
            if (!r.TryGetProperty(nombre, out var v) || v.ValueKind != JsonValueKind.Object) return (null, null);
            double? pct = v.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : null;
            DateTime? reinicio = v.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.String &&
                                 DateTime.TryParse(ra.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d)
                ? DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime() : null;
            return (pct, reinicio);
        }
        var h = Ventana("five_hour");
        var s = Ventana("seven_day");
        return new EstadoCuenta { Pct5h = h.Pct, Reinicio5h = h.Reinicio, PctSemana = s.Pct, ReinicioSemana = s.Reinicio };
    }
}
