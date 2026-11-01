#nullable enable
using System;
using System.Collections.Generic;
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
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        // Parte fija del saludo: tipo, versión, tick, token, huella y bloque cifrado. Después va
        // la cuenta con su longitud uint16; la selección de personaje ocurre tras autenticar.
        private const int HelloFixedSize =
            1 + 2 + 2 + 4 + ProtocolConstants.MapHashSize + HelloSecrets.EncryptedSize;

        // Tipo, nonce, longitud del exponente y exponente; el módulo ocupa lo que falte.
        private const int ChallengeHeaderSize = 1 + HelloSecrets.NonceSize + 1;

        // Tipo, secuencia del más nuevo y número de inputs. Cada input añade dos bytes
        // (dirX, dirY), así que el mensaje mide 6 + 2n entre 8 y 14 bytes.
        private const int MoveInputHeaderSize = 1 + 4 + 1;
        private const int AttackSize = 1 + 4;
        private const int PickUpSize = 1 + 4;
        private const int UseItemSize = 1 + 1;
        private const int EquipSize = 1 + 1;
        private const int UnequipSize = 1 + 1 + 1;
        private const int MoveItemSize = 1 + 1 + 1 + 2;
        private const int DestroyItemSize = 1 + 1 + 2;
        private const int AllocatePointsSize = 1 + 4;
        private const int ResetPointsSize = 1;
        private const int RejectSize = 1 + 1;
        private const int ProgressSize = 1 + 1 + 4 + 4 + 4;
        private const int CombatEventHeaderSize = 1 + 1;
        private const int CombatEventEntrySize = 4 + 4 + 2 + 1;
        private const int LootGainedHeaderSize = 1 + 4 + 4 + 1;
        private const int LootGainedEntrySize = 2 + 2 + 1;
        private const int StatsSize = 37;
        private const int ChatHeaderSize = 1 + 4;
        private const int SnapshotHeaderSize = 1 + 4 + 4 + 2;
        private const int ItemCatalogHeaderSize = 1 + 4 + 4 + 4 + 4 + 2;
        // Tamaño anterior (32) + tres flags/niveles y la longitud de weaponType.
        private const int ItemCatalogEntryFixedSize = 32 + 3 + 2;
        private const int EntityStateSize = 4 + 2 + 2 + 1 + 1 + 1 + 1 + 1 + 2;

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
        /// Codifica un saludo. La parte fija ocupa 297 bytes (cabecera más el bloque cifrado) y la
        /// cuenta añade su longitud uint16 más los bytes UTF-8.
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
            int accountBytes = GetStrictUtf8ByteCount(account);

            RequireStringLength(accountBytes, ProtocolConstants.MaxAccountNameBytes, nameof(message.AccountName));

            int size = HelloFixedSize + (2 + accountBytes);

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
                    encryptedSecrets);
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

        /// <summary>Codifica CreateCharacter: nombre UTF-8 y classId.</summary>
        public static byte[] EncodeCreateCharacter(in CreateCharacterMessage message)
        {
            string name = message.Name ?? string.Empty;
            int nameBytes = GetStrictUtf8ByteCount(name);
            RequireStringLength(nameBytes, ProtocolConstants.MaxCharacterNameBytes, nameof(message.Name));
            if (message.ClassId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(message), "classId debe ser distinto de cero.");
            }

            var buffer = new byte[1 + 2 + nameBytes + 1];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.CreateCharacter);
            writer.WriteString(name, ProtocolConstants.MaxCharacterNameBytes);
            writer.WriteByte(message.ClassId);
            return buffer;
        }

        /// <summary>Lee CreateCharacter y valida UTF-8, longitud, clase y tamaño exacto.</summary>
        public static bool TryReadCreateCharacter(
            byte[] buffer, int offset, int length, out CreateCharacterMessage message)
        {
            message = default;
            if (!HasHeader(buffer, offset, length, MessageType.CreateCharacter) || length < 4)
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                string name = reader.ReadString(ProtocolConstants.MaxCharacterNameBytes);
                byte classId = reader.ReadByte();
                if (classId == 0 || reader.Remaining != 0)
                {
                    return false;
                }

                message = new CreateCharacterMessage(name, classId);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>Codifica DeleteCharacter: characterId y nombre de confirmación.</summary>
        public static byte[] EncodeDeleteCharacter(in DeleteCharacterMessage message)
        {
            RequireCharacterId(message.CharacterId, nameof(message));
            string name = message.ConfirmationName ?? string.Empty;
            int nameBytes = GetStrictUtf8ByteCount(name);
            RequireStringLength(nameBytes, ProtocolConstants.MaxCharacterNameBytes, nameof(message.ConfirmationName));

            var buffer = new byte[1 + 4 + 2 + nameBytes];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.DeleteCharacter);
            writer.WriteInt32(message.CharacterId);
            writer.WriteString(name, ProtocolConstants.MaxCharacterNameBytes);
            return buffer;
        }

        /// <summary>Lee DeleteCharacter y valida id, UTF-8, longitud y tamaño exacto.</summary>
        public static bool TryReadDeleteCharacter(
            byte[] buffer, int offset, int length, out DeleteCharacterMessage message)
        {
            message = default;
            if (!HasHeader(buffer, offset, length, MessageType.DeleteCharacter) || length < 7)
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                int characterId = reader.ReadInt32();
                string name = reader.ReadString(ProtocolConstants.MaxCharacterNameBytes);
                if (characterId <= 0 || reader.Remaining != 0)
                {
                    return false;
                }

                message = new DeleteCharacterMessage(characterId, name);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>Codifica SelectCharacter: characterId positivo.</summary>
        public static byte[] EncodeSelectCharacter(in SelectCharacterMessage message)
        {
            RequireCharacterId(message.CharacterId, nameof(message));
            var buffer = new byte[5];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.SelectCharacter);
            writer.WriteInt32(message.CharacterId);
            return buffer;
        }

        /// <summary>Lee SelectCharacter y exige un id positivo y longitud exacta.</summary>
        public static bool TryReadSelectCharacter(
            byte[] buffer, int offset, int length, out SelectCharacterMessage message)
        {
            message = default;
            if (!HasExactHeader(buffer, offset, length, MessageType.SelectCharacter, 5))
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                int characterId = reader.ReadInt32();
                if (characterId <= 0)
                {
                    return false;
                }

                message = new SelectCharacterMessage(characterId);
                return true;
            }
            catch (ProtocolException)
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

        /// <summary>Codifica una intención de recoger: tipo (1) + entityId (4) = 5 bytes.</summary>
        public static byte[] EncodePickUp(in PickUpMessage message)
        {
            var buffer = new byte[PickUpSize];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.PickUp);
            writer.WriteInt32(message.EntityId);
            return buffer;
        }

        /// <summary>Lee una intención de recoger. La longitud debe ser exactamente 5 bytes.</summary>
        public static bool TryReadPickUp(byte[] buffer, int offset, int length, out PickUpMessage message)
        {
            message = default;

            if (!HasExactHeader(buffer, offset, length, MessageType.PickUp, PickUpSize))
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                message = new PickUpMessage(reader.ReadInt32());
                return reader.Remaining == 0;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>Codifica una intención de usar objeto: tipo (1) + slot (1) = 2 bytes.</summary>
        public static byte[] EncodeUseItem(in UseItemMessage message)
        {
            var buffer = new byte[UseItemSize];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.UseItem);
            writer.WriteByte(message.Slot);
            return buffer;
        }

        /// <summary>Lee una intención de usar objeto. La longitud debe ser exactamente 2 bytes.</summary>
        public static bool TryReadUseItem(byte[] buffer, int offset, int length, out UseItemMessage message)
        {
            message = default;

            if (!HasExactHeader(buffer, offset, length, MessageType.UseItem, UseItemSize))
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                message = new UseItemMessage(reader.ReadByte());
                return reader.Remaining == 0;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>Codifica Equip: tipo (1) + hueco (1) = 2 bytes.</summary>
        public static byte[] EncodeEquip(in EquipMessage message)
        {
            RequireInventorySlot(message.Slot, nameof(message));
            var buffer = new byte[EquipSize];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.Equip);
            writer.WriteByte(message.Slot);
            return buffer;
        }

        /// <summary>Lee Equip; exige exactamente dos bytes.</summary>
        public static bool TryReadEquip(byte[] buffer, int offset, int length, out EquipMessage message)
        {
            message = default;
            if (!HasExactHeader(buffer, offset, length, MessageType.Equip, EquipSize))
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                byte slot = reader.ReadByte();
                if (slot >= ProtocolConstants.MaxInventorySlots || reader.Remaining != 0)
                {
                    return false;
                }

                message = new EquipMessage(slot);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>Codifica Unequip: tipo, hueco equipado y destino del inventario.</summary>
        public static byte[] EncodeUnequip(in UnequipMessage message)
        {
            RequireEquipSlot(message.EquipSlot, nameof(message));
            RequireUnequipTarget(message.TargetSlot, nameof(message));
            var buffer = new byte[UnequipSize];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.Unequip);
            writer.WriteByte(message.EquipSlot);
            writer.WriteByte(message.TargetSlot);
            return buffer;
        }

        /// <summary>Lee Unequip; valida hueco equipado y destino.</summary>
        public static bool TryReadUnequip(byte[] buffer, int offset, int length, out UnequipMessage message)
        {
            message = default;
            if (!HasExactHeader(buffer, offset, length, MessageType.Unequip, UnequipSize))
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                byte equipSlot = reader.ReadByte();
                byte targetSlot = reader.ReadByte();
                if (equipSlot >= ProtocolConstants.EquipSlotCount ||
                    !IsValidUnequipTarget(targetSlot) || reader.Remaining != 0)
                {
                    return false;
                }

                message = new UnequipMessage(equipSlot, targetSlot);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>Codifica ChatSend: tipo y cadena UTF-8 con longitud uint16.</summary>
        public static byte[] EncodeChatSend(in ChatSendMessage message)
        {
            string text = message.Text ?? string.Empty;
            int byteCount = Encoding.UTF8.GetByteCount(text);
            RequireStringLength(byteCount, ProtocolConstants.MaxChatBytes, nameof(message.Text));

            var buffer = new byte[1 + 2 + byteCount];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.ChatSend);
            writer.WriteString(text, ProtocolConstants.MaxChatBytes);
            return buffer;
        }

        /// <summary>Lee ChatSend; rechaza texto mayor de 120 bytes, UTF-8 roto y longitudes erróneas.</summary>
        public static bool TryReadChatSend(byte[] buffer, int offset, int length, out ChatSendMessage message)
        {
            message = default;
            if (!HasHeader(buffer, offset, length, MessageType.ChatSend) || length < 3)
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                string text = reader.ReadString(ProtocolConstants.MaxChatBytes);
                if (reader.Remaining != 0)
                {
                    return false;
                }

                message = new ChatSendMessage(text);
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

        /// <summary>Codifica el reparto solicitado: tipo y cuatro valores u8.</summary>
        public static byte[] EncodeAllocatePoints(in AllocatePointsMessage message)
        {
            var buffer = new byte[AllocatePointsSize];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.AllocatePoints);
            writer.WriteByte(message.Str);
            writer.WriteByte(message.Dex);
            writer.WriteByte(message.Int);
            writer.WriteByte(message.Vit);
            return buffer;
        }

        /// <summary>Lee un reparto y exige exactamente cuatro bytes de atributos.</summary>
        public static bool TryReadAllocatePoints(
            byte[] buffer,
            int offset,
            int length,
            out AllocatePointsMessage message)
        {
            message = default;
            if (!HasExactHeader(buffer, offset, length, MessageType.AllocatePoints, AllocatePointsSize))
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                message = new AllocatePointsMessage(
                    reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                return reader.Remaining == 0;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>Codifica el reajuste, que no lleva cuerpo.</summary>
        public static byte[] EncodeResetPoints(in ResetPointsMessage message) =>
            new[] { (byte)MessageType.ResetPoints };

        /// <summary>Lee el reajuste y exige exactamente un byte.</summary>
        public static bool TryReadResetPoints(
            byte[] buffer,
            int offset,
            int length,
            out ResetPointsMessage message)
        {
            message = default;
            if (!HasExactHeader(buffer, offset, length, MessageType.ResetPoints, ResetPointsSize))
            {
                return false;
            }

            message = new ResetPointsMessage();
            return true;
        }

        /// <summary>Codifica MoveItem: tipo, origen, destino y cantidad.</summary>
        public static byte[] EncodeMoveItem(in MoveItemMessage message)
        {
            RequireInventorySlot(message.FromSlot, nameof(message));
            RequireInventorySlot(message.ToSlot, nameof(message));
            var buffer = new byte[MoveItemSize];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.MoveItem);
            writer.WriteByte(message.FromSlot);
            writer.WriteByte(message.ToSlot);
            writer.WriteUInt16(message.Count);
            return buffer;
        }

        /// <summary>Lee MoveItem; comprueba tamaño y que ambos huecos estén en rango.</summary>
        public static bool TryReadMoveItem(byte[] buffer, int offset, int length, out MoveItemMessage message)
        {
            message = default;
            if (!HasExactHeader(buffer, offset, length, MessageType.MoveItem, MoveItemSize))
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                byte fromSlot = reader.ReadByte();
                byte toSlot = reader.ReadByte();
                ushort count = reader.ReadUInt16();
                if (fromSlot >= ProtocolConstants.MaxInventorySlots ||
                    toSlot >= ProtocolConstants.MaxInventorySlots || reader.Remaining != 0)
                {
                    return false;
                }

                message = new MoveItemMessage(fromSlot, toSlot, count);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>Codifica DestroyItem: tipo, hueco y cantidad.</summary>
        public static byte[] EncodeDestroyItem(in DestroyItemMessage message)
        {
            RequireInventorySlot(message.Slot, nameof(message));
            var buffer = new byte[DestroyItemSize];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.DestroyItem);
            writer.WriteByte(message.Slot);
            writer.WriteUInt16(message.Count);
            return buffer;
        }

        /// <summary>Lee DestroyItem; comprueba tamaño exacto y hueco.</summary>
        public static bool TryReadDestroyItem(byte[] buffer, int offset, int length, out DestroyItemMessage message)
        {
            message = default;
            if (!HasExactHeader(buffer, offset, length, MessageType.DestroyItem, DestroyItemSize))
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                byte slot = reader.ReadByte();
                ushort count = reader.ReadUInt16();
                if (slot >= ProtocolConstants.MaxInventorySlots || reader.Remaining != 0)
                {
                    return false;
                }

                message = new DestroyItemMessage(slot, count);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>Codifica personajes y clases disponibles, limitando el paquete a 1400 bytes.</summary>
        public static byte[] EncodeCharacterList(in CharacterListMessage message)
        {
            CharacterListEntry[] characters = message.Characters ?? Array.Empty<CharacterListEntry>();
            CharacterClassEntry[] classes = message.Classes ?? Array.Empty<CharacterClassEntry>();
            RequireCharacterListHeader(message.MaxCharacters, characters.Length, classes.Length, nameof(message));

            int size = 1 + 1 + 1 + 1;
            for (int i = 0; i < characters.Length; i++)
            {
                CharacterListEntry character = characters[i];
                RequireCharacterId(character.CharacterId, nameof(message));
                if (character.ClassId == 0 || character.Level == 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(message), "El personaje lleva classId o nivel cero.");
                }

                int nameBytes = GetStrictUtf8ByteCount(character.Name ?? string.Empty);
                RequireStringLength(nameBytes, ProtocolConstants.MaxCharacterNameBytes, nameof(message));
                size += 8 + nameBytes;
            }

            for (int i = 0; i < classes.Length; i++)
            {
                if (classes[i].ClassId == 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(message), "classId debe ser distinto de cero.");
                }

                int nameBytes = GetStrictUtf8ByteCount(classes[i].Name ?? string.Empty);
                RequireStringLength(nameBytes, ProtocolConstants.MaxCharacterNameBytes, nameof(message));
                size += 3 + nameBytes;
            }

            if (size > ProtocolConstants.MaxMessageSize)
            {
                throw new ArgumentException("CharacterList ocupa " + size + " bytes y supera el tope del protocolo.", nameof(message));
            }

            var buffer = new byte[size];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.CharacterList);
            writer.WriteByte(message.MaxCharacters);
            writer.WriteByte((byte)characters.Length);
            for (int i = 0; i < characters.Length; i++)
            {
                CharacterListEntry character = characters[i];
                writer.WriteInt32(character.CharacterId);
                writer.WriteString(character.Name, ProtocolConstants.MaxCharacterNameBytes);
                writer.WriteByte(character.ClassId);
                writer.WriteByte(character.Level);
            }

            writer.WriteByte((byte)classes.Length);
            for (int i = 0; i < classes.Length; i++)
            {
                writer.WriteByte(classes[i].ClassId);
                writer.WriteString(classes[i].Name, ProtocolConstants.MaxCharacterNameBytes);
            }

            return buffer;
        }

        /// <summary>Lee CharacterList con límites estrictos de cantidades, campos, UTF-8 y longitud.</summary>
        public static bool TryReadCharacterList(
            byte[] buffer, int offset, int length, out CharacterListMessage message)
        {
            message = default;
            if (!HasHeader(buffer, offset, length, MessageType.CharacterList) || length < 4)
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                byte maxCharacters = reader.ReadByte();
                int characterCount = reader.ReadByte();
                if (maxCharacters == 0 || maxCharacters > ProtocolConstants.MaxCharactersPerAccount ||
                    characterCount > ProtocolConstants.MaxCharactersPerAccount)
                {
                    return false;
                }

                var characters = characterCount == 0
                    ? Array.Empty<CharacterListEntry>()
                    : new CharacterListEntry[characterCount];
                for (int i = 0; i < characterCount; i++)
                {
                    int characterId = reader.ReadInt32();
                    string name = reader.ReadString(ProtocolConstants.MaxCharacterNameBytes);
                    byte classId = reader.ReadByte();
                    byte level = reader.ReadByte();
                    if (characterId <= 0 || classId == 0 || level == 0)
                    {
                        return false;
                    }

                    characters[i] = new CharacterListEntry(characterId, name, classId, level);
                }

                int classCount = reader.ReadByte();
                var classes = classCount == 0
                    ? Array.Empty<CharacterClassEntry>()
                    : new CharacterClassEntry[classCount];
                for (int i = 0; i < classCount; i++)
                {
                    byte classId = reader.ReadByte();
                    string name = reader.ReadString(ProtocolConstants.MaxCharacterNameBytes);
                    if (classId == 0)
                    {
                        return false;
                    }

                    classes[i] = new CharacterClassEntry(classId, name);
                }

                if (reader.Remaining != 0)
                {
                    return false;
                }

                message = new CharacterListMessage(maxCharacters, characters, classes);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>Codifica un rechazo de personaje y solo admite sus motivos definidos.</summary>
        public static byte[] EncodeCharacterRejected(in CharacterRejectedMessage message)
        {
            if (!IsCharacterRejectReason(message.Reason))
            {
                throw new ArgumentOutOfRangeException(nameof(message), "Motivo de rechazo de personaje desconocido.");
            }

            return new[] { (byte)MessageType.CharacterRejected, (byte)message.Reason };
        }

        /// <summary>Lee CharacterRejected y rechaza motivos ajenos a las acciones de personaje.</summary>
        public static bool TryReadCharacterRejected(
            byte[] buffer, int offset, int length, out CharacterRejectedMessage message)
        {
            message = default;
            if (!HasExactHeader(buffer, offset, length, MessageType.CharacterRejected, 2))
            {
                return false;
            }

            RejectReason reason = (RejectReason)buffer[offset + 1];
            if (!IsCharacterRejectReason(reason))
            {
                return false;
            }

            message = new CharacterRejectedMessage(reason);
            return true;
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
                if (rawReason > (byte)RejectReason.InternalError)
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
        /// Codifica un snapshot: cabecera de 11 bytes más 15 por entidad. El número de entidades
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
                // La vida, la acción y el rango son parte del formato: un valor fuera de rango rompería al
                // cliente, así que se para en el servidor (donde el fallo es un error de código).
                if (entities[i].HealthPercent > 100 ||
                    entities[i].Action > EntityAction.Dead ||
                    entities[i].Rank > ProtocolConstants.MaxEntityRank ||
                    (entities[i].Kind != EntityKind.Npc && entities[i].Rank != 0))
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
                // Bits 0–3: acción; bits 4–5: rango; bits 6–7: reservados (0).
                writer.WriteByte((byte)((byte)state.Action | (state.Rank << 4)));
                writer.WriteUInt16(state.TypeId);
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
                    byte packedAction = reader.ReadByte();
                    byte rawAction = (byte)(packedAction & 0x0f);
                    byte rank = (byte)((packedAction >> 4) & 0x03);

                    // El códec no conoce el catálogo de PNJ: cualquier tipo es válido aquí y el
                    // cliente dibuja uno desconocido con el aspecto por defecto.
                    ushort typeId = reader.ReadUInt16();

                    // Ni el tipo, la dirección, la vida ni la acción pueden ser valores inventados:
                    // si lo son, el snapshot entero está mal y no se aplica. El límite es el último
                    // valor definido; la prueba de enumeraciones completas avisa si se añade otro.
                    if (rawKind > (byte)EntityKind.Item ||
                        !MovementSimulator.IsValidDirection(directionX, directionY) ||
                        healthPercent > 100 ||
                        rawAction > (byte)EntityAction.Dead ||
                        (packedAction & 0xc0) != 0 ||
                        (rawKind != (byte)EntityKind.Npc && rank != 0))
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
                        (EntityAction)rawAction,
                        typeId,
                        rank);
                }

                message = new SnapshotMessage(serverTick, lastSequence, entities);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>
        /// Codifica un inventario: tipo, número de huecos, cuatro bytes por pila y diez ids equipados.
        /// </summary>
        public static byte[] EncodeInventory(in InventoryMessage message)
        {
            if (message.SlotCount > ProtocolConstants.MaxInventorySlots ||
                message.ItemIds == null ||
                message.Counts == null ||
                message.Equipment == null ||
                message.ItemIds.Length != message.SlotCount ||
                message.Counts.Length != message.SlotCount ||
                message.Equipment.Length != ProtocolConstants.EquipSlotCount)
            {
                throw new ArgumentException("El inventario debe tener entre 0 y 60 huecos coherentes.", nameof(message));
            }

            for (int i = 0; i < message.SlotCount; i++)
            {
                if ((message.ItemIds[i] == 0) != (message.Counts[i] == 0))
                {
                    throw new ArgumentException("El itemId y la cantidad del hueco no son coherentes.", nameof(message));
                }
            }

            if (message.Gold > ProtocolConstants.MaxGold)
            {
                throw new ArgumentException("El oro supera el máximo del protocolo.", nameof(message));
            }

            int size = 1 + 1 + (message.SlotCount * 4) + (ProtocolConstants.EquipSlotCount * 2) + 4;
            var buffer = new byte[size];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.Inventory);
            writer.WriteByte(message.SlotCount);

            for (int i = 0; i < message.SlotCount; i++)
            {
                writer.WriteUInt16(message.ItemIds[i]);
                writer.WriteUInt16(message.Counts[i]);
            }

            for (int i = 0; i < ProtocolConstants.EquipSlotCount; i++)
            {
                writer.WriteUInt16(message.Equipment[i]);
            }

            writer.WriteUInt32(message.Gold);

            return buffer;
        }

        /// <summary>
        /// Lee un inventario. Valida longitud exacta, slotCount ≤ 60 y coherencia itemId/count.
        /// </summary>
        public static bool TryReadInventory(byte[] buffer, int offset, int length, out InventoryMessage message)
        {
            message = default;

            if (buffer is null || offset < 0 || offset > buffer.Length ||
                length < 4 || length > ProtocolConstants.MaxMessageSize || length > buffer.Length - offset)
            {
                return false;
            }

            if (buffer[offset] != (byte)MessageType.Inventory)
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                byte slotCount = reader.ReadByte();

                if (slotCount > 60)
                {
                    return false;
                }

                int expectedSize = 1 + 1 + (slotCount * 4) + (ProtocolConstants.EquipSlotCount * 2) + 4;
                if (length != expectedSize)
                {
                    return false;
                }

                var itemIds = new ushort[slotCount];
                var counts = new ushort[slotCount];

                for (int i = 0; i < slotCount; i++)
                {
                    ushort itemId = reader.ReadUInt16();
                    ushort count = reader.ReadUInt16();

                    // Validar coherencia: itemId 0 con count > 0, o itemId > 0 con count 0
                    if ((itemId == 0 && count > 0) || (itemId > 0 && count == 0))
                    {
                        return false;
                    }

                    itemIds[i] = itemId;
                    counts[i] = count;
                }

                var equipment = new ushort[ProtocolConstants.EquipSlotCount];
                for (int i = 0; i < equipment.Length; i++)
                {
                    equipment[i] = reader.ReadUInt16();
                }

                uint gold = reader.ReadUInt32();
                if (gold > ProtocolConstants.MaxGold)
                {
                    return false;
                }

                if (reader.Remaining != 0)
                {
                    return false;
                }

                message = new InventoryMessage(slotCount, itemIds, counts, equipment, gold);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>
        /// Codifica una parte del catálogo: cabecera fija de 19 bytes y, por objeto, 32 bytes previos
        /// más handedness, nivel, permiso de clase y weaponType UTF-8, además del nombre.
        /// </summary>
        public static byte[][] EncodeItemCatalogChunks(in ItemCatalogMessage message)
        {
            ItemCatalogEntry[] items = message.Items ?? Array.Empty<ItemCatalogEntry>();
            var chunks = new List<byte[]>();
            var currentItems = new List<ItemCatalogEntry>();
            int currentSize = ItemCatalogHeaderSize;

            for (int i = 0; i < items.Length; i++)
            {
                ItemCatalogEntry item = items[i];
                if (!IsValidItemCatalogEntry(item))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(message), "El catálogo contiene un tipo o hueco de equipo inválido.");
                }

                int nameBytes = GetStrictUtf8ByteCount(item.Name ?? string.Empty);
                int weaponTypeBytes = GetStrictUtf8ByteCount(item.WeaponType ?? string.Empty);
                if (nameBytes > 48)
                {
                    throw new ArgumentException(
                        "El nombre del objeto supera los 48 bytes UTF-8.", nameof(message));
                }

                int itemSize = ItemCatalogEntryFixedSize + weaponTypeBytes + nameBytes;
                if (ItemCatalogHeaderSize + itemSize > ProtocolConstants.MaxMessageSize)
                {
                    throw new ArgumentException(
                        "El objeto " + item.Id + " ocupa " + itemSize +
                        " bytes y no cabe solo en un mensaje ItemCatalog.", nameof(message));
                }

                if (currentItems.Count > 0 && currentSize + itemSize > ProtocolConstants.MaxMessageSize)
                {
                    chunks.Add(EncodeItemCatalog(new ItemCatalogMessage(
                        message.PickupRange,
                        message.FistDamage,
                        message.FistRange,
                        message.FistCooldownSeconds,
                        currentItems.ToArray())));
                    currentItems.Clear();
                    currentSize = ItemCatalogHeaderSize;
                }

                currentItems.Add(item);
                currentSize += itemSize;
            }

            if (currentItems.Count > 0 || chunks.Count == 0)
            {
                chunks.Add(EncodeItemCatalog(new ItemCatalogMessage(
                    message.PickupRange,
                    message.FistDamage,
                    message.FistRange,
                    message.FistCooldownSeconds,
                    currentItems.ToArray())));
            }

            return chunks.ToArray();
        }

        /// <summary>Codifica una parte del catálogo y exige que quepa en un único mensaje.</summary>
        public static byte[] EncodeItemCatalog(in ItemCatalogMessage message)
        {
            ItemCatalogEntry[] items = message.Items ?? Array.Empty<ItemCatalogEntry>();

            int size = ItemCatalogHeaderSize;
            for (int i = 0; i < items.Length; i++)
            {
                if (!IsValidItemCatalogEntry(items[i]))
                {
                    throw new ArgumentOutOfRangeException(nameof(message), "El catálogo contiene un tipo o hueco de equipo inválido.");
                }

                int nameBytes = GetStrictUtf8ByteCount(items[i].Name ?? string.Empty);
                int weaponTypeBytes = GetStrictUtf8ByteCount(items[i].WeaponType ?? string.Empty);
                if (nameBytes > 48)
                {
                    throw new ArgumentException("El nombre del objeto supera los 48 bytes UTF-8.", nameof(message));
                }

                size += ItemCatalogEntryFixedSize + weaponTypeBytes + nameBytes;
            }

            if (size > ProtocolConstants.MaxMessageSize)
            {
                throw new ArgumentException(
                    "El catálogo ocupa " + size + " bytes y el tope es " + ProtocolConstants.MaxMessageSize + ".",
                    nameof(message));
            }

            var buffer = new byte[size];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.ItemCatalog);
            writer.WriteSingle(message.PickupRange);
            writer.WriteUInt32(message.FistDamage);
            writer.WriteSingle(message.FistRange);
            writer.WriteSingle(message.FistCooldownSeconds);
            writer.WriteUInt16((ushort)items.Length);

            for (int i = 0; i < items.Length; i++)
            {
                ItemCatalogEntry item = items[i];
                writer.WriteUInt16(item.Id);
                writer.WriteByte((byte)item.Kind);
                writer.WriteUInt16(item.MaxStack);
                writer.WriteUInt32(item.Heal);
                writer.WriteUInt32(item.Damage);
                writer.WriteSingle(item.Range);
                writer.WriteSingle(item.CooldownSeconds);
                writer.WriteByte(item.EquipSlot);
                writer.WriteUInt16(item.Defense);
                writer.WriteUInt16(item.MagicResist);
                writer.WriteByte(item.Str);
                writer.WriteByte(item.Dex);
                writer.WriteByte(item.Int);
                writer.WriteByte(item.Vit);
                writer.WriteByte(item.TwoHanded ? (byte)1 : (byte)0);
                writer.WriteByte(item.RequiredLevel);
                writer.WriteByte(item.UsableByClass ? (byte)1 : (byte)0);
                writer.WriteString(item.WeaponType, 16);
                writer.WriteString(item.Name, 48);
            }

            return buffer;
        }

        /// <summary>Lee el catálogo validando tipo, cantidad, tamaño y longitud exacta.</summary>
        public static bool TryReadItemCatalog(
            byte[] buffer,
            int offset,
            int length,
            out ItemCatalogMessage message)
        {
            message = default;
            if (!HasHeader(buffer, offset, length, MessageType.ItemCatalog) || length < ItemCatalogHeaderSize)
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                float pickupRange = reader.ReadSingle();
                uint fistDamage = reader.ReadUInt32();
                float fistRange = reader.ReadSingle();
                float fistCooldownSeconds = reader.ReadSingle();
                int count = reader.ReadUInt16();
                var items = count == 0 ? Array.Empty<ItemCatalogEntry>() : new ItemCatalogEntry[count];
                for (int i = 0; i < count; i++)
                {
                    ushort id = reader.ReadUInt16();
                    byte rawKind = reader.ReadByte();
                    if (rawKind > (byte)ItemKind.Armor)
                    {
                        return false;
                    }

                    ushort maxStack = reader.ReadUInt16();
                    uint heal = reader.ReadUInt32();
                    uint damage = reader.ReadUInt32();
                    float range = reader.ReadSingle();
                    float cooldownSeconds = reader.ReadSingle();
                    byte equipSlot = reader.ReadByte();
                    ushort defense = reader.ReadUInt16();
                    ushort magicResist = reader.ReadUInt16();
                    byte str = reader.ReadByte();
                    byte dex = reader.ReadByte();
                    byte intelligence = reader.ReadByte();
                    byte vit = reader.ReadByte();
                    byte handedness = reader.ReadByte();
                    byte requiredLevel = reader.ReadByte();
                    byte usableByClass = reader.ReadByte();
                    if (handedness > 1 || usableByClass > 1)
                    {
                        return false;
                    }

                    string weaponType = reader.ReadString(16);
                    string name = reader.ReadString(48);
                    var entry = new ItemCatalogEntry(
                        id, (ItemKind)rawKind, maxStack, heal, damage, range, cooldownSeconds, name,
                        equipSlot, defense, magicResist, str, dex, intelligence, vit,
                        weaponType, handedness == 1, requiredLevel, usableByClass == 1);
                    if (!IsValidItemCatalogEntry(entry))
                    {
                        return false;
                    }

                    items[i] = entry;
                }

                if (reader.Remaining != 0)
                {
                    return false;
                }

                message = new ItemCatalogMessage(
                    pickupRange, fistDamage, fistRange, fistCooldownSeconds, items);
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

        /// <summary>Codifica eventos de combate: cabecera de 2 bytes y 11 por entrada.</summary>
        public static byte[] EncodeCombatEvent(in CombatEventMessage message)
        {
            CombatEventEntry[] entries = message.Entries ?? Array.Empty<CombatEventEntry>();
            if (entries.Length < 1 || entries.Length > ProtocolConstants.MaxCombatEventsPerMessage)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(message), "CombatEvent debe contener entre 1 y " +
                    ProtocolConstants.MaxCombatEventsPerMessage + " entradas.");
            }

            for (int i = 0; i < entries.Length; i++)
            {
                if (((byte)entries[i].Flags & ~7) != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(message), "CombatEvent contiene banderas desconocidas.");
                }
            }

            var buffer = new byte[CombatEventHeaderSize + (CombatEventEntrySize * entries.Length)];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.CombatEvent);
            writer.WriteByte((byte)entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                writer.WriteInt32(entries[i].AttackerId);
                writer.WriteInt32(entries[i].TargetId);
                writer.WriteUInt16(entries[i].Damage);
                writer.WriteByte((byte)entries[i].Flags);
            }

            return buffer;
        }

        /// <summary>Lee CombatEvent y valida el número de entradas, el tamaño y sus banderas.</summary>
        public static bool TryReadCombatEvent(
            byte[] buffer, int offset, int length, out CombatEventMessage message)
        {
            message = default;
            if (!HasHeader(buffer, offset, length, MessageType.CombatEvent) || length < CombatEventHeaderSize)
            {
                return false;
            }

            int count = buffer[offset + 1];
            if (count < 1 || count > ProtocolConstants.MaxCombatEventsPerMessage ||
                length != CombatEventHeaderSize + (CombatEventEntrySize * count))
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 2, length - 2);
                var entries = new CombatEventEntry[count];
                for (int i = 0; i < count; i++)
                {
                    int attackerId = reader.ReadInt32();
                    int targetId = reader.ReadInt32();
                    ushort damage = reader.ReadUInt16();
                    byte flags = reader.ReadByte();
                    if ((flags & ~7) != 0)
                    {
                        return false;
                    }

                    entries[i] = new CombatEventEntry(
                        attackerId, targetId, damage, (CombatEventFlags)flags);
                }

                if (reader.Remaining != 0)
                {
                    return false;
                }

                message = new CombatEventMessage(entries);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>Codifica el botín obtenido: 10 bytes fijos y 5 por objeto.</summary>
        public static byte[] EncodeLootGained(in LootGainedMessage message)
        {
            LootItemEntry[] items = message.Items ?? Array.Empty<LootItemEntry>();
            if (items.Length > ProtocolConstants.MaxLootItemsPerMessage ||
                message.Gold > ProtocolConstants.MaxGold)
            {
                throw new ArgumentOutOfRangeException(nameof(message), "LootGained excede sus límites.");
            }

            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].ItemId == 0 || items[i].Count == 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(message), "LootGained contiene un objeto inválido.");
                }
            }

            var buffer = new byte[LootGainedHeaderSize + (LootGainedEntrySize * items.Length)];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.LootGained);
            writer.WriteUInt32(message.Experience);
            writer.WriteUInt32(message.Gold);
            writer.WriteByte((byte)items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                writer.WriteUInt16(items[i].ItemId);
                writer.WriteUInt16(items[i].Count);
                writer.WriteByte(items[i].OnGround ? (byte)1 : (byte)0);
            }

            return buffer;
        }

        /// <summary>Lee LootGained y valida cantidad, longitud, oro, objetos y banderas.</summary>
        public static bool TryReadLootGained(
            byte[] buffer, int offset, int length, out LootGainedMessage message)
        {
            message = default;
            if (!HasHeader(buffer, offset, length, MessageType.LootGained) || length < LootGainedHeaderSize)
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                uint experience = reader.ReadUInt32();
                uint gold = reader.ReadUInt32();
                int count = reader.ReadByte();
                if (count > ProtocolConstants.MaxLootItemsPerMessage ||
                    length != LootGainedHeaderSize + (LootGainedEntrySize * count) ||
                    gold > ProtocolConstants.MaxGold)
                {
                    return false;
                }

                var items = new LootItemEntry[count];
                for (int i = 0; i < count; i++)
                {
                    ushort itemId = reader.ReadUInt16();
                    ushort itemCount = reader.ReadUInt16();
                    byte flags = reader.ReadByte();
                    if (itemId == 0 || itemCount == 0 || (flags & ~1) != 0)
                    {
                        return false;
                    }

                    items[i] = new LootItemEntry(itemId, itemCount, (flags & 1) != 0);
                }

                if (reader.Remaining != 0)
                {
                    return false;
                }

                message = new LootGainedMessage(experience, gold, items);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>Codifica el progreso: tipo, nivel y tres enteros uint32 (14 bytes).</summary>
        public static byte[] EncodeProgress(in ProgressMessage message)
        {
            if (message.Level < 1 || message.MaxHealth == 0 ||
                (message.ExperienceToNext == 0 && message.Experience != 0) ||
                (message.ExperienceToNext != 0 && message.Experience >= message.ExperienceToNext))
            {
                throw new ArgumentOutOfRangeException(nameof(message), "El progreso contiene valores incoherentes.");
            }

            var buffer = new byte[ProgressSize];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.Progress);
            writer.WriteByte(message.Level);
            writer.WriteUInt32(message.Experience);
            writer.WriteUInt32(message.ExperienceToNext);
            writer.WriteUInt32(message.MaxHealth);
            return buffer;
        }

        /// <summary>Lee Progress y exige su longitud exacta.</summary>
        public static bool TryReadProgress(byte[] buffer, int offset, int length, out ProgressMessage message)
        {
            message = default;
            if (!HasExactHeader(buffer, offset, length, MessageType.Progress, ProgressSize))
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                byte level = reader.ReadByte();
                uint experience = reader.ReadUInt32();
                uint experienceToNext = reader.ReadUInt32();
                uint maxHealth = reader.ReadUInt32();
                if (level < 1 || maxHealth == 0 ||
                    (experienceToNext == 0 && experience != 0) ||
                    (experienceToNext != 0 && experience >= experienceToNext) ||
                    reader.Remaining != 0)
                {
                    return false;
                }

                message = new ProgressMessage(level, experience, experienceToNext, maxHealth);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>Codifica Stats en sus 37 bytes fijos.</summary>
        public static byte[] EncodeStats(in StatsMessage message)
        {
            if (message.ClassId == 0 || message.Level == 0 || message.MaxHealth == 0 ||
                message.DamageType > 1 || message.FreeReset > 1 || message.Resource > message.MaxResource)
            {
                throw new ArgumentOutOfRangeException(nameof(message), "Stats contiene un campo fuera de rango.");
            }

            var buffer = new byte[StatsSize];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.Stats);
            writer.WriteByte(message.ClassId);
            writer.WriteByte(message.Level);
            writer.WriteUInt16(message.Str);
            writer.WriteUInt16(message.Dex);
            writer.WriteUInt16(message.Int);
            writer.WriteUInt16(message.Vit);
            writer.WriteUInt16(message.AllocStr);
            writer.WriteUInt16(message.AllocDex);
            writer.WriteUInt16(message.AllocInt);
            writer.WriteUInt16(message.AllocVit);
            writer.WriteUInt16(message.FreePoints);
            writer.WriteUInt32(message.MaxHealth);
            writer.WriteUInt16(message.AttackDamage);
            writer.WriteByte(message.DamageType);
            writer.WriteUInt16(message.Defense);
            writer.WriteUInt16(message.MagicResist);
            writer.WriteUInt16(message.Resource);
            writer.WriteUInt16(message.MaxResource);
            writer.WriteByte(message.FreeReset);
            return buffer;
        }

        /// <summary>Lee Stats y valida la longitud, el tipo de daño y los indicadores de rango.</summary>
        public static bool TryReadStats(byte[] buffer, int offset, int length, out StatsMessage message)
        {
            message = default;
            if (!HasExactHeader(buffer, offset, length, MessageType.Stats, StatsSize))
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                byte classId = reader.ReadByte();
                byte level = reader.ReadByte();
                ushort str = reader.ReadUInt16();
                ushort dex = reader.ReadUInt16();
                ushort intelligence = reader.ReadUInt16();
                ushort vit = reader.ReadUInt16();
                ushort allocStr = reader.ReadUInt16();
                ushort allocDex = reader.ReadUInt16();
                ushort allocInt = reader.ReadUInt16();
                ushort allocVit = reader.ReadUInt16();
                ushort freePoints = reader.ReadUInt16();
                uint maxHealth = reader.ReadUInt32();
                ushort attackDamage = reader.ReadUInt16();
                byte damageType = reader.ReadByte();
                ushort defense = reader.ReadUInt16();
                ushort magicResist = reader.ReadUInt16();
                ushort resource = reader.ReadUInt16();
                ushort maxResource = reader.ReadUInt16();
                byte freeReset = reader.ReadByte();

                if (classId == 0 || level == 0 || maxHealth == 0 || damageType > 1 || freeReset > 1 ||
                    resource > maxResource || reader.Remaining != 0)
                {
                    return false;
                }

                message = new StatsMessage(
                    classId,
                    level,
                    str,
                    dex,
                    intelligence,
                    vit,
                    allocStr,
                    allocDex,
                    allocInt,
                    allocVit,
                    freePoints,
                    maxHealth,
                    attackDamage,
                    damageType,
                    defense,
                    magicResist,
                    resource,
                    maxResource,
                    freeReset);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>
        /// Codifica Chat: tipo, id de entidad y nombre/texto con sus longitudes UTF-8 uint16.
        /// </summary>
        public static byte[] EncodeChat(in ChatMessage message)
        {
            string name = message.SenderName ?? string.Empty;
            string text = message.Text ?? string.Empty;
            RequireStringLength(
                Encoding.UTF8.GetByteCount(name),
                ProtocolConstants.MaxChatNameBytes,
                nameof(message.SenderName));
            RequireStringLength(
                Encoding.UTF8.GetByteCount(text),
                ProtocolConstants.MaxChatBytes,
                nameof(message.Text));

            int size = ChatHeaderSize + 2 + Encoding.UTF8.GetByteCount(name) + 2 + Encoding.UTF8.GetByteCount(text);
            if (size > ProtocolConstants.MaxMessageSize)
            {
                throw new ArgumentException("El mensaje de chat supera el tope del protocolo.", nameof(message));
            }

            var buffer = new byte[size];
            var writer = new WireWriter(buffer);
            writer.WriteByte((byte)MessageType.Chat);
            writer.WriteInt32(message.SenderEntityId);
            writer.WriteString(name, ProtocolConstants.MaxChatNameBytes);
            writer.WriteString(text, ProtocolConstants.MaxChatBytes);
            return buffer;
        }

        /// <summary>Lee Chat y valida longitudes, UTF-8 y tamaño exacto.</summary>
        public static bool TryReadChat(byte[] buffer, int offset, int length, out ChatMessage message)
        {
            message = default;
            if (!HasHeader(buffer, offset, length, MessageType.Chat) || length < ChatHeaderSize + 4)
            {
                return false;
            }

            try
            {
                var reader = new WireReader(buffer, offset + 1, length - 1);
                int senderEntityId = reader.ReadInt32();
                string senderName = reader.ReadString(ProtocolConstants.MaxChatNameBytes);
                string text = reader.ReadString(ProtocolConstants.MaxChatBytes);
                if (reader.Remaining != 0)
                {
                    return false;
                }

                message = new ChatMessage(senderEntityId, senderName, text);
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

        private static int GetStrictUtf8ByteCount(string value) => StrictUtf8.GetByteCount(value);

        private static void RequireCharacterId(int characterId, string parameterName)
        {
            if (characterId <= 0)
            {
                throw new ArgumentOutOfRangeException(parameterName, "characterId debe ser positivo.");
            }
        }

        private static void RequireCharacterListHeader(
            byte maxCharacters, int characterCount, int classCount, string parameterName)
        {
            if (maxCharacters == 0 || maxCharacters > ProtocolConstants.MaxCharactersPerAccount)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName, "El máximo de personajes debe estar entre 1 y " +
                    ProtocolConstants.MaxCharactersPerAccount + ".");
            }

            if (characterCount > ProtocolConstants.MaxCharactersPerAccount)
            {
                throw new ArgumentException("CharacterList supera el máximo de personajes permitido.", parameterName);
            }

            if (classCount > byte.MaxValue)
            {
                throw new ArgumentException("CharacterList no puede llevar más de 255 clases.", parameterName);
            }
        }

        private static bool IsCharacterRejectReason(RejectReason reason) =>
            reason == RejectReason.NameTaken ||
            reason == RejectReason.NameInvalid ||
            reason == RejectReason.CharacterLimit ||
            reason == RejectReason.CharacterNotFound ||
            reason == RejectReason.ClassUnknown ||
            reason == RejectReason.ConfirmMismatch ||
            reason == RejectReason.InternalError;

        private static void RequireInventorySlot(byte slot, string parameterName)
        {
            if (slot >= ProtocolConstants.MaxInventorySlots)
            {
                throw new ArgumentOutOfRangeException(parameterName, "El hueco de inventario debe ser menor que 60.");
            }
        }

        private static void RequireEquipSlot(byte slot, string parameterName)
        {
            if (slot >= ProtocolConstants.EquipSlotCount)
            {
                throw new ArgumentOutOfRangeException(parameterName, "El hueco de equipo debe ser menor que 10.");
            }
        }

        private static void RequireUnequipTarget(byte slot, string parameterName)
        {
            if (!IsValidUnequipTarget(slot))
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "El destino debe ser un hueco de inventario menor que 60 o 255 (primer hueco libre).");
            }
        }

        private static bool IsValidUnequipTarget(byte slot) =>
            slot == ProtocolConstants.FirstFreeInventorySlot || slot < ProtocolConstants.MaxInventorySlots;

        private static bool IsValidItemCatalogEntry(ItemCatalogEntry item)
        {
            if (item.Kind > ItemKind.Armor)
            {
                return false;
            }

            string weaponType = item.WeaponType ?? string.Empty;
            int weaponTypeBytes;
            try
            {
                weaponTypeBytes = GetStrictUtf8ByteCount(weaponType);
            }
            catch (EncoderFallbackException)
            {
                return false;
            }

            if (weaponTypeBytes > 16 || item.RequiredLevel == 0 ||
                (weaponType.Length > 0 && !string.Equals(weaponType, weaponType.Trim(), StringComparison.Ordinal)))
            {
                return false;
            }

            return item.Kind switch
            {
                ItemKind.Weapon => item.EquipSlot == (byte)Avelora.Shared.EquipSlot.Weapon &&
                    weaponTypeBytes > 0,
                ItemKind.Armor => item.EquipSlot > (byte)Avelora.Shared.EquipSlot.Weapon &&
                    item.EquipSlot < ProtocolConstants.EquipSlotCount && weaponTypeBytes == 0 &&
                    !item.TwoHanded && item.UsableByClass,
                _ => item.EquipSlot == byte.MaxValue && weaponTypeBytes == 0 &&
                    !item.TwoHanded && item.UsableByClass,
            };
        }
    }
}
