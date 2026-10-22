using Microsoft.Data.Sqlite;
using MultiAgente.Nucleo;

namespace MultiAgente.Datos;

/// <summary>
/// Base de datos SQLite de la carpeta portátil (<c>datos\multiagente.db</c>). Sin ORM: consultas directas.
/// El esquema se crea y se pone al día solo (<c>PRAGMA user_version</c>). Nunca guarda tokens ni claves.
/// </summary>
public sealed class BaseDatos : IDisposable
{
    private readonly SqliteConnection _conexion;
    private readonly object _cerrojo = new();

    private BaseDatos(SqliteConnection conexion) => _conexion = conexion;

    public string Fichero => _conexion.DataSource;

    /// <summary>Versión del esquema que deja <see cref="Abrir"/>.</summary>
    public static int VersionEsquema => Esquema.Pasos.Length;

    /// <summary>Abre (o crea) la base y pone el esquema al día.</summary>
    public static BaseDatos Abrir(string fichero)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fichero))!);
        // Sin pool: al cerrar se suelta el fichero (la carpeta se puede copiar o borrar en seguida).
        var cadena = new SqliteConnectionStringBuilder { DataSource = fichero, Pooling = false }.ToString();
        var conexion = new SqliteConnection(cadena);
        conexion.Open();
        var bd = new BaseDatos(conexion);
        bd.Ejecutar("PRAGMA foreign_keys = ON;");
        bd.Migrar();
        return bd;
    }

    /// <summary>Versión del esquema en el fichero.</summary>
    public int Version => Convert.ToInt32(Escalar("PRAGMA user_version;"));

    private void Migrar()
    {
        var actual = Version;
        if (actual > Esquema.Pasos.Length)
            throw new InvalidOperationException(
                $"La base de datos es de una versión más nueva de MultiAgente (esquema {actual}). Usa una versión igual o más reciente.");
        for (var v = actual; v < Esquema.Pasos.Length; v++)
        {
            lock (_cerrojo)
            {
                using var tx = _conexion.BeginTransaction();
                using (var cmd = _conexion.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = Esquema.Pasos[v] + $"\nPRAGMA user_version = {v + 1};";
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            Registro.Info($"Base de datos: esquema {v + 1} aplicado");
        }
    }

    /// <summary>Lee un ajuste (null si no existe).</summary>
    public string? LeerAjuste(string clave) =>
        Escalar("SELECT Valor FROM Ajuste WHERE Clave = $c;", ("$c", clave)) as string;

    /// <summary>Guarda un ajuste; null lo borra.</summary>
    public void GuardarAjuste(string clave, string? valor)
    {
        if (valor is null) Ejecutar("DELETE FROM Ajuste WHERE Clave = $c;", ("$c", clave));
        else Ejecutar("INSERT INTO Ajuste (Clave, Valor) VALUES ($c, $v) ON CONFLICT(Clave) DO UPDATE SET Valor = excluded.Valor;",
                      ("$c", clave), ("$v", valor));
    }

    /// <summary>Nombres de las tablas (para diagnóstico y pruebas).</summary>
    public IReadOnlyList<string> Tablas()
    {
        lock (_cerrojo)
        {
            using var cmd = _conexion.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var r = cmd.ExecuteReader();
            var lista = new List<string>();
            while (r.Read()) lista.Add(r.GetString(0));
            return lista;
        }
    }

    internal List<T> Leer<T>(string sql, Func<SqliteDataReader, T> leer, params (string Nombre, object? Valor)[] parametros)
    {
        lock (_cerrojo)
        {
            using var cmd = Preparar(sql, parametros);
            using var r = cmd.ExecuteReader();
            var lista = new List<T>();
            while (r.Read()) lista.Add(leer(r));
            return lista;
        }
    }

    internal int Ejecutar(string sql, params (string Nombre, object? Valor)[] parametros)
    {
        lock (_cerrojo)
        {
            using var cmd = Preparar(sql, parametros);
            return cmd.ExecuteNonQuery();
        }
    }

    internal object? Escalar(string sql, params (string Nombre, object? Valor)[] parametros)
    {
        lock (_cerrojo)
        {
            using var cmd = Preparar(sql, parametros);
            var v = cmd.ExecuteScalar();
            return v is DBNull ? null : v;
        }
    }

    private SqliteCommand Preparar(string sql, (string Nombre, object? Valor)[] parametros)
    {
        var cmd = _conexion.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parametros) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    /// <summary>Copia coherente de la base, aunque esté en uso (copia de seguridad de SQLite).</summary>
    public void CopiarA(string fichero)
    {
        lock (_cerrojo)
        {
            using var destino = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fichero, Pooling = false }.ToString());
            destino.Open();
            _conexion.BackupDatabase(destino);
        }
    }

    public void Dispose()
    {
        lock (_cerrojo) _conexion.Dispose();
    }
}
