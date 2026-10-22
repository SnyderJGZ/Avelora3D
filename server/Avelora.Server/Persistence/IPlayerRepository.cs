#nullable enable
using System;
using Avelora.Shared;

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
        /// Verifica la contraseña. Si la cuenta no existe, solo la crea cuando el código de
        /// invitación coincide con <paramref name="registrationCode"/>; si ese código está vacío,
        /// el registro está cerrado.
        /// </summary>
        AccountLoginResult Login(
            string accountName,
            string password,
            string invitationCode,
            string registrationCode);

        /// <summary>
        /// Devuelve el personaje de esa cuenta o lo crea en <paramref name="spawn"/> si es la
        /// primera vez. El nombre se compara sin distinguir mayúsculas. Devuelve <c>null</c> si la
        /// cuenta ya tiene el máximo de personajes que admite la implementación.
        /// </summary>
        CharacterRecord? GetOrCreateCharacter(
            int accountId,
            string characterName,
            int zoneId,
            Vec2 spawn,
            byte[] mapHash);

        /// <summary>Guarda el estado actual del personaje. Debe ser seguro llamarlo desde la cola de guardado.</summary>
        void SaveCharacter(CharacterRecord character);
    }
}
