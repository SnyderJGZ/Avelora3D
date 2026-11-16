using System;
using System.IO;
using Avelora.Server.Persistence;
using Avelora.Server.Progression;
using Avelora.Shared;
using Avelora.Shared.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Avelora.Tests
{
    public sealed class U3D02Tests : IDisposable
    {
        private readonly string _folder = Path.Combine(
            Path.GetTempPath(), "avelora-u3d02-tests-" + Guid.NewGuid().ToString("N"));

        private string DatabasePath => Path.Combine(_folder, "avelora.db");

        public U3D02Tests()
        {
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
                // SQLite puede tardar un instante en soltar el archivo en Windows.
            }
        }

        [Fact]
        public void CreateCharacter_Lleva_El_Sexo_Y_Rechaza_Valores_Invalidos()
        {
            byte[] bytes = MessageCodec.EncodeCreateCharacter(new CreateCharacterMessage("Ana", 1, sex: 1));
            Assert.True(MessageCodec.TryReadCreateCharacter(bytes, 0, bytes.Length, out CreateCharacterMessage decoded));
            Assert.Equal((byte)1, decoded.Sex);

            byte[] invalid = (byte[])bytes.Clone();
            invalid[invalid.Length - 1] = 2;
            Assert.False(MessageCodec.TryReadCreateCharacter(invalid, 0, invalid.Length, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeCreateCharacter(
                new CreateCharacterMessage("Ana", 1, sex: 2)));
        }

        [Fact]
        public void CharacterList_Lleva_El_Sexo()
        {
            var original = new[]
            {
                new CharacterListEntry(1, "Ana", 1, 1),
                new CharacterListEntry(2, "Eva", 2, 3, sex: 1),
            };
            byte[] bytes = MessageCodec.EncodeCharacterList(new CharacterListMessage(
                3, original, Array.Empty<CharacterClassEntry>()));

            Assert.True(MessageCodec.TryReadCharacterList(bytes, 0, bytes.Length, out CharacterListMessage decoded));
            Assert.Equal((byte)0, decoded.Characters[0].Sex);
            Assert.Equal((byte)1, decoded.Characters[1].Sex);
        }

        [Fact]
        public void Snapshot_Lleva_El_Sexo_Del_Jugador_En_El_Bit_7()
        {
            var female = new EntityState(
                1, 0, 0, 0, 0, EntityKind.Player, 100, EntityAction.Attacking, pvp: true, sex: 1);
            var male = new EntityState(
                1, 0, 0, 0, 0, EntityKind.Player, 100, EntityAction.Attacking, pvp: true, sex: 0);
            byte[] femaleBytes = MessageCodec.EncodeSnapshot(new SnapshotMessage(1, 0, new[] { female }));
            byte[] maleBytes = MessageCodec.EncodeSnapshot(new SnapshotMessage(1, 0, new[] { male }));

            Assert.Equal(maleBytes.Length, femaleBytes.Length);
            Assert.True(MessageCodec.TryReadSnapshot(femaleBytes, 0, femaleBytes.Length, out SnapshotMessage decoded));
            Assert.Equal((byte)1, decoded.Entities[0].Sex);
            Assert.True(decoded.Entities[0].Pvp);
            Assert.Equal(EntityAction.Attacking, decoded.Entities[0].Action);
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeSnapshot(new SnapshotMessage(
                1, 0, new[] { new EntityState(2, 0, 0, 0, 0, EntityKind.Npc, 100, EntityAction.None, sex: 1) })));
        }

        [Fact]
        public void El_Protocolo_Usa_La_Version_27()
        {
            Assert.Equal((ushort)27, ProtocolConstants.Version);
        }

        [Fact]
        public void Migracion_V8_A_V9_Añade_Sexo_Cero()
        {
            int accountId;
            using (var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                repository.Initialize();
                accountId = repository.Register("u3d02-migracion", "secreto1", "inv", "inv").AccountId;
                repository.CreateCharacter(accountId, "Persistido", 1, 3, 1, new Vec2(1, 1), new byte[32]);
            }

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand downgrade = connection.CreateCommand();
                downgrade.CommandText = "ALTER TABLE characters DROP COLUMN sex; PRAGMA user_version=8;";
                downgrade.ExecuteNonQuery();
            }

            using var migrated = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            migrated.Initialize();
            Assert.Equal(0, Assert.Single(migrated.ListCharacters(accountId)).Sex);
            Assert.Equal(9, QuerySchemaVersion());
        }

        [Fact]
        public void Sqlite_Guarda_Y_Carga_El_Sexo()
        {
            using var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            repository.Initialize();
            int accountId = repository.Register("u3d02-sexo", "secreto1", "inv", "inv").AccountId;
            CharacterRecord character = repository.CreateCharacter(
                accountId, "Eva", 1, 3, 1, new Vec2(1, 1), new byte[32], sex: 1).Character!;
            Assert.Equal(1, Assert.Single(repository.ListCharacters(accountId)).Sex);

            repository.SaveCharacter(character with { PvpMode = 1, PvpForcedSeconds = 25 });
            Assert.Equal(1, Assert.Single(repository.ListCharacters(accountId)).Sex);

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand corrupt = connection.CreateCommand();
                corrupt.CommandText = "UPDATE characters SET sex=7 WHERE id=@id;";
                corrupt.Parameters.AddWithValue("@id", character.Id);
                corrupt.ExecuteNonQuery();
            }

            Assert.Equal(0, Assert.Single(repository.ListCharacters(accountId)).Sex);
        }

        [Fact]
        public void CreateCharacter_Rechaza_Sexo_Invalido_En_Los_Repositorios()
        {
            using (var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                repository.Initialize();
                int accountId = repository.Register("u3d02-sql", "secreto1", "inv", "inv").AccountId;
                Assert.Throws<ArgumentOutOfRangeException>(() => repository.CreateCharacter(
                    accountId, "Eva", 1, 3, 1, new Vec2(1, 1), new byte[32], sex: 2));
            }

            using var memory = new InMemoryPlayerRepository(ClassCatalog.Default, 1);
            memory.Initialize();
            int memoryAccountId = memory.Register("u3d02-memory", "secreto1", "inv", "inv").AccountId;
            Assert.Throws<ArgumentOutOfRangeException>(() => memory.CreateCharacter(
                memoryAccountId, "Eva", 1, 3, 1, new Vec2(1, 1), new byte[32], sex: 2));
        }

        private int QuerySchemaVersion()
        {
            using var connection = new SqliteConnection("Data Source=" + DatabasePath);
            connection.Open();
            using SqliteCommand query = connection.CreateCommand();
            query.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(query.ExecuteScalar());
        }
    }
}
