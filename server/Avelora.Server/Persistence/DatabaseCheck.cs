#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Avelora.Server.Persistence
{
    /// <summary>
    /// Comprobación de una base SQLite sin arrancar el servidor ni escribir en ella
    /// (<c>--check-db</c>, G-05): integridad, claves ajenas y número de cuentas y personajes.
    /// </summary>
    /// <remarks>
    /// La conexión se abre en <see cref="SqliteOpenMode.ReadOnly"/>: la comprobación se puede
    /// lanzar sobre la base de un servidor que acaba de caer sin tocar su contenido.
    /// </remarks>
    public static class DatabaseCheck
    {
        /// <summary>Lo que SQLite responde cuando la comprobación de integridad pasa.</summary>
        public const string OkText = "ok";

        /// <summary>Comprueba la base indicada. Nunca crea el archivo ni modifica la base.</summary>
        public static DatabaseCheckResult Run(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                throw new ArgumentException("La ruta de la base de datos está vacía.", nameof(databasePath));
            }

            string fullPath = Path.GetFullPath(databasePath);

            if (!File.Exists(fullPath))
            {
                return DatabaseCheckResult.Failed(
                    fullPath,
                    "no existe el archivo (--check-db no crea bases nuevas)");
            }

            // ReadOnly a propósito: ni WAL ni esquema se pueden tocar desde aquí.
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            };

            try
            {
                using SqliteConnection connection = new SqliteConnection(builder.ToString());
                connection.Open();

                List<string> integrity = ReadStrings(connection, "PRAGMA integrity_check;");
                List<string> foreignKeys = ReadForeignKeys(connection);
                long accounts = CountRows(connection, "accounts");
                long characters = CountRows(connection, "characters");

                return new DatabaseCheckResult(
                    fullPath,
                    integrity,
                    foreignKeys,
                    accounts,
                    characters,
                    null);
            }
            catch (SqliteException ex)
            {
                return DatabaseCheckResult.Failed(fullPath, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return DatabaseCheckResult.Failed(fullPath, ex.Message);
            }
        }

        /// <summary>Lee las filas del <c>PRAGMA integrity_check</c> tal cual.</summary>
        private static List<string> ReadStrings(SqliteConnection connection, string sql)
        {
            var rows = new List<string>();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
            }

            return rows;
        }

        /// <summary>
        /// Filas del <c>PRAGMA foreign_key_check</c>: tabla, rowid, tabla padre e índice de la clave.
        /// Una lista vacía significa que no hay filas huérfanas.
        /// </summary>
        private static List<string> ReadForeignKeys(SqliteConnection connection)
        {
            var rows = new List<string>();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_key_check;";

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                long rowId = reader.IsDBNull(1) ? -1L : reader.GetInt64(1);
                string parent = reader.IsDBNull(2) ? "?" : reader.GetString(2);
                string index = reader.IsDBNull(3) ? "?" : Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? "?";

                rows.Add(
                    "tabla=" + (reader.IsDBNull(0) ? "?" : reader.GetString(0)) +
                    " rowid=" + rowId.ToString(CultureInfo.InvariantCulture) +
                    " padre=" + parent +
                    " fila=" + index);
            }

            return rows;
        }

        private static long CountRows(SqliteConnection connection, string table)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Resultado de <see cref="DatabaseCheck.Run"/>.</summary>
    public sealed class DatabaseCheckResult
    {
        public DatabaseCheckResult(
            string databasePath,
            IReadOnlyList<string> integrityMessages,
            IReadOnlyList<string> foreignKeyViolations,
            long accounts,
            long characters,
            string? error)
        {
            DatabasePath = databasePath;
            IntegrityMessages = integrityMessages;
            ForeignKeyViolations = foreignKeyViolations;
            Accounts = accounts;
            Characters = characters;
            Error = error;
            Ok = error == null &&
                IntegrityMessages.Count > 0 &&
                IsIntegrityOk(IntegrityMessages) &&
                ForeignKeyViolations.Count == 0;
        }

        /// <summary>Ruta absoluta comprobada.</summary>
        public string DatabasePath { get; }

        /// <summary>Filas del <c>PRAGMA integrity_check</c> (normalmente una sola: «ok»).</summary>
        public IReadOnlyList<string> IntegrityMessages { get; }

        /// <summary>Filas huérfanas del <c>PRAGMA foreign_key_check</c>.</summary>
        public IReadOnlyList<string> ForeignKeyViolations { get; }

        public long Accounts { get; }

        public long Characters { get; }

        /// <summary>Motivo por el que no se pudo comprobar (archivo ausente, error de SQLite…).</summary>
        public string? Error { get; }

        /// <summary>¿La base está sana? La respuesta que decide el código de salida 0/1.</summary>
        public bool Ok { get; }

        /// <summary>¿Todas las filas de integridad dicen «ok»?</summary>
        public bool IntegrityOk => IntegrityMessages.Count > 0 && IsIntegrityOk(IntegrityMessages);

        private static bool IsIntegrityOk(IReadOnlyList<string> messages)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                if (!string.Equals(messages[i], DatabaseCheck.OkText, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        public static DatabaseCheckResult Failed(string databasePath, string error) =>
            new DatabaseCheckResult(
                databasePath,
                Array.Empty<string>(),
                Array.Empty<string>(),
                -1,
                -1,
                error);
    }
}
