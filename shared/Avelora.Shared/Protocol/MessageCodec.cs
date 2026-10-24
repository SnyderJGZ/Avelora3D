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
        // Parte fija del saludo: tipo, versión, tick, token, huella y bloque cifrado. Después van
        // la cuenta y el personaje, cada una con su longitud uint16. El tamaño total es variable,
        // pero siempre menor que el tope del mensaje.
        private const int HelloFixedSize =
            1 + 2 + 2 + 4 + ProtocolConstants.MapHashSize + HelloSecrets.EncryptedSize;

        // Tipo, nonce, longitud del exponente y exponente; el módulo ocupa lo que falte.
        private const int ChallengeHeaderSize = 1 + HelloSecrets.NonceSize + 1;

        // Tipo, secuencia del más nuevo y número de inputs. Cada input añade dos bytes
        // (dirX, dirY), así que el mensaje mide 6 + 2n entre 8 y 14 bytes.
        private const int MoveInputHeaderSize = 1 + 4 + 1;
        private const int AttackSize = 1 + 4;
        private const int RejectSize = 1 + 1;
        private const int SnapshotHeaderSize = 1 + 4 + 4 + 2;
        private const int EntityStateSize = 4 + 2 + 2 + 1 + 1 + 1 + 1 + 1;

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
        /// Codifica un saludo. La parte fija ocupa 297 bytes (cabecera más el bloque cifrado) y cada
        /// cadena añade su longitud uint16 más los bytes UTF-8.
        /// </summary>
        public static byte[] EncodeHello(in HelloMessage message)
        {
            RequireMapHash(message.MapHash);

            if (message.EncryptedSecrets is null ||
                message.EncryptedSecrets.Length != HelloSecrets.EncryptedSize)
            {
                throw new ArgumentException(
                    "El bloque cifrado del saludo debe tener " + HelloSecrets.EncryptedSize + " bytes.",
                    nameof(message));
            }

            string account = message.AccountName ?? string.Empty;
            string character = message.CharacterName ?? string.Empty;

            int accountBytes = Encoding.UTF8.GetByteCount(account);
            int characterBytes = Encoding.UTF8.GetByteCount(character);

            RequireStringLength(accountBytes, ProtocolConstants.MaxAccountNameBytes, nameof(message.AccountName));
            RequireStringLength(characterBytes, ProtocolConstants.MaxCharacterNameBytes, nameof(message.CharacterName));

            int size = HelloFixedSize +
                (2 + accountBytes) +
                (2 + characterBytes);

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
            writer.WriteBytes(message.EncryptedSecrets, 0, HelloSecrets.EncryptedSize);
            writer.WriteString(account, ProtocolConstants.MaxAccountNameBytes);
            writer.WriteString(character, ProtocolConstants.MaxCharacterNameBytes);
            return buffer;
        }

        /// <summary>
        /// Lee un saludo. Devuelve <c>false</c> si el tipo, el tamaño o alguna cadena no encajan;
        /// el servidor decide después si las credenciales cifradas son válidas.
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
                var encryptedSecrets = new byte[HelloSecrets.EncryptedSize];
                reader.ReadBytes(encryptedSecrets, encryptedSecrets.Length);
                string account = reader.ReadString(ProtocolConstants.MaxAccountNameBytes);
                string character = reader.ReadString(ProtocolConstants.MaxCharacterNameBytes);

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
                    encryptedSecrets,
                    character);
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

        /// <summary>
        /// Codifica el desafío: tipo (1) + nonce (16) + longitud del exponente (1) + exponente
        /// (1..4) + módulo (256).
        /// </summary>
        public static byte[] EncodeChallenge(in ChallengeMessage message)
        {
            if (message.Nonce is null || message.Nonce.Length != HelloSecrets.NonceSize)
            {
                throw new ArgumentException("El nonce del desafío debe tener 16 bytes.", nameof(message));
            }

            if (message.Modulus is null || message.Modulus.Length != HelloSecrets.ModulusSize)
            {
                throw new ArgumentException("El módulo del desafío debe tener 256 bytes.", nameof(message));
            }

            if (message.Exponent is null || message.Exponent.Length < 1 || message.Exponent.Length > 4)
            {
                throw new ArgumentException("El exponente del desafío debe ocupar de 1 a 4 bytes.", nameof(message));
            }

            var buffer = new byte[ChallengeHeaderSize + message.Exponent.Length + HelloSecrets.ModulusSize];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.Challenge);
            writer.WriteBytes(message.Nonce, 0, HelloSecrets.NonceSize);
            writer.WriteByte((byte)message.Exponent.Length);
            writer.WriteBytes(message.Exponent, 0, message.Exponent.Length);
            writer.WriteBytes(message.Modulus, 0, HelloSecrets.ModulusSize);
            return buffer;
        }

        /// <summary>
        /// Lee un desafío. Rechaza longitudes que no encajan, un exponente de 0 a 4 bytes o un
        /// exponente todo a cero.
        /// </summary>
        public static bool TryReadChallenge(byte[] buffer, int offset, int length, out ChallengeMessage message)
        {
            message = default;

            if (!HasHeader(buffer, offset, length, MessageType.Challenge) || length < ChallengeHeaderSize + 1 + HelloSecrets.ModulusSize)
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                var nonce = new byte[HelloSecrets.NonceSize];
                reader.ReadBytes(nonce, nonce.Length);
                int exponentLength = reader.ReadByte();

                if (exponentLength < 1 ||
                    exponentLength > 4 ||
                    length != ChallengeHeaderSize + exponentLength + HelloSecrets.ModulusSize)
                {
                    return false;
                }

                var exponent = new byte[exponentLength];
                reader.ReadBytes(exponent, exponent.Length);

                if (IsAllZero(exponent))
                {
                    return false;
                }

                var modulus = new byte[HelloSecrets.ModulusSize];
                reader.ReadBytes(modulus, modulus.Length);

                if (reader.Remaining != 0 || IsAllZero(modulus))
                {
                    return false;
                }

                message = new ChallengeMessage(nonce, modulus, exponent);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>
        /// Codifica una intención de movimiento con sus copias redundantes: 6 + 2n bytes.
        /// </summary>
        /// <remarks>
        /// No se valida la dirección al codificar: el modo <c>speed</c> de los bots debe poder
        /// mandar un mensaje inválido para comprobar que el servidor lo rechaza. La longitud y la
        /// secuencia sí se comprueban porque determinan el tamaño del búfer.
        /// </remarks>
        public static byte[] EncodeMoveInput(in MoveInputMessage message)
        {
            int count = message.Count;

            if (count < 1 || count > ProtocolConstants.MaxInputsPerMessage)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(message),
                    count,
                    "El mensaje debe llevar entre 1 y " + ProtocolConstants.MaxInputsPerMessage + " inputs.");
            }

            if (message.Sequence < (uint)count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(message),
                    "La secuencia más nueva (" + message.Sequence + ") no puede ser menor que el número de inputs (" + count + ").");
            }

            var buffer = new byte[MoveInputHeaderSize + (count * 2)];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.MoveInput);
            writer.WriteUInt32(message.Sequence);
            writer.WriteByte((byte)count);

            for (int i = 0; i < count; i++)
            {
                message.TryGetDirection(i, out sbyte directionX, out sbyte directionY);
                writer.WriteSByte(directionX);
                writer.WriteSByte(directionY);
            }

            return buffer;
        }

        /// <summary>
        /// Lee una intención de movimiento. Rechaza <c>n</c> fuera de 1..4, una longitud distinta de
        /// <c>6 + 2n</c>, una dirección inválida y una secuencia más nueva menor que <c>n</c> (la
        /// aritmética <c>sequence - i</c> no puede llegar a 0).
        /// </summary>
        public static bool TryReadMoveInput(byte[] buffer, int offset, int length, out MoveInputMessage message)
        {
            message = default;

            if (buffer is null ||
                offset < 0 ||
                length < MoveInputHeaderSize ||
                length > ProtocolConstants.MaxMessageSize ||
                offset + length > buffer.Length ||
                buffer[offset] != (byte)MessageType.MoveInput)
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                uint sequence = reader.ReadUInt32();
                int count = reader.ReadByte();

                if (count < 1 ||
                    count > ProtocolConstants.MaxInputsPerMessage ||
                    length != MoveInputHeaderSize + (count * 2) ||
                    sequence < (uint)count)
                {
                    return false;
                }

                sbyte dir0X = 0;
                sbyte dir0Y = 0;
                sbyte dir1X = 0;
                sbyte dir1Y = 0;
                sbyte dir2X = 0;
                sbyte dir2Y = 0;
                sbyte dir3X = 0;
                sbyte dir3Y = 0;

                for (int i = 0; i < count; i++)
                {
                    sbyte directionX = reader.ReadSByte();
                    sbyte directionY = reader.ReadSByte();

                    // La dirección es parte del formato (-1, 0 o 1). Un ángulo arbitrario no es una
                    // intención rara: es un mensaje malformado (plan §4, validación al deserializar).
                    if (!MovementSimulator.IsValidDirection(directionX, directionY))
                    {
                        return false;
                    }

                    switch (i)
                    {
                        case 0:
                            dir0X = directionX;
                            dir0Y = directionY;
                            break;
                        case 1:
                            dir1X = directionX;
                            dir1Y = directionY;
                            break;
                        case 2:
                            dir2X = directionX;
                            dir2Y = directionY;
                            break;
                        default:
                            dir3X = directionX;
                            dir3Y = directionY;
                            break;
                    }
                }

                message = new MoveInputMessage(
                    sequence,
                    count,
                    dir0X,
                    dir0Y,
                    dir1X,
                    dir1Y,
                    dir2X,
                    dir2Y,
                    dir3X,
                    dir3Y);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>Codifica una intención de ataque: tipo (1) + objetivo (4) = 5 bytes.</summary>
        public static byte[] EncodeAttack(in AttackMessage message)
        {
            var buffer = new byte[AttackSize];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.Attack);
            writer.WriteInt32(message.TargetId);
            return buffer;
        }

        /// <summary>Lee una intención de ataque. La longitud debe ser exactamente 5 bytes.</summary>
        public static bool TryReadAttack(byte[] buffer, int offset, int length, out AttackMessage message)
        {
            message = default;

            if (!HasExactHeader(buffer, offset, length, MessageType.Attack, AttackSize))
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                message = new AttackMessage(reader.ReadInt32());
                return reader.Remaining == 0;
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
                if (rawReason > (byte)RejectReason.CharacterLimit)
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
        /// Codifica un snapshot: cabecera de 11 bytes más 13 por entidad. El número de entidades
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

            for (int i = 0; i < entities.Length; i++)
            {
                // La vida y la acción son parte del formato: un valor fuera de rango rompería al
                // cliente, así que se para en el servidor (donde el fallo es un error de código).
                if (entities[i].HealthPercent > 100 ||
                    entities[i].Action > EntityAction.Dead)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(message),
                        "La entidad " + entities[i].Id + " lleva vida o acción fuera de rango.");
                }
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
                writer.WriteByte(state.HealthPercent);
                writer.WriteByte((byte)state.Action);
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
                    byte healthPercent = reader.ReadByte();
                    byte rawAction = reader.ReadByte();

                    // Ni el tipo, la dirección, la vida ni la acción pueden ser valores inventados:
                    // si lo son, el snapshot entero está mal y no se aplica. El límite es el último
                    // valor definido; la prueba de enumeraciones completas avisa si se añade otro.
                    if (rawKind > (byte)EntityKind.Npc ||
                        !MovementSimulator.IsValidDirection(directionX, directionY) ||
                        healthPercent > 100 ||
                        rawAction > (byte)EntityAction.Dead)
                    {
                        return false;
                    }

                    entities[i] = new EntityState(
                        id,
                        x,
                        y,
                        directionX,
                        directionY,
                        (EntityKind)rawKind,
                        healthPercent,
                        (EntityAction)rawAction);
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

        private static bool IsAllZero(byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != 0)
                {
                    return false;
                }
            }

            return true;
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
