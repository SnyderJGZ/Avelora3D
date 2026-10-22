#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;

namespace Avelora.Shared.Protocol
{
    /// <summary>
    /// Cifrado de las credenciales del saludo (protocolo v4): RSA-OAEP-SHA1 con la clave pública
    /// que viaja en el <see cref="ChallengeMessage"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Este archivo lo comparten servidor, bots y cliente de Unity. Por eso usa solo lo que hay en
    /// <c>netstandard2.1</c> y funciona en el Mono de Unity 6: <see cref="RSA.Create()"/> con
    /// <c>RSAParameters</c> y <see cref="RSAEncryptionPadding.OaepSHA1"/>. No se usan
    /// <c>ImportSubjectPublicKeyInfo</c>, <c>AesGcm</c> ni <c>ECDiffieHellman</c>.
    /// </para>
    /// <para>
    /// SHA-1 en OAEP sigue siendo seguro para esto: la debilidad de SHA-1 (colisiones) no afecta a
    /// OAEP, que solo necesita una función hash resistente a preimagen. Además, así funciona en
    /// todas las implementaciones de RSA de .NET y de Unity.
    /// </para>
    /// <para>
    /// Lo que se cifra es <c>nonce(16) ‖ len+contraseña UTF-8 ‖ len+invitación UTF-8</c>, con la
    /// longitud en un byte. Con los máximos actuales (128 y 64 bytes) ocupa 210 bytes, por debajo
    /// del tope de 214 que impone OAEP con una clave de 2048 bits.
    /// </para>
    /// </remarks>
    public static class HelloSecrets
    {
        /// <summary>Tamaño del nonce del desafío, en bytes.</summary>
        public const int NonceSize = 16;

        /// <summary>Tamaño del módulo RSA de 2048 bits, en bytes.</summary>
        public const int ModulusSize = 256;

        /// <summary>Tamaño del bloque cifrado que viaja en el saludo, en bytes.</summary>
        public const int EncryptedSize = 256;

        /// <summary>
        /// Máximo de bytes que caben en un bloque OAEP-SHA1 de 2048 bits:
        /// <c>256 - 2·20 - 2 = 214</c>.
        /// </summary>
        public const int MaxPlaintextSize = EncryptedSize - (2 * 20) - 2;

        /// <summary>
        /// Cifra el nonce y las credenciales con la clave pública del servidor. Devuelve exactamente
        /// <see cref="EncryptedSize"/> bytes.
        /// </summary>
        public static byte[] Encrypt(
            RSAParameters publicKey,
            byte[] nonce,
            string password,
            string invitationCode)
        {
            if (nonce is null || nonce.Length != NonceSize)
            {
                throw new ArgumentException(
                    "El nonce del desafío debe tener " + NonceSize + " bytes.",
                    nameof(nonce));
            }

            if (publicKey.Modulus is null ||
                publicKey.Modulus.Length != ModulusSize ||
                publicKey.Exponent is null ||
                publicKey.Exponent.Length == 0 ||
                publicKey.Exponent.Length > 4)
            {
                throw new ArgumentException("La clave pública del servidor no es RSA de 2048 bits.", nameof(publicKey));
            }

            byte[] passwordBytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
            byte[] invitationBytes = Encoding.UTF8.GetBytes(invitationCode ?? string.Empty);

            if (passwordBytes.Length > ProtocolConstants.MaxPasswordBytes)
            {
                throw new ArgumentException(
                    "La contraseña ocupa " + passwordBytes.Length + " bytes y el tope es " +
                    ProtocolConstants.MaxPasswordBytes + ".",
                    nameof(password));
            }

            if (invitationBytes.Length > ProtocolConstants.MaxInvitationCodeBytes)
            {
                throw new ArgumentException(
                    "La invitación ocupa " + invitationBytes.Length + " bytes y el tope es " +
                    ProtocolConstants.MaxInvitationCodeBytes + ".",
                    nameof(invitationCode));
            }

            int size = NonceSize + 1 + passwordBytes.Length + 1 + invitationBytes.Length;

            if (size > MaxPlaintextSize)
            {
                throw new ArgumentException(
                    "Las credenciales ocupan " + size + " bytes y el máximo que cabe en OAEP es " +
                    MaxPlaintextSize + ".");
            }

            var plaintext = new byte[size];
            int offset = 0;

            Buffer.BlockCopy(nonce, 0, plaintext, offset, NonceSize);
            offset += NonceSize;

            plaintext[offset++] = (byte)passwordBytes.Length;
            Buffer.BlockCopy(passwordBytes, 0, plaintext, offset, passwordBytes.Length);
            offset += passwordBytes.Length;

            plaintext[offset++] = (byte)invitationBytes.Length;
            Buffer.BlockCopy(invitationBytes, 0, plaintext, offset, invitationBytes.Length);

            using RSA rsa = RSA.Create();
            rsa.ImportParameters(publicKey);
            return rsa.Encrypt(plaintext, RSAEncryptionPadding.OaepSHA1);
        }

        /// <summary>
        /// Descifra el bloque del saludo y comprueba que el nonce coincide con el del desafío de
        /// esta conexión. Devuelve <c>false</c> y un motivo legible si algo no encaja.
        /// </summary>
        public static bool TryDecrypt(
            RSAParameters privateKey,
            byte[] encrypted,
            byte[] expectedNonce,
            out string password,
            out string invitationCode,
            out string reason)
        {
            password = string.Empty;
            invitationCode = string.Empty;
            reason = string.Empty;

            if (encrypted is null || encrypted.Length != EncryptedSize)
            {
                reason = "el bloque cifrado no mide " + EncryptedSize + " bytes";
                return false;
            }

            if (expectedNonce is null || expectedNonce.Length != NonceSize)
            {
                reason = "no hay un desafío válido para esta conexión";
                return false;
            }

            byte[] plaintext;

            try
            {
                using RSA rsa = RSA.Create();
                rsa.ImportParameters(privateKey);
                plaintext = rsa.Decrypt(encrypted, RSAEncryptionPadding.OaepSHA1);
            }
            catch (CryptographicException)
            {
                reason = "el bloque no se puede descifrar con la clave del servidor";
                return false;
            }
            catch (ArgumentException)
            {
                reason = "la clave del servidor no es válida";
                return false;
            }

            if (plaintext.Length < NonceSize + 2)
            {
                reason = "el texto cifrado es demasiado corto";
                return false;
            }

            // El nonce va dentro del bloque cifrado: si no coincide con el de esta conexión, el
            // saludo es un reenvío de otra sesión.
            for (int i = 0; i < NonceSize; i++)
            {
                if (plaintext[i] != expectedNonce[i])
                {
                    reason = "el nonce del saludo no coincide con el del desafío";
                    return false;
                }
            }

            int offset = NonceSize;

            if (!TryReadString(plaintext, ref offset, out password) ||
                !TryReadString(plaintext, ref offset, out invitationCode))
            {
                reason = "las longitudes del texto cifrado no encajan";
                return false;
            }

            if (offset != plaintext.Length)
            {
                reason = "sobran bytes en el texto cifrado";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Huella SHA-256 de <c>módulo ‖ exponente</c> en hexadecimal minúscula. Sirve para que el
        /// cliente compruebe que habla con el servidor que espera.
        /// </summary>
        public static string Fingerprint(RSAParameters publicKey)
        {
            if (publicKey.Modulus is null || publicKey.Exponent is null)
            {
                throw new ArgumentException("La clave pública no tiene módulo ni exponente.", nameof(publicKey));
            }

            var material = new byte[publicKey.Modulus.Length + publicKey.Exponent.Length];
            Buffer.BlockCopy(publicKey.Modulus, 0, material, 0, publicKey.Modulus.Length);
            Buffer.BlockCopy(publicKey.Exponent, 0, material, publicKey.Modulus.Length, publicKey.Exponent.Length);

            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(material);

            var text = new StringBuilder(hash.Length * 2);

            for (int i = 0; i < hash.Length; i++)
            {
                text.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        /// <summary>Construye los parámetros públicos a partir de lo que trajo el desafío.</summary>
        public static RSAParameters ToPublicKey(byte[] modulus, byte[] exponent) =>
            new RSAParameters
            {
                Modulus = modulus,
                Exponent = exponent,
            };

        private static bool TryReadString(byte[] plaintext, ref int offset, out string value)
        {
            value = string.Empty;

            if (offset >= plaintext.Length)
            {
                return false;
            }

            int length = plaintext[offset++];

            if (length < 0 || offset + length > plaintext.Length)
            {
                return false;
            }

            try
            {
                var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                value = encoding.GetString(plaintext, offset, length);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }

            offset += length;
            return true;
        }
    }
}
