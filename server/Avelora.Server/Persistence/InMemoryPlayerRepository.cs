#nullable enable
using System;
using System.Collections.Generic;
using Avelora.Server.Progression;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Persistence
{
    /// <summary>Repositorio en memoria para el modo explícito sin persistencia.</summary>
    /// <remarks>Solo lo usa el hilo de red; las fotos de simulación se le entregan por una cola.</remarks>
    public sealed class InMemoryPlayerRepository : IPlayerRepository
    {
        private readonly ClassCatalog _classes;
        private readonly Dictionary<string, int> _accountIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, List<CharacterRecord>> _characters = new Dictionary<int, List<CharacterRecord>>();
        private readonly HashSet<(int AccountId, int CharacterId)> _deletedCharacters = new HashSet<(int, int)>();
        private int _nextAccountId = 1;
        private int _nextCharacterId;
        private bool _initialized;
        private bool _disposed;

        public InMemoryPlayerRepository(ClassCatalog classes, int firstCharacterId)
        {
            _classes = classes ?? throw new ArgumentNullException(nameof(classes));
            if (firstCharacterId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(firstCharacterId));
            }

            _nextCharacterId = firstCharacterId;
        }

        public void Initialize()
        {
            ThrowIfDisposed();
            _initialized = true;
        }

        public AccountLoginResult Login(string accountName, string password)
        {
            ThrowIfNotInitialized();
            string normalized = LoginValidation.NormalizeAccount(accountName);
            if (!LoginValidation.IsValidAccountName(normalized))
            {
                return AccountLoginResult.Failed(RejectReason.InvalidCredentials);
            }

            if (!_accountIds.TryGetValue(normalized, out int accountId))
            {
                accountId = _nextAccountId++;
                _accountIds.Add(normalized, accountId);
                _characters.Add(accountId, new List<CharacterRecord>());
            }

            // Sin persistencia se conserva el modo local anterior: la cuenta identifica al jugador,
            // pero no se autentica una contraseña ni se registra fuera del proceso.
            return AccountLoginResult.Ok(accountId);
        }

        public AccountLoginResult Register(
            string accountName,
            string password,
            string invitationCode,
            string registrationCode) => Login(accountName, password);

        public CharacterRecord[] ListCharacters(int accountId)
        {
            ThrowIfNotInitialized();
            return _characters.TryGetValue(accountId, out List<CharacterRecord>? characters)
                ? characters.ToArray()
                : Array.Empty<CharacterRecord>();
        }

        public CharacterCreateResult CreateCharacter(
            int accountId,
            string name,
            byte classId,
            int maxCharacters,
            int zoneId,
            Vec2 spawn,
            byte[] mapHash,
            byte sex = 0)
        {
            if (sex > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(sex));
            }

            ThrowIfNotInitialized();
            if (!_characters.TryGetValue(accountId, out List<CharacterRecord>? characters))
            {
                return CharacterCreateResult.Rejected(RejectReason.CharacterNotFound);
            }

            if (_classes.Find(classId) == null)
            {
                return CharacterCreateResult.Rejected(RejectReason.ClassUnknown);
            }

            string nameKey = LoginValidation.CharacterNameKey(name);
            foreach (List<CharacterRecord> accountCharacters in _characters.Values)
            {
                for (int i = 0; i < accountCharacters.Count; i++)
                {
                    if (LoginValidation.CharacterNameKey(accountCharacters[i].Name) == nameKey)
                    {
                        return CharacterCreateResult.Rejected(RejectReason.NameTaken);
                    }
                }
            }

            if (characters.Count >= maxCharacters)
            {
                return CharacterCreateResult.Rejected(RejectReason.CharacterLimit);
            }

            if (mapHash is null || mapHash.Length != ProtocolConstants.MapHashSize)
            {
                throw new ArgumentException("La huella del mapa no es válida.", nameof(mapHash));
            }

            DerivedCharacterStats stats = CharacterStats.Calculate(_classes, classId, 1, 0, 0, 0, 0);
            DateTime now = DateTime.UtcNow;
            var character = new CharacterRecord(
                _nextCharacterId++,
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
                classId: classId)
            {
                Sex = sex,
            };
            characters.Add(character);
            return CharacterCreateResult.Created(character);
        }

        public RejectReason DeleteCharacter(int accountId, int characterId, string confirmationName)
        {
            ThrowIfNotInitialized();
            if (!_characters.TryGetValue(accountId, out List<CharacterRecord>? characters))
            {
                return RejectReason.CharacterNotFound;
            }

            for (int i = 0; i < characters.Count; i++)
            {
                if (characters[i].Id != characterId)
                {
                    continue;
                }

                if (!string.Equals(characters[i].Name, confirmationName, StringComparison.Ordinal))
                {
                    return RejectReason.ConfirmMismatch;
                }

                _deletedCharacters.Add((accountId, characterId));
                characters.RemoveAt(i);
                return RejectReason.Unknown;
            }

            return RejectReason.CharacterNotFound;
        }

        public bool TryGetCharacter(int accountId, int characterId, out CharacterRecord? character)
        {
            ThrowIfNotInitialized();
            if (_characters.TryGetValue(accountId, out List<CharacterRecord>? characters))
            {
                for (int i = 0; i < characters.Count; i++)
                {
                    if (characters[i].Id == characterId)
                    {
                        character = characters[i];
                        return true;
                    }
                }
            }

            character = null;
            return false;
        }

        public void SaveCharacter(CharacterRecord character)
        {
            ThrowIfNotInitialized();
            if (_characters.TryGetValue(character.AccountId, out List<CharacterRecord>? characters))
            {
                for (int i = 0; i < characters.Count; i++)
                {
                    if (characters[i].Id == character.Id)
                    {
                        characters[i] = character;
                        return;
                    }
                }
            }

            // Un guardado pendiente de una sesión expulsada no debe recuperar un personaje borrado.
            if (_deletedCharacters.Contains((character.AccountId, character.Id)))
            {
                return;
            }

            throw new InvalidOperationException("El personaje en memoria ya no existe.");
        }

        public void Dispose() => _disposed = true;

        private void ThrowIfNotInitialized()
        {
            ThrowIfDisposed();
            if (!_initialized)
            {
                throw new InvalidOperationException("El repositorio en memoria no está inicializado.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(InMemoryPlayerRepository));
            }
        }
    }
}
