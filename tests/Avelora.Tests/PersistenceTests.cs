using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avelora.Net;
using Avelora.Server.Configuration;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Net;
using Avelora.Server.Npcs;
using Avelora.Server.Persistence;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Avelora.Shared.Protocol;
using Xunit;
using Microsoft.Data.Sqlite;

namespace Avelora.Tests
{
    /// <summary>
    /// Pruebas de la Etapa F: cuentas, personajes y guardado/carga de la posición.
    /// </summary>
    public class PersistenceTests : IDisposable
    {
        private static string MapPath =>
            Path.Combine(AppContext.BaseDirectory, "maps", "avelora-etapa-a.avmap");

        private readonly string _folder;

        public PersistenceTests()
        {
            _folder = Path.Combine(
                Path.GetTempPath(),
                "avelora-persistence-tests-" + Guid.NewGuid().ToString("N"));

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
                // SQLite puede tardar un instante en soltar el archivo en Windows; no debe fallar
                // la prueba por eso.
            }
        }

        private string DatabasePath => Path.Combine(_folder, "avelora.db");

        private static ItemCatalog ItemCatalogForTests() => new ItemCatalog
        {
            Items = new List<ItemType>
            {
                new ItemType { Id = 3001, Name = "Colmillo", Kind = "material", MaxStack = 5 },
                new ItemType { Id = 3002, Name = "Poción", Kind = "potion", MaxStack = 10, Heal = 30 },
                new ItemType { Id = 3101, Name = "Espada corta", Kind = "weapon", MaxStack = 1, Damage = 14, Range = 1.2f, CooldownSeconds = 0.8d },
            },
        };

        [Fact]
        public void La_Cuenta_Se_Registra_Y_Se_Verifica_Sin_Distinguir_Mayusculas()
        {
            using var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            repository.Initialize();

            AccountLoginResult registered = repository.Login(
                "Cuenta",
                "secreto1",
                "avelora-dev",
                "avelora-dev");

            Assert.True(registered.Success);
            Assert.True(registered.AccountId > 0);

            AccountLoginResult wrong = repository.Login(
                "cuenta",
                "otra-clave",
                "avelora-dev",
                "avelora-dev");

            Assert.False(wrong.Success);
            Assert.Equal(RejectReason.InvalidCredentials, wrong.Failure);
            Assert.True(wrong.CountsAsFailedAttempt);

            AccountLoginResult again = repository.Login(
                "CUENTA",
                "secreto1",
                string.Empty,
                "avelora-dev");

            Assert.True(again.Success);
            Assert.Equal(registered.AccountId, again.AccountId);
        }

        [Fact]
        public void El_Registro_Cerrado_No_Crea_Cuentas()
        {
            using var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            repository.Initialize();

            AccountLoginResult closed = repository.Login("nadie", "secreto1", "lo-que-sea", string.Empty);
            AccountLoginResult wrongCode = repository.Login("nadie", "secreto1", "otra", "avelora-dev");

            Assert.False(closed.Success);
            Assert.Equal(RejectReason.InvalidCredentials, closed.Failure);
            Assert.False(closed.CountsAsFailedAttempt);
            Assert.False(wrongCode.Success);
            Assert.Equal(RejectReason.InvalidCredentials, wrongCode.Failure);
            Assert.False(wrongCode.CountsAsFailedAttempt);
        }

        [Fact]
        public void Una_Cuenta_Inexistente_Tarda_Lo_Mismo_Que_Una_Clave_Mala()
        {
            // Si la cuenta inexistente se rechazara sin PBKDF2, el tiempo de respuesta diría qué
            // cuentas existen. Con 200 000 iteraciones el PBKDF2 domina; el mínimo de 3 quita ruido.
            using var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 200_000);
            repository.Initialize();
            Assert.True(repository.Login("existe", "secreto1", "avelora-dev", "avelora-dev").Success);

            double MinMilliseconds(string account)
            {
                double best = double.MaxValue;
                for (int i = 0; i < 3; i++)
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    Assert.False(repository.Login(account, "otra-clave", string.Empty, "avelora-dev").Success);
                    best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
                }

                return best;
            }

            double wrongPassword = MinMilliseconds("existe");
            double missingAccount = MinMilliseconds("no-existe");

            Assert.True(
                missingAccount >= wrongPassword * 0.5d,
                "Cuenta inexistente: " + missingAccount.ToString("0.0") + " ms; clave mala: " +
                wrongPassword.ToString("0.0") + " ms.");
        }

        [Fact]
        public void El_Personaje_Se_Guarda_Y_Se_Recupera_Al_Reabrir_La_Base()
        {
            byte[] mapHash = NewHash(3);
            int accountId;

            using (var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                repository.Initialize();

                AccountLoginResult login = repository.Login(
                    "cuenta",
                    "secreto1",
                    "avelora-dev",
                    "avelora-dev");

                Assert.True(login.Success);
                accountId = login.AccountId;

                CharacterRecord? created = repository.GetOrCreateCharacter(
                    accountId,
                    "Heroe",
                    1,
                    new Vec2(-1.5f, -2.5f),
                    mapHash);

                Assert.NotNull(created);
                Assert.Equal(1, created.Level);
                Assert.Equal(new Vec2(-1.5f, -2.5f), created.Position);

                repository.SaveCharacter(created.WithPosition(
                    1,
                    new Vec2(3.25f, -4.5f),
                    NewHash(9)));
            }

            using (var reopened = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                reopened.Initialize();

                AccountLoginResult login = reopened.Login(
                    "cuenta",
                    "secreto1",
                    string.Empty,
                    string.Empty);

                Assert.True(login.Success);
                Assert.Equal(accountId, login.AccountId);

                CharacterRecord? loaded = reopened.GetOrCreateCharacter(
                    login.AccountId,
                    "heroe",
                    1,
                    new Vec2(-1.5f, -2.5f),
                    mapHash);

                Assert.NotNull(loaded);
                Assert.Equal("Heroe", loaded.Name);
                Assert.Equal(new Vec2(3.25f, -4.5f), loaded.Position);
                Assert.Equal(NewHash(9), loaded.MapHash);
                Assert.Equal(1, loaded.ZoneId);
            }
        }

        [Fact]
        public void Al_Cargar_Se_Corrigen_Level_Y_Experience_Invalidos_Sin_Cambiar_El_Esquema()
        {
            using var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            repository.Initialize();
            AccountLoginResult login = repository.Login("cuenta", "secreto1", "invita", "invita");
            byte[] mapHash = NewHash(4);
            Vec2 spawn = new Vec2(1.5f, 1.5f);

            CharacterRecord character = repository.GetOrCreateCharacter(
                login.AccountId, "personaje", 1, spawn, mapHash)!;

            void SetProgress(long level, long experience)
            {
                using var connection = new SqliteConnection("Data Source=" + DatabasePath);
                connection.Open();
                using SqliteCommand update = connection.CreateCommand();
                update.CommandText = "UPDATE characters SET level=@level, experience=@experience WHERE id=@id;";
                update.Parameters.AddWithValue("@level", level);
                update.Parameters.AddWithValue("@experience", experience);
                update.Parameters.AddWithValue("@id", character.Id);
                update.ExecuteNonQuery();
            }

            CharacterRecord Load() => repository.GetOrCreateCharacter(
                login.AccountId, "personaje", 1, spawn, mapHash)!;

            SetProgress(-1, 10);
            CharacterRecord corrected = Load();
            Assert.Equal(1, corrected.Level);
            Assert.Equal(10, corrected.Experience);

            SetProgress(long.MaxValue, 10000);
            corrected = Load();
            Assert.Equal(25, corrected.Level);
            Assert.Equal(0, corrected.Experience);

            SetProgress(1, -3);
            corrected = Load();
            Assert.Equal(1, corrected.Level);
            Assert.Equal(0, corrected.Experience);

            SetProgress(1, long.MaxValue);
            Assert.Equal(99, Load().Experience);

            SetProgress(25, 1);
            Assert.Equal(0, Load().Experience);
            Assert.Equal(SqlitePlayerRepository.SchemaVersion, QuerySchemaVersion());
        }

        [Fact]
        public void Nivel_Y_Experiencia_Se_Guardan_Y_Se_Cargan_En_El_Esquema_Existente()
        {
            using var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            repository.Initialize();
            AccountLoginResult login = repository.Login("cuenta", "secreto1", "invita", "invita");
            byte[] mapHash = NewHash(5);
            Vec2 spawn = new Vec2(1.5f, 1.5f);
            CharacterRecord character = repository.GetOrCreateCharacter(
                login.AccountId, "personaje", 1, spawn, mapHash)!;

            repository.SaveCharacter(character.WithProgress(4, 275));

            CharacterRecord loaded = repository.GetOrCreateCharacter(
                login.AccountId, "personaje", 1, spawn, mapHash)!;
            Assert.Equal(4, loaded.Level);
            Assert.Equal(275, loaded.Experience);
            Assert.Equal(SqlitePlayerRepository.SchemaVersion, QuerySchemaVersion());
        }

        [Fact]
        public void Clase_Atributos_Repartidos_Y_Recurso_Se_Guardan_Y_Cargan()
        {
            byte[] mapHash = NewHash(31);
            int accountId;
            using (var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                repository.Initialize();
                accountId = repository.Login("clase", "secreto1", "inv", "inv").AccountId;
                CharacterRecord character = repository.GetOrCreateCharacter(
                    accountId, "Heroe", 1, new Vec2(1, 1), mapHash)!;
                repository.SaveCharacter(character
                    .WithClassAndAllocations(2, 0, 0, 2, 0)
                    .WithState(1, new Vec2(2, 3), mapHash, 75, 95));
            }

            using var reopened = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            reopened.Initialize();
            int loadedAccount = reopened.Login("clase", "secreto1", "", "").AccountId;
            CharacterRecord loaded = reopened.GetOrCreateCharacter(
                loadedAccount, "Heroe", 1, new Vec2(0, 0), mapHash)!;

            Assert.Equal(2, loaded.ClassId);
            Assert.Equal(2, loaded.AllocInt);
            Assert.Equal(75, loaded.Health);
            Assert.Equal(95, loaded.Mana);
        }

        [Fact]
        public void Clase_Desconocida_Asignaciones_Invalidas_Y_Vida_Recurso_Fuera_De_Rango_Se_Corrigen()
        {
            byte[] mapHash = NewHash(32);
            int accountId;
            int characterId;
            using (var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                repository.Initialize();
                accountId = repository.Login("correcciones-clase", "secreto1", "inv", "inv").AccountId;
                characterId = repository.GetOrCreateCharacter(
                    accountId, "Heroe", 1, new Vec2(1, 1), mapHash)!.Id;
            }

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand update = connection.CreateCommand();
                update.CommandText =
                    "UPDATE characters SET class_id=99, alloc_str=4, alloc_dex=0, alloc_int=0, alloc_vit=0, " +
                    "health=999, mana=-7 WHERE id=@id;";
                update.Parameters.AddWithValue("@id", characterId);
                update.ExecuteNonQuery();
            }

            using var reopened = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            reopened.Initialize();
            CharacterRecord loaded = reopened.GetOrCreateCharacter(
                accountId, "Heroe", 1, new Vec2(0, 0), mapHash)!;

            Assert.Equal(1, loaded.ClassId);
            Assert.Equal(0, loaded.AllocStr + loaded.AllocDex + loaded.AllocInt + loaded.AllocVit);
            Assert.Equal(100, loaded.Health);
            Assert.Equal(0, loaded.Mana);
        }

        [Fact]
        public void El_Inventario_Se_Guarda_Y_Se_Carga()
        {
            byte[] mapHash = NewHash(8);
            using (var repository = new SqlitePlayerRepository(
                       DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests(), inventorySlots: 3))
            {
                repository.Initialize();
                AccountLoginResult login = repository.Login("bolsa", "secreto1", "inv", "inv");
                CharacterRecord character = repository.GetOrCreateCharacter(
                    login.AccountId, "Heroe", 1, new Vec2(1, 1), mapHash)!;
                repository.SaveCharacter(character.WithInventory(new[]
                {
                    new InventoryItemRecord(0, 3001, 5),
                    new InventoryItemRecord(2, 3002, 1),
                }));
            }

            using var reopened = new SqlitePlayerRepository(
                DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests(), inventorySlots: 3);
            reopened.Initialize();
            AccountLoginResult again = reopened.Login("bolsa", "secreto1", "", "");
            CharacterRecord loaded = reopened.GetOrCreateCharacter(
                again.AccountId, "Heroe", 1, new Vec2(0, 0), mapHash)!;
            Assert.Equal(2, loaded.InventoryItems.Length);
            Assert.Contains(loaded.InventoryItems, item => item.Slot == 0 && item.ItemId == 3001 && item.Count == 5);
            Assert.Contains(loaded.InventoryItems, item => item.Slot == 2 && item.ItemId == 3002 && item.Count == 1);
        }

        [Fact]
        public void El_Arma_Equipada_Se_Guarda_Y_Se_Carga()
        {
            byte[] mapHash = NewHash(14);
            using (var repository = new SqlitePlayerRepository(
                       DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests()))
            {
                repository.Initialize();
                int accountId = repository.Login("equipo", "secreto1", "inv", "inv").AccountId;
                CharacterRecord character = repository.GetOrCreateCharacter(
                    accountId, "Heroe", 1, new Vec2(1, 1), mapHash)!;
                repository.SaveCharacter(character.WithEquippedItem(3101));
            }

            using var reopened = new SqlitePlayerRepository(
                DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests());
            reopened.Initialize();
            int loadedAccount = reopened.Login("equipo", "secreto1", "", "").AccountId;
            CharacterRecord loaded = reopened.GetOrCreateCharacter(
                loadedAccount, "Heroe", 1, new Vec2(0, 0), mapHash)!;
            Assert.Equal((ushort)3101, loaded.EquippedItemId);
        }

        [Theory]
        [InlineData(9999)]
        [InlineData(3001)]
        public void Arma_Equipada_Desconocida_O_Que_No_Es_Arma_Se_Descarta(int equippedItemId)
        {
            byte[] mapHash = NewHash(15);
            int accountId;
            int characterId;
            using (var repository = new SqlitePlayerRepository(
                       DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests()))
            {
                repository.Initialize();
                accountId = repository.Login("equipo-invalido", "secreto1", "inv", "inv").AccountId;
                characterId = repository.GetOrCreateCharacter(
                    accountId, "Heroe", 1, new Vec2(1, 1), mapHash)!.Id;
            }

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand update = connection.CreateCommand();
                update.CommandText = "UPDATE characters SET equipped_item_id=@item WHERE id=@id;";
                update.Parameters.AddWithValue("@item", equippedItemId);
                update.Parameters.AddWithValue("@id", characterId);
                update.ExecuteNonQuery();
            }

            using var reopened = new SqlitePlayerRepository(
                DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests());
            reopened.Initialize();
            CharacterRecord loaded = reopened.GetOrCreateCharacter(
                accountId, "Heroe", 1, new Vec2(0, 0), mapHash)!;
            Assert.Equal((ushort)0, loaded.EquippedItemId);
        }

        [Fact]
        public void La_Migracion_De_V1_A_V4_Conserva_Los_Personajes()
        {
            int characterId;
            using (var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                repository.Initialize();
                AccountLoginResult login = repository.Login("migracion", "secreto1", "inv", "inv");
                CharacterRecord character = repository.GetOrCreateCharacter(
                    login.AccountId, "Heroe", 1, new Vec2(1, 1), NewHash(9))!;
                characterId = character.Id;
            }

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "DROP TABLE inventory_items; " +
                    "ALTER TABLE characters DROP COLUMN equipped_item_id; " +
                    "ALTER TABLE characters DROP COLUMN alloc_vit; " +
                    "ALTER TABLE characters DROP COLUMN alloc_int; " +
                    "ALTER TABLE characters DROP COLUMN alloc_dex; " +
                    "ALTER TABLE characters DROP COLUMN alloc_str; " +
                    "ALTER TABLE characters DROP COLUMN class_id; " +
                    "PRAGMA user_version=1;";
                command.ExecuteNonQuery();
            }

            using (var migrated = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                migrated.Initialize();
            }

            using var verify = new SqliteConnection("Data Source=" + DatabasePath);
            verify.Open();
            using SqliteCommand version = verify.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(4, Convert.ToInt32(version.ExecuteScalar()));
            using SqliteCommand characters = verify.CreateCommand();
            characters.CommandText = "SELECT COUNT(*) FROM characters WHERE id=@id;";
            characters.Parameters.AddWithValue("@id", characterId);
            Assert.Equal(1, Convert.ToInt32(characters.ExecuteScalar()));
        }

        [Fact]
        public void La_Migracion_De_V2_A_V4_Conserva_Personaje_E_Inventario()
        {
            int accountId;
            int characterId;
            byte[] mapHash = NewHash(12);
            using (var repository = new SqlitePlayerRepository(
                       DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests(), inventorySlots: 3))
            {
                repository.Initialize();
                accountId = repository.Login("migracion2", "secreto1", "inv", "inv").AccountId;
                CharacterRecord character = repository.GetOrCreateCharacter(
                    accountId, "Heroe", 1, new Vec2(2, 3), mapHash)!;
                characterId = character.Id;
                repository.SaveCharacter(character.WithInventory(new[] { new InventoryItemRecord(1, 3001, 2) }));
            }

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "ALTER TABLE characters DROP COLUMN equipped_item_id; " +
                    "ALTER TABLE characters DROP COLUMN alloc_vit; " +
                    "ALTER TABLE characters DROP COLUMN alloc_int; " +
                    "ALTER TABLE characters DROP COLUMN alloc_dex; " +
                    "ALTER TABLE characters DROP COLUMN alloc_str; " +
                    "ALTER TABLE characters DROP COLUMN class_id; " +
                    "PRAGMA user_version=2;";
                command.ExecuteNonQuery();
            }

            using (var migrated = new SqlitePlayerRepository(
                       DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests(), inventorySlots: 3))
            {
                migrated.Initialize();
                CharacterRecord character = migrated.GetOrCreateCharacter(
                    accountId, "Heroe", 1, new Vec2(0, 0), mapHash)!;
                Assert.Equal(characterId, character.Id);
                Assert.Contains(character.InventoryItems, item => item.Slot == 1 && item.ItemId == 3001 && item.Count == 2);
                Assert.Equal((ushort)0, character.EquippedItemId);
            }

            using var verify = new SqliteConnection("Data Source=" + DatabasePath);
            verify.Open();
            using SqliteCommand version = verify.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(4, Convert.ToInt32(version.ExecuteScalar()));
        }

        [Fact]
        public void La_Migracion_De_V3_A_V4_Conserva_Progreso_Inventario_Y_Arma_Y_Asigna_Clase_1()
        {
            int accountId;
            int characterId;
            byte[] mapHash = NewHash(23);
            using (var repository = new SqlitePlayerRepository(
                       DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests(), inventorySlots: 3))
            {
                repository.Initialize();
                accountId = repository.Login("migracion3", "secreto1", "inv", "inv").AccountId;
                CharacterRecord character = repository.GetOrCreateCharacter(
                    accountId, "Heroe", 1, new Vec2(2, 3), mapHash)!;
                characterId = character.Id;
                repository.SaveCharacter(character
                    .WithState(1, new Vec2(4, 5), mapHash, 70, 73)
                    .WithProgress(4, 275)
                    .WithInventory(new[] { new InventoryItemRecord(1, 3001, 2) })
                    .WithEquippedItem(3101));
            }

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "ALTER TABLE characters DROP COLUMN alloc_vit; " +
                    "ALTER TABLE characters DROP COLUMN alloc_int; " +
                    "ALTER TABLE characters DROP COLUMN alloc_dex; " +
                    "ALTER TABLE characters DROP COLUMN alloc_str; " +
                    "ALTER TABLE characters DROP COLUMN class_id; " +
                    "PRAGMA user_version=3;";
                command.ExecuteNonQuery();
            }

            using (var migrated = new SqlitePlayerRepository(
                       DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests(), inventorySlots: 3))
            {
                migrated.Initialize();
                CharacterRecord loaded = migrated.GetOrCreateCharacter(
                    accountId, "Heroe", 1, new Vec2(0, 0), mapHash)!;
                Assert.Equal(characterId, loaded.Id);
                Assert.Equal(4, loaded.Level);
                Assert.Equal(275, loaded.Experience);
                Assert.Equal(70, loaded.Health);
                Assert.Equal(73, loaded.Mana);
                Assert.Equal((ushort)3101, loaded.EquippedItemId);
                Assert.Contains(loaded.InventoryItems, item => item.Slot == 1 && item.ItemId == 3001 && item.Count == 2);
                Assert.Equal(1, loaded.ClassId);
                Assert.Equal(0, loaded.AllocStr + loaded.AllocDex + loaded.AllocInt + loaded.AllocVit);
            }

            Assert.Equal(4, QuerySchemaVersion());
        }

        [Fact]
        public void Un_Esquema_Desconocido_Sigue_Impidiendo_El_Arranque()
        {
            using (var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                repository.Initialize();
            }

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version=99;";
                command.ExecuteNonQuery();
            }

            using var incompatible = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(incompatible.Initialize);
            Assert.Contains("versión 99", error.Message);
        }

        [Fact]
        public void Filas_De_Inventario_Invalidas_Se_Descartan_Al_Cargar()
        {
            int accountId;
            int characterId;
            byte[] mapHash = NewHash(10);
            using (var repository = new SqlitePlayerRepository(
                       DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests(), inventorySlots: 2))
            {
                repository.Initialize();
                accountId = repository.Login("filas", "secreto1", "inv", "inv").AccountId;
                characterId = repository.GetOrCreateCharacter(
                    accountId, "Heroe", 1, new Vec2(1, 1), mapHash)!.Id;
            }

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = @"
INSERT INTO inventory_items VALUES (@character, 0, 3001, 5);
INSERT INTO inventory_items VALUES (@character, 1, 3999, 1);
INSERT INTO inventory_items VALUES (@character, 2, 3001, 1);
INSERT INTO inventory_items VALUES (@character, 3, 3001, 6);
INSERT INTO inventory_items VALUES (@character, 4, 3001, 0);
INSERT INTO inventory_items VALUES (@character, 5, 9223372036854775807, 1);";
                command.Parameters.AddWithValue("@character", characterId);
                command.ExecuteNonQuery();
            }

            using var reopened = new SqlitePlayerRepository(
                DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests(), inventorySlots: 2);
            reopened.Initialize();
            CharacterRecord loaded = reopened.GetOrCreateCharacter(
                accountId, "Heroe", 1, new Vec2(0, 0), mapHash)!;
            InventoryItemRecord only = Assert.Single(loaded.InventoryItems);
            Assert.Equal(0, only.Slot);
            Assert.Equal(3001, only.ItemId);
            Assert.Equal(5, only.Count);
        }

        [Fact]
        public void La_Vida_Se_Guarda_Y_Se_Carga()
        {
            byte[] hash = NewHash(4);

            using (var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                repository.Initialize();

                AccountLoginResult login = repository.Login(
                    "vida",
                    "secreto1",
                    "avelora-dev",
                    "avelora-dev");

                Assert.True(login.Success);

                CharacterRecord? created = repository.GetOrCreateCharacter(
                    login.AccountId,
                    "Heroe",
                    1,
                    new Vec2(-1.5f, -2.5f),
                    hash);

                Assert.NotNull(created);
                repository.SaveCharacter(created.WithState(1, new Vec2(3.5f, 4.5f), hash, 37));
            }

            using (var reopened = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                reopened.Initialize();

                AccountLoginResult login = reopened.Login("vida", "secreto1", string.Empty, string.Empty);
                Assert.True(login.Success);

                CharacterRecord? loaded = reopened.GetOrCreateCharacter(
                    login.AccountId,
                    "Heroe",
                    1,
                    new Vec2(-1.5f, -2.5f),
                    hash);

                Assert.NotNull(loaded);
                Assert.Equal(37, loaded.Health);
                Assert.Equal(new Vec2(3.5f, 4.5f), loaded.Position);
            }
        }

        [Fact]
        public void Un_Muerto_Se_Guarda_Vivo_Con_La_Vida_Llena_En_El_Spawn()
        {
            using var harness = new ServerHarness(DatabasePath);

            harness.Connect(1);
            harness.Handshake(1, "dev", "avelora-dev", "dev");
            harness.Pump(1);

            PlayerEntity player = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            player.Position = new Vec2(3f, 3f);
            player.Health = 0;
            player.Action = EntityAction.Dead;

            harness.Transport.RaiseDisconnected(1);
            harness.Server.BeforeTick(harness.Zone);
            Assert.True(harness.Saves.Flush(TimeSpan.FromSeconds(5)));

            harness.Connect(2);
            harness.Handshake(2, "dev", "avelora-dev", "dev");
            harness.Pump(2);

            WelcomeMessage welcome = harness.LastWelcome(2);
            Assert.Equal(harness.Config.SpawnX, welcome.SpawnX);
            Assert.Equal(harness.Config.SpawnY, welcome.SpawnY);

            PlayerEntity reconnected = (PlayerEntity)harness.Zone.Entities.Get(welcome.EntityId)!;
            Assert.Equal(reconnected.MaxHealth, reconnected.Health);
        }

        [Fact]
        public void Una_Vida_Guardada_Menor_O_Igual_A_Cero_Entra_Con_La_Vida_Llena()
        {
            byte[] hash = NewHash(5);

            using (var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                repository.Initialize();

                AccountLoginResult login = repository.Login(
                    "cero",
                    "secreto1",
                    "avelora-dev",
                    "avelora-dev");

                Assert.True(login.Success);

                CharacterRecord? created = repository.GetOrCreateCharacter(
                    login.AccountId,
                    "Heroe",
                    1,
                    new Vec2(-1.5f, -2.5f),
                    hash);

                Assert.NotNull(created);
                repository.SaveCharacter(created.WithState(1, new Vec2(-1.5f, -2.5f), hash, 0));
            }

            using var harness = new ServerHarness(DatabasePath);

            harness.Connect(1);
            harness.Handshake(1, "cero", "secreto1", "Heroe");
            harness.Pump(1);

            WelcomeMessage welcome = harness.LastWelcome(1);
            PlayerEntity player = (PlayerEntity)harness.Zone.Entities.Get(welcome.EntityId)!;
            Assert.Equal(player.MaxHealth, player.Health);
        }

        [Fact]
        public void La_Cola_Coalesce_Posiciones_Y_Guarda_La_Ultima()
        {
            var repository = new RecordingRepository();
            repository.Gate.Reset();

            using var saves = new CharacterSaveService(repository);

            try
            {
                // Se espera a que el hilo quede bloqueado escribiendo la primera foto: así las
                // siguientes caen seguro en la cola, sin carrera con el hilo de guardado.
                saves.Enqueue(NewCharacter(1, new Vec2(-1, 1)));
                Assert.True(repository.Entered.Wait(TimeSpan.FromSeconds(5)));

                Vec2 last = default;

                for (int i = 0; i < 5; i++)
                {
                    last = new Vec2(i, -i);
                    saves.Enqueue(NewCharacter(1, last));
                }

                Assert.Equal(1, saves.Pending);
                repository.Gate.Set();

                Assert.True(saves.Flush(TimeSpan.FromSeconds(5)));
                Assert.Equal(0, saves.Pending);
                Assert.Equal(2, repository.Saved.Count);
                Assert.Equal(last, repository.Saved[1].Position);
                Assert.Equal(0L, saves.Failed);
            }
            finally
            {
                // Si un Assert falla con la compuerta cerrada, Dispose esperaría 10 s al hilo.
                repository.Gate.Set();
            }
        }

        [Fact]
        public void El_Servidor_Carga_La_Posicion_Guardada_Al_Reconectar()
        {
            using var harness = new ServerHarness(DatabasePath);

            harness.Connect(1);
            harness.Handshake(1, "dev", "avelora-dev", "dev");
            harness.Pump(1);

            Assert.Equal(1, harness.Zone.Entities.Count);

            PlayerEntity first = (PlayerEntity)harness.Zone.Entities.All[0];
            first.Position = new Vec2(2.5f, -3.5f);

            harness.Transport.RaiseDisconnected(1);
            harness.Server.BeforeTick(harness.Zone);
            Assert.True(harness.Saves.Flush(TimeSpan.FromSeconds(5)));

            harness.Connect(2);
            harness.Handshake(2, "dev", "avelora-dev", "dev");
            harness.Pump(2);

            WelcomeMessage welcome = harness.LastWelcome(2);

            Assert.Equal(2.5f, welcome.SpawnX);
            Assert.Equal(-3.5f, welcome.SpawnY);
            Assert.Equal(1, harness.Zone.Entities.Count);
            Assert.True(harness.Saves.Saved >= 1);
        }

        [Fact]
        public void El_Guardado_Con_Enemigos_Solo_Escribe_Personajes()
        {
            var npcs = new NpcCatalog
            {
                Types = new List<NpcType>
                {
                    new NpcType
                    {
                        Id = 2001,
                        Name = "Orco",
                        Speed = 2f,
                        HalfWidth = 0.35f,
                        HalfHeight = 0.35f,
                        MaxHealth = 30,
                        Experience = 20,
                        Level = 1,
                        RespawnSeconds = 10f,
                        AggroRadius = 0f,
                        LeashRadius = 8f,
                        AttackDamage = 1,
                        AttackRange = 1f,
                        AttackCooldownSeconds = 1.5f,
                    },
                },
                Spawners = new List<NpcSpawner>
                {
                    new NpcSpawner
                    {
                        TypeId = 2001,
                        X = 3.5f,
                        Y = 1.5f,
                        Count = 3,
                        WanderRadius = 2f,
                    },
                },
            };

            using var harness = new ServerHarness(DatabasePath, npcs);

            Assert.Equal(3, harness.Zone.Entities.CountOf(EntityKind.Npc));

            harness.Connect(1);
            harness.Handshake(1, "dev", "avelora-dev", "dev");
            harness.Pump(1);

            Assert.Equal(4, harness.Zone.Entities.Count);

            harness.Server.SaveAll(harness.Zone);
            Assert.True(harness.Saves.Flush(TimeSpan.FromSeconds(5)));

            // Solo el personaje conectado se escribe: los PNJ no pasan por la cola de guardado.
            Assert.Equal(1L, harness.Saves.Saved);
            Assert.Equal(0, harness.Saves.Pending);
        }

        [Fact]
        public void Una_Contrasena_Incorrecta_Se_Rechaza_Sin_Crear_Entidad()
        {
            using var harness = new ServerHarness(DatabasePath);

            harness.Connect(1);
            harness.Handshake(1, "dev", "avelora-dev", "dev");
            harness.Pump(1);

            harness.Transport.RaiseDisconnected(1);
            harness.Server.BeforeTick(harness.Zone);
            Assert.Equal(0, harness.Zone.Entities.Count);

            harness.Connect(2);
            harness.Handshake(2, "dev", "otra-clave", "dev");
            harness.Pump(2);

            Assert.Equal(0, harness.Zone.Entities.Count);
            Assert.True(harness.LastReject(2, out RejectMessage reject));
            Assert.Equal(RejectReason.InvalidCredentials, reject.Reason);

            harness.Server.PollOnce();
            Assert.Contains(2, harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void La_Misma_Cuenta_Desde_Dos_Conexiones_Echa_A_La_Primera()
        {
            using var harness = new ServerHarness(DatabasePath);

            harness.Connect(1);
            harness.Handshake(1, "dev", "avelora-dev", "dev");
            harness.Pump(1);

            PlayerEntity first = (PlayerEntity)harness.Zone.Entities.All[0];
            first.Position = new Vec2(2.5f, -3.5f);

            // Misma cuenta y mismo personaje desde otra conexión: la nueva gana.
            harness.Connect(2);
            harness.Handshake(2, "dev", "avelora-dev", "dev");
            harness.Pump(2);

            // La primera se va con motivo y se cierra; queda una sola entidad, en la posición viva.
            Assert.True(harness.LastReject(1, out RejectMessage reject));
            Assert.Equal(RejectReason.LoggedInElsewhere, reject.Reason);

            harness.Server.PollOnce();
            Assert.Contains(1, harness.Transport.DisconnectedConnections);

            Assert.Equal(1, harness.Zone.Entities.Count);
            WelcomeMessage welcome = harness.LastWelcome(2);
            Assert.Equal(2.5f, welcome.SpawnX);
            Assert.Equal(-3.5f, welcome.SpawnY);

            // La baja tardía de la conexión antigua no suelta la reserva de la nueva: si la
            // soltara, la tercera conexión entraría sin echar a nadie y habría dos sesiones.
            harness.Transport.RaiseDisconnected(1);
            harness.Server.BeforeTick(harness.Zone);

            PlayerEntity second = (PlayerEntity)harness.Zone.Entities.Get(welcome.EntityId)!;
            second.Position = new Vec2(4.5f, 1.5f);

            harness.Connect(3);
            harness.Handshake(3, "dev", "avelora-dev", "dev");
            harness.Pump(3);

            Assert.True(harness.LastReject(2, out RejectMessage secondReject));
            Assert.Equal(RejectReason.LoggedInElsewhere, secondReject.Reason);
            Assert.Equal(1, harness.Zone.Entities.Count);

            WelcomeMessage third = harness.LastWelcome(3);
            Assert.Equal(4.5f, third.SpawnX);
            Assert.Equal(1.5f, third.SpawnY);
        }

        [Fact]
        public void Al_Reconectar_Se_Conservan_La_Vida_Y_El_Inventario_Vivos()
        {
            // Auditoría: antes solo se conservaba la posición. La vida y el inventario salían de la
            // base (hasta un intervalo de guardado atrás) y el siguiente guardado pisaba el bueno.
            using var harness = new ServerHarness(DatabasePath);

            harness.Connect(1);
            harness.Handshake(1, "dev", "avelora-dev", "dev");
            harness.Pump(1);

            PlayerEntity first = (PlayerEntity)harness.Zone.Entities.All[0];
            first.Health = 37;
            first.Inventory[0] = new InventorySlot(3002, 4);

            // 1) La misma cuenta entra desde otra conexión mientras la primera sigue viva.
            harness.Connect(2);
            harness.Handshake(2, "dev", "avelora-dev", "dev");
            harness.Pump(2);

            PlayerEntity second = (PlayerEntity)harness.Zone.Entities.Get(harness.LastWelcome(2).EntityId)!;
            Assert.Equal(37, second.Health);
            Assert.Equal(new InventorySlot(3002, 4), second.Inventory[0]);

            // 2) Sale y vuelve a entrar enseguida: entra con la foto de la salida aunque el login
            // haya leído la base antes de que se escribiera.
            second.Health = 21;
            harness.Transport.RaiseDisconnected(2);
            harness.Server.BeforeTick(harness.Zone);

            harness.Connect(3);
            harness.Handshake(3, "dev", "avelora-dev", "dev");
            harness.Pump(3);

            PlayerEntity third = (PlayerEntity)harness.Zone.Entities.Get(harness.LastWelcome(3).EntityId)!;
            Assert.Equal(21, third.Health);
            Assert.Equal(new InventorySlot(3002, 4), third.Inventory[0]);
        }

        [Fact]
        public void Al_Reconectar_TryGetLatest_Conserva_El_Arma_Equipada()
        {
            using var harness = new ServerHarness(DatabasePath, items: ItemCatalogForTests());
            harness.Connect(1);
            harness.Handshake(1, "dev", "avelora-dev", "dev");
            harness.Pump(1);

            PlayerEntity first = Assert.IsType<PlayerEntity>(harness.Zone.Entities.Get(1));
            first.EquippedWeaponId = 3101;
            first.Level = 4;
            first.Experience = 275;
            first.ClassId = 2;
            first.AllocInt = 2;
            first.Health = 75;
            first.Mana = 95;

            harness.Connect(2);
            harness.Handshake(2, "dev", "avelora-dev", "dev");
            harness.Pump(2);

            PlayerEntity reconnected = Assert.IsType<PlayerEntity>(
                harness.Zone.Entities.Get(harness.LastWelcome(2).EntityId));
            Assert.Equal((ushort)3101, reconnected.EquippedWeaponId);
            Assert.Equal(4, reconnected.Level);
            Assert.Equal(275, reconnected.Experience);
            Assert.Equal(2, reconnected.ClassId);
            Assert.Equal(2, reconnected.AllocInt);
            Assert.Equal(75, reconnected.Health);
            Assert.Equal(95, reconnected.Mana);
            Assert.True(harness.Saves.TryGetLatest(first.CharacterId, out CharacterRecord latest));
            Assert.Equal((ushort)3101, latest.EquippedItemId);
            Assert.Equal(4, latest.Level);
            Assert.Equal(275, latest.Experience);
            Assert.Equal(2, latest.ClassId);
            Assert.Equal(2, latest.AllocInt);
        }

        private int QuerySchemaVersion()
        {
            using var connection = new SqliteConnection("Data Source=" + DatabasePath);
            connection.Open();
            using SqliteCommand query = connection.CreateCommand();
            query.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(query.ExecuteScalar());
        }

        [Fact]
        public void La_Misma_Cuenta_Desde_Dos_Conexiones_Echa_A_La_Conexion_Cero()
        {
            using var harness = new ServerHarness(DatabasePath);

            // LiteNetLib empieza en 0: la conexión 0 es una sesión real, no «sin conexión».
            harness.Connect(0);
            harness.Handshake(0, "dev", "avelora-dev", "dev");
            harness.Pump(0);

            PlayerEntity first = (PlayerEntity)harness.Zone.Entities.All[0];
            first.Position = new Vec2(2.5f, -3.5f);

            // Misma cuenta y mismo personaje desde la conexión 1: la nueva gana.
            harness.Connect(1);
            harness.Handshake(1, "dev", "avelora-dev", "dev");
            harness.Pump(1);

            // La 0 se va con motivo y se cierra; queda una sola entidad, en la posición viva.
            Assert.True(harness.LastReject(0, out RejectMessage reject));
            Assert.Equal(RejectReason.LoggedInElsewhere, reject.Reason);

            harness.Server.PollOnce();
            Assert.Contains(0, harness.Transport.DisconnectedConnections);

            Assert.Equal(1, harness.Zone.Entities.Count);
            WelcomeMessage welcome = harness.LastWelcome(1);
            Assert.Equal(2.5f, welcome.SpawnX);
            Assert.Equal(-3.5f, welcome.SpawnY);
        }

        [Fact]
        public void El_Cuarto_Personaje_De_Una_Cuenta_Se_Rechaza_Y_Los_Tres_Primeros_Siguen_Entrando()
        {
            using var harness = new ServerHarness(DatabasePath);

            string[] nombres = { "uno", "dos", "tres" };

            for (int i = 0; i < nombres.Length; i++)
            {
                int connectionId = i + 1;
                harness.Connect(connectionId);
                harness.Handshake(connectionId, "dev", "avelora-dev", nombres[i]);
                harness.Pump(connectionId);

                Assert.False(harness.LastReject(connectionId, out _));
            }

            harness.Connect(4);
            harness.Handshake(4, "dev", "avelora-dev", "cuatro");
            harness.Pump(4);

            Assert.True(harness.LastReject(4, out RejectMessage reject));
            Assert.Equal(RejectReason.CharacterLimit, reject.Reason);

            // Los tres personajes que ya existían siguen valiendo: el tope no los invalida.
            AccountLoginResult login = harness.Repository.Login(
                "dev",
                "avelora-dev",
                string.Empty,
                "avelora-dev");

            Assert.True(login.Success);
            Assert.NotNull(harness.Repository.GetOrCreateCharacter(
                login.AccountId,
                "uno",
                1,
                new Vec2(-1.5f, -2.5f),
                harness.MapHash));

            harness.Connect(5);
            harness.Handshake(5, "dev", "avelora-dev", "uno");
            harness.Pump(5);

            Assert.False(harness.LastReject(5, out _));
        }

        private static byte[] NewHash(byte seed)
        {
            var hash = new byte[ProtocolConstants.MapHashSize];

            for (int i = 0; i < hash.Length; i++)
            {
                hash[i] = (byte)(seed + i);
            }

            return hash;
        }

        private static CharacterRecord NewCharacter(int id, Vec2 position)
        {
            DateTime now = DateTime.UtcNow;

            return new CharacterRecord(
                id,
                1,
                "personaje" + id,
                1,
                0,
                100,
                100,
                1,
                position,
                NewHash(1),
                now,
                now);
        }

        private sealed class ServerHarness : IDisposable
        {
            private readonly Dictionary<int, byte[]> _challengeNonces = new Dictionary<int, byte[]>();

            public ServerHarness(string databasePath, NpcCatalog? npcs = null, ItemCatalog? items = null)
            {
                Config = new ServerConfig
                {
                    HandshakeTimeoutSeconds = 60d,
                    PersistenceEnabled = true,
                    DatabasePath = databasePath,
                    RegistrationInvitationCode = "avelora-dev",
                    SaveIntervalSeconds = 60d,

                    // Pruebas sin sockets: el corte se pide en la vuelta siguiente y se puede
                    // comprobar con un PollOnce más, sin esperar medio segundo.
                    RejectDisconnectDelayMilliseconds = 0,
                };

                MapCollisionGrid map = MapFile.Load(MapPath);

                Zone = new Zone(
                    1,
                    map,
                    new EntityStore(),
                    new EntityIdAllocator(),
                    new InputQueue(Config.MaxQueuedIntents),
                    new MovementSystem(Config.PlayerSpeed, SimulationConstants.FixedDt),
                    Config.MaxIntentsPerTick,
                    Config.MaxIntentsPerPlayerPerTick,
                    new InterestSettings(Config.InterestRadius, Config.InterestCellSize, Config.InterestHysteresis),
                    npcs,
                    12345,
                    playerVitals: Config.PlayerVitals,
                    items: items,
                    itemActions: items == null ? null : new ItemActionQueue(Config.MaxQueuedItemActions),
                    inventorySlots: Config.InventorySlots);

                MapHash = MapFile.ComputeContentHashBytes(map);
                Transport = new FakeServerTransport();

                // Pocas iteraciones: estas pruebas no miden PBKDF2, solo el flujo de login.
                Repository = new SqlitePlayerRepository(databasePath, passwordIterations: 1_000, items: items);
                Repository.Initialize();
                Saves = new CharacterSaveService(Repository);
                Server = new NetworkServer(
                    Zone,
                    Config,
                    Transport,
                    MapHash,
                    Repository,
                    Saves,
                    Config.RegistrationInvitationCode);
            }

            public ServerConfig Config { get; }

            public Zone Zone { get; }

            public byte[] MapHash { get; }

            public FakeServerTransport Transport { get; }

            public SqlitePlayerRepository Repository { get; }

            public CharacterSaveService Saves { get; }

            public NetworkServer Server { get; }

            public void Connect(int connectionId)
            {
                Transport.RaiseConnected(connectionId);

                for (int i = Transport.Sent.Count - 1; i >= 0; i--)
                {
                    SentMessage sent = Transport.Sent[i];

                    if (sent.ConnectionId == connectionId &&
                        MessageCodec.TryReadChallenge(sent.Data, 0, sent.Data.Length, out ChallengeMessage challenge))
                    {
                        _challengeNonces[connectionId] = challenge.Nonce;
                        Transport.Sent.RemoveAt(i);
                        return;
                    }
                }

                throw new InvalidOperationException(
                    "La conexión " + connectionId + " no recibió el desafío del servidor.");
            }

            public void Handshake(int connectionId, string account, string password, string character)
            {
                byte[] encrypted = HelloSecrets.Encrypt(
                    Server.PublicKey,
                    _challengeNonces[connectionId],
                    password,
                    Config.RegistrationInvitationCode);

                var hello = new HelloMessage(
                    ProtocolConstants.Version,
                    (ushort)SimulationConstants.TickRate,
                    MapHash,
                    1000u + (uint)connectionId,
                    account,
                    encrypted,
                    character);

                Transport.RaiseReceived(connectionId, MessageCodec.EncodeHello(hello));
            }

            /// <summary>
            /// Una vuelta de los dos hilos hasta que la conexión reciba el desenlace de su saludo
            /// (<c>Welcome</c> o <c>Reject</c>). El login ya no es síncrono: corre en su hilo y hay
            /// que esperar a que la red recoja el resultado.
            /// </summary>
            public void Pump(int connectionId)
            {
                long deadline = Environment.TickCount64 + 5_000;

                while (true)
                {
                    Server.BeforeTick(Zone);
                    Server.PollOnce();

                    if (HasOutcome(connectionId))
                    {
                        return;
                    }

                    if (Environment.TickCount64 >= deadline)
                    {
                        throw new TimeoutException(
                            "La conexión " + connectionId + " no recibió Welcome ni Reject a tiempo.");
                    }

                    Thread.Sleep(1);
                }
            }

            private bool HasOutcome(int connectionId)
            {
                for (int i = 0; i < Transport.Sent.Count; i++)
                {
                    SentMessage sent = Transport.Sent[i];

                    if (sent.ConnectionId != connectionId)
                    {
                        continue;
                    }

                    if (MessageCodec.TryReadWelcome(sent.Data, 0, sent.Data.Length, out _) ||
                        MessageCodec.TryReadReject(sent.Data, 0, sent.Data.Length, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            public WelcomeMessage LastWelcome(int connectionId)
            {
                for (int i = Transport.Sent.Count - 1; i >= 0; i--)
                {
                    SentMessage sent = Transport.Sent[i];

                    if (sent.ConnectionId == connectionId &&
                        MessageCodec.TryReadWelcome(sent.Data, 0, sent.Data.Length, out WelcomeMessage welcome))
                    {
                        return welcome;
                    }
                }

                throw new InvalidOperationException("La conexión " + connectionId + " no recibió Welcome.");
            }

            public bool LastReject(int connectionId, out RejectMessage reject)
            {
                for (int i = Transport.Sent.Count - 1; i >= 0; i--)
                {
                    SentMessage sent = Transport.Sent[i];

                    if (sent.ConnectionId == connectionId &&
                        MessageCodec.TryReadReject(sent.Data, 0, sent.Data.Length, out reject))
                    {
                        return true;
                    }
                }

                reject = default;
                return false;
            }

            public void Dispose()
            {
                Server.Dispose();
                Saves.Dispose();
                Repository.Dispose();
            }
        }

        private sealed class RecordingRepository : IPlayerRepository
        {
            private readonly object _gate = new object();

            public ManualResetEventSlim Gate { get; } = new ManualResetEventSlim(true);

            /// <summary>Se activa en cuanto el hilo de guardado entra en <see cref="SaveCharacter"/>.</summary>
            public ManualResetEventSlim Entered { get; } = new ManualResetEventSlim(false);

            public List<CharacterRecord> Saved { get; } = new List<CharacterRecord>();

            public void Initialize()
            {
            }

            public AccountLoginResult Login(
                string accountName,
                string password,
                string invitationCode,
                string registrationCode) =>
                throw new NotSupportedException();

            public CharacterRecord GetOrCreateCharacter(
                int accountId,
                string characterName,
                int zoneId,
                Vec2 spawn,
                byte[] mapHash) =>
                throw new NotSupportedException();

            public void SaveCharacter(CharacterRecord character)
            {
                Entered.Set();
                Gate.Wait();

                lock (_gate)
                {
                    Saved.Add(character);
                }
            }

            public void Dispose()
            {
            }
        }

        private sealed class FakeServerTransport : IServerTransport
        {
            public List<SentMessage> Sent { get; } = new List<SentMessage>();

            public List<int> DisconnectedConnections { get; } = new List<int>();

            public int Port => 0;

            public int ConnectionCount => 0;

            public event TransportConnectedHandler? Connected;

            public event TransportDisconnectedHandler? Disconnected;

            public event TransportMessageHandler? Received;

            public void Start(int port)
            {
            }

            public void Stop()
            {
            }

            public void Poll()
            {
            }

            public void Send(int connectionId, byte[] data, int length, DeliveryMode mode)
            {
                var copy = new byte[length];
                Array.Copy(data, copy, length);
                Sent.Add(new SentMessage(connectionId, copy));
            }

            public void Disconnect(int connectionId, DisconnectReason reason) =>
                DisconnectedConnections.Add(connectionId);

            public void Dispose()
            {
            }

            public void RaiseConnected(int connectionId) => Connected?.Invoke(connectionId);

            public void RaiseDisconnected(int connectionId) =>
                Disconnected?.Invoke(connectionId, DisconnectReason.RemoteClosed);

            public void RaiseReceived(int connectionId, byte[] data) =>
                Received?.Invoke(connectionId, data, 0, data.Length);
        }

        private readonly struct SentMessage
        {
            public SentMessage(int connectionId, byte[] data)
            {
                ConnectionId = connectionId;
                Data = data;
            }

            public int ConnectionId { get; }

            public byte[] Data { get; }
        }
    }
}
