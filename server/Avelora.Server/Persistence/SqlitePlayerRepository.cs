#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Avelora.Server.Items;
using Avelora.Server.Entities;
using Avelora.Server.Progression;
using Avelora.Shared;
using Avelora.Shared.Protocol;
using Microsoft.Data.Sqlite;

namespace Avelora.Server.Persistence
{
    /// <summary>
    /// Implementación de <see cref="IPlayerRepository"/> sobre un único archivo SQLite
    /// (plan del servidor §8).
    /// </summary>
    /// <remarks>
    /// Cada operación abre y cierra su propia conexión. SQLite serializa las escrituras y el
    /// servicio de guardado usa un solo hilo, así que no hay dos escritores compitiendo; las
    /// lecturas de login son breves y WAL permite que no bloqueen al escritor.
    /// </remarks>
    public sealed class SqlitePlayerRepository : IPlayerRepository
    {
        public const int SchemaVersion = 8;

        private const string TimeFormat = "O";

        // Sal y hash de tamaño válido para que Verify haga el PBKDF2 completo con cuentas inexistentes.
        private static readonly byte[] DummySalt = new byte[16];
        private static readonly byte[] DummyHash = new byte[32];

        private readonly string _connectionString;
        private readonly int _passwordIterations;
        private readonly ItemCatalog? _items;
        private readonly int _inventorySlots;
        private readonly LevelTable _levels;
        private readonly ClassCatalog _classes;
        private readonly object _initializeGate = new object();
        private bool _initialized;
        private bool _disposed;

        public SqlitePlayerRepository(
            string databasePath,
            int passwordIterations = PasswordHasher.DefaultIterations,
            ItemCatalog? items = null,
            int inventorySlots = 20,
            LevelTable? levels = null,
            ClassCatalog? classes = null)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                throw new ArgumentException("La ruta de la base de datos está vacía.", nameof(databasePath));
            }

            if (passwordIterations < 1 || passwordIterations > PasswordHasher.MaxIterations)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(passwordIterations),
                    "Las iteraciones de PBKDF2 deben estar entre 1 y " + PasswordHasher.MaxIterations + ".");
            }

            DatabasePath = databasePath;
            _passwordIterations = passwordIterations;
            _items = items;
            _levels = levels ?? LevelTable.Default;
            _classes = classes ?? ClassCatalog.Default;
            if (inventorySlots < 1 || inventorySlots > 60)
            {
                throw new ArgumentOutOfRangeException(nameof(inventorySlots));
            }

            _inventorySlots = inventorySlots;

            if (databasePath != ":memory:")
            {
                string? directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
            };

            _connectionString = builder.ToString();
        }

        /// <summary>Ruta efectiva del archivo, para los informes de arranque.</summary>
        public string DatabasePath { get; }

        public void Initialize()
        {
            lock (_initializeGate)
            {
                ThrowIfDisposed();

                if (_initialized)
                {
                    return;
                }

                using SqliteConnection connection = OpenConnection();

                // WAL deja leer mientras la cola escribe. No es imprescindible con 10 jugadores,
                // pero evita que un guardado pare un login.
                using (SqliteCommand pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA journal_mode=WAL;";
                    pragma.ExecuteScalar();
                }

                int version = GetUserVersion(connection);

                switch (version)
                {
                    case 0:
                        using (SqliteTransaction transaction = connection.BeginTransaction())
                        {
                            CreateSchema(connection, transaction);
                            SetUserVersion(connection, transaction, SchemaVersion);
                            transaction.Commit();
                        }

                        break;
                    case 1:
                        using (SqliteTransaction transaction = connection.BeginTransaction())
                        {
                            CreateInventorySchema(connection, transaction);
                        SetUserVersion(connection, transaction, 2);
                            transaction.Commit();
                        }

                        MigrateV2ToV3(connection);
                        MigrateV3ToV4(connection);
                        MigrateV4ToV5(connection);
                        MigrateV5ToV6(connection);
                        MigrateV6ToV7(connection);
                        MigrateV7ToV8(connection);
                        break;
                    case 2:
                        MigrateV2ToV3(connection);
                        MigrateV3ToV4(connection);
                        MigrateV4ToV5(connection);
                        MigrateV5ToV6(connection);
                        MigrateV6ToV7(connection);
                        MigrateV7ToV8(connection);
                        break;
                    case 3:
                        MigrateV3ToV4(connection);
                        MigrateV4ToV5(connection);
                        MigrateV5ToV6(connection);
                        MigrateV6ToV7(connection);
                        MigrateV7ToV8(connection);
                        break;
                    case 4:
                        MigrateV4ToV5(connection);
                        MigrateV5ToV6(connection);
                        MigrateV6ToV7(connection);
                        MigrateV7ToV8(connection);
                        break;
                    case 5:
                        MigrateV5ToV6(connection);
                        MigrateV6ToV7(connection);
                        MigrateV7ToV8(connection);
                        break;
                    case 6:
                        MigrateV6ToV7(connection);
                        MigrateV7ToV8(connection);
                        break;
                    case 7:
                        MigrateV7ToV8(connection);
                        break;
                    case SchemaVersion:
                        break;
                    default:
                        throw new InvalidOperationException(
                            "La base de datos '" + DatabasePath + "' tiene esquema versión " + version +
                            "; este servidor espera la " + SchemaVersion + ".");
                }

                _initialized = true;
            }
        }

        public AccountLoginResult Login(string accountName, string password)
        {
            ThrowIfNotInitialized();

            if (!LoginValidation.IsValidAccountName(accountName) ||
                !LoginValidation.IsValidPassword(password))
            {
                return AccountLoginResult.Failed(RejectReason.InvalidCredentials);
            }

            using SqliteConnection connection = OpenConnection();

            AccountRow? existing = FindAccount(connection, accountName);

            if (existing.HasValue)
            {
                AccountRow row = existing.Value;

                bool valid = PasswordHasher.Verify(
                    password,
                    row.PasswordSalt,
                    row.PasswordHash,
                    row.PasswordIterations);

                if (!valid)
                {
                    return AccountLoginResult.Failed(
                        RejectReason.InvalidCredentials,
                        countsAsFailedAttempt: true);
                }

                // Rehash al entrar: la cuenta se quedó con menos iteraciones de las configuradas.
                // Se hace en la misma operación que el login, sobre la conexión ya abierta.
                if (row.PasswordIterations < _passwordIterations)
                {
                    RewritePassword(connection, row.Id, password, _passwordIterations);
                }

                return AccountLoginResult.Ok(row.Id);
            }

            // Mismo coste que una contraseña mala: una cuenta inexistente no revela si hay registro.
            PasswordHasher.Verify(password, DummySalt, DummyHash, _passwordIterations);
            return AccountLoginResult.Failed(RejectReason.InvalidCredentials);
        }

        public AccountLoginResult Register(
            string accountName,
            string password,
            string invitationCode,
            string registrationCode)
        {
            ThrowIfNotInitialized();

            if (!LoginValidation.IsValidAccountName(accountName) ||
                !LoginValidation.IsValidPassword(password))
            {
                return AccountLoginResult.Failed(RejectReason.InvalidCredentials);
            }

            // Sin invitación válida se devuelve siempre el mismo error y coste, exista o no la cuenta.
            if (string.IsNullOrEmpty(registrationCode) ||
                !string.Equals(invitationCode ?? string.Empty, registrationCode, StringComparison.Ordinal))
            {
                PasswordHasher.Verify(password, DummySalt, DummyHash, _passwordIterations);
                return AccountLoginResult.Failed(RejectReason.InvalidCredentials);
            }

            using SqliteConnection connection = OpenConnection();
            if (FindAccount(connection, accountName).HasValue)
            {
                return AccountLoginResult.Failed(RejectReason.AccountTaken);
            }

            PasswordHasher.Create(password, _passwordIterations, out byte[] salt, out byte[] hash);

            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO accounts " +
                "(name, password_hash, password_salt, password_iterations, created_utc) " +
                "VALUES (@name, @hash, @salt, @iterations, @created) " +
                "RETURNING id;";
            insert.Parameters.AddWithValue("@name", accountName);
            insert.Parameters.AddWithValue("@hash", hash);
            insert.Parameters.AddWithValue("@salt", salt);
            insert.Parameters.AddWithValue("@iterations", _passwordIterations);
            insert.Parameters.AddWithValue("@created", FormatTime(DateTime.UtcNow));

            try
            {
                object? result = insert.ExecuteScalar();
                int accountId = Convert.ToInt32(result, CultureInfo.InvariantCulture);

                return AccountLoginResult.Ok(accountId);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return AccountLoginResult.Failed(RejectReason.AccountTaken);
            }
        }

        public CharacterRecord[] ListCharacters(int accountId)
        {
            ThrowIfNotInitialized();

            if (accountId <= 0)
            {
                return Array.Empty<CharacterRecord>();
            }

            using SqliteConnection connection = OpenConnection();
            var characterIds = new List<int>();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT id FROM characters WHERE account_id = @account AND deleted_utc IS NULL ORDER BY id;";
                command.Parameters.AddWithValue("@account", accountId);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    characterIds.Add(reader.GetInt32(0));
                }
            }

            var characters = new List<CharacterRecord>(characterIds.Count);
            for (int i = 0; i < characterIds.Count; i++)
            {
                CharacterRecord? character = LoadCharacter(connection, accountId, characterIds[i]);
                if (character != null)
                {
                    characters.Add(character);
                }
            }

            return characters.ToArray();
        }

        public CharacterCreateResult CreateCharacter(
            int accountId,
            string name,
            byte classId,
            int maxCharacters,
            int zoneId,
            Vec2 spawn,
            byte[] mapHash)
        {
            ThrowIfNotInitialized();

            if (accountId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(accountId));
            }

            if (maxCharacters < 1 || maxCharacters > 10)
            {
                throw new ArgumentOutOfRangeException(nameof(maxCharacters), "El máximo debe estar entre 1 y 10.");
            }

            if (name is null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (mapHash is null || mapHash.Length != ProtocolConstants.MapHashSize)
            {
                throw new ArgumentException(
                    "La huella del mapa debe tener " + ProtocolConstants.MapHashSize + " bytes.",
                    nameof(mapHash));
            }

            if (_classes.Find(classId) == null)
            {
                return CharacterCreateResult.Rejected(RejectReason.ClassUnknown);
            }

            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand account = connection.CreateCommand())
            {
                account.Transaction = transaction;
                account.CommandText = "SELECT 1 FROM accounts WHERE id = @account;";
                account.Parameters.AddWithValue("@account", accountId);
                if (account.ExecuteScalar() == null)
                {
                    return CharacterCreateResult.Rejected(RejectReason.CharacterNotFound);
                }
            }

            string nameKey = name.ToLowerInvariant();
            using (SqliteCommand duplicate = connection.CreateCommand())
            {
                duplicate.Transaction = transaction;
                duplicate.CommandText =
                    "SELECT 1 FROM characters WHERE name_key = @key AND deleted_utc IS NULL LIMIT 1;";
                duplicate.Parameters.AddWithValue("@key", nameKey);
                if (duplicate.ExecuteScalar() != null)
                {
                    return CharacterCreateResult.Rejected(RejectReason.NameTaken);
                }
            }

            using (SqliteCommand count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText =
                    "SELECT COUNT(*) FROM characters WHERE account_id = @account AND deleted_utc IS NULL;";
                count.Parameters.AddWithValue("@account", accountId);
                if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) >= maxCharacters)
                {
                    return CharacterCreateResult.Rejected(RejectReason.CharacterLimit);
                }
            }

            DateTime now = DateTime.UtcNow;
            DerivedCharacterStats stats = CharacterStats.Calculate(_classes, classId, 1, 0, 0, 0, 0);

            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO characters " +
                "(account_id, name, name_key, level, experience, health, mana, zone_id, x, y, map_hash, " +
                "class_id, alloc_str, alloc_dex, alloc_int, alloc_vit, created_utc, updated_utc) " +
                "VALUES (@account, @name, @key, 1, 0, @health, @mana, @zone, @x, @y, @map, @class, 0, 0, 0, 0, @created, @updated) " +
                "RETURNING id;";
            insert.Parameters.AddWithValue("@account", accountId);
            insert.Parameters.AddWithValue("@name", name);
            insert.Parameters.AddWithValue("@key", nameKey);
            insert.Parameters.AddWithValue("@health", stats.MaxHealth);
            insert.Parameters.AddWithValue("@mana", stats.MaxResource);
            insert.Parameters.AddWithValue("@zone", zoneId);
            insert.Parameters.AddWithValue("@x", (double)spawn.X);
            insert.Parameters.AddWithValue("@y", (double)spawn.Y);
            insert.Parameters.AddWithValue("@map", mapHash);
            insert.Parameters.AddWithValue("@class", classId);
            insert.Parameters.AddWithValue("@created", FormatTime(now));
            insert.Parameters.AddWithValue("@updated", FormatTime(now));

            int characterId;
            try
            {
                object? result = insert.ExecuteScalar();
                characterId = Convert.ToInt32(result, CultureInfo.InvariantCulture);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && IsNameKeyTaken(connection, transaction, nameKey))
            {
                return CharacterCreateResult.Rejected(RejectReason.NameTaken);
            }

            transaction.Commit();

            return CharacterCreateResult.Created(new CharacterRecord(
                characterId,
                accountId,
                name,
                1,
                0,
                stats.MaxHealth,
                stats.MaxResource,
                zoneId,
                spawn,
                mapHash,
                now,
                now,
                classId: classId));
        }

        public RejectReason DeleteCharacter(int accountId, int characterId, string confirmationName)
        {
            ThrowIfNotInitialized();

            if (accountId <= 0 || characterId <= 0)
            {
                return RejectReason.CharacterNotFound;
            }

            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            string? name;
            using (SqliteCommand find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText =
                    "SELECT name FROM characters WHERE id = @id AND account_id = @account AND deleted_utc IS NULL;";
                find.Parameters.AddWithValue("@id", characterId);
                find.Parameters.AddWithValue("@account", accountId);
                name = find.ExecuteScalar() as string;
            }

            if (name == null)
            {
                return RejectReason.CharacterNotFound;
            }

            if (!string.Equals(name, confirmationName, StringComparison.Ordinal))
            {
                return RejectReason.ConfirmMismatch;
            }

            using (SqliteCommand delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText =
                    "UPDATE characters SET deleted_utc = @deleted " +
                    "WHERE id = @id AND account_id = @account AND deleted_utc IS NULL;";
                delete.Parameters.AddWithValue("@deleted", FormatTime(DateTime.UtcNow));
                delete.Parameters.AddWithValue("@id", characterId);
                delete.Parameters.AddWithValue("@account", accountId);
                if (delete.ExecuteNonQuery() != 1)
                {
                    return RejectReason.CharacterNotFound;
                }
            }

            transaction.Commit();
            return RejectReason.Unknown;
        }

        public bool TryGetCharacter(int accountId, int characterId, out CharacterRecord? character)
        {
            ThrowIfNotInitialized();
            if (accountId <= 0 || characterId <= 0)
            {
                character = null;
                return false;
            }

            using SqliteConnection connection = OpenConnection();
            character = LoadCharacter(connection, accountId, characterId);
            return character != null;
        }

        public void SaveCharacter(CharacterRecord character)
        {
            ThrowIfNotInitialized();

            if (character is null)
            {
                throw new ArgumentNullException(nameof(character));
            }

            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE characters SET " +
                "level = @level, experience = @experience, health = @health, mana = @mana, " +
                "zone_id = @zone, x = @x, y = @y, map_hash = @map, " +
                "class_id = @class, alloc_str = @allocStr, alloc_dex = @allocDex, " +
                "alloc_int = @allocInt, alloc_vit = @allocVit, gold = @gold, " +
                "pvp_mode = @pvpMode, pvp_forced_seconds = @pvpForced, updated_utc = @updated " +
                "WHERE id = @id AND account_id = @account AND deleted_utc IS NULL;";
            update.Parameters.AddWithValue("@level", character.Level);
            update.Parameters.AddWithValue("@experience", character.Experience);
            update.Parameters.AddWithValue("@health", character.Health);
            update.Parameters.AddWithValue("@mana", character.Mana);
            update.Parameters.AddWithValue("@zone", character.ZoneId);
            update.Parameters.AddWithValue("@x", (double)character.Position.X);
            update.Parameters.AddWithValue("@y", (double)character.Position.Y);
            update.Parameters.AddWithValue("@map", character.MapHash);
            update.Parameters.AddWithValue("@class", character.ClassId);
            update.Parameters.AddWithValue("@allocStr", character.AllocStr);
            update.Parameters.AddWithValue("@allocDex", character.AllocDex);
            update.Parameters.AddWithValue("@allocInt", character.AllocInt);
            update.Parameters.AddWithValue("@allocVit", character.AllocVit);
            update.Parameters.AddWithValue("@gold", character.Gold);
            update.Parameters.AddWithValue("@pvpMode", character.PvpMode);
            update.Parameters.AddWithValue("@pvpForced", character.PvpForcedSeconds);
            update.Parameters.AddWithValue("@updated", FormatTime(character.UpdatedUtc));
            update.Parameters.AddWithValue("@id", character.Id);
            update.Parameters.AddWithValue("@account", character.AccountId);

            int rows = update.ExecuteNonQuery();

            if (rows != 1)
            {
                using SqliteCommand deleted = connection.CreateCommand();
                deleted.Transaction = transaction;
                deleted.CommandText =
                    "SELECT 1 FROM characters WHERE id = @id AND account_id = @account AND deleted_utc IS NOT NULL;";
                deleted.Parameters.AddWithValue("@id", character.Id);
                deleted.Parameters.AddWithValue("@account", character.AccountId);
                if (rows == 0 && deleted.ExecuteScalar() != null)
                {
                    // Un guardado pendiente de una sesión expulsada no debe recuperar un personaje borrado.
                    return;
                }

                throw new InvalidOperationException(
                    "No se pudo guardar el personaje " + character.Id +
                    ": la base de datos no conoce esa combinación de personaje y cuenta.");
            }

            using (SqliteCommand delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM inventory_items WHERE character_id = @id;";
                delete.Parameters.AddWithValue("@id", character.Id);
                delete.ExecuteNonQuery();
            }

            for (int i = 0; i < character.InventoryItems.Length; i++)
            {
                InventoryItemRecord item = character.InventoryItems[i];
                using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO inventory_items (character_id, slot, item_id, count) " +
                    "VALUES (@character, @slot, @item, @count);";
                insert.Parameters.AddWithValue("@character", character.Id);
                insert.Parameters.AddWithValue("@slot", item.Slot);
                insert.Parameters.AddWithValue("@item", item.ItemId);
                insert.Parameters.AddWithValue("@count", item.Count);
                insert.ExecuteNonQuery();
            }

            if (character.Equipment.Length != ProtocolConstants.EquipSlotCount)
            {
                throw new InvalidOperationException("El personaje " + character.Id + " no tiene diez huecos de equipo.");
            }

            using (SqliteCommand deleteEquipment = connection.CreateCommand())
            {
                deleteEquipment.Transaction = transaction;
                deleteEquipment.CommandText = "DELETE FROM character_equipment WHERE character_id = @id;";
                deleteEquipment.Parameters.AddWithValue("@id", character.Id);
                deleteEquipment.ExecuteNonQuery();
            }

            for (int slot = 0; slot < character.Equipment.Length; slot++)
            {
                ushort itemId = character.Equipment[slot];
                if (itemId == 0)
                {
                    continue;
                }

                using SqliteCommand insertEquipment = connection.CreateCommand();
                insertEquipment.Transaction = transaction;
                insertEquipment.CommandText =
                    "INSERT INTO character_equipment (character_id, slot, item_id) VALUES (@character, @slot, @item);";
                insertEquipment.Parameters.AddWithValue("@character", character.Id);
                insertEquipment.Parameters.AddWithValue("@slot", slot);
                insertEquipment.Parameters.AddWithValue("@item", itemId);
                insertEquipment.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public void Dispose()
        {
            // Las conexiones se abren por operación; no hay nada que cerrar aquí. SQLite libera
            // sus recursos al terminar cada bloque using.
            _disposed = true;
        }

        // ---------------------------------------------------------------- esquema

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using SqliteCommand pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();

            return connection;
        }

        private static void CreateSchema(SqliteConnection connection, SqliteTransaction transaction)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
CREATE TABLE accounts (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    name                TEXT NOT NULL COLLATE NOCASE UNIQUE,
    password_hash       BLOB NOT NULL,
    password_salt       BLOB NOT NULL,
    password_iterations INTEGER NOT NULL,
    created_utc         TEXT NOT NULL
);

CREATE TABLE characters (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id      INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    name            TEXT NOT NULL,
    name_key        TEXT NOT NULL,
    deleted_utc     TEXT NULL,
    level           INTEGER NOT NULL DEFAULT 1,
    experience      INTEGER NOT NULL DEFAULT 0,
    health          INTEGER NOT NULL DEFAULT 100,
    mana            INTEGER NOT NULL DEFAULT 100,
    zone_id         INTEGER NOT NULL,
    x               REAL NOT NULL,
    y               REAL NOT NULL,
    map_hash        BLOB NOT NULL,
    equipped_item_id INTEGER NOT NULL DEFAULT 0,
    class_id        INTEGER NOT NULL DEFAULT 1,
    alloc_str       INTEGER NOT NULL DEFAULT 0,
    alloc_dex       INTEGER NOT NULL DEFAULT 0,
    alloc_int       INTEGER NOT NULL DEFAULT 0,
    alloc_vit       INTEGER NOT NULL DEFAULT 0,
    gold            INTEGER NOT NULL DEFAULT 0,
    pvp_mode        INTEGER NOT NULL DEFAULT 0,
    pvp_forced_seconds INTEGER NOT NULL DEFAULT 0,
    created_utc     TEXT NOT NULL,
    updated_utc     TEXT NOT NULL
);

CREATE INDEX characters_account_idx ON characters(account_id);
CREATE UNIQUE INDEX characters_name_key_unique ON characters(name_key) WHERE deleted_utc IS NULL;
";
            command.ExecuteNonQuery();
            CreateInventorySchema(connection, transaction);
            CreateEquipmentSchema(connection, transaction);
        }

        private static void MigrateV2ToV3(SqliteConnection connection)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "ALTER TABLE characters ADD COLUMN equipped_item_id INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();
            SetUserVersion(connection, transaction, 3);
            transaction.Commit();
        }

        private static void MigrateV3ToV4(SqliteConnection connection)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
ALTER TABLE characters ADD COLUMN class_id INTEGER NOT NULL DEFAULT 1;
ALTER TABLE characters ADD COLUMN alloc_str INTEGER NOT NULL DEFAULT 0;
ALTER TABLE characters ADD COLUMN alloc_dex INTEGER NOT NULL DEFAULT 0;
ALTER TABLE characters ADD COLUMN alloc_int INTEGER NOT NULL DEFAULT 0;
ALTER TABLE characters ADD COLUMN alloc_vit INTEGER NOT NULL DEFAULT 0;
";
            command.ExecuteNonQuery();
            SetUserVersion(connection, transaction, 4);
            transaction.Commit();
        }

        private static void MigrateV4ToV5(SqliteConnection connection)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
CREATE TABLE character_equipment (
    character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
    slot         INTEGER NOT NULL,
    item_id      INTEGER NOT NULL,
    PRIMARY KEY (character_id, slot)
);
INSERT INTO character_equipment (character_id, slot, item_id)
SELECT id, 0, equipped_item_id FROM characters WHERE equipped_item_id <> 0;
";
            command.ExecuteNonQuery();
            SetUserVersion(connection, transaction, 5);
            transaction.Commit();
        }

        private static void MigrateV5ToV6(SqliteConnection connection)
        {
            using (SqliteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys=OFF;";
                pragma.ExecuteNonQuery();
            }

            try
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                var rows = new List<object[]>();
                using (SqliteCommand read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText =
                        "SELECT id, account_id, name, level, experience, health, mana, zone_id, x, y, map_hash, " +
                        "equipped_item_id, class_id, alloc_str, alloc_dex, alloc_int, alloc_vit, created_utc, updated_utc " +
                        "FROM characters ORDER BY id;";
                    using SqliteDataReader reader = read.ExecuteReader();
                    while (reader.Read())
                    {
                        var row = new object[19];
                        for (int i = 0; i < row.Length; i++)
                        {
                            row[i] = reader.GetValue(i);
                        }

                        rows.Add(row);
                    }
                }

                using (SqliteCommand create = connection.CreateCommand())
                {
                    create.Transaction = transaction;
                    create.CommandText = @"
CREATE TABLE characters_v6 (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id      INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    name            TEXT NOT NULL,
    name_key        TEXT NOT NULL,
    deleted_utc     TEXT NULL,
    level           INTEGER NOT NULL DEFAULT 1,
    experience      INTEGER NOT NULL DEFAULT 0,
    health          INTEGER NOT NULL DEFAULT 100,
    mana            INTEGER NOT NULL DEFAULT 100,
    zone_id         INTEGER NOT NULL,
    x               REAL NOT NULL,
    y               REAL NOT NULL,
    map_hash        BLOB NOT NULL,
    equipped_item_id INTEGER NOT NULL DEFAULT 0,
    class_id        INTEGER NOT NULL DEFAULT 1,
    alloc_str       INTEGER NOT NULL DEFAULT 0,
    alloc_dex       INTEGER NOT NULL DEFAULT 0,
    alloc_int       INTEGER NOT NULL DEFAULT 0,
    alloc_vit       INTEGER NOT NULL DEFAULT 0,
    created_utc     TEXT NOT NULL,
    updated_utc     TEXT NOT NULL
);";
                    create.ExecuteNonQuery();
                }

                var originalCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < rows.Count; i++)
                {
                    string originalName = Convert.ToString(rows[i][2], CultureInfo.InvariantCulture) ?? string.Empty;
                    string originalKey = originalName.ToLowerInvariant();
                    originalCounts.TryGetValue(originalKey, out int count);
                    originalCounts[originalKey] = count + 1;
                }

                // Se reservan también los nombres únicos que todavía no se han procesado: al
                // renombrar un duplicado no se debe desplazar un nombre original posterior.
                var usedKeys = new HashSet<string>(originalCounts.Keys, StringComparer.Ordinal);
                var seenOriginalKeys = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < rows.Count; i++)
                {
                    object[] row = rows[i];
                    string originalName = Convert.ToString(row[2], CultureInfo.InvariantCulture) ?? string.Empty;
                    string name = originalName;
                    if (!IsValidLegacyCharacterName(originalName))
                    {
                        Console.Error.WriteLine(
                            "AVISO: el nombre legacy del personaje " + row[0] + " ('" + originalName +
                            "') no cumple las reglas nuevas; se conserva salvo que deba renombrarse por duplicado.");
                    }

                    string nameKey = name.ToLowerInvariant();
                    if (!seenOriginalKeys.Add(nameKey))
                    {
                        int suffix = 2;
                        do
                        {
                            name = CreateDuplicateName(originalName, suffix++);
                            nameKey = name.ToLowerInvariant();
                        }
                        while (!usedKeys.Add(nameKey));

                        Console.Error.WriteLine(
                            "AVISO: el nombre legacy duplicado del personaje " + row[0] + " ('" + originalName +
                            "') se renombró a '" + name + "'.");
                    }

                    using SqliteCommand insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = @"
INSERT INTO characters_v6
(id, account_id, name, name_key, deleted_utc, level, experience, health, mana, zone_id, x, y, map_hash,
 equipped_item_id, class_id, alloc_str, alloc_dex, alloc_int, alloc_vit, created_utc, updated_utc)
VALUES
(@id, @account, @name, @key, NULL, @level, @experience, @health, @mana, @zone, @x, @y, @map,
 @equipped, @class, @str, @dex, @int, @vit, @created, @updated);";
                    insert.Parameters.AddWithValue("@id", row[0]);
                    insert.Parameters.AddWithValue("@account", row[1]);
                    insert.Parameters.AddWithValue("@name", name);
                    insert.Parameters.AddWithValue("@key", nameKey);
                    insert.Parameters.AddWithValue("@level", row[3]);
                    insert.Parameters.AddWithValue("@experience", row[4]);
                    insert.Parameters.AddWithValue("@health", row[5]);
                    insert.Parameters.AddWithValue("@mana", row[6]);
                    insert.Parameters.AddWithValue("@zone", row[7]);
                    insert.Parameters.AddWithValue("@x", row[8]);
                    insert.Parameters.AddWithValue("@y", row[9]);
                    insert.Parameters.AddWithValue("@map", row[10]);
                    insert.Parameters.AddWithValue("@equipped", row[11]);
                    insert.Parameters.AddWithValue("@class", row[12]);
                    insert.Parameters.AddWithValue("@str", row[13]);
                    insert.Parameters.AddWithValue("@dex", row[14]);
                    insert.Parameters.AddWithValue("@int", row[15]);
                    insert.Parameters.AddWithValue("@vit", row[16]);
                    insert.Parameters.AddWithValue("@created", row[17]);
                    insert.Parameters.AddWithValue("@updated", row[18]);
                    insert.ExecuteNonQuery();
                }

                using (SqliteCommand replace = connection.CreateCommand())
                {
                    replace.Transaction = transaction;
                    replace.CommandText = @"
DROP INDEX IF EXISTS characters_account_idx;
DROP TABLE characters;
ALTER TABLE characters_v6 RENAME TO characters;
CREATE INDEX characters_account_idx ON characters(account_id);
CREATE UNIQUE INDEX characters_name_key_unique ON characters(name_key) WHERE deleted_utc IS NULL;";
                    replace.ExecuteNonQuery();
                }

                SetUserVersion(connection, transaction, 6);
                transaction.Commit();
            }
            finally
            {
                using SqliteCommand pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA foreign_keys=ON;";
                pragma.ExecuteNonQuery();
            }
        }

        private static void MigrateV6ToV7(SqliteConnection connection)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "ALTER TABLE characters ADD COLUMN gold INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();
            SetUserVersion(connection, transaction, 7);
            transaction.Commit();
        }

        private static void MigrateV7ToV8(SqliteConnection connection)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "ALTER TABLE characters ADD COLUMN pvp_mode INTEGER NOT NULL DEFAULT 0; " +
                "ALTER TABLE characters ADD COLUMN pvp_forced_seconds INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();
            SetUserVersion(connection, transaction, 8);
            transaction.Commit();
        }

        private static void CreateInventorySchema(SqliteConnection connection, SqliteTransaction transaction)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
CREATE TABLE inventory_items (
    character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
    slot         INTEGER NOT NULL,
    item_id      INTEGER NOT NULL,
    count        INTEGER NOT NULL,
    PRIMARY KEY (character_id, slot)
);
";
            command.ExecuteNonQuery();
        }

        private static void CreateEquipmentSchema(SqliteConnection connection, SqliteTransaction transaction)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
CREATE TABLE character_equipment (
    character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
    slot         INTEGER NOT NULL,
    item_id      INTEGER NOT NULL,
    PRIMARY KEY (character_id, slot)
);
";
            command.ExecuteNonQuery();
        }

        private static int GetUserVersion(SqliteConnection connection)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static void SetUserVersion(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int version)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA user_version = " + version + ";";
            command.ExecuteNonQuery();
        }

        // ---------------------------------------------------------------- consultas

        /// <summary>
        /// Recalcula sal, hash e iteraciones de una cuenta (rehash al entrar con más iteraciones).
        /// </summary>
        private static void RewritePassword(
            SqliteConnection connection,
            int accountId,
            string password,
            int iterations)
        {
            PasswordHasher.Create(password, iterations, out byte[] salt, out byte[] hash);

            using SqliteCommand update = connection.CreateCommand();
            update.CommandText =
                "UPDATE accounts SET password_hash = @hash, password_salt = @salt, " +
                "password_iterations = @iterations WHERE id = @id;";
            update.Parameters.AddWithValue("@hash", hash);
            update.Parameters.AddWithValue("@salt", salt);
            update.Parameters.AddWithValue("@iterations", iterations);
            update.Parameters.AddWithValue("@id", accountId);
            update.ExecuteNonQuery();
        }

        private static AccountRow? FindAccount(SqliteConnection connection, string accountName)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, password_hash, password_salt, password_iterations " +
                "FROM accounts WHERE name = @name COLLATE NOCASE LIMIT 1;";
            command.Parameters.AddWithValue("@name", accountName);

            using SqliteDataReader reader = command.ExecuteReader();

            if (!reader.Read())
            {
                return null;
            }

            return new AccountRow(
                reader.GetInt32(0),
                (byte[])reader.GetValue(1),
                (byte[])reader.GetValue(2),
                reader.GetInt32(3));
        }

        private static bool IsNameKeyTaken(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string nameKey)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT 1 FROM characters WHERE name_key = @key AND deleted_utc IS NULL LIMIT 1;";
            command.Parameters.AddWithValue("@key", nameKey);
            return command.ExecuteScalar() != null;
        }

        private static bool IsValidLegacyCharacterName(string name)
        {
            if (name.Length < 3 || name.Length > 16 || name != name.Trim() ||
                Encoding.UTF8.GetByteCount(name) > ProtocolConstants.MaxChatNameBytes ||
                !IsLetter(name[0]))
            {
                return false;
            }

            bool previousWasSpace = false;
            for (int i = 0; i < name.Length; i++)
            {
                char value = name[i];
                if (value == ' ')
                {
                    if (previousWasSpace || i == name.Length - 1)
                    {
                        return false;
                    }

                    previousWasSpace = true;
                    continue;
                }

                if (!IsLetter(value) && (value < '0' || value > '9'))
                {
                    return false;
                }

                previousWasSpace = false;
            }

            string key = name.ToLowerInvariant();
            return key != "admin" && key != "administrador" && key != "gm" && key != "moderador" &&
                key != "mod" && key != "avelora" && key != "sistema" && key != "servidor" &&
                key != "server" && key != "system";
        }

        private static bool IsLetter(char value) =>
            (value >= 'a' && value <= 'z') ||
            (value >= 'A' && value <= 'Z') ||
            value == 'á' || value == 'é' || value == 'í' || value == 'ó' || value == 'ú' ||
            value == 'ü' || value == 'ñ' || value == 'Á' || value == 'É' || value == 'Í' ||
            value == 'Ó' || value == 'Ú' || value == 'Ü' || value == 'Ñ';

        private static string CreateDuplicateName(string originalName, int suffix)
        {
            string suffixText = suffix.ToString(CultureInfo.InvariantCulture);
            string prefix = IsValidLegacyCharacterName(originalName) ? originalName : "Nombre";
            int maxPrefixLength = 16 - suffixText.Length;
            if (maxPrefixLength < 1)
            {
                throw new InvalidOperationException("Hay demasiados nombres duplicados para renombrar en la migración v6.");
            }

            if (prefix.Length > maxPrefixLength)
            {
                prefix = prefix.Substring(0, maxPrefixLength).TrimEnd();
            }

            if (prefix.Length == 0)
            {
                prefix = "N";
            }

            return prefix + suffixText;
        }

        private CharacterRecord? LoadCharacter(
            SqliteConnection connection,
            int accountId,
            int characterId)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, account_id, name, level, experience, health, mana, zone_id, x, y, " +
                "map_hash, created_utc, updated_utc, class_id, " +
                "alloc_str, alloc_dex, alloc_int, alloc_vit, gold, pvp_mode, pvp_forced_seconds " +
                "FROM characters WHERE account_id = @account AND id = @id AND deleted_utc IS NULL LIMIT 1;";
            command.Parameters.AddWithValue("@account", accountId);
            command.Parameters.AddWithValue("@id", characterId);

            CharacterRecord? character;
            ushort[]? equipment = null;
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                byte[] mapHash = (byte[])reader.GetValue(10);
                if (mapHash.Length != ProtocolConstants.MapHashSize)
                {
                    throw new InvalidOperationException(
                        "El personaje " + reader.GetInt32(0) + " tiene una huella de mapa corrupta.");
                }

                long rawLevel = reader.GetInt64(3);
                int level;
                if (rawLevel < 1)
                {
                    Console.Error.WriteLine(
                        "AVISO: se corrigió level inválido del personaje " + reader.GetInt32(0) +
                        " (" + rawLevel + " → 1).");
                    level = 1;
                }
                else if (rawLevel > _levels.MaxLevel)
                {
                    Console.Error.WriteLine(
                        "AVISO: se corrigió level fuera del máximo del personaje " + reader.GetInt32(0) +
                        " (" + rawLevel + " → " + _levels.MaxLevel + ").");
                    level = _levels.MaxLevel;
                }
                else
                {
                    level = (int)rawLevel;
                }

                int threshold = _levels.GetExperienceToNext(level);
                long rawExperience = reader.GetInt64(4);
                int experience;
                if (rawExperience < 0)
                {
                    Console.Error.WriteLine(
                        "AVISO: se corrigió experience negativo del personaje " + reader.GetInt32(0) +
                        " (" + rawExperience + " → 0).");
                    experience = 0;
                }
                else if (threshold == 0 && rawExperience != 0)
                {
                    Console.Error.WriteLine(
                        "AVISO: se corrigió experience del personaje máximo " + reader.GetInt32(0) +
                        " (" + rawExperience + " → 0).");
                    experience = 0;
                }
                else if (threshold > 0 && rawExperience >= threshold)
                {
                    Console.Error.WriteLine(
                        "AVISO: se recortó experience sobre el umbral del personaje " + reader.GetInt32(0) +
                        " (" + rawExperience + " → " + (threshold - 1) + ").");
                    experience = threshold - 1;
                }
                else
                {
                    experience = (int)rawExperience;
                }

                long rawClassId = reader.GetInt64(13);
                int classId;
                if (rawClassId < 1 || rawClassId > byte.MaxValue || _classes.Find((int)rawClassId) == null)
                {
                    Console.Error.WriteLine(
                        "AVISO: se corrigió class_id desconocida del personaje " + reader.GetInt32(0) +
                        " (" + rawClassId + " → 1). ");
                    classId = 1;
                }
                else
                {
                    classId = (int)rawClassId;
                }

                long rawAllocStr = reader.GetInt64(14);
                long rawAllocDex = reader.GetInt64(15);
                long rawAllocInt = reader.GetInt64(16);
                long rawAllocVit = reader.GetInt64(17);
                long rawGold = reader.GetInt64(18);
                long rawPvpMode = reader.GetInt64(19);
                long rawPvpForcedSeconds = reader.GetInt64(20);
                int gold = rawGold < 0 ? 0 : rawGold > PlayerEntity.MaxGold
                    ? PlayerEntity.MaxGold : (int)rawGold;
                if (rawGold < 0 || rawGold > PlayerEntity.MaxGold)
                {
                    Console.Error.WriteLine(
                        "AVISO: se corrigió gold inválido del personaje " + reader.GetInt32(0) +
                        " (" + rawGold + " → " + gold + ").");
                }

                int pvpMode = rawPvpMode == 1 ? 1 : 0;
                if (rawPvpMode < 0 || rawPvpMode > 1)
                {
                    Console.Error.WriteLine(
                        "AVISO: se corrigió pvp_mode inválido del personaje " + reader.GetInt32(0) +
                        " (" + rawPvpMode + " → " + pvpMode + ").");
                }

                int pvpForcedSeconds = rawPvpForcedSeconds < 0 ? 0 :
                    rawPvpForcedSeconds > ushort.MaxValue ? ushort.MaxValue : (int)rawPvpForcedSeconds;
                if (rawPvpForcedSeconds < 0 || rawPvpForcedSeconds > ushort.MaxValue)
                {
                    Console.Error.WriteLine(
                        "AVISO: se corrigió pvp_forced_seconds inválido del personaje " + reader.GetInt32(0) +
                        " (" + rawPvpForcedSeconds + " → " + pvpForcedSeconds + ").");
                }
                long maxAllocated = (long)_levels.PointsPerLevel * level;
                bool validAllocations = rawAllocStr >= 0 && rawAllocDex >= 0 && rawAllocInt >= 0 && rawAllocVit >= 0 &&
                    rawAllocStr <= int.MaxValue && rawAllocDex <= int.MaxValue && rawAllocInt <= int.MaxValue &&
                    rawAllocVit <= int.MaxValue;
                long allocationTotal = validAllocations
                    ? rawAllocStr + rawAllocDex + rawAllocInt + rawAllocVit
                    : long.MaxValue;
                validAllocations &= allocationTotal <= maxAllocated;
                int allocStr = validAllocations ? (int)rawAllocStr : 0;
                int allocDex = validAllocations ? (int)rawAllocDex : 0;
                int allocInt = validAllocations ? (int)rawAllocInt : 0;
                int allocVit = validAllocations ? (int)rawAllocVit : 0;
                if (!validAllocations)
                {
                    Console.Error.WriteLine(
                        "AVISO: se corrigieron alloc_* inválidos del personaje " + reader.GetInt32(0) + " (→ 0). ");
                }

                // El equipo puede subir la vida máxima (bonus de VIT): se lee antes de recortar la vida.
                equipment = LoadEquipment(connection, reader.GetInt32(0));
                DerivedCharacterStats stats = CharacterStats.Calculate(
                    _classes, classId, level, allocStr, allocDex, allocInt, allocVit, _items, equipment);
                long rawHealth = reader.GetInt64(5);
                int health = rawHealth > stats.MaxHealth ? stats.MaxHealth :
                    rawHealth < 0 ? 0 : rawHealth > int.MaxValue ? stats.MaxHealth : (int)rawHealth;
                if (rawHealth > stats.MaxHealth || rawHealth < 0 || rawHealth > int.MaxValue)
                {
                    Console.Error.WriteLine(
                        "AVISO: se recortó health del personaje " + reader.GetInt32(0) +
                        " (" + rawHealth + " → " + health + ").");
                }

                long rawMana = reader.GetInt64(6);
                int mana = rawMana < 0 ? 0 : rawMana > stats.MaxResource ? stats.MaxResource : (int)rawMana;
                if (rawMana < 0 || rawMana > stats.MaxResource)
                {
                    Console.Error.WriteLine(
                        "AVISO: se recortó mana del personaje " + reader.GetInt32(0) +
                        " (" + rawMana + " → " + mana + ").");
                }

                character = new CharacterRecord(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    level,
                    experience,
                    health,
                    mana,
                    reader.GetInt32(7),
                    new Vec2((float)reader.GetDouble(8), (float)reader.GetDouble(9)),
                    mapHash,
                    ParseTime(reader.GetString(11)),
                    ParseTime(reader.GetString(12)),
                    inventoryItems: null,
                    classId: classId,
                    allocStr: allocStr,
                    allocDex: allocDex,
                    allocInt: allocInt,
                    allocVit: allocVit,
                    gold: gold)
                {
                    PvpMode = pvpMode,
                    PvpForcedSeconds = pvpForcedSeconds,
                };

            }

            return character with
            {
                InventoryItems = LoadInventory(connection, character.Id),
                Equipment = equipment!,
                UpdatedUtc = DateTime.UtcNow,
            };
        }

        private ushort[] LoadEquipment(SqliteConnection connection, int characterId)
        {
            var equipment = new ushort[ProtocolConstants.EquipSlotCount];
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT slot, item_id FROM character_equipment " +
                "WHERE character_id = @character ORDER BY slot;";
            command.Parameters.AddWithValue("@character", characterId);

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                long slot = reader.GetInt64(0);
                long itemId = reader.GetInt64(1);
                ItemType? item = itemId > 0 && itemId <= ushort.MaxValue
                    ? _items?.Find((int)itemId)
                    : null;
                bool valid = slot >= 0 && slot < ProtocolConstants.EquipSlotCount &&
                    item != null && item.TryGetEquipSlot(out byte itemSlot) && itemSlot == slot;

                if (!valid)
                {
                    Console.Error.WriteLine(
                        "AVISO: se descartó equipo inválido del personaje " + characterId +
                        " (slot=" + slot + ", item_id=" + itemId + ").");
                    continue;
                }

                equipment[(int)slot] = (ushort)itemId;
            }

            return equipment;
        }

        private InventoryItemRecord[] LoadInventory(SqliteConnection connection, int characterId)
        {
            var inventory = new System.Collections.Generic.List<InventoryItemRecord>();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT slot, item_id, count FROM inventory_items " +
                "WHERE character_id = @character ORDER BY slot;";
            command.Parameters.AddWithValue("@character", characterId);

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                long slot = reader.GetInt64(0);
                long itemId = reader.GetInt64(1);
                long count = reader.GetInt64(2);
                ItemType? type = itemId > 0 && itemId <= ushort.MaxValue
                    ? _items?.Find((int)itemId)
                    : null;
                bool valid = slot >= 0 && slot < _inventorySlots &&
                    itemId > 0 && itemId <= ushort.MaxValue && count >= 1 && count <= 999 &&
                    (_items == null || (type != null && count <= type.MaxStack));

                if (!valid)
                {
                    Console.Error.WriteLine(
                        "AVISO: se descartó una fila de inventory_items inválida del personaje " +
                        characterId + " (slot=" + slot + ", item_id=" + itemId + ", count=" + count + ").");
                    continue;
                }

                inventory.Add(new InventoryItemRecord((int)slot, (int)itemId, (int)count));
            }

            return inventory.ToArray();
        }

        private static string FormatTime(DateTime value) =>
            value.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);

        private static DateTime ParseTime(string value) =>
            DateTime.ParseExact(
                value,
                TimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

        private void ThrowIfNotInitialized()
        {
            ThrowIfDisposed();

            if (!_initialized)
            {
                throw new InvalidOperationException("La base de datos no se ha inicializado.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SqlitePlayerRepository));
            }
        }

        private readonly struct AccountRow
        {
            public AccountRow(int id, byte[] passwordHash, byte[] passwordSalt, int passwordIterations)
            {
                Id = id;
                PasswordHash = passwordHash;
                PasswordSalt = passwordSalt;
                PasswordIterations = passwordIterations;
            }

            public int Id { get; }

            public byte[] PasswordHash { get; }

            public byte[] PasswordSalt { get; }

            public int PasswordIterations { get; }
        }
    }
}
