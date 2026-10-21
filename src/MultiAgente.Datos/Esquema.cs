namespace MultiAgente.Datos;

/// <summary>
/// Pasos del esquema, en orden. Nunca se cambia un paso ya publicado: se añade uno nuevo al final.
/// Rutas dentro de la carpeta portátil, relativas (<see cref="Nucleo.Rutas.ParaGuardar"/>). Fechas en ISO 8601.
/// </summary>
internal static class Esquema
{
    public static readonly string[] Pasos =
    [
        // 1 · plan §8
        """
        CREATE TABLE Ajuste (
            Clave TEXT PRIMARY KEY,
            Valor TEXT NOT NULL
        );
        CREATE TABLE Proyecto (
            Id TEXT PRIMARY KEY,
            Nombre TEXT NOT NULL,
            Ruta TEXT NOT NULL UNIQUE COLLATE NOCASE,
            EquipoPreferido TEXT,
            Orden INTEGER NOT NULL DEFAULT 0,
            Oculto INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE Cuenta (
            Id TEXT PRIMARY KEY,
            Alias TEXT NOT NULL UNIQUE COLLATE NOCASE,
            Proveedor TEXT NOT NULL,             -- Claude | Codex | Gemini
            Tipo TEXT NOT NULL,                  -- Login | Api
            Carpeta TEXT NOT NULL,               -- relativa: datos\cuentas\<alias>
            RefClave TEXT,                       -- entrada de la bóveda (nunca la clave)
            Umbrales TEXT,                       -- JSON
            Presupuesto REAL,
            Orden INTEGER NOT NULL DEFAULT 0,
            Creada TEXT NOT NULL
        );
        CREATE TABLE Equipo (
            Id TEXT PRIMARY KEY,
            Nombre TEXT NOT NULL UNIQUE COLLATE NOCASE,
            Regla TEXT NOT NULL DEFAULT 'Prioridad',   -- Prioridad | Rotacion | MenosUsada | SuscripcionPrimero
            PermiteOtroProveedor INTEGER NOT NULL DEFAULT 1,
            CuentaAtras INTEGER NOT NULL DEFAULT 60,
            Predeterminado INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE EquipoCuenta (
            EquipoId TEXT NOT NULL REFERENCES Equipo(Id) ON DELETE CASCADE,
            CuentaId TEXT NOT NULL REFERENCES Cuenta(Id) ON DELETE CASCADE,
            Orden INTEGER NOT NULL,
            PRIMARY KEY (EquipoId, CuentaId)
        );
        CREATE TABLE Sesion (
            Id TEXT PRIMARY KEY,
            ProyectoId TEXT REFERENCES Proyecto(Id) ON DELETE SET NULL,
            Titulo TEXT,
            Carpeta TEXT NOT NULL,
            EquipoId TEXT REFERENCES Equipo(Id) ON DELETE SET NULL,
            CuentaFija TEXT REFERENCES Cuenta(Id) ON DELETE SET NULL,
            Modelo TEXT,
            Esfuerzo TEXT,
            ModoPermisos TEXT,
            Estado TEXT NOT NULL DEFAULT 'Cerrada',
            Creada TEXT NOT NULL,
            UltimoUso TEXT NOT NULL,
            Archivada INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE Tramo (
            SesionId TEXT NOT NULL REFERENCES Sesion(Id) ON DELETE CASCADE,
            Orden INTEGER NOT NULL,
            CuentaId TEXT REFERENCES Cuenta(Id) ON DELETE SET NULL,
            SesionNativa TEXT,
            Desde TEXT NOT NULL,
            Hasta TEXT,
            Motivo TEXT,
            PRIMARY KEY (SesionId, Orden)
        );
        CREATE TABLE Consumo (
            CuentaId TEXT NOT NULL REFERENCES Cuenta(Id) ON DELETE CASCADE,
            Momento TEXT NOT NULL,
            Ventana TEXT NOT NULL,               -- 5h | semana | mes | ...
            Porcentaje REAL,
            Reinicio TEXT,
            Gasto REAL
        );
        CREATE INDEX IX_Consumo_Cuenta ON Consumo (CuentaId, Momento);
        """,
        // 2 · cuentas de API compatibles (GLM, DeepSeek…: el agente de Claude Code con otro destino y otro modelo)
        """
        ALTER TABLE Cuenta ADD COLUMN Endpoint TEXT;
        ALTER TABLE Cuenta ADD COLUMN Modelo TEXT;
        ALTER TABLE Cuenta ADD COLUMN Catalogo TEXT;
        """,
        // 3 · estadísticas y límites de consumo
        """
        ALTER TABLE Cuenta ADD COLUMN Umbral5h REAL;
        ALTER TABLE Cuenta ADD COLUMN UmbralSemana REAL;
        CREATE TABLE UsoTurno (
            SesionId TEXT NOT NULL,
            CuentaId TEXT,
            Momento TEXT NOT NULL,
            Entrada INTEGER NOT NULL DEFAULT 0,
            Salida INTEGER NOT NULL DEFAULT 0,
            Total INTEGER NOT NULL DEFAULT 0,
            Coste REAL,
            Moneda TEXT
        );
        CREATE INDEX IX_UsoTurno_Momento ON UsoTurno (Momento);
        CREATE INDEX IX_UsoTurno_Cuenta ON UsoTurno (CuentaId, Momento);
        """,
        // 4 · proyectos creados a mano: varios pueden compartir carpeta (se quita el UNIQUE de Ruta rehaciendo la tabla;
        //     al borrar la vieja, la clave ajena deja a NULL el proyecto de las sesiones, así que se guarda y se repone)
        """
        CREATE TEMP TABLE _SesionProyecto AS SELECT Id, ProyectoId FROM Sesion;
        CREATE TABLE Proyecto_n (
            Id TEXT PRIMARY KEY,
            Nombre TEXT NOT NULL,
            Ruta TEXT NOT NULL COLLATE NOCASE,
            EquipoPreferido TEXT,
            Orden INTEGER NOT NULL DEFAULT 0,
            Oculto INTEGER NOT NULL DEFAULT 0
        );
        INSERT INTO Proyecto_n (Id, Nombre, Ruta, EquipoPreferido, Orden, Oculto)
            SELECT Id, Nombre, Ruta, EquipoPreferido, Orden, Oculto FROM Proyecto;
        DROP TABLE Proyecto;
        ALTER TABLE Proyecto_n RENAME TO Proyecto;
        UPDATE Sesion SET ProyectoId = (SELECT t.ProyectoId FROM _SesionProyecto t WHERE t.Id = Sesion.Id);
        DROP TABLE _SesionProyecto;
        """,
    ];
}
