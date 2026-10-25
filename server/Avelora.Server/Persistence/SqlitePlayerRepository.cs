#nullable enable
using System;
using System.Globalization;
using System.IO;
using Avelora.Server.Items;
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
        public const int SchemaVersion = 2;

        /// <summary>Personajes que puede tener una cuenta. Frena la creación de filas por login.</summary>
        public const int MaxCharactersPerAccount = 3;

        private const string TimeFormat = "O";

        private readonly string _connectionString;
        private readonly int _passwordIterations;
        private readonly ItemCatalog? _items;
        private readonly int _inventorySlots;
        private readonly object _initializeGate = new object();
        private bool _initialized;
        private bool _disposed;

        public SqlitePlayerRepository(
            string databasePath,
            int passwordIterations = PasswordHasher.DefaultIterations,
            ItemCatalog? items = null,
            int inventorySlots = 20)
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

                if (version == 0)
                {
                    using SqliteTransaction transaction = connection.BeginTransaction();
                    CreateSchema(connection, transaction);
                    SetUserVersion(connection, transaction, SchemaVersion);
                    transaction.Commit();
                }
                else if (version == 1)
                {
                    using SqliteTransaction transaction = connection.BeginTransaction();
                    CreateInventorySchema(connection, transaction);
                    SetUserVersion(connection, transaction, SchemaVersion);
                    transaction.Commit();
                }
                else if (version != SchemaVersion)
                {
                    throw new InvalidOperationException(
                        "La base de datos '" + DatabasePath + "' tiene esquema versión " + version +
                        "; este servidor espera la " + SchemaVersion + ".");
                }

                _initialized = true;
            }
        }

        public AccountLoginResult Login(
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

            // Misma respuesta que una contraseña mala: si dijéramos «registro cerrado» o
            // «invitación incorrecta», se podría averiguar qué cuentas existen probando nombres.
            if (string.IsNullOrEmpty(registrationCode) ||
                !string.Equals(invitationCode ?? string.Empty, registrationCode, StringComparison.Ordinal))
            {
                return AccountLoginResult.Failed(RejectReason.InvalidCredentials);
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

            object? result = insert.ExecuteScalar();
            int accountId = Convert.ToInt32(result, CultureInfo.InvariantCulture);

            return AccountLoginResult.Ok(accountId);
        }

        public CharacterRecord? GetOrCreateCharacter(
            int accountId,
            string characterName,
            int zoneId,
            Vec2 spawn,
            byte[] mapHash)
        {
            ThrowIfNotInitialized();

            if (accountId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(accountId));
            }

            if (!LoginValidation.IsValidCharacterName(characterName))
            {
                throw new ArgumentException("El nombre del personaje no es válido.", nameof(characterName));
            }

            if (mapHash is null || mapHash.Length != ProtocolConstants.MapHashSize)
            {
                throw new ArgumentException(
                    "La huella del mapa debe tener " + ProtocolConstants.MapHashSize + " bytes.",
                    nameof(mapHash));
            }

            using SqliteConnection connection = OpenConnection();

            CharacterRecord? existing = FindCharacter(connection, accountId, characterName);

            if (existing != null)
            {
                return existing;
            }

            if (CountCharacters(connection, accountId) >= MaxCharactersPerAccount)
            {
                // El llamador traduce el null a Reject(CharacterLimit): la cuenta existe y las
                // credenciales eran válidas, pero no hay sitio para otro personaje.
                return null;
            }

            DateTime now = DateTime.UtcNow;

            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO characters " +
                "(account_id, name, level, experience, health, mana, zone_id, x, y, map_hash, created_utc, updated_utc) " +
                "VALUES (@account, @name, 1, 0, 100, 100, @zone, @x, @y, @map, @created, @updated) " +
                "RETURNING id;";
            insert.Parameters.AddWithValue("@account", accountId);
            insert.Parameters.AddWithValue("@name", characterName);
            insert.Parameters.AddWithValue("@zone", zoneId);
            insert.Parameters.AddWithValue("@x", (double)spawn.X);
            insert.Parameters.AddWithValue("@y", (double)spawn.Y);
            insert.Parameters.AddWithValue("@map", mapHash);
            insert.Parameters.AddWithValue("@created", FormatTime(now));
            insert.Parameters.AddWithValue("@updated", FormatTime(now));

            object? result = insert.ExecuteScalar();
            int characterId = Convert.ToInt32(result, CultureInfo.InvariantCulture);

            return new CharacterRecord(
                characterId,
                accountId,
                characterName,
                1,
                0,
                100,
                100,
                zoneId,
                spawn,
                mapHash,
                now,
                now);
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
                "zone_id = @zone, x = @x, y = @y, map_hash = @map, updated_utc = @updated " +
                "WHERE id = @id AND account_id = @account;";
            update.Parameters.AddWithValue("@level", character.Level);
            update.Parameters.AddWithValue("@experience", character.Experience);
            update.Parameters.AddWithValue("@health", character.Health);
            update.Parameters.AddWithValue("@mana", character.Mana);
            update.Parameters.AddWithValue("@zone", character.ZoneId);
            update.Parameters.AddWithValue("@x", (double)character.Position.X);
            update.Parameters.AddWithValue("@y", (double)character.Position.Y);
            update.Parameters.AddWithValue("@map", character.MapHash);
            update.Parameters.AddWithValue("@updated", FormatTime(character.UpdatedUtc));
            update.Parameters.AddWithValue("@id", character.Id);
            update.Parameters.AddWithValue("@account", character.AccountId);

            int rows = update.ExecuteNonQuery();

            if (rows != 1)
            {
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
    name            TEXT NOT NULL COLLATE NOCASE,
    level           INTEGER NOT NULL DEFAULT 1,
    experience      INTEGER NOT NULL DEFAULT 0,
    health          INTEGER NOT NULL DEFAULT 100,
    mana            INTEGER NOT NULL DEFAULT 100,
    zone_id         INTEGER NOT NULL,
    x               REAL NOT NULL,
    y               REAL NOT NULL,
    map_hash        BLOB NOT NULL,
    created_utc     TEXT NOT NULL,
    updated_utc     TEXT NOT NULL,
    UNIQUE (account_id, name)
);

CREATE INDEX characters_account_idx ON characters(account_id);
";
            command.ExecuteNonQuery();
            CreateInventorySchema(connection, transaction);
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

        private static int CountCharacters(SqliteConnection connection, int accountId)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM characters WHERE account_id = @account;";
            command.Parameters.AddWithValue("@account", accountId);

            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private CharacterRecord? FindCharacter(
            SqliteConnection connection,
            int accountId,
            string characterName)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, account_id, name, level, experience, health, mana, zone_id, x, y, " +
                "map_hash, created_utc, updated_utc " +
                "FROM characters WHERE account_id = @account AND name = @name COLLATE NOCASE LIMIT 1;";
            command.Parameters.AddWithValue("@account", accountId);
            command.Parameters.AddWithValue("@name", characterName);

            CharacterRecord? character;
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

                character = new CharacterRecord(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    new Vec2((float)reader.GetDouble(8), (float)reader.GetDouble(9)),
                    mapHash,
                    ParseTime(reader.GetString(11)),
                    ParseTime(reader.GetString(12)));
            }

            return character.WithInventory(LoadInventory(connection, character.Id));
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
