#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;

namespace Avelora.Shared.Maps
{
    /// <summary>
    /// Lectura y escritura del archivo de mapa de colisiones (<c>.avmap</c>).
    /// </summary>
    /// <remarks>
    /// El servidor nunca lee escenas de Unity: la rejilla se exporta desde el Tilemap a este
    /// archivo binario y ambos lados cargan <b>el mismo</b> archivo (guía del núcleo §6.3).
    /// Por eso el formato es:
    /// <list type="bullet">
    /// <item><b>Portable:</b> todos los enteros van en little-endian escritos a mano, no vía
    /// <see cref="BitConverter"/> (que depende del orden de bytes de la máquina).</item>
    /// <item><b>Versionado:</b> la cabecera lleva <see cref="FormatVersion"/> para rechazar
    /// archivos de un formato distinto.</item>
    /// <item><b>Verificable:</b> termina en un SHA-256 de todo lo anterior, de modo que un
    /// archivo truncado o corrupto se detecta al cargar, y dos instalaciones pueden comparar
    /// sus mapas por huella (<see cref="ComputeContentHash"/>).</item>
    /// </list>
    ///
    /// <para><b>Distribución del archivo</b></para>
    /// <code>
    /// Offset  Tamaño  Campo
    ///   0       4     Magic "AVMP"
    ///   4       2     FormatVersion (ushort LE)
    ///   6       2     Flags (ushort LE, reservado = 0)
    ///   8       4     Width  (int32 LE)  - nº de celdas en X
    ///  12       4     Height (int32  LE) - nº de celdas en Y
    ///  16       4     OriginX (int32 LE) - tile de la celda (0,0)
    ///  20       4     OriginY (int32 LE) - tile de la celda (0,0)
    ///  24       8     reservado (= 0)
    ///  32       N     Payload: mapa de bits de bloqueo, fila a fila, LSB-primero
    ///  32+N    32     SHA-256 de (cabecera + payload)
    /// </code>
    /// donde <c>N = ceil(Width * Height / 8)</c>.
    /// </remarks>
    public static class MapFile
    {
        /// <summary>Marca del archivo: bytes ASCII "AVMP" (Avelora Map).</summary>
        public static readonly byte[] Magic = { (byte)'A', (byte)'V', (byte)'M', (byte)'P' };

        /// <summary>Versión del formato. Se sube al cambiar la cabecera o el empaquetado.</summary>
        public const ushort FormatVersion = 1;

        /// <summary>Tamaño de la cabecera en bytes.</summary>
        public const int HeaderSize = 32;

        /// <summary>Tamaño de la huella SHA-256 final.</summary>
        public const int HashSize = 32;

        /// <summary>Extensión recomendada del archivo.</summary>
        public const string Extension = ".avmap";

        /// <summary>
        /// Límite defensivo de celdas (16.7 M ≈ 4096×4096). Un mapa grande real ronda los
        /// cientos de miles de tiles; el tope evita desbordamientos y reservas absurdas si
        /// llega un archivo hostil o corrupto.
        /// </summary>
        public const int MaxTiles = 1 << 24;

        /// <summary>Bytes que ocupa el payload de bloqueo (1 bit por tile).</summary>
        public static int PayloadSize(int width, int height)
        {
            long tiles = (long)width * height;
            return (int)((tiles + 7) / 8);
        }

        /// <summary>Serializa la rejilla a un archivo .avmap en memoria.</summary>
        public static byte[] Serialize(MapCollisionGrid grid)
        {
            if (grid is null)
            {
                throw new ArgumentNullException(nameof(grid));
            }

            int payloadBytes = PayloadSize(grid.Width, grid.Height);
            var buffer = new byte[HeaderSize + payloadBytes + HashSize];

            Magic.CopyTo(buffer, 0);
            WriteUInt16(buffer, 4, FormatVersion);
            WriteUInt16(buffer, 6, 0);
            WriteInt32(buffer, 8, grid.Width);
            WriteInt32(buffer, 12, grid.Height);
            WriteInt32(buffer, 16, grid.OriginX);
            WriteInt32(buffer, 20, grid.OriginY);

            // Empaquetado de bits: índice i -> byte i/8, bit i%8 (LSB primero).
            bool[] blocked = grid.BlockedTiles;
            for (int i = 0; i < blocked.Length; i++)
            {
                if (blocked[i])
                {
                    buffer[HeaderSize + (i >> 3)] |= (byte)(1 << (i & 7));
                }
            }

            byte[] hash = ComputeSha256(buffer, 0, HeaderSize + payloadBytes);
            Buffer.BlockCopy(hash, 0, buffer, HeaderSize + payloadBytes, HashSize);

            return buffer;
        }

        /// <summary>Deserializa y valida un archivo .avmap.</summary>
        /// <exception cref="MapFormatException">Si el archivo no es válido o está corrupto.</exception>
        public static MapCollisionGrid Deserialize(byte[] data)
        {
            if (data is null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (data.Length < HeaderSize + HashSize)
            {
                throw new MapFormatException("Archivo demasiado corto: le faltan la cabecera o la huella.");
            }

            for (int i = 0; i < Magic.Length; i++)
            {
                if (data[i] != Magic[i])
                {
                    throw new MapFormatException("Marca de archivo incorrecta: no es un mapa de Avelora (.avmap).");
                }
            }

            ushort version = ReadUInt16(data, 4);
            if (version != FormatVersion)
            {
                throw new MapFormatException(
                    "Versión de formato no soportada: " + version + " (se esperaba " + FormatVersion + ").");
            }

            int width = ReadInt32(data, 8);
            int height = ReadInt32(data, 12);
            int originX = ReadInt32(data, 16);
            int originY = ReadInt32(data, 20);

            if (width <= 0 || height <= 0)
            {
                throw new MapFormatException("Dimensiones inválidas: " + width + "x" + height + ".");
            }

            long tiles = (long)width * height;
            if (tiles > MaxTiles)
            {
                throw new MapFormatException("Mapa demasiado grande: " + tiles + " celdas (máximo " + MaxTiles + ").");
            }

            int payloadBytes = PayloadSize(width, height);
            int expected = HeaderSize + payloadBytes + HashSize;
            if (data.Length != expected)
            {
                throw new MapFormatException(
                    "Longitud inesperada: " + data.Length + " bytes (se esperaban " + expected + ").");
            }

            byte[] expectedHash = ComputeSha256(data, 0, HeaderSize + payloadBytes);
            for (int i = 0; i < HashSize; i++)
            {
                if (data[HeaderSize + payloadBytes + i] != expectedHash[i])
                {
                    throw new MapFormatException("Huella SHA-256 incorrecta: el archivo está corrupto o truncado.");
                }
            }

            var blocked = new bool[(int)tiles];
            int baseOffset = HeaderSize;
            for (int i = 0; i < blocked.Length; i++)
            {
                blocked[i] = (data[baseOffset + (i >> 3)] & (1 << (i & 7))) != 0;
            }

            return new MapCollisionGrid(width, height, blocked, originX, originY);
        }

        /// <summary>Guarda la rejilla en disco.</summary>
        public static void Save(string path, MapCollisionGrid grid)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Falta la ruta de destino.", nameof(path));
            }

            File.WriteAllBytes(path, Serialize(grid));
        }

        /// <summary>Carga la rejilla desde disco.</summary>
        public static MapCollisionGrid Load(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Falta la ruta de origen.", nameof(path));
            }

            return Deserialize(File.ReadAllBytes(path));
        }

        /// <summary>
        /// Huella hexadecimal del contenido del mapa (tamaño, origen y bits de bloqueo).
        /// Cliente y servidor la comparan al conectar para detectar mapas desajustados
        /// (guía del núcleo §6.3).
        /// </summary>
        public static string ComputeContentHash(MapCollisionGrid grid) => ToHexString(ComputeContentHashBytes(grid));

        /// <summary>
        /// La misma huella que <see cref="ComputeContentHash"/>, pero en sus 32 bytes. Es la forma
        /// que viaja en el saludo del protocolo: comparar 32 bytes evita depender de mayúsculas,
        /// espacios o del formato hexadecimal.
        /// </summary>
        public static byte[] ComputeContentHashBytes(MapCollisionGrid grid)
        {
            if (grid is null)
            {
                throw new ArgumentNullException(nameof(grid));
            }

            byte[] payload = Serialize(grid);
            return ComputeSha256(payload, 0, payload.Length - HashSize);
        }

        private static byte[] ComputeSha256(byte[] buffer, int offset, int count)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(buffer, offset, count);
            }
        }

        /// <summary>Convierte una huella a hexadecimal minúscula, para mostrarla o compararla a ojo.</summary>
        public static string ToHexString(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            const string hex = "0123456789abcdef";
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = hex[bytes[i] >> 4];
                chars[(i * 2) + 1] = hex[bytes[i] & 0xF];
            }

            return new string(chars);
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static ushort ReadUInt16(byte[] buffer, int offset) =>
            (ushort)(buffer[offset] | (buffer[offset + 1] << 8));

        private static int ReadInt32(byte[] buffer, int offset) =>
            buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);
    }

    /// <summary>Error de formato al leer un archivo de mapa.</summary>
    public sealed class MapFormatException : Exception
    {
        public MapFormatException(string message)
            : base(message)
        {
        }
    }
}
