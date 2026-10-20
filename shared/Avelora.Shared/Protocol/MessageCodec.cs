#nullable enable
using System;
using System.Text;

namespace Avelora.Shared.Protocol
{
    /// <summary>
    /// Traduce mensajes a bytes y de vuelta. Es el único sitio que conoce el orden de los campos:
    /// cliente y servidor comparten este archivo, así que no pueden discrepar (plan del servidor §4).
    /// </summary>
    /// <remarks>
    /// Los mensajes tienen tamaño fijo salvo el snapshot, que lleva una lista. Al leer se comprueba
    /// el tipo, el tamaño y que el tramo no se salga del búfer: un paquete mal formado devuelve
    /// <c>false</c> y nunca llega a la simulación.
    /// </remarks>
    public static class MessageCodec
    {
        // Parte fija del saludo: tipo, versión, tick, token y huella. Las cuatro cadenas van
        // después, cada una con su longitud uint16 (plan §8: cuenta, contraseña, personaje e
        // invitación). El tamaño total es variable, pero siempre menor que el tope del mensaje.
        private const int HelloFixedSize = 1 + 2 + 2 + 4 + ProtocolConstants.MapHashSize;
        private const int MoveInputSize = 1 + 4 + 1 + 1;
        private const int RejectSize = 1 + 1;
        private const int SnapshotHeaderSize = 1 + 4 + 4 + 2;
        private const int EntityStateSize = 4 + 2 + 2 + 1 + 1 + 1;

        private const int WelcomeSize =
            1 + // tipo
            2 + // versión
            2 + // tick rate
            4 + // entidad
            4 + // tick del servidor
            4 + // token del cliente
            4 + // spawn X
            4 + // spawn Y
            4 + // velocidad
            4 + // semiancho
            4 + // semialto
            ProtocolConstants.MapHashSize;

        // ---------------------------------------------------------------- cliente → servidor

        /// <summary>
        /// Codifica un saludo. La parte fija ocupa 41 bytes y cada cadena añade su longitud uint16
        /// más los bytes UTF-8.
        /// </summary>
        public static byte[] EncodeHello(in HelloMessage message)
        {
            RequireMapHash(message.MapHash);

            string account = message.AccountName ?? string.Empty;
            string password = message.Password ?? string.Empty;
            string character = message.CharacterName ?? string.Empty;
            string invitation = message.InvitationCode ?? string.Empty;

            int accountBytes = Encoding.UTF8.GetByteCount(account);
            int passwordBytes = Encoding.UTF8.GetByteCount(password);
            int characterBytes = Encoding.UTF8.GetByteCount(character);
            int invitationBytes = Encoding.UTF8.GetByteCount(invitation);

            RequireStringLength(accountBytes, ProtocolConstants.MaxAccountNameBytes, nameof(message.AccountName));
            RequireStringLength(passwordBytes, ProtocolConstants.MaxPasswordBytes, nameof(message.Password));
            RequireStringLength(characterBytes, ProtocolConstants.MaxCharacterNameBytes, nameof(message.CharacterName));
            RequireStringLength(invitationBytes, ProtocolConstants.MaxInvitationCodeBytes, nameof(message.InvitationCode));

            int size = HelloFixedSize +
                (2 + accountBytes) +
                (2 + passwordBytes) +
                (2 + characterBytes) +
                (2 + invitationBytes);

            if (size > ProtocolConstants.MaxMessageSize)
            {
                throw new ArgumentException(
                    "El saludo ocupa " + size + " bytes y el tope es " + ProtocolConstants.MaxMessageSize + ".",
                    nameof(message));
            }

            var buffer = new byte[size];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.Hello);
            writer.WriteUInt16(message.ProtocolVersion);
            writer.WriteUInt16(message.TickRate);
            writer.WriteUInt32(message.ClientToken);
            writer.WriteBytes(message.MapHash, 0, ProtocolConstants.MapHashSize);
            writer.WriteString(account, ProtocolConstants.MaxAccountNameBytes);
            writer.WriteString(password, ProtocolConstants.MaxPasswordBytes);
            writer.WriteString(character, ProtocolConstants.MaxCharacterNameBytes);
            writer.WriteString(invitation, ProtocolConstants.MaxInvitationCodeBytes);
            return buffer;
        }

        /// <summary>
        /// Lee un saludo. Devuelve <c>false</c> si el tipo, el tamaño o alguna cadena no encajan;
        /// el servidor decide después si las credenciales son válidas.
        /// </summary>
        public static bool TryReadHello(byte[] buffer, int offset, int length, out HelloMessage message)
        {
            message = default;

            if (!HasHeader(buffer, offset, length, MessageType.Hello) || length < HelloFixedSize)
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                ushort version = reader.ReadUInt16();
                ushort tickRate = reader.ReadUInt16();
                uint token = reader.ReadUInt32();
                var mapHash = new byte[ProtocolConstants.MapHashSize];
                reader.ReadBytes(mapHash, mapHash.Length);
                string account = reader.ReadString(ProtocolConstants.MaxAccountNameBytes);
                string password = reader.ReadString(ProtocolConstants.MaxPasswordBytes);
                string character = reader.ReadString(ProtocolConstants.MaxCharacterNameBytes);
                string invitation = reader.ReadString(ProtocolConstants.MaxInvitationCodeBytes);

                if (reader.Remaining != 0)
                {
                    return false;
                }

                message = new HelloMessage(
                    version,
                    tickRate,
                    mapHash,
                    token,
                    account,
                    password,
                    character,
                    invitation);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        /// <summary>Codifica una intención de movimiento: 7 bytes.</summary>
        public static byte[] EncodeMoveInput(in MoveInputMessage message)
        {
            var buffer = new byte[MoveInputSize];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.MoveInput);
            writer.WriteUInt32(message.Sequence);
            writer.WriteSByte(message.DirectionX);
            writer.WriteSByte(message.DirectionY);
            return buffer;
        }

        /// <summary>Lee una intención de movimiento.</summary>
        public static bool TryReadMoveInput(byte[] buffer, int offset, int length, out MoveInputMessage message)
        {
            message = default;

            if (!HasExactHeader(buffer, offset, length, MessageType.MoveInput, MoveInputSize))
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                uint sequence = reader.ReadUInt32();
                sbyte directionX = reader.ReadSByte();
                sbyte directionY = reader.ReadSByte();

                // La dirección es parte del formato (-1, 0 o 1). Un ángulo arbitrario no es una
                // intención rara: es un mensaje malformado (plan §4, validación al deserializar).
                if (!MovementSimulator.IsValidDirection(directionX, directionY))
                {
                    return false;
                }

                message = new MoveInputMessage(sequence, directionX, directionY);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- servidor → cliente

        /// <summary>Codifica la aceptación del saludo: 69 bytes.</summary>
        public static byte[] EncodeWelcome(in WelcomeMessage message)
        {
            RequireMapHash(message.MapHash);

            var buffer = new byte[WelcomeSize];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.Welcome);
            writer.WriteUInt16(message.ProtocolVersion);
            writer.WriteUInt16(message.TickRate);
            writer.WriteInt32(message.EntityId);
            writer.WriteUInt32(message.ServerTick);
            writer.WriteUInt32(message.ClientToken);
            writer.WriteSingle(message.SpawnX);
            writer.WriteSingle(message.SpawnY);
            writer.WriteSingle(message.Speed);
            writer.WriteSingle(message.HalfWidth);
            writer.WriteSingle(message.HalfHeight);
            writer.WriteBytes(message.MapHash, 0, ProtocolConstants.MapHashSize);
            return buffer;
        }

        /// <summary>Lee la aceptación del saludo.</summary>
        public static bool TryReadWelcome(byte[] buffer, int offset, int length, out WelcomeMessage message)
        {
            message = default;

            if (!HasExactHeader(buffer, offset, length, MessageType.Welcome, WelcomeSize))
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                ushort version = reader.ReadUInt16();
                ushort tickRate = reader.ReadUInt16();
                int entityId = reader.ReadInt32();
                uint serverTick = reader.ReadUInt32();
                uint token = reader.ReadUInt32();
                float spawnX = reader.ReadSingle();
                float spawnY = reader.ReadSingle();
                float speed = reader.ReadSingle();
                float halfWidth = reader.ReadSingle();
                float halfHeight = reader.ReadSingle();
                var mapHash = new byte[ProtocolConstants.MapHashSize];
                reader.ReadBytes(mapHash, mapHash.Length);

                message = new WelcomeMessage(
                    version,
                    tickRate,
                    entityId,
                    serverTick,
                    token,
                    spawnX,
                    spawnY,
                    speed,
                    halfWidth,
                    halfHeight,
                    mapHash);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>Codifica un rechazo: 2 bytes.</summary>
        public static byte[] EncodeReject(in RejectMessage message)
        {
            var buffer = new byte[RejectSize];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.Reject);
            writer.WriteByte((byte)message.Reason);
            return buffer;
        }

        /// <summary>Lee un rechazo.</summary>
        public static bool TryReadReject(byte[] buffer, int offset, int length, out RejectMessage message)
        {
            message = default;

            if (!HasExactHeader(buffer, offset, length, MessageType.Reject, RejectSize))
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                byte rawReason = reader.ReadByte();

                // Un motivo inventado no se puede mostrar ni interpretar: se rechaza el mensaje.
                // El límite es el último valor definido; la prueba
                // Todos_Los_Valores_Definidos_De_Las_Enumeraciones_Viajan avisa si se olvida.
                if (rawReason > (byte)RejectReason.LoggedInElsewhere)
                {
                    return false;
                }

                message = new RejectMessage((RejectReason)rawReason);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>
        /// Codifica un snapshot: cabecera de 11 bytes más 11 por entidad. El número de entidades
        /// debe caber en <see cref="ProtocolConstants.MaxEntitiesPerSnapshot"/>.
        /// </summary>
        public static byte[] EncodeSnapshot(in SnapshotMessage message)
        {
            EntityState[] entities = message.Entities ?? Array.Empty<EntityState>();

            if (entities.Length > ProtocolConstants.MaxEntitiesPerSnapshot)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(message),
                    "Un snapshot no puede llevar más de " + ProtocolConstants.MaxEntitiesPerSnapshot + " entidades.");
            }

            var buffer = new byte[SnapshotHeaderSize + (entities.Length * EntityStateSize)];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.Snapshot);
            writer.WriteUInt32(message.ServerTick);
            writer.WriteUInt32(message.LastProcessedInputSequence);
            writer.WriteUInt16((ushort)entities.Length);

            for (int i = 0; i < entities.Length; i++)
            {
                EntityState state = entities[i];
                writer.WriteInt32(state.Id);
                writer.WriteUInt16(unchecked((ushort)state.X));
                writer.WriteUInt16(unchecked((ushort)state.Y));
                writer.WriteSByte(state.DirectionX);
                writer.WriteSByte(state.DirectionY);
                writer.WriteByte((byte)state.Kind);
            }

            return buffer;
        }

        /// <summary>Lee un snapshot y reserva su lista de entidades.</summary>
        public static bool TryReadSnapshot(byte[] buffer, int offset, int length, out SnapshotMessage message)
        {
            message = default;

            if (buffer is null || offset < 0 || length < SnapshotHeaderSize || length > ProtocolConstants.MaxMessageSize)
            {
                return false;
            }

            if (offset + length > buffer.Length || buffer[offset] != (byte)MessageType.Snapshot)
            {
                return false;
            }

            int payload = length - SnapshotHeaderSize;
            if (payload % EntityStateSize != 0)
            {
                return false;
            }

            int count = payload / EntityStateSize;
            if (count > ProtocolConstants.MaxEntitiesPerSnapshot)
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                uint serverTick = reader.ReadUInt32();
                uint lastSequence = reader.ReadUInt32();
                int declared = reader.ReadUInt16();

                if (declared != count)
                {
                    return false;
                }

                var entities = count == 0 ? Array.Empty<EntityState>() : new EntityState[count];

                for (int i = 0; i < count; i++)
                {
                    int id = reader.ReadInt32();
                    short x = unchecked((short)reader.ReadUInt16());
                    short y = unchecked((short)reader.ReadUInt16());
                    sbyte directionX = reader.ReadSByte();
                    sbyte directionY = reader.ReadSByte();
                    byte rawKind = reader.ReadByte();

                    // Ni el tipo ni la dirección pueden ser valores inventados: si lo son, el
                    // snapshot entero está mal y no se aplica. El límite es el último valor
                    // definido; la prueba de enumeraciones completas avisa si se añade otro.
                    if (rawKind > (byte)EntityKind.Npc ||
                        !MovementSimulator.IsValidDirection(directionX, directionY))
                    {
                        return false;
                    }

                    entities[i] = new EntityState(
                        id, x, y, directionX, directionY, (EntityKind)rawKind);
                }

                message = new SnapshotMessage(serverTick, lastSequence, entities);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- ayudas

        private static bool HasExactHeader(
            byte[] buffer,
            int offset,
            int length,
            MessageType expected,
            int exactSize)
        {
            return length == exactSize && HasHeader(buffer, offset, length, expected);
        }

        private static bool HasHeader(byte[] buffer, int offset, int length, MessageType expected)
        {
            if (buffer is null || offset < 0 || length < 1)
            {
                return false;
            }

            if (length > ProtocolConstants.MaxMessageSize || offset + length > buffer.Length)
            {
                return false;
            }

            return buffer[offset] == (byte)expected;
        }

        private static void RequireMapHash(byte[] mapHash)
        {
            if (mapHash is null || mapHash.Length != ProtocolConstants.MapHashSize)
            {
                throw new ArgumentException(
                    "La huella del mapa debe tener " + ProtocolConstants.MapHashSize + " bytes.",
                    nameof(mapHash));
            }
        }

        private static void RequireStringLength(int byteCount, int maxBytes, string parameterName)
        {
            if (byteCount > maxBytes)
            {
                throw new ArgumentException(
                    "El texto ocupa " + byteCount + " bytes y el tope es " + maxBytes + ".",
                    parameterName);
            }
        }
    }
}
