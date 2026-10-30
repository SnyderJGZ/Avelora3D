using System;
using System.Globalization;
using System.IO;
using Avelora.Server.Persistence;
using Avelora.Shared;
using Avelora.Shared.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// Pruebas de la comprobación <c>--check-db</c> (G-05): base sana, fila huérfana y archivo
    /// dañado, siempre sin arrancar el servidor y sin escribir en la base.
    /// </summary>
    public class DatabaseCheckTests : IDisposable
    {
        private readonly string _folder;

        public DatabaseCheckTests()
        {
            _folder = Path.Combine(
                Path.GetTempPath(),
                "avelora-checkdb-tests-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (IOException)
            {
                // Windows puede tardar un instante en soltar el archivo; no debe romper la prueba.
            }
        }

        private string DatabasePath => Path.Combine(_folder, "avelora.db");

        private static CharacterRecord? CreateOrLoadCharacterForTest(
            IPlayerRepository repository,
            int accountId,
            string name,
            int zoneId,
            Vec2 spawn,
            byte[] mapHash)
        {
            CharacterCreateResult result = repository.CreateCharacter(
                accountId, name, 1, 10, zoneId, spawn, mapHash);
            return result.Success ? result.Character : null;
        }

        [Fact]
        public void Una_Base_Sana_Devuelve_Ok_Con_Sus_Cuentas_Y_Personajes()
        {
            using (var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                repository.Initialize();

                AccountLoginResult login = repository.Login(
                    "cuenta",
                    "secreto1",
                    "avelora-dev",
                    "avelora-dev");

                Assert.True(login.Success);

                CreateOrLoadCharacterForTest(repository,
                    login.AccountId,
                    "Heroe",
                    1,
                    new Vec2(-1.5f, -2.5f),
                    NewHash());
            }

            string[] before = Directory.GetFiles(_folder);
            byte[] beforeBytes = File.ReadAllBytes(DatabasePath);

            DatabaseCheckResult result = DatabaseCheck.Run(DatabasePath);

            Assert.Null(result.Error);
            Assert.True(result.Ok);
            Assert.True(result.IntegrityOk);
            Assert.Contains(result.IntegrityMessages, message => message == "ok");
            Assert.Empty(result.ForeignKeyViolations);
            Assert.Equal(1, result.Accounts);
            Assert.Equal(1, result.Characters);

            // Abrir en solo lectura no modifica el archivo de la base. SQLite puede crear sus
            // archivos auxiliares -wal/-shm (forma parte de abrir una base en modo WAL), pero el
            // contenido de avelora.db queda byte a byte igual.
            Assert.Equal(beforeBytes, File.ReadAllBytes(DatabasePath));
            Assert.Contains(DatabasePath, before);
        }

        [Fact]
        public void Una_Fila_Huerfana_Se_Detecta_Con_Foreign_Key_Check()
        {
            using (var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                repository.Initialize();
                repository.Login("cuenta", "secreto1", "avelora-dev", "avelora-dev");
            }

            // Se inserta a propósito un personaje de una cuenta que no existe. Las claves ajenas
            // se desactivan en esta conexión de la prueba: es la única forma de fabricar el daño
            // sin tocar el archivo a mano.
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            };

            using (var connection = new SqliteConnection(builder.ToString()))
            {
                connection.Open();

                using SqliteCommand pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA foreign_keys=OFF;";
                pragma.ExecuteNonQuery();

                using SqliteCommand insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT INTO characters " +
                    "(account_id, name, name_key, deleted_utc, level, experience, health, mana, zone_id, x, y, map_hash, created_utc, updated_utc) " +
                    "VALUES (999, 'Huerfano', 'huerfano', NULL, 1, 0, 100, 100, 1, 0, 0, @map, '2026-01-01T00:00:00.0000000Z', " +
                    "'2026-01-01T00:00:00.0000000Z');";
                insert.Parameters.AddWithValue("@map", NewHash());
                insert.ExecuteNonQuery();
            }

            DatabaseCheckResult result = DatabaseCheck.Run(DatabasePath);

            Assert.Null(result.Error);
            Assert.False(result.Ok);
            Assert.True(result.IntegrityOk);
            Assert.Single(result.ForeignKeyViolations);
            Assert.Contains("tabla=characters", result.ForeignKeyViolations[0]);
            Assert.Contains("padre=accounts", result.ForeignKeyViolations[0]);
            Assert.Equal(1, result.Accounts);
            Assert.Equal(1, result.Characters);

            // La comprobación no repara nada: la fila sigue ahí.
            using var verify = new SqliteConnection(builder.ToString());
            verify.Open();
            using SqliteCommand count = verify.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM characters;";
            Assert.Equal(1L, Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture));
        }

        [Fact]
        public void Un_Archivo_Danado_No_Se_Considera_Sano()
        {
            File.WriteAllText(DatabasePath, "esto no es una base SQLite");

            DatabaseCheckResult result = DatabaseCheck.Run(DatabasePath);

            Assert.False(result.Ok);
            Assert.NotNull(result.Error);
        }

        [Fact]
        public void Una_Base_Que_No_Existe_No_Se_Crea()
        {
            DatabaseCheckResult result = DatabaseCheck.Run(DatabasePath);

            Assert.False(result.Ok);
            Assert.NotNull(result.Error);
            Assert.False(File.Exists(DatabasePath));
        }

        private static byte[] NewHash()
        {
            var hash = new byte[ProtocolConstants.MapHashSize];

            for (int i = 0; i < hash.Length; i++)
            {
                hash[i] = (byte)(3 + i);
            }

            return hash;
        }
    }
}
