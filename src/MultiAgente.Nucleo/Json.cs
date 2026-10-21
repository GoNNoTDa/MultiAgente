using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiAgente.Nucleo;

/// <summary>Opciones JSON comunes: nombres en camelCase, sin nulos, sin escapar tildes.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
}
