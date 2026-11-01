using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avelora.Net;
using Avelora.Server.Configuration;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Net;
using Avelora.Server.Npcs;
using Avelora.Server.Persistence;
using Avelora.Server.Progression;
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

        private static CharacterRecord? CreateOrLoadCharacterForTest(
            IPlayerRepository repository,
            int accountId,
            string name,
            int zoneId,
            Vec2 spawn,
            byte[] mapHash,
            byte classId = 1)
        {
            CharacterRecord[] existing = repository.ListCharacters(accountId);
            string nameKey = name.ToLowerInvariant();
            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i].Name.ToLowerInvariant() == nameKey)
                {
                    return existing[i];
                }
            }

            CharacterCreateResult result = repository.CreateCharacter(
                accountId,
                name,
                classId,
                10,
                zoneId,
                spawn,
                mapHash);
            return result.Success ? result.Character : null;
        }

        private void ConvertCharactersToVersion5()
        {
            using var connection = new SqliteConnection("Data Source=" + DatabasePath);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA foreign_keys=OFF;
DROP INDEX characters_name_key_unique;
DROP INDEX characters_account_idx;
CREATE TABLE characters_v5 (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    name TEXT NOT NULL COLLATE NOCASE,
    level INTEGER NOT NULL DEFAULT 1,
    experience INTEGER NOT NULL DEFAULT 0,
    health INTEGER NOT NULL DEFAULT 100,
    mana INTEGER NOT NULL DEFAULT 100,
    zone_id INTEGER NOT NULL,
    x REAL NOT NULL,
    y REAL NOT NULL,
    map_hash BLOB NOT NULL,
    equipped_item_id INTEGER NOT NULL DEFAULT 0,
    class_id INTEGER NOT NULL DEFAULT 1,
    alloc_str INTEGER NOT NULL DEFAULT 0,
    alloc_dex INTEGER NOT NULL DEFAULT 0,
    alloc_int INTEGER NOT NULL DEFAULT 0,
    alloc_vit INTEGER NOT NULL DEFAULT 0,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    UNIQUE(account_id, name)
);
INSERT INTO characters_v5
(id, account_id, name, level, experience, health, mana, zone_id, x, y, map_hash, equipped_item_id,
 class_id, alloc_str, alloc_dex, alloc_int, alloc_vit, created_utc, updated_utc)
SELECT id, account_id, name, level, experience, health, mana, zone_id, x, y, map_hash, equipped_item_id,
 class_id, alloc_str, alloc_dex, alloc_int, alloc_vit, created_utc, updated_utc FROM characters;
DROP TABLE characters;
ALTER TABLE characters_v5 RENAME TO characters;
CREATE INDEX characters_account_idx ON characters(account_id);
PRAGMA user_version=5;
PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
        }

        [Fact]
        public void CharacterRecord_Conserva_Las_Validaciones_Y_Copia_Los_Arrays_Al_Crear()
        {
            CharacterRecord Create(
                int id = 1,
                int accountId = 1,
                string name = "Heroe",
                byte[]? mapHash = null,
                InventoryItemRecord[]? inventory = null,
                ushort[]? equipment = null) =>
                new CharacterRecord(
                    id,
                    accountId,
                    name,
                    1,
                    0,
                    100,
                    100,
                    1,
                    new Vec2(0, 0),
                    mapHash ?? NewHash(1),
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    inventory,
                    equipment: equipment);

            CharacterRecord CreateWithHash(byte[]? mapHash) =>
                new CharacterRecord(
                    1,
                    1,
                    "Heroe",
                    1,
                    0,
                    100,
                    100,
                    1,
                    new Vec2(0, 0),
                    mapHash!,
                    DateTime.UtcNow,
                    DateTime.UtcNow);

            Assert.Throws<ArgumentOutOfRangeException>(() => Create(id: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Create(accountId: 0));
            Assert.Throws<ArgumentException>(() => Create(name: " "));
            Assert.Throws<ArgumentException>(() => CreateWithHash(null));
            Assert.Throws<ArgumentException>(() => Create(mapHash: new byte[ProtocolConstants.MapHashSize - 1]));
            Assert.Throws<ArgumentException>(() => Create(equipment: new ushort[ProtocolConstants.EquipSlotCount - 1]));

            byte[] hash = NewHash(2);
            var inventory = new[] { new InventoryItemRecord(0, 3001, 2) };
            var equipment = new ushort[ProtocolConstants.EquipSlotCount];
            equipment[(int)EquipSlot.Weapon] = 3101;
            CharacterRecord record = Create(mapHash: hash, inventory: inventory, equipment: equipment);

            hash[0]++;
            inventory[0] = new InventoryItemRecord(1, 3002, 1);
            equipment[(int)EquipSlot.Weapon] = 0;

            Assert.Equal(NewHash(2), record.MapHash);
            Assert.Equal(new InventoryItemRecord(0, 3001, 2), record.InventoryItems[0]);
            Assert.Equal((ushort)3101, record.Equipment[(int)EquipSlot.Weapon]);

            // `with` también copia y valida: el guardado le pasa los arrays vivos del jugador.
            var live = new ushort[ProtocolConstants.EquipSlotCount];
            live[(int)EquipSlot.Head] = 3301;
            CharacterRecord copy = record with { Equipment = live, InventoryItems = null! };
            live[(int)EquipSlot.Head] = 0;
            Assert.Equal((ushort)3301, copy.Equipment[(int)EquipSlot.Head]);
            Assert.Empty(copy.InventoryItems);
            Assert.Throws<ArgumentException>(() => record with { Equipment = new ushort[3] });
            Assert.Throws<ArgumentException>(() => record with { MapHash = new byte[1] });
        }

        private static ItemCatalog ItemCatalogForTests(string weaponType = "Espada") => new ItemCatalog
        {
            Items = new List<ItemType>
            {
                new ItemType { Id = 3001, Name = "Colmillo", Kind = "material", MaxStack = 5 },
                new ItemType { Id = 3002, Name = "Poción", Kind = "potion", MaxStack = 10, Heal = 30 },
                new ItemType { Id = 3101, Name = "Arma de prueba", Kind = "weapon", WeaponType = weaponType, MaxStack = 1, Damage = 14, Range = 1.2f, CooldownSeconds = 0.8d },
                new ItemType { Id = 3301, Name = "Casco de cuero", Kind = "armor", Slot = "head", MaxStack = 1, Defense = 2 },
                new ItemType { Id = 3308, Name = "Anillo de vigor", Kind = "armor", Slot = "ring", MaxStack = 1, Vit = 2 },
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

                CharacterRecord? created = CreateOrLoadCharacterForTest(repository,
                    accountId,
                    "Heroe",
                    1,
                    new Vec2(-1.5f, -2.5f),
                    mapHash);

                Assert.NotNull(created);
                Assert.Equal(1, created.Level);
                Assert.Equal(new Vec2(-1.5f, -2.5f), created.Position);

                repository.SaveCharacter(created with
                {
                    ZoneId = 1,
                    Position = new Vec2(3.25f, -4.5f),
                    MapHash = NewHash(9),
                    UpdatedUtc = DateTime.UtcNow,
                });
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

                CharacterRecord? loaded = CreateOrLoadCharacterForTest(reopened,
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
        public void Crear_Lista_Y_Borrar_Usa_Clase_Orden_Y_Confirmacion_Exacta()
        {
            using var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            repository.Initialize();
            int accountId = repository.Login("personajes", "secreto1", "inv", "inv").AccountId;
            int otherAccountId = repository.Login("otra-cuenta", "secreto1", "inv", "inv").AccountId;

            CharacterCreateResult firstResult = repository.CreateCharacter(
                accountId, "Primero", 2, 10, 1, new Vec2(1, 2), NewHash(41));
            CharacterCreateResult secondResult = repository.CreateCharacter(
                accountId, "Segundo", 3, 10, 1, new Vec2(3, 4), NewHash(42));

            Assert.True(firstResult.Success);
            Assert.NotNull(firstResult.Character);
            Assert.Equal(2, firstResult.Character.ClassId);
            DerivedCharacterStats mage = CharacterStats.Calculate(ClassCatalog.Default, 2, 1, 0, 0, 0, 0);
            Assert.Equal(mage.MaxHealth, firstResult.Character.Health);
            Assert.Equal(mage.MaxResource, firstResult.Character.Mana);
            Assert.True(secondResult.Success);
            Assert.Equal(new[] { "Primero", "Segundo" }, repository.ListCharacters(accountId).Select(c => c.Name));

            Assert.False(repository.TryGetCharacter(otherAccountId, firstResult.Character.Id, out CharacterRecord? foreign));
            Assert.Null(foreign);
            Assert.True(repository.TryGetCharacter(accountId, firstResult.Character.Id, out CharacterRecord? found));
            Assert.Equal("Primero", found!.Name);

            Assert.Equal(RejectReason.ConfirmMismatch,
                repository.DeleteCharacter(accountId, firstResult.Character.Id, "primero"));
            Assert.Equal(RejectReason.CharacterNotFound,
                repository.DeleteCharacter(otherAccountId, firstResult.Character.Id, "Primero"));
            Assert.Equal(RejectReason.Unknown,
                repository.DeleteCharacter(accountId, firstResult.Character.Id, "Primero"));
            Assert.False(repository.TryGetCharacter(accountId, firstResult.Character.Id, out _));
            Assert.Equal(new[] { "Segundo" }, repository.ListCharacters(accountId).Select(c => c.Name));

            CharacterCreateResult reused = repository.CreateCharacter(
                otherAccountId, "primero", 1, 10, 1, new Vec2(0, 0), NewHash(43));
            Assert.True(reused.Success);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Guardar_Personaje_Borrado_No_Lanza_Ni_Lo_Recupera(bool sqlite)
        {
            using IPlayerRepository repository = sqlite
                ? new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000)
                : new InMemoryPlayerRepository(ClassCatalog.Default, 1);
            repository.Initialize();
            int accountId = repository.Login("personajes", "secreto1", "inv", "inv").AccountId;
            CharacterRecord character = repository.CreateCharacter(
                accountId, "Ana", 1, 3, 1, new Vec2(1, 2), NewHash(41)).Character!;
            Assert.Equal(RejectReason.Unknown, repository.DeleteCharacter(accountId, character.Id, character.Name));

            repository.SaveCharacter(character);

            Assert.Empty(repository.ListCharacters(accountId));
            Assert.False(repository.TryGetCharacter(accountId, character.Id, out _));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Guardar_Personaje_Inexistente_Lanza(bool sqlite)
        {
            using IPlayerRepository repository = sqlite
                ? new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000)
                : new InMemoryPlayerRepository(ClassCatalog.Default, 1);
            repository.Initialize();
            int accountId = repository.Login("personajes", "secreto1", "inv", "inv").AccountId;
            CharacterRecord character = repository.CreateCharacter(
                accountId, "Ana", 1, 3, 1, new Vec2(1, 2), NewHash(41)).Character!;

            Assert.Throws<InvalidOperationException>(() =>
                repository.SaveCharacter(character with { Id = character.Id + 100 }));
            Assert.Single(repository.ListCharacters(accountId));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Guardar_Personaje_De_Otra_Cuenta_Lanza_Incluso_Si_Esta_Borrado(bool sqlite, bool deleted)
        {
            using IPlayerRepository repository = sqlite
                ? new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000)
                : new InMemoryPlayerRepository(ClassCatalog.Default, 1);
            repository.Initialize();
            int accountId = repository.Login("personajes", "secreto1", "inv", "inv").AccountId;
            int otherAccountId = repository.Login("otra-cuenta", "secreto1", "inv", "inv").AccountId;
            CharacterRecord character = repository.CreateCharacter(
                accountId, "Ana", 1, 3, 1, new Vec2(1, 2), NewHash(41)).Character!;
            if (deleted)
            {
                Assert.Equal(RejectReason.Unknown, repository.DeleteCharacter(accountId, character.Id, character.Name));
            }

            Assert.Throws<InvalidOperationException>(() =>
                repository.SaveCharacter(character with { AccountId = otherAccountId }));
            Assert.Empty(repository.ListCharacters(otherAccountId));
            Assert.Equal(!deleted, repository.TryGetCharacter(accountId, character.Id, out _));
        }

        [Fact]
        public void Crear_Rechaza_Clase_Desconocida_Limite_Y_Nombres_Globales_Sin_Distinguir_Tildes()
        {
            using var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            repository.Initialize();
            int firstAccount = repository.Login("cuenta-a", "secreto1", "inv", "inv").AccountId;
            int secondAccount = repository.Login("cuenta-b", "secreto1", "inv", "inv").AccountId;

            CharacterCreateResult unknownClass = repository.CreateCharacter(
                firstAccount, "Personaje", 99, 10, 1, new Vec2(0, 0), NewHash(44));
            Assert.False(unknownClass.Success);
            Assert.Equal(RejectReason.ClassUnknown, unknownClass.Failure);

            CharacterCreateResult first = repository.CreateCharacter(
                firstAccount, "Ána", 1, 1, 1, new Vec2(0, 0), NewHash(45));
            Assert.True(first.Success);
            CharacterCreateResult sameName = repository.CreateCharacter(
                secondAccount, "ána", 1, 10, 1, new Vec2(0, 0), NewHash(46));
            Assert.False(sameName.Success);
            Assert.Equal(RejectReason.NameTaken, sameName.Failure);

            CharacterCreateResult overLimit = repository.CreateCharacter(
                firstAccount, "Otro", 1, 1, 1, new Vec2(0, 0), NewHash(47));
            Assert.False(overLimit.Success);
            Assert.Equal(RejectReason.CharacterLimit, overLimit.Failure);

            Assert.Equal(RejectReason.Unknown,
                repository.DeleteCharacter(firstAccount, first.Character!.Id, "Ána"));
            CharacterCreateResult afterDelete = repository.CreateCharacter(
                firstAccount, "Otro", 1, 1, 1, new Vec2(0, 0), NewHash(55));
            Assert.True(afterDelete.Success);
        }

        [Fact]
        public void Borrado_Logico_Conserva_Inventario_Y_Equipo_Y_Libera_El_Nombre()
        {
            using var repository = new SqlitePlayerRepository(
                DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests());
            repository.Initialize();
            int accountId = repository.Login("borrado", "secreto1", "inv", "inv").AccountId;
            CharacterRecord character = repository.CreateCharacter(
                accountId, "Conservado", 1, 10, 1, new Vec2(0, 0), NewHash(48)).Character!;
            var equipment = new ushort[ProtocolConstants.EquipSlotCount];
            equipment[(int)EquipSlot.Weapon] = 3101;
            repository.SaveCharacter(character with
            {
                InventoryItems = new[] { new InventoryItemRecord(0, 3001, 2) },
                Equipment = equipment,
            });

            Assert.Equal(RejectReason.Unknown,
                repository.DeleteCharacter(accountId, character.Id, character.Name));
            Assert.False(repository.TryGetCharacter(accountId, character.Id, out _));
            CharacterRecord replacement = repository.CreateCharacter(
                accountId, "Conservado", 1, 10, 1, new Vec2(1, 1), NewHash(49)).Character!;
            Assert.NotEqual(character.Id, replacement.Id);

            using var connection = new SqliteConnection("Data Source=" + DatabasePath);
            connection.Open();
            using SqliteCommand counts = connection.CreateCommand();
            counts.CommandText = @"
SELECT (SELECT COUNT(*) FROM inventory_items WHERE character_id=@id),
       (SELECT COUNT(*) FROM character_equipment WHERE character_id=@id),
       (SELECT deleted_utc IS NOT NULL FROM characters WHERE id=@id);";
            counts.Parameters.AddWithValue("@id", character.Id);
            using SqliteDataReader reader = counts.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt32(2));
        }

        [Fact]
        public async Task Creaciones_Concurrentes_Del_Mismo_Nombre_Solo_Aceptan_Una()
        {
            using var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            repository.Initialize();
            int firstAccount = repository.Login("concurrente-a", "secreto1", "inv", "inv").AccountId;
            int secondAccount = repository.Login("concurrente-b", "secreto1", "inv", "inv").AccountId;
            using var barrier = new Barrier(2);

            Task<CharacterCreateResult> Create(int accountId) => Task.Run(() =>
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
                return repository.CreateCharacter(accountId, "Carrera", 1, 10, 1, new Vec2(0, 0), NewHash(50));
            });

            Task<CharacterCreateResult> first = Create(firstAccount);
            Task<CharacterCreateResult> second = Create(secondAccount);
            CharacterCreateResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, (results[0].Success ? 1 : 0) + (results[1].Success ? 1 : 0));
            CharacterCreateResult rejected = results[0].Success ? results[1] : results[0];
            Assert.Equal(RejectReason.NameTaken, rejected.Failure);
        }

        [Fact]
        public void Migracion_V5_Renombra_Claves_Duplicadas_Y_Avisa_Sin_Cambiar_Nombres_Invalidos()
        {
            int firstAccount;
            int secondAccount;
            int thirdAccount;
            int fourthAccount;
            using (var repository = new SqlitePlayerRepository(
                       DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests()))
            {
                repository.Initialize();
                firstAccount = repository.Login("migrar-a", "secreto1", "inv", "inv").AccountId;
                secondAccount = repository.Login("migrar-b", "secreto1", "inv", "inv").AccountId;
                thirdAccount = repository.Login("migrar-c", "secreto1", "inv", "inv").AccountId;
                fourthAccount = repository.Login("migrar-d", "secreto1", "inv", "inv").AccountId;
                repository.CreateCharacter(firstAccount, "Ana", 1, 10, 1, new Vec2(0, 0), NewHash(51));
                repository.CreateCharacter(secondAccount, "Otra", 1, 10, 1, new Vec2(0, 0), NewHash(52));
                repository.CreateCharacter(thirdAccount, "Bad_Name", 1, 10, 1, new Vec2(0, 0), NewHash(53));
                repository.CreateCharacter(fourthAccount, "Ana2", 1, 10, 1, new Vec2(0, 0), NewHash(55));
                CharacterRecord ana = Assert.Single(repository.ListCharacters(firstAccount));
                var equipment = new ushort[ProtocolConstants.EquipSlotCount];
                equipment[(int)EquipSlot.Weapon] = 3101;
                equipment[(int)EquipSlot.Head] = 3301;
                repository.SaveCharacter(ana with
                {
                    InventoryItems = new[] { new InventoryItemRecord(2, 3001, 4) },
                    Equipment = equipment,
                });
            }

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand update = connection.CreateCommand();
                update.CommandText = "UPDATE characters SET name='ANA' WHERE account_id=@account;";
                update.Parameters.AddWithValue("@account", secondAccount);
                update.ExecuteNonQuery();
            }

            ConvertCharactersToVersion5();
            TextWriter previousError = Console.Error;
            using var warningLog = new StringWriter(CultureInfo.InvariantCulture);
            try
            {
                Console.SetError(warningLog);
                using var migrated = new SqlitePlayerRepository(
                    DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests());
                migrated.Initialize();
                CharacterRecord migratedAna = Assert.Single(migrated.ListCharacters(firstAccount));
                Assert.Equal("Ana", migratedAna.Name);
                Assert.Contains(migratedAna.InventoryItems,
                    item => item.Slot == 2 && item.ItemId == 3001 && item.Count == 4);
                Assert.Equal((ushort)3101, migratedAna.Equipment[(int)EquipSlot.Weapon]);
                Assert.Equal((ushort)3301, migratedAna.Equipment[(int)EquipSlot.Head]);
                Assert.Equal("ANA3", Assert.Single(migrated.ListCharacters(secondAccount)).Name);
                Assert.Equal("Bad_Name", Assert.Single(migrated.ListCharacters(thirdAccount)).Name);
                Assert.Equal("Ana2", Assert.Single(migrated.ListCharacters(fourthAccount)).Name);
                Assert.Equal(7, QuerySchemaVersion());
            }
            finally
            {
                Console.SetError(previousError);
            }

            Assert.Contains("renombró", warningLog.ToString());
            Assert.Contains("no cumple las reglas nuevas", warningLog.ToString());
        }

        [Fact]
        public void Migracion_V5_Sin_Duplicados_Rellena_Las_Claves_Y_Crea_Indice_Parcial()
        {
            int accountId;
            using (var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                repository.Initialize();
                accountId = repository.Login("migrar-solo", "secreto1", "inv", "inv").AccountId;
                repository.CreateCharacter(accountId, "Único", 1, 10, 1, new Vec2(0, 0), NewHash(54));
            }

            ConvertCharactersToVersion5();
            using (var migrated = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                migrated.Initialize();
                CharacterRecord character = Assert.Single(migrated.ListCharacters(accountId));
                Assert.Equal("único", ReadNameKey(character.Id));
                Assert.Equal(7, QuerySchemaVersion());
            }

            Assert.True(IndexIsPartialAndUnique());
        }

        [Fact]
        public void Al_Cargar_Se_Corrigen_Level_Y_Experience_Invalidos_Sin_Cambiar_El_Esquema()
        {
            using var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            repository.Initialize();
            AccountLoginResult login = repository.Login("cuenta", "secreto1", "invita", "invita");
            byte[] mapHash = NewHash(4);
            Vec2 spawn = new Vec2(1.5f, 1.5f);

            CharacterRecord character = CreateOrLoadCharacterForTest(repository,
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

            CharacterRecord Load() => CreateOrLoadCharacterForTest(repository,
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
            CharacterRecord character = CreateOrLoadCharacterForTest(repository,
                login.AccountId, "personaje", 1, spawn, mapHash)!;

            repository.SaveCharacter(character with
            {
                Level = 4,
                Experience = 275,
                UpdatedUtc = DateTime.UtcNow,
            });

            CharacterRecord loaded = CreateOrLoadCharacterForTest(repository,
                login.AccountId, "personaje", 1, spawn, mapHash)!;
            Assert.Equal(4, loaded.Level);
            Assert.Equal(275, loaded.Experience);
            Assert.Equal(SqlitePlayerRepository.SchemaVersion, QuerySchemaVersion());
        }

        [Fact]
        public void Oro_Se_Guarda_Se_Carga_Y_Se_Recorta_Al_Rango_Permitido()
        {
            using var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            repository.Initialize();
            int accountId = repository.Login("oro", "secreto1", "invita", "invita").AccountId;
            CharacterRecord character = CreateOrLoadCharacterForTest(
                repository, accountId, "Aureo", 1, new Vec2(1.5f, 1.5f), NewHash(76))!;

            repository.SaveCharacter(character with { Gold = 12345 });
            Assert.Equal(12345, Assert.Single(repository.ListCharacters(accountId)).Gold);

            void SetGold(long gold)
            {
                using var connection = new SqliteConnection("Data Source=" + DatabasePath);
                connection.Open();
                using SqliteCommand update = connection.CreateCommand();
                update.CommandText = "UPDATE characters SET gold=@gold WHERE id=@id;";
                update.Parameters.AddWithValue("@gold", gold);
                update.Parameters.AddWithValue("@id", character.Id);
                update.ExecuteNonQuery();
            }

            SetGold(-1);
            Assert.Equal(0, Assert.Single(repository.ListCharacters(accountId)).Gold);
            SetGold((long)PlayerEntity.MaxGold + 1);
            Assert.Equal(PlayerEntity.MaxGold, Assert.Single(repository.ListCharacters(accountId)).Gold);
        }

        [Fact]
        public void Migracion_V6_A_V7_Añade_Oro_Cero_Y_Conserva_El_Personaje()
        {
            using var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            repository.Initialize();
            int accountId = repository.Login("migrav6", "secreto1", "invita", "invita").AccountId;
            CharacterRecord character = CreateOrLoadCharacterForTest(
                repository, accountId, "Persistido", 1, new Vec2(1.5f, 1.5f), NewHash(77))!;
            repository.SaveCharacter(character with { Level = 4, Gold = 2345 });

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand downgrade = connection.CreateCommand();
                downgrade.CommandText = "ALTER TABLE characters DROP COLUMN gold; PRAGMA user_version=6;";
                downgrade.ExecuteNonQuery();
            }

            using var migrated = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            migrated.Initialize();
            CharacterRecord restored = Assert.Single(migrated.ListCharacters(accountId));
            Assert.Equal(4, restored.Level);
            Assert.Equal(0, restored.Gold);
            Assert.Equal(7, QuerySchemaVersion());
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
                CharacterRecord character = CreateOrLoadCharacterForTest(repository,
                    accountId, "Heroe", 1, new Vec2(1, 1), mapHash)!;
                repository.SaveCharacter(character with
                {
                    ClassId = 2,
                    AllocStr = 0,
                    AllocDex = 0,
                    AllocInt = 2,
                    AllocVit = 0,
                    ZoneId = 1,
                    Position = new Vec2(2, 3),
                    MapHash = (byte[])mapHash.Clone(),
                    Health = 75,
                    Mana = 95,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }

            using var reopened = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            reopened.Initialize();
            int loadedAccount = reopened.Login("clase", "secreto1", "", "").AccountId;
            CharacterRecord loaded = CreateOrLoadCharacterForTest(reopened,
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
                characterId = CreateOrLoadCharacterForTest(repository,
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
            CharacterRecord loaded = CreateOrLoadCharacterForTest(reopened,
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
                CharacterRecord character = CreateOrLoadCharacterForTest(repository,
                    login.AccountId, "Heroe", 1, new Vec2(1, 1), mapHash)!;
                repository.SaveCharacter(character with
                {
                    InventoryItems = new[]
                    {
                        new InventoryItemRecord(0, 3001, 5),
                        new InventoryItemRecord(2, 3002, 1),
                    },
                    UpdatedUtc = DateTime.UtcNow,
                });
            }

            using var reopened = new SqlitePlayerRepository(
                DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests(), inventorySlots: 3);
            reopened.Initialize();
            AccountLoginResult again = reopened.Login("bolsa", "secreto1", "", "");
            CharacterRecord loaded = CreateOrLoadCharacterForTest(reopened,
                again.AccountId, "Heroe", 1, new Vec2(0, 0), mapHash)!;
            Assert.Equal(2, loaded.InventoryItems.Length);
            Assert.Contains(loaded.InventoryItems, item => item.Slot == 0 && item.ItemId == 3001 && item.Count == 5);
            Assert.Contains(loaded.InventoryItems, item => item.Slot == 2 && item.ItemId == 3002 && item.Count == 1);
        }

        [Fact]
        public void El_Equipo_De_Varios_Huecos_Se_Guarda_Y_Se_Carga()
        {
            byte[] mapHash = NewHash(14);
            using (var repository = new SqlitePlayerRepository(
                       DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests()))
            {
                repository.Initialize();
                int accountId = repository.Login("equipo", "secreto1", "inv", "inv").AccountId;
                CharacterRecord character = CreateOrLoadCharacterForTest(repository,
                    accountId, "Heroe", 1, new Vec2(1, 1), mapHash)!;
                var equipment = new ushort[ProtocolConstants.EquipSlotCount];
                equipment[(int)EquipSlot.Weapon] = 3101;
                equipment[(int)EquipSlot.Head] = 3301;
                equipment[(int)EquipSlot.Ring] = 3308;
                repository.SaveCharacter(character with
                {
                    InventoryItems = new[] { new InventoryItemRecord(1, 3001, 2) },
                    Equipment = (ushort[])equipment.Clone(),
                    UpdatedUtc = DateTime.UtcNow,
                });
            }

            using var reopened = new SqlitePlayerRepository(
                DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests());
            reopened.Initialize();
            int loadedAccount = reopened.Login("equipo", "secreto1", "", "").AccountId;
            CharacterRecord loaded = CreateOrLoadCharacterForTest(reopened,
                loadedAccount, "Heroe", 1, new Vec2(0, 0), mapHash)!;
            Assert.Equal((ushort)3101, loaded.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal((ushort)3301, loaded.Equipment[(int)EquipSlot.Head]);
            Assert.Equal((ushort)3308, loaded.Equipment[(int)EquipSlot.Ring]);
            Assert.Contains(loaded.InventoryItems, item => item.Slot == 1 && item.ItemId == 3001 && item.Count == 2);
        }

        [Fact]
        public void La_Vida_Que_Da_El_Equipo_No_Se_Recorta_Al_Cargar()
        {
            byte[] mapHash = NewHash(16);
            int fullHealth;
            using (var repository = new SqlitePlayerRepository(
                       DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests()))
            {
                repository.Initialize();
                int accountId = repository.Login("anillo", "secreto1", "inv", "inv").AccountId;
                CharacterRecord character = CreateOrLoadCharacterForTest(repository,
                    accountId, "Heroe", 1, new Vec2(1, 1), mapHash)!;
                var equipment = new ushort[ProtocolConstants.EquipSlotCount];
                equipment[(int)EquipSlot.Ring] = 3308; // +2 VIT = +10 de vida máxima
                fullHealth = CharacterStats.Calculate(
                    ClassCatalog.Default, character.ClassId, 1, 0, 0, 0, 0, ItemCatalogForTests(), equipment).MaxHealth;
                repository.SaveCharacter(character with
                {
                    Health = fullHealth,
                    Equipment = (ushort[])equipment.Clone(),
                    UpdatedUtc = DateTime.UtcNow,
                });
            }

            using var reopened = new SqlitePlayerRepository(
                DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests());
            reopened.Initialize();
            int loadedAccount = reopened.Login("anillo", "secreto1", "", "").AccountId;
            CharacterRecord loaded = CreateOrLoadCharacterForTest(reopened,
                loadedAccount, "Heroe", 1, new Vec2(0, 0), mapHash)!;
            Assert.Equal(fullHealth, loaded.Health);
        }

        [Theory]
        [InlineData(9999)]
        [InlineData(3001)]
        [InlineData(3301)]
        public void Equipo_Desconocido_O_Que_No_Cabe_En_El_Hueco_Se_Descarta(int equippedItemId)
        {
            byte[] mapHash = NewHash(15);
            int accountId;
            int characterId;
            using (var repository = new SqlitePlayerRepository(
                       DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests()))
            {
                repository.Initialize();
                accountId = repository.Login("equipo-invalido", "secreto1", "inv", "inv").AccountId;
                characterId = CreateOrLoadCharacterForTest(repository,
                    accountId, "Heroe", 1, new Vec2(1, 1), mapHash)!.Id;
            }

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand update = connection.CreateCommand();
                update.CommandText =
                    "INSERT INTO character_equipment (character_id, slot, item_id) VALUES (@id, 0, @item);";
                update.Parameters.AddWithValue("@item", equippedItemId);
                update.Parameters.AddWithValue("@id", characterId);
                update.ExecuteNonQuery();
            }

            using var reopened = new SqlitePlayerRepository(
                DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests());
            reopened.Initialize();
            CharacterRecord loaded = CreateOrLoadCharacterForTest(reopened,
                accountId, "Heroe", 1, new Vec2(0, 0), mapHash)!;
            Assert.Equal((ushort)0, loaded.Equipment[(int)EquipSlot.Weapon]);
        }

        [Fact]
        public void La_Migracion_De_V1_A_V5_Conserva_Los_Personajes()
        {
            int characterId;
            using (var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                repository.Initialize();
                AccountLoginResult login = repository.Login("migracion", "secreto1", "inv", "inv");
                CharacterRecord character = CreateOrLoadCharacterForTest(repository,
                    login.AccountId, "Heroe", 1, new Vec2(1, 1), NewHash(9))!;
                characterId = character.Id;
            }

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "DROP TABLE inventory_items; " +
                    "DROP TABLE character_equipment; " +
                    "DROP INDEX characters_name_key_unique; " +
                    "ALTER TABLE characters DROP COLUMN name_key; " +
                    "ALTER TABLE characters DROP COLUMN deleted_utc; " +
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
            Assert.Equal(7, Convert.ToInt32(version.ExecuteScalar()));
            using SqliteCommand characters = verify.CreateCommand();
            characters.CommandText = "SELECT COUNT(*) FROM characters WHERE id=@id;";
            characters.Parameters.AddWithValue("@id", characterId);
            Assert.Equal(1, Convert.ToInt32(characters.ExecuteScalar()));
        }

        [Fact]
        public void La_Migracion_De_V2_A_V5_Conserva_Personaje_E_Inventario()
        {
            int accountId;
            int characterId;
            byte[] mapHash = NewHash(12);
            using (var repository = new SqlitePlayerRepository(
                       DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests(), inventorySlots: 3))
            {
                repository.Initialize();
                accountId = repository.Login("migracion2", "secreto1", "inv", "inv").AccountId;
                CharacterRecord character = CreateOrLoadCharacterForTest(repository,
                    accountId, "Heroe", 1, new Vec2(2, 3), mapHash)!;
                characterId = character.Id;
                repository.SaveCharacter(character with
                {
                    InventoryItems = new[] { new InventoryItemRecord(1, 3001, 2) },
                    UpdatedUtc = DateTime.UtcNow,
                });
            }

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "DROP TABLE character_equipment; " +
                    "DROP INDEX characters_name_key_unique; " +
                    "ALTER TABLE characters DROP COLUMN name_key; " +
                    "ALTER TABLE characters DROP COLUMN deleted_utc; " +
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
                CharacterRecord character = CreateOrLoadCharacterForTest(migrated,
                    accountId, "Heroe", 1, new Vec2(0, 0), mapHash)!;
                Assert.Equal(characterId, character.Id);
                Assert.Contains(character.InventoryItems, item => item.Slot == 1 && item.ItemId == 3001 && item.Count == 2);
                Assert.Equal((ushort)0, character.Equipment[(int)EquipSlot.Weapon]);
            }

            using var verify = new SqliteConnection("Data Source=" + DatabasePath);
            verify.Open();
            using SqliteCommand version = verify.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(7, Convert.ToInt32(version.ExecuteScalar()));
        }

        [Fact]
        public void La_Migracion_De_V3_A_V5_Conserva_Progreso_Inventario_Y_Arma_Y_Asigna_Clase_1()
        {
            int accountId;
            int characterId;
            byte[] mapHash = NewHash(23);
            using (var repository = new SqlitePlayerRepository(
                       DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests(), inventorySlots: 3))
            {
                repository.Initialize();
                accountId = repository.Login("migracion3", "secreto1", "inv", "inv").AccountId;
                CharacterRecord character = CreateOrLoadCharacterForTest(repository,
                    accountId, "Heroe", 1, new Vec2(2, 3), mapHash)!;
                characterId = character.Id;
                var equipment = new ushort[ProtocolConstants.EquipSlotCount];
                equipment[(int)EquipSlot.Weapon] = 3101;
                repository.SaveCharacter(character with
                {
                    ZoneId = 1,
                    Position = new Vec2(4, 5),
                    MapHash = (byte[])mapHash.Clone(),
                    Health = 70,
                    Mana = 73,
                    Level = 4,
                    Experience = 275,
                    InventoryItems = new[] { new InventoryItemRecord(1, 3001, 2) },
                    Equipment = equipment,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "DROP TABLE character_equipment; " +
                    "UPDATE characters SET equipped_item_id=3101; " +
                    "DROP INDEX characters_name_key_unique; " +
                    "ALTER TABLE characters DROP COLUMN name_key; " +
                    "ALTER TABLE characters DROP COLUMN deleted_utc; " +
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
                CharacterRecord loaded = CreateOrLoadCharacterForTest(migrated,
                    accountId, "Heroe", 1, new Vec2(0, 0), mapHash)!;
                Assert.Equal(characterId, loaded.Id);
                Assert.Equal(4, loaded.Level);
                Assert.Equal(275, loaded.Experience);
                Assert.Equal(70, loaded.Health);
                Assert.Equal(73, loaded.Mana);
                Assert.Equal((ushort)3101, loaded.Equipment[(int)EquipSlot.Weapon]);
                Assert.Contains(loaded.InventoryItems, item => item.Slot == 1 && item.ItemId == 3001 && item.Count == 2);
                Assert.Equal(1, loaded.ClassId);
                Assert.Equal(0, loaded.AllocStr + loaded.AllocDex + loaded.AllocInt + loaded.AllocVit);
            }

            Assert.Equal(7, QuerySchemaVersion());
        }

        [Fact]
        public void La_Migracion_De_V4_A_V5_Mueve_El_Arma_Legada_Al_Hueco_Weapon()
        {
            byte[] mapHash = NewHash(24);
            int accountId;
            using (var repository = new SqlitePlayerRepository(
                       DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests()))
            {
                repository.Initialize();
                accountId = repository.Login("migracion-v4", "secreto1", "inv", "inv").AccountId;
                CreateOrLoadCharacterForTest(repository,accountId, "Heroe", 1, new Vec2(1, 1), mapHash);
            }

            using (var connection = new SqliteConnection("Data Source=" + DatabasePath))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = @"
DROP TABLE character_equipment;
UPDATE characters SET equipped_item_id = 3101;
DROP INDEX characters_name_key_unique;
ALTER TABLE characters DROP COLUMN name_key;
ALTER TABLE characters DROP COLUMN deleted_utc;
PRAGMA user_version = 4;";
                command.ExecuteNonQuery();
            }

            using var migrated = new SqlitePlayerRepository(
                DatabasePath, passwordIterations: 1_000, items: ItemCatalogForTests());
            migrated.Initialize();
            CharacterRecord loaded = CreateOrLoadCharacterForTest(migrated,
                accountId, "Heroe", 1, new Vec2(0, 0), mapHash)!;

            Assert.Equal(7, QuerySchemaVersion());
            Assert.Equal((ushort)3101, loaded.Equipment[(int)EquipSlot.Weapon]);
            Assert.All(loaded.Equipment.Skip(1), itemId => Assert.Equal((ushort)0, itemId));
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
                characterId = CreateOrLoadCharacterForTest(repository,
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
            CharacterRecord loaded = CreateOrLoadCharacterForTest(reopened,
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

                CharacterRecord? created = CreateOrLoadCharacterForTest(repository,
                    login.AccountId,
                    "Heroe",
                    1,
                    new Vec2(-1.5f, -2.5f),
                    hash);

                Assert.NotNull(created);
                repository.SaveCharacter(created with
                {
                    ZoneId = 1,
                    Position = new Vec2(3.5f, 4.5f),
                    MapHash = (byte[])hash.Clone(),
                    Health = 37,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }

            using (var reopened = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                reopened.Initialize();

                AccountLoginResult login = reopened.Login("vida", "secreto1", string.Empty, string.Empty);
                Assert.True(login.Success);

                CharacterRecord? loaded = CreateOrLoadCharacterForTest(reopened,
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

                CharacterRecord? created = CreateOrLoadCharacterForTest(repository,
                    login.AccountId,
                    "Heroe",
                    1,
                    new Vec2(-1.5f, -2.5f),
                    hash);

                Assert.NotNull(created);
                repository.SaveCharacter(created with
                {
                    ZoneId = 1,
                    Position = new Vec2(-1.5f, -2.5f),
                    MapHash = (byte[])hash.Clone(),
                    Health = 0,
                    UpdatedUtc = DateTime.UtcNow,
                });
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
        public void La_Experiencia_Penalizada_Por_Pnj_Se_Conserva_Al_Desconectar_Y_Reconectar()
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
                        AggroRadius = 4f,
                        LeashRadius = 8f,
                        AttackDamage = 1000,
                        AttackRange = 1.5f,
                        AttackCooldownSeconds = 1.5f,
                    },
                },
                Spawners = new List<NpcSpawner>
                {
                    new NpcSpawner { TypeId = 2001, X = -1.5f, Y = -2.5f, Count = 1, WanderRadius = 1f },
                },
            };
            using var harness = new ServerHarness(DatabasePath, npcs);
            harness.Connect(1);
            harness.Handshake(1, "dev", "avelora-dev", "dev");
            harness.Pump(1);

            WelcomeMessage welcome = harness.LastWelcome(1);
            PlayerEntity player = Assert.IsType<PlayerEntity>(harness.Zone.Entities.Get(welcome.EntityId));
            player.Experience = 50;
            player.Health = 1;
            harness.Zone.Tick();
            harness.Zone.Tick();
            harness.Zone.Tick();
            Assert.Equal(EntityAction.Dead, player.Action);
            Assert.Equal(40, player.Experience);

            harness.Transport.RaiseDisconnected(1);
            harness.Server.PollOnce();
            harness.Server.BeforeTick(harness.Zone);
            Assert.True(harness.Saves.Flush(TimeSpan.FromSeconds(5)));

            harness.Connect(2);
            harness.Handshake(2, "dev", "avelora-dev", "dev");
            harness.Pump(2);

            WelcomeMessage reconnectedWelcome = harness.LastWelcome(2);
            PlayerEntity reconnected = Assert.IsType<PlayerEntity>(
                harness.Zone.Entities.Get(reconnectedWelcome.EntityId));
            Assert.Equal(1, reconnected.Level);
            Assert.Equal(40, reconnected.Experience);
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
            using var harness = new ServerHarness(DatabasePath, items: ItemCatalogForTests("Bastón"));
            harness.Connect(1);
            harness.Handshake(1, "dev", "avelora-dev", "dev");
            harness.Pump(1);

            PlayerEntity first = Assert.IsType<PlayerEntity>(harness.Zone.Entities.Get(1));
            first.EquippedWeaponId = 3101;
            first.Equipment[(int)EquipSlot.Head] = 3301;
            first.Equipment[(int)EquipSlot.Ring] = 3308;
            first.Level = 4;
            first.Experience = 275;
            first.ClassId = 2;
            first.AllocInt = 2;
            first.Health = 75;
            first.Mana = 95;
            first.Gold = 12345;

            harness.Connect(2);
            harness.Handshake(2, "dev", "avelora-dev", "dev");
            harness.Pump(2);

            PlayerEntity reconnected = Assert.IsType<PlayerEntity>(
                harness.Zone.Entities.Get(harness.LastWelcome(2).EntityId));
            Assert.Equal((ushort)3101, reconnected.EquippedWeaponId);
            Assert.Equal((ushort)3301, reconnected.Equipment[(int)EquipSlot.Head]);
            Assert.Equal((ushort)3308, reconnected.Equipment[(int)EquipSlot.Ring]);
            Assert.Equal(4, reconnected.Level);
            Assert.Equal(275, reconnected.Experience);
            Assert.Equal(2, reconnected.ClassId);
            Assert.Equal(2, reconnected.AllocInt);
            Assert.Equal(75, reconnected.Health);
            Assert.Equal(95, reconnected.Mana);
            Assert.Equal(12345, reconnected.Gold);
            Assert.Contains(harness.Transport.Sent, sent => sent.ConnectionId == 2 &&
                MessageCodec.TryReadInventory(sent.Data, 0, sent.Data.Length, out InventoryMessage inventory) &&
                inventory.Gold == 12345);
            Assert.True(harness.Saves.TryGetLatest(first.CharacterId, out CharacterRecord latest));
            Assert.Equal((ushort)3101, latest.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal((ushort)3301, latest.Equipment[(int)EquipSlot.Head]);
            Assert.Equal((ushort)3308, latest.Equipment[(int)EquipSlot.Ring]);
            Assert.Equal(4, latest.Level);
            Assert.Equal(275, latest.Experience);
            Assert.Equal(2, latest.ClassId);
            Assert.Equal(2, latest.AllocInt);
            Assert.Equal(12345, latest.Gold);
        }

        private int QuerySchemaVersion()
        {
            using var connection = new SqliteConnection("Data Source=" + DatabasePath);
            connection.Open();
            using SqliteCommand query = connection.CreateCommand();
            query.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(query.ExecuteScalar());
        }

        private string ReadNameKey(int characterId)
        {
            using var connection = new SqliteConnection("Data Source=" + DatabasePath);
            connection.Open();
            using SqliteCommand query = connection.CreateCommand();
            query.CommandText = "SELECT name_key FROM characters WHERE id=@id;";
            query.Parameters.AddWithValue("@id", characterId);
            return Convert.ToString(query.ExecuteScalar(), CultureInfo.InvariantCulture)!;
        }

        private bool IndexIsPartialAndUnique()
        {
            using var connection = new SqliteConnection("Data Source=" + DatabasePath);
            connection.Open();
            using SqliteCommand query = connection.CreateCommand();
            query.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name='characters_name_key_unique';";
            string? sql = Convert.ToString(query.ExecuteScalar(), CultureInfo.InvariantCulture);
            return sql != null && sql.Contains("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains("WHERE deleted_utc IS NULL", StringComparison.OrdinalIgnoreCase);
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

            Assert.True(harness.LastCharacterRejected(4, out CharacterRejectedMessage reject));
            Assert.Equal(RejectReason.CharacterLimit, reject.Reason);

            // Los tres personajes que ya existían siguen valiendo: el tope no los invalida.
            AccountLoginResult login = harness.Repository.Login(
                "dev",
                "avelora-dev",
                string.Empty,
                "avelora-dev");

            Assert.True(login.Success);
            Assert.NotNull(CreateOrLoadCharacterForTest(harness.Repository,
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
            private readonly Dictionary<int, string> _characterNames = new Dictionary<int, string>();
            private readonly HashSet<int> _creationRequestsSent = new HashSet<int>();
            private readonly HashSet<int> _selectionRequestsSent = new HashSet<int>();

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
                    encrypted);

                Transport.RaiseReceived(connectionId, MessageCodec.EncodeHello(hello));
                _characterNames[connectionId] = character;
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
                    ProcessCharacterLists(connectionId);

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

            private void ProcessCharacterLists(int connectionId)
            {
                for (int i = 0; i < Transport.Sent.Count; i++)
                {
                    SentMessage sent = Transport.Sent[i];
                    if (sent.ConnectionId != connectionId ||
                        !MessageCodec.TryReadCharacterList(sent.Data, 0, sent.Data.Length, out CharacterListMessage list))
                    {
                        continue;
                    }

                    CharacterListEntry? requested = null;
                    for (int j = 0; j < list.Characters.Length; j++)
                    {
                        if (list.Characters[j].Name == _characterNames[connectionId])
                        {
                            requested = list.Characters[j];
                            break;
                        }
                    }

                    if (requested.HasValue && _selectionRequestsSent.Add(connectionId))
                    {
                        byte[] select = MessageCodec.EncodeSelectCharacter(
                            new SelectCharacterMessage(requested.Value.CharacterId));
                        Transport.RaiseReceived(connectionId, select);
                    }
                    else if (!requested.HasValue && list.Classes.Length > 0 &&
                        _creationRequestsSent.Add(connectionId))
                    {
                        byte[] create = MessageCodec.EncodeCreateCharacter(new CreateCharacterMessage(
                            _characterNames[connectionId],
                            list.Classes[0].ClassId));
                        Transport.RaiseReceived(connectionId, create);
                    }
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
                        MessageCodec.TryReadReject(sent.Data, 0, sent.Data.Length, out _) ||
                        MessageCodec.TryReadCharacterRejected(sent.Data, 0, sent.Data.Length, out _))
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

            public bool LastCharacterRejected(int connectionId, out CharacterRejectedMessage reject)
            {
                for (int i = Transport.Sent.Count - 1; i >= 0; i--)
                {
                    SentMessage sent = Transport.Sent[i];
                    if (sent.ConnectionId == connectionId &&
                        MessageCodec.TryReadCharacterRejected(sent.Data, 0, sent.Data.Length, out reject))
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
