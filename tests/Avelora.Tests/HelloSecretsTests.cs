using System;
using System.IO;
using System.Security.Cryptography;
using Avelora.Server.Security;
using Avelora.Shared;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// Pruebas del saludo cifrado (protocolo v4): RSA-OAEP-SHA1 con el nonce del desafío y la
    /// huella de la clave del servidor.
    /// </summary>
    public class HelloSecretsTests
    {
        private static byte[] NewNonce(byte seed)
        {
            var nonce = new byte[HelloSecrets.NonceSize];

            for (int i = 0; i < nonce.Length; i++)
            {
                nonce[i] = (byte)(seed + i);
            }

            return nonce;
        }

        [Fact]
        public void Cifrar_Y_Descifrar_Con_El_Maximo_De_Longitudes()
        {
            using RSA rsa = RSA.Create(2048);
            RSAParameters publicKey = rsa.ExportParameters(includePrivateParameters: false);
            RSAParameters privateKey = rsa.ExportParameters(includePrivateParameters: true);

            byte[] nonce = NewNonce(1);
            string password = new string('p', ProtocolConstants.MaxPasswordBytes);
            string invitation = new string('i', ProtocolConstants.MaxInvitationCodeBytes);

            // Con los máximos actuales el texto plano ocupa 210 de los 214 bytes de OAEP.
            int plaintextSize = HelloSecrets.NonceSize + 1 +
                ProtocolConstants.MaxPasswordBytes + 1 +
                ProtocolConstants.MaxInvitationCodeBytes;

            Assert.True(
                plaintextSize <= HelloSecrets.MaxPlaintextSize,
                "Los máximos actuales ya no caben en OAEP: " + plaintextSize + " > " + HelloSecrets.MaxPlaintextSize + ".");

            byte[] encrypted = HelloSecrets.Encrypt(publicKey, nonce, password, invitation);

            Assert.Equal(HelloSecrets.EncryptedSize, encrypted.Length);
            Assert.True(
                HelloSecrets.TryDecrypt(privateKey, encrypted, nonce, out string decodedPassword, out string decodedInvitation, out string reason),
                reason);

            Assert.Equal(password, decodedPassword);
            Assert.Equal(invitation, decodedInvitation);
        }

        [Fact]
        public void Un_Nonce_Distinto_Se_Rechaza()
        {
            using RSA rsa = RSA.Create(2048);
            RSAParameters publicKey = rsa.ExportParameters(includePrivateParameters: false);
            RSAParameters privateKey = rsa.ExportParameters(includePrivateParameters: true);

            byte[] nonce = NewNonce(2);
            byte[] other = NewNonce(2);
            other[0] ^= 0xFF;

            byte[] encrypted = HelloSecrets.Encrypt(publicKey, nonce, "secreto", "invitacion");

            Assert.False(HelloSecrets.TryDecrypt(privateKey, encrypted, other, out _, out _, out string reason));
            Assert.Contains("nonce", reason);
        }

        [Fact]
        public void Un_Bloque_Manipulado_Se_Rechaza()
        {
            using RSA rsa = RSA.Create(2048);
            RSAParameters publicKey = rsa.ExportParameters(includePrivateParameters: false);
            RSAParameters privateKey = rsa.ExportParameters(includePrivateParameters: true);

            byte[] nonce = NewNonce(3);
            byte[] encrypted = HelloSecrets.Encrypt(publicKey, nonce, "secreto", "invitacion");
            encrypted[encrypted.Length / 2] ^= 0x01;

            Assert.False(HelloSecrets.TryDecrypt(privateKey, encrypted, nonce, out _, out _, out string reason));
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void Un_Nonce_De_Tamano_Incorrecto_No_Se_Puede_Cifrar()
        {
            using RSA rsa = RSA.Create(2048);
            RSAParameters publicKey = rsa.ExportParameters(includePrivateParameters: false);

            Assert.Throws<ArgumentException>(
                () => HelloSecrets.Encrypt(publicKey, new byte[HelloSecrets.NonceSize - 1], "secreto", "invitacion"));
        }

        [Fact]
        public void La_Huella_Es_Estable_Y_Distingue_Claves()
        {
            using RSA first = RSA.Create(2048);
            using RSA second = RSA.Create(2048);

            RSAParameters firstPublic = first.ExportParameters(includePrivateParameters: false);
            RSAParameters firstAgain = first.ExportParameters(includePrivateParameters: false);
            RSAParameters secondPublic = second.ExportParameters(includePrivateParameters: false);

            string fingerprint = HelloSecrets.Fingerprint(firstPublic);

            Assert.Equal(64, fingerprint.Length);
            Assert.Equal(fingerprint, HelloSecrets.Fingerprint(firstAgain));
            Assert.NotEqual(fingerprint, HelloSecrets.Fingerprint(secondPublic));
        }

        [Fact]
        public void Cargar_La_Clave_Del_Archivo_Da_La_Misma_Huella()
        {
            string folder = Path.Combine(
                Path.GetTempPath(),
                "avelora-h02-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "server-key.pem");

            try
            {
                string generated;

                using (ServerKey key = ServerKey.LoadOrCreate(path, persist: true))
                {
                    generated = key.Fingerprint;
                }

                Assert.True(File.Exists(path), "La clave debería haberse guardado en PKCS#8 PEM.");

                using (ServerKey loaded = ServerKey.LoadOrCreate(path, persist: true))
                {
                    Assert.Equal(generated, loaded.Fingerprint);
                    Assert.Equal(HelloSecrets.Fingerprint(loaded.PublicParameters), loaded.Fingerprint);
                }
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [Fact]
        public void Sin_Persistencia_La_Clave_Es_Efimera()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "avelora-h02-" + Guid.NewGuid().ToString("N"),
                "server-key.pem");

            using (ServerKey key = ServerKey.LoadOrCreate(path, persist: false))
            {
                Assert.Null(key.Path);
                Assert.False(File.Exists(path));
            }
        }
    }
}
