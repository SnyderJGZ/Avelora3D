#nullable enable
using System;
using System.Security.Cryptography;

namespace Avelora.Server.Persistence
{
    /// <summary>
    /// Hash de contraseñas con PBKDF2-HMAC-SHA256 y sal aleatoria (plan del servidor §8).
    /// </summary>
    /// <remarks>
    /// PBKDF2 viene en la biblioteca estándar y evita añadir una dependencia nativa al servidor.
    /// El número de iteraciones se guarda con cada cuenta para poder subirlo en el futuro sin
    /// invalidar las contraseñas existentes.
    /// </remarks>
    public static class PasswordHasher
    {
        public const int DefaultIterations = 100_000;

        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int MaxIterations = 1_000_000;

        /// <summary>Crea sal y hash nuevos para una contraseña.</summary>
        public static void Create(
            string password,
            out byte[] salt,
            out byte[] hash,
            out int iterations)
        {
            if (password is null)
            {
                throw new ArgumentNullException(nameof(password));
            }

            salt = new byte[SaltSize];
            RandomNumberGenerator.Fill(salt);
            iterations = DefaultIterations;
            hash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                HashSize);
        }

        /// <summary>
        /// Comprueba una contraseña en tiempo constante. Un hash o una sal corruptos se tratan
        /// como credenciales inválidas, no como una excepción que tumbe la conexión.
        /// </summary>
        public static bool Verify(
            string password,
            byte[] salt,
            byte[] expectedHash,
            int iterations)
        {
            if (password is null ||
                salt is null ||
                expectedHash is null ||
                salt.Length < 8 ||
                expectedHash.Length != HashSize ||
                iterations < 1 ||
                iterations > MaxIterations)
            {
                return false;
            }

            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expectedHash.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
        }
    }
}
