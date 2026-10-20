#nullable enable
using System;
using System.Text;

namespace Avelora.Shared.Protocol
{
    /// <summary>
    /// Lector binario little-endian sobre un tramo de un búfer recibido.
    /// </summary>
    /// <remarks>
    /// Cada lectura comprueba que quedan bytes: un paquete truncado lanza
    /// <see cref="ProtocolException"/> en el punto exacto, en vez de leer basura del resto del
    /// búfer o provocar un <see cref="IndexOutOfRangeException"/> difícil de diagnosticar.
    /// </remarks>
    public sealed class WireReader
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly byte[] _buffer;
        private readonly int _end;
        private int _position;

        public WireReader(byte[] buffer, int offset, int length)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || length < 0 || offset + length > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "El tramo pedido se sale del búfer.");
            }

            _position = offset;
            _end = offset + length;
        }

        /// <summary>Bytes que quedan por leer.</summary>
        public int Remaining => _end - _position;

        public byte ReadByte()
        {
            Ensure(1);
            return _buffer[_position++];
        }

        public sbyte ReadSByte() => (sbyte)ReadByte();

        public ushort ReadUInt16()
        {
            Ensure(2);
            ushort value = (ushort)(_buffer[_position] | (_buffer[_position + 1] << 8));
            _position += 2;
            return value;
        }

        public int ReadInt32()
        {
            Ensure(4);
            int value =
                _buffer[_position] |
                (_buffer[_position + 1] << 8) |
                (_buffer[_position + 2] << 16) |
                (_buffer[_position + 3] << 24);
            _position += 4;
            return value;
        }

        public uint ReadUInt32() => unchecked((uint)ReadInt32());

        public float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());

        public void ReadBytes(byte[] destination, int count)
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            Ensure(count);
            Buffer.BlockCopy(_buffer, _position, destination, 0, count);
            _position += count;
        }

        /// <summary>
        /// Lee una cadena con longitud en bytes (uint16) y UTF-8 estricto. Un texto que no sea
        /// UTF-8 válido o que supere <paramref name="maxBytes"/> se trata como mensaje malformado.
        /// </summary>
        public string ReadString(int maxBytes)
        {
            if (maxBytes < 0 || maxBytes > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }

            int byteCount = ReadUInt16();

            if (byteCount > maxBytes)
            {
                throw new ProtocolException(
                    "Cadena de " + byteCount + " bytes; el tope es " + maxBytes + ".");
            }

            Ensure(byteCount);

            if (byteCount == 0)
            {
                return string.Empty;
            }

            try
            {
                string value = StrictUtf8.GetString(_buffer, _position, byteCount);
                _position += byteCount;
                return value;
            }
            catch (DecoderFallbackException)
            {
                throw new ProtocolException("La cadena no es UTF-8 válido.");
            }
        }

        private void Ensure(int count)
        {
            if (Remaining < count)
            {
                throw new ProtocolException(
                    "Mensaje truncado: faltan " + (count - Remaining) + " bytes.");
            }
        }
    }
}
