using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiAgente.Nucleo;

/// <summary>Un fichero adjunto a un mensaje del usuario (copiado o pegado en <c>adjuntos\</c>, o una ruta del disco).</summary>
public sealed record Adjunto(string Ruta, string Nombre, string Tipo, bool EsImagen);

/// <summary>
/// Una entrada de la conversación canónica de MultiAgente, en formato neutro (igual para cualquier proveedor).
/// Tipos: usuario, agente, pensamiento, herramienta, plan, aviso.
/// </summary>
public sealed class Entrada
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Tipo { get; set; } = "";
    public DateTime Hora { get; set; } = DateTime.Now;
    public string? Texto { get; set; }
    public List<Adjunto>? Adjuntos { get; set; }
    /// <summary>Herramienta: título que da el agente («Leer Program.cs», «Ejecutar dotnet build»…).</summary>
    public string? Titulo { get; set; }
    /// <summary>Herramienta: read, edit, delete, move, search, execute, think, fetch, other.</summary>
    public string? Clase { get; set; }
    /// <summary>Herramienta: pending, in_progress, completed, failed. Plan: no se usa.</summary>
    public string? Estado { get; set; }
    /// <summary>Herramienta: contenido ACP (texto, diferencias…).</summary>
    public JsonNode? Contenido { get; set; }
    /// <summary>Herramienta: ficheros afectados (ACP «locations»).</summary>
    public JsonNode? Ubicaciones { get; set; }
    /// <summary>Herramienta: datos de entrada tal cual los da el agente.</summary>
    public JsonNode? Datos { get; set; }
    /// <summary>Plan: lista de tareas (ACP «entries»: content, priority, status).</summary>
    public JsonNode? Pasos { get; set; }
    /// <summary>Aviso: info, error, limite, interrumpido.</summary>
    public string? Nivel { get; set; }
    /// <summary>Cuenta que trabajaba (alias), para ver los relevos.</summary>
    public string? Cuenta { get; set; }

    public Entrada Clonar() => JsonSerializer.Deserialize<Entrada>(JsonSerializer.SerializeToUtf8Bytes(this, Json.Opciones), Json.Opciones)!;
}

/// <summary>
/// La conversación canónica de una sesión: <c>datos\sesiones\&lt;id&gt;\hilo.jsonl</c>, una entrada por línea. Una
/// entrada que cambia se vuelve a añadir con el mismo Id (al leer gana la última, en su sitio original).
/// </summary>
public sealed class Hilo
{
    private readonly List<Entrada> _entradas = [];
    private readonly Dictionary<string, int> _indice = [];
    private readonly object _cerrojo = new();

    public Hilo(string carpeta)
    {
        Carpeta = carpeta;
        Cargar();
    }

    public string Carpeta { get; }
    public string Fichero => Path.Combine(Carpeta, "hilo.jsonl");
    public string Adjuntos => Path.Combine(Carpeta, "adjuntos");

    public IReadOnlyList<Entrada> Entradas
    {
        get { lock (_cerrojo) return _entradas.Select(e => e.Clonar()).ToList(); }
    }

    public int Cuantas
    {
        get { lock (_cerrojo) return _entradas.Count; }
    }

    private void Cargar()
    {
        if (!File.Exists(Fichero)) return;
        foreach (var linea in File.ReadLines(Fichero, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(linea)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<Entrada>(linea, Json.Opciones);
                if (e is not null) Poner(e);
            }
            catch (JsonException) { Registro.Aviso($"Línea dañada en {Fichero}"); }
        }
    }

    private void Poner(Entrada e)
    {
        if (_indice.TryGetValue(e.Id, out var i)) _entradas[i] = e;
        else
        {
            _indice[e.Id] = _entradas.Count;
            _entradas.Add(e);
        }
    }

    /// <summary>Añade o actualiza una entrada y la guarda en el fichero.</summary>
    public void Guardar(Entrada e)
    {
        var copia = e.Clonar();
        lock (_cerrojo)
        {
            Poner(copia);
            Directory.CreateDirectory(Carpeta);
            File.AppendAllText(Fichero, JsonSerializer.Serialize(copia, Json.Opciones) + "\n", new UTF8Encoding(false));
        }
    }

    /// <summary>Escribe de una vez la conversación de una sesión nueva (al importar un historial).</summary>
    public static void Crear(string carpeta, IEnumerable<Entrada> entradas)
    {
        Directory.CreateDirectory(carpeta);
        File.WriteAllLines(Path.Combine(carpeta, "hilo.jsonl"), entradas.Select(e => JsonSerializer.Serialize(e, Json.Opciones)), new UTF8Encoding(false));
    }
}

/// <summary>
/// Paquete de traspaso: lo esencial de la conversación para un agente que no puede recuperarla por sí mismo (otra cuenta
/// de otro proveedor, o una sesión nativa que ya no existe). En F3 se completa con el resumen vivo.
/// </summary>
public static class Traspaso
{
    /// <param name="resumen">Resumen vivo de la sesión (<see cref="ResumenVivo"/>), si lo hay: va delante de los últimos mensajes.</param>
    /// <param name="deDonde">De qué cuenta viene (para que el agente sepa que es un relevo).</param>
    public static string Componer(IReadOnlyList<Entrada> entradas, int maxCaracteres = 16000, string? resumen = null, string? deDonde = null)
    {
        var sb = new StringBuilder();
        sb.Append("Esta conversación ya estaba en marcha")
          .Append(deDonde is null ? " con otro agente o en otra sesión que no se puede recuperar." : $" con {deDonde}; sigues tú.")
          .Append(" Los ficheros del proyecto reflejan el trabajo hecho: compruébalos antes de seguir y no repitas lo hecho.\n\n");
        if (!string.IsNullOrWhiteSpace(resumen))
            sb.Append("Resumen de la sesión hasta ahora:\n").Append(resumen.Trim()).Append("\n\n");
        sb.Append("Últimos mensajes:\n").Append(Conversacion(entradas, resumen is null ? maxCaracteres : maxCaracteres / 2));
        sb.Append("\n\n--- Mensaje nuevo del usuario:\n");
        return sb.ToString();
    }

    /// <summary>La conversación en texto plano, de lo más reciente hacia atrás hasta el tope de caracteres.</summary>
    public static string Conversacion(IReadOnlyList<Entrada> entradas, int maxCaracteres)
    {
        var partes = new List<string>();
        var total = 0;
        for (var i = entradas.Count - 1; i >= 0 && total < maxCaracteres; i--)
        {
            var e = entradas[i];
            var linea = e.Tipo switch
            {
                "usuario" => $"[Usuario] {Recortar(e.Texto, 2000)}" +
                             (e.Adjuntos is { Count: > 0 } a ? $" (adjuntos: {string.Join(", ", a.Select(x => x.Ruta))})" : ""),
                "agente" => $"[Agente] {Recortar(e.Texto, 2000)}",
                "herramienta" => $"[Herramienta] {e.Titulo} ({e.Estado})",
                "plan" => "[Tareas] " + string.Join("; ", (e.Pasos as JsonArray ?? []).Select(p => $"{p?["content"]} ({p?["status"]})")),
                _ => null,
            };
            if (linea is null) continue;
            partes.Add(linea);
            total += linea.Length;
        }
        partes.Reverse();
        return string.Join("\n", partes);
    }

    private static string Recortar(string? t, int max) =>
        t is null ? "" : t.Length <= max ? t : t[..max] + "…";
}
