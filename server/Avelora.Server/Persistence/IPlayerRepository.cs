#nullable enable
using System;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Persistence
{
    /// <summary>
    /// Persistencia de cuentas y personajes. Es la única costura que conoce la base de datos:
    /// cambiar SQLite por PostgreSQL será escribir otra implementación (plan del servidor §8).
    /// </summary>
    public interface IPlayerRepository : IDisposable
    {
        /// <summary>Crea el esquema si falta y comprueba que la versión es la esperada.</summary>
        void Initialize();

        /// <summary>
        /// Verifica la contraseña de una cuenta existente. Nunca crea cuentas.
        /// </summary>
        AccountLoginResult Login(string accountName, string password);

        /// <summary>Crea una cuenta si el registro está abierto y la invitación es válida.</summary>
        AccountLoginResult Register(
            string accountName,
            string password,
            string invitationCode,
            string registrationCode);

        /// <summary>Lista los personajes activos de la cuenta en orden de creación.</summary>
        CharacterRecord[] ListCharacters(int accountId) => Array.Empty<CharacterRecord>();

        /// <summary>Crea un personaje con la clase y los límites configurados.</summary>
        CharacterCreateResult CreateCharacter(
            int accountId,
            string name,
            byte classId,
            int maxCharacters,
            int zoneId,
            Vec2 spawn,
            byte[] mapHash) => throw new NotSupportedException(
                "Este repositorio no implementa la creación de personajes.");

        /// <summary>Borra lógicamente un personaje tras confirmar su nombre exacto.</summary>
        RejectReason DeleteCharacter(int accountId, int characterId, string confirmationName) =>
            RejectReason.CharacterNotFound;

        /// <summary>Busca un personaje activo perteneciente a la cuenta.</summary>
        bool TryGetCharacter(int accountId, int characterId, out CharacterRecord? character)
        {
            character = null;
            return false;
        }

        /// <summary>Guarda el estado actual del personaje. Debe ser seguro llamarlo desde la cola de guardado.</summary>
        void SaveCharacter(CharacterRecord character);
    }
}
