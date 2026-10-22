#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Security
{
    /// <summary>
    /// Clave RSA-2048 del servidor para cifrar el saludo (H-02).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Se guarda en PKCS#8 PEM (<c>-----BEGIN PRIVATE KEY-----</c>) en
    /// <see cref="Configuration.ServerConfig.ServerKeyPath"/>. Si el archivo no existe se genera y
    /// se escribe; si la persistencia está desactivada, se genera una clave efímera en cada
    /// arranque y no se toca el disco.
    /// </para>
    /// <para>
    /// La huella (SHA-256 de módulo ‖ exponente) se imprime al arrancar y es lo que el cliente puede
    /// fijar con <c>serverKeyFingerprint</c> para no aceptar una clave distinta.
    /// </para>
    /// </remarks>
    public sealed class ServerKey : IDisposable
    {
        private const string PemHeader = "-----BEGIN PRIVATE KEY-----";
        private const string PemFooter = "-----END PRIVATE KEY-----";

        private readonly RSA _rsa;

        private ServerKey(RSA rsa, string? path)
        {
            _rsa = rsa;
            Path = path;
            PrivateParameters = rsa.ExportParameters(includePrivateParameters: true);
            PublicParameters = rsa.ExportParameters(includePrivateParameters: false);
            Fingerprint = HelloSecrets.Fingerprint(PublicParameters);
        }

        /// <summary>Archivo del que se cargó o en el que se guardó la clave; <c>null</c> si es efímera.</summary>
        public string? Path { get; }

        /// <summary>Clave RSA viva, para el hilo de login.</summary>
        public RSA Rsa => _rsa;

        public RSAParameters PrivateParameters { get; }

        public RSAParameters PublicParameters { get; }

        /// <summary>Huella hexadecimal de <c>módulo ‖ exponente</c>.</summary>
        public string Fingerprint { get; }

        /// <summary>
        /// Carga la clave de <paramref name="path"/> si existe y hay persistencia; si no, genera una
        /// nueva (y la guarda cuando <paramref name="persist"/> es verdadero).
        /// </summary>
        public static ServerKey LoadOrCreate(string path, bool persist)
        {
            if (persist && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return Load(path);
            }

            RSA rsa = RSA.Create(2048);
            ValidateKeySize(rsa, path);

            if (persist && !string.IsNullOrWhiteSpace(path))
            {
                Save(path, rsa);
            }

            return new ServerKey(rsa, persist && !string.IsNullOrWhiteSpace(path) ? path : null);
        }

        public void Dispose() => _rsa.Dispose();

        private static ServerKey Load(string path)
        {
            RSA rsa = RSA.Create();

            try
            {
                rsa.ImportFromPem(File.ReadAllText(path));
                ValidateKeySize(rsa, path);
                return new ServerKey(rsa, path);
            }
            catch
            {
                // Un archivo corrupto no debe dejar la clave a medias: se libera y se propaga.
                rsa.Dispose();
                throw;
            }
        }

        private static void Save(string path, RSA rsa)
        {
            string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string body = Convert.ToBase64String(
                rsa.ExportPkcs8PrivateKey(),
                Base64FormattingOptions.InsertLineBreaks);

            File.WriteAllText(
                path,
                PemHeader + Environment.NewLine + body + Environment.NewLine + PemFooter + Environment.NewLine);
        }

        private static void ValidateKeySize(RSA rsa, string path)
        {
            if (rsa.KeySize != 2048)
            {
                throw new InvalidOperationException(
                    "La clave del servidor en '" + path + "' es de " + rsa.KeySize +
                    " bits; el protocolo exige RSA de 2048.");
            }
        }
    }
}
