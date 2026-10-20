#nullable enable
using System;
using System.Text;

namespace Avelora.Shared.Protocol
{
    /// <summary>
    /// Escritor binario little-endian sobre un búfer ya reservado.
    /// </summary>
    /// <remarks>
    /// Se escriben los enteros byte a byte, igual que en <see cref="Maps.MapFile"/>, para que el
    /// formato no dependa de la plataforma ni del endianness del equipo. Es una clase y no un
    /// <c>struct</c> para no copiar la posición sin querer al pasarlo como parámetro.
    /// </remarks>
    public sealed class WireWriter
    {
        private readonly byte[] _buffer;
        private int _position;

        public WireWriter(byte[] buffer)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        }

        /// <summary>Bytes escritos hasta ahora.</summary>
        public int Position => _position;

        public void WriteByte(byte value)
        {
            _buffer[_position++] = value;
        }

        public void WriteSByte(sbyte value)
        {
            _buffer[_position++] = (byte)value;
        }

        public void WriteUInt16(ushort value)
        {
            _buffer[_position++] = (byte)value;
            _buffer[_position++] = (byte)(value >> 8);
        }

        public void WriteInt32(int value)
        {
            _buffer[_position++] = (byte)value;
            _buffer[_position++] = (byte)(value >> 8);
            _buffer[_position++] = (byte)(value >> 16);
            _buffer[_position++] = (byte)(value >> 24);
        }

        public void WriteUInt32(uint value)
        {
            WriteInt32(unchecked((int)value));
        }

        /// <summary>Escribe un <c>float</c> por sus bits: ida y vuelta exacta, sin pérdida decimal.</summary>
        public void WriteSingle(float value)
        {
            WriteInt32(BitConverter.SingleToInt32Bits(value));
        }

        public void WriteBytes(byte[] source, int offset, int count)
        {
            Buffer.BlockCopy(source, offset, _buffer, _position, count);
            _position += count;
        }

        /// <summary>
        /// Escribe una cadena con longitud en bytes (uint16) y UTF-8. El tope se comprueba antes de
        /// reservar, para que un texto absurdo no pueda agrandar el mensaje sin límite.
        /// </summary>
        public void WriteString(string? value, int maxBytes)
        {
            string text = value ?? string.Empty;
            int byteCount = Encoding.UTF8.GetByteCount(text);

            if (maxBytes < 0 || maxBytes > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }

            if (byteCount > maxBytes)
            {
                throw new ArgumentException(
                    "El texto ocupa " + byteCount + " bytes y el tope es " + maxBytes + ".",
                    nameof(value));
            }

            WriteUInt16((ushort)byteCount);

            if (byteCount == 0)
            {
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            WriteBytes(bytes, 0, byteCount);
        }
    }
}
