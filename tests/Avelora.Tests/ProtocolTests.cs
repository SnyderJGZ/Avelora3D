using System;
using Avelora.Shared;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// Pruebas del códec binario: ida y vuelta de cada mensaje y rechazo de lo que llega mal.
    /// </summary>
    /// <remarks>
    /// El protocolo es la frontera más expuesta del servidor: estas pruebas fijan el formato y
    /// comprueban que un paquete hostil no puede colarse hasta la simulación.
    /// </remarks>
    public class ProtocolTests
    {
        private static byte[] NewHash(byte seed)
        {
            var hash = new byte[ProtocolConstants.MapHashSize];

            for (int i = 0; i < hash.Length; i++)
            {
                hash[i] = (byte)(seed + i);
            }

            return hash;
        }

        private static byte[] NewEncrypted(byte seed)
        {
            var encrypted = new byte[HelloSecrets.EncryptedSize];

            for (int i = 0; i < encrypted.Length; i++)
            {
                encrypted[i] = (byte)(seed + i);
            }

            return encrypted;
        }

        [Fact]
        public void El_Saludo_Va_Y_Vuelve_Igual()
        {
            var original = new HelloMessage(
                ProtocolConstants.Version,
                20,
                NewHash(7),
                123456u,
                "cuenta",
                NewEncrypted(3));

            byte[] bytes = MessageCodec.EncodeHello(original);

            Assert.True(MessageCodec.TryReadHello(bytes, 0, bytes.Length, out HelloMessage decoded));

            // Cabecera fija (297: tipo, versión, tick, token, huella y bloque cifrado) más la cuenta.
            Assert.Equal(297 + 2 + 6, bytes.Length);
            Assert.Equal(ProtocolConstants.Version, original.ProtocolVersion);
            Assert.Equal(original.ProtocolVersion, decoded.ProtocolVersion);
            Assert.Equal(original.TickRate, decoded.TickRate);
            Assert.Equal(original.ClientToken, decoded.ClientToken);
            Assert.Equal(original.MapHash, decoded.MapHash);
            Assert.Equal(original.AccountName, decoded.AccountName);
            Assert.Equal(original.EncryptedSecrets, decoded.EncryptedSecrets);
        }

        [Fact]
        public void Un_Saludo_Sin_El_Bloque_Cifrado_Exacto_Se_Rechaza()
        {
            Assert.Throws<ArgumentException>(() => MessageCodec.EncodeHello(new HelloMessage(
                ProtocolConstants.Version,
                20,
                NewHash(0),
                1u,
                "dev",
                new byte[HelloSecrets.EncryptedSize - 1])));

            // Al leer, un saludo truncado en el bloque cifrado tampoco pasa.
            byte[] bytes = MessageCodec.EncodeHello(new HelloMessage(ProtocolConstants.Version, 20, NewHash(0), 1u));
            Assert.False(MessageCodec.TryReadHello(bytes, 0, bytes.Length - 1, out _));
        }

        [Fact]
        public void Un_Saludo_Con_Una_Cadena_Demasiado_Larga_No_Se_Puede_Codificar()
        {
            var hello = new HelloMessage(
                ProtocolConstants.Version,
                20,
                NewHash(0),
                1u,
                new string('a', ProtocolConstants.MaxAccountNameBytes + 1),
                NewEncrypted(0));

            Assert.Throws<ArgumentException>(() => MessageCodec.EncodeHello(hello));
        }

        [Fact]
        public void Un_Saludo_Con_Bytes_De_Mas_Se_Rechaza()
        {
            byte[] bytes = MessageCodec.EncodeHello(new HelloMessage(
                ProtocolConstants.Version,
                20,
                NewHash(0),
                1u,
                "dev",
                NewEncrypted(0)));

            byte[] withExtra = new byte[bytes.Length + 1];
            Array.Copy(bytes, withExtra, bytes.Length);

            Assert.False(MessageCodec.TryReadHello(withExtra, 0, withExtra.Length, out _));
        }

        [Fact]
        public void Los_Mensajes_De_Personajes_Van_Y_Vuelven_Y_El_Grande_Cabe_En_1400_Bytes()
        {
            var create = new CreateCharacterMessage("Ána", 4);
            byte[] createBytes = MessageCodec.EncodeCreateCharacter(create);
            Assert.Equal((byte)23, createBytes[0]);
            Assert.True(MessageCodec.TryReadCreateCharacter(
                createBytes, 0, createBytes.Length, out CreateCharacterMessage createRead));
            Assert.Equal(create.Name, createRead.Name);
            Assert.Equal(create.ClassId, createRead.ClassId);

            var delete = new DeleteCharacterMessage(123, "Ána");
            byte[] deleteBytes = MessageCodec.EncodeDeleteCharacter(delete);
            Assert.Equal((byte)24, deleteBytes[0]);
            Assert.True(MessageCodec.TryReadDeleteCharacter(
                deleteBytes, 0, deleteBytes.Length, out DeleteCharacterMessage deleteRead));
            Assert.Equal(delete.CharacterId, deleteRead.CharacterId);
            Assert.Equal(delete.ConfirmationName, deleteRead.ConfirmationName);

            var select = new SelectCharacterMessage(456);
            byte[] selectBytes = MessageCodec.EncodeSelectCharacter(select);
            Assert.Equal(new byte[] { 25, 200, 1, 0, 0 }, selectBytes);
            Assert.True(MessageCodec.TryReadSelectCharacter(
                selectBytes, 0, selectBytes.Length, out SelectCharacterMessage selectRead));
            Assert.Equal(select.CharacterId, selectRead.CharacterId);

            var characters = new CharacterListEntry[ProtocolConstants.MaxCharactersPerAccount];
            for (int i = 0; i < characters.Length; i++)
            {
                characters[i] = new CharacterListEntry(i + 1, new string('c', 32), (byte)(i + 1), 25);
            }

            var classes = new CharacterClassEntry[4];
            for (int i = 0; i < classes.Length; i++)
            {
                classes[i] = new CharacterClassEntry((byte)(i + 1), new string('k', 32));
            }

            var list = new CharacterListMessage(10, characters, classes);
            byte[] listBytes = MessageCodec.EncodeCharacterList(list);
            Assert.Equal((byte)26, listBytes[0]);
            Assert.True(listBytes.Length <= ProtocolConstants.MaxMessageSize);
            Assert.True(MessageCodec.TryReadCharacterList(
                listBytes, 0, listBytes.Length, out CharacterListMessage listRead));
            Assert.Equal(10, listRead.MaxCharacters);
            Assert.Equal(characters.Length, listRead.Characters.Length);
            Assert.Equal(classes.Length, listRead.Classes.Length);
            Assert.Equal(characters[9].Name, listRead.Characters[9].Name);
            Assert.Equal(classes[3].Name, listRead.Classes[3].Name);

            var rejected = new CharacterRejectedMessage(RejectReason.ConfirmMismatch);
            byte[] rejectedBytes = MessageCodec.EncodeCharacterRejected(rejected);
            Assert.Equal(new byte[] { 27, (byte)RejectReason.ConfirmMismatch }, rejectedBytes);
            Assert.True(MessageCodec.TryReadCharacterRejected(
                rejectedBytes, 0, rejectedBytes.Length, out CharacterRejectedMessage rejectedRead));
            Assert.Equal(rejected.Reason, rejectedRead.Reason);

            foreach (RejectReason reason in new[]
            {
                RejectReason.NameTaken,
                RejectReason.NameInvalid,
                RejectReason.CharacterLimit,
                RejectReason.CharacterNotFound,
                RejectReason.ClassUnknown,
                RejectReason.ConfirmMismatch,
                RejectReason.InternalError,
            })
            {
                byte[] reasonBytes = MessageCodec.EncodeCharacterRejected(new CharacterRejectedMessage(reason));
                Assert.True(MessageCodec.TryReadCharacterRejected(
                    reasonBytes, 0, reasonBytes.Length, out CharacterRejectedMessage reasonRead));
                Assert.Equal(reason, reasonRead.Reason);
            }

            Assert.Equal((ushort)20, ProtocolConstants.Version);
            Assert.False(Enum.IsDefined(typeof(MessageType), (byte)21));
        }

        [Fact]
        public void CharacterList_Admite_Mas_Personajes_Que_El_Limite_De_Creacion()
        {
            var characters = new[]
            {
                new CharacterListEntry(1, "Ana", 1, 1),
                new CharacterListEntry(2, "Juan", 1, 1),
                new CharacterListEntry(3, "Luis", 1, 1),
            };
            byte[] bytes = MessageCodec.EncodeCharacterList(new CharacterListMessage(
                2, characters, Array.Empty<CharacterClassEntry>()));

            Assert.True(MessageCodec.TryReadCharacterList(bytes, 0, bytes.Length, out CharacterListMessage decoded));
            Assert.Equal(2, decoded.MaxCharacters);
            Assert.Equal(characters, decoded.Characters);
        }

        [Fact]
        public void Los_Mensajes_De_Personajes_Rechazan_Longitudes_UTF8_Y_Rangos_Invalidos()
        {
            Assert.Throws<ArgumentException>(() => MessageCodec.EncodeCreateCharacter(
                new CreateCharacterMessage(new string('n', ProtocolConstants.MaxCharacterNameBytes + 1), 1)));
            Assert.Throws<System.Text.EncoderFallbackException>(() => MessageCodec.EncodeCreateCharacter(
                new CreateCharacterMessage("\uD800", 1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeCreateCharacter(
                new CreateCharacterMessage("Ana", 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeDeleteCharacter(
                new DeleteCharacterMessage(0, "Ana")));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeSelectCharacter(
                new SelectCharacterMessage(-1)));

            byte[] tooLongName = new byte[] { 23, 33, 0 };
            Array.Resize(ref tooLongName, 3 + 33 + 1);
            tooLongName[tooLongName.Length - 1] = 1;
            Assert.False(MessageCodec.TryReadCreateCharacter(
                tooLongName, 0, tooLongName.Length, out _));

            byte[] brokenUtf8 = { 23, 1, 0, 0xFF, 1 };
            Assert.False(MessageCodec.TryReadCreateCharacter(brokenUtf8, 0, brokenUtf8.Length, out _));
            byte[] zeroClass = { 23, 0, 0, 0 };
            Assert.False(MessageCodec.TryReadCreateCharacter(zeroClass, 0, zeroClass.Length, out _));
            Assert.False(MessageCodec.TryReadCreateCharacter(new byte[] { 23, 2, 0, (byte)'A' }, 0, 4, out _));

            byte[] delete = MessageCodec.EncodeDeleteCharacter(new DeleteCharacterMessage(5, "Ana"));
            Assert.False(MessageCodec.TryReadDeleteCharacter(delete, 0, delete.Length - 1, out _));
            Assert.False(MessageCodec.TryReadDeleteCharacter(delete, 0, delete.Length + 1, out _));
            byte[] zeroDeleteId = (byte[])delete.Clone();
            Array.Clear(zeroDeleteId, 1, 4);
            Assert.False(MessageCodec.TryReadDeleteCharacter(zeroDeleteId, 0, zeroDeleteId.Length, out _));
            Assert.False(MessageCodec.TryReadSelectCharacter(new byte[] { 25, 1, 0, 0 }, 0, 4, out _));
            Assert.False(MessageCodec.TryReadSelectCharacter(new byte[] { 25, 0, 0, 0, 0 }, 0, 5, out _));
            Assert.False(MessageCodec.TryReadSelectCharacter(new byte[] { 25, 255, 255, 255, 255 }, 0, 5, out _));

            byte[] tooManyCharacters = { 26, 10, 11 };
            Assert.False(MessageCodec.TryReadCharacterList(
                tooManyCharacters, 0, tooManyCharacters.Length, out _));
            byte[] invalidMaximum = { 26, 0, 0 };
            Assert.False(MessageCodec.TryReadCharacterList(invalidMaximum, 0, invalidMaximum.Length, out _));
            byte[] maximumAboveLimit = { 26, 11, 0, 0 };
            Assert.False(MessageCodec.TryReadCharacterList(
                maximumAboveLimit, 0, maximumAboveLimit.Length, out _));
            byte[] truncatedCharacters = { 26, 1, 2 };
            Assert.False(MessageCodec.TryReadCharacterList(
                truncatedCharacters, 0, truncatedCharacters.Length, out _));

            var overLimit = new CharacterListEntry[ProtocolConstants.MaxCharactersPerAccount + 1];
            Assert.Throws<ArgumentException>(() => MessageCodec.EncodeCharacterList(
                new CharacterListMessage(10, overLimit, Array.Empty<CharacterClassEntry>())));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeCharacterList(
                new CharacterListMessage(0, Array.Empty<CharacterListEntry>(), Array.Empty<CharacterClassEntry>())));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeCharacterList(
                new CharacterListMessage(1,
                    new[] { new CharacterListEntry(0, "Ana", 1, 1) },
                    Array.Empty<CharacterClassEntry>())));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeCharacterList(
                new CharacterListMessage(1,
                    new[] { new CharacterListEntry(1, "Ana", 0, 1) },
                    Array.Empty<CharacterClassEntry>())));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeCharacterList(
                new CharacterListMessage(1,
                    new[] { new CharacterListEntry(1, "Ana", 1, 0) },
                    Array.Empty<CharacterClassEntry>())));
            Assert.Throws<ArgumentException>(() => MessageCodec.EncodeCharacterList(
                new CharacterListMessage(1, Array.Empty<CharacterListEntry>(),
                    new[] { new CharacterClassEntry(1, new string('k', ProtocolConstants.MaxCharacterNameBytes + 1)) })));
            var tooManyClasses = new CharacterClassEntry[40];
            for (int i = 0; i < tooManyClasses.Length; i++)
            {
                tooManyClasses[i] = new CharacterClassEntry((byte)(i + 1), new string('k', 32));
            }
            Assert.Throws<ArgumentException>(() => MessageCodec.EncodeCharacterList(
                new CharacterListMessage(1, Array.Empty<CharacterListEntry>(), tooManyClasses)));

            byte[] validList = MessageCodec.EncodeCharacterList(new CharacterListMessage(
                1,
                new[] { new CharacterListEntry(1, "Ana", 1, 1) },
                Array.Empty<CharacterClassEntry>()));
            byte[] badCharacterId = (byte[])validList.Clone();
            Array.Clear(badCharacterId, 3, 4);
            Assert.False(MessageCodec.TryReadCharacterList(badCharacterId, 0, badCharacterId.Length, out _));
            byte[] badCharacterClass = (byte[])validList.Clone();
            badCharacterClass[12] = 0;
            Assert.False(MessageCodec.TryReadCharacterList(badCharacterClass, 0, badCharacterClass.Length, out _));
            byte[] badCharacterLevel = (byte[])validList.Clone();
            badCharacterLevel[13] = 0;
            Assert.False(MessageCodec.TryReadCharacterList(badCharacterLevel, 0, badCharacterLevel.Length, out _));
            byte[] badListUtf8 = (byte[])validList.Clone();
            badListUtf8[9] = 0xFF;
            Assert.False(MessageCodec.TryReadCharacterList(badListUtf8, 0, badListUtf8.Length, out _));

            byte[] invalidRejection = { 27, (byte)RejectReason.ProtocolVersion };
            Assert.False(MessageCodec.TryReadCharacterRejected(
                invalidRejection, 0, invalidRejection.Length, out _));
            Assert.False(MessageCodec.TryReadCharacterRejected(new byte[] { 27, 255 }, 0, 2, out _));
        }

        [Fact]
        public void El_Desafio_Va_Y_Vuelve_Igual()
        {
            var nonce = new byte[HelloSecrets.NonceSize];

            for (int i = 0; i < nonce.Length; i++)
            {
                nonce[i] = (byte)(i + 1);
            }

            var modulus = new byte[HelloSecrets.ModulusSize];
            modulus[0] = 0xC5;
            modulus[modulus.Length - 1] = 0x7A;

            var original = new ChallengeMessage(nonce, modulus, new byte[] { 0x01, 0x00, 0x01 });

            byte[] bytes = MessageCodec.EncodeChallenge(original);

            Assert.True(MessageCodec.TryReadChallenge(bytes, 0, bytes.Length, out ChallengeMessage decoded));
            Assert.Equal(1 + HelloSecrets.NonceSize + 1 + 3 + HelloSecrets.ModulusSize, bytes.Length);
            Assert.Equal(original.Nonce, decoded.Nonce);
            Assert.Equal(original.Modulus, decoded.Modulus);
            Assert.Equal(original.Exponent, decoded.Exponent);
        }

        [Fact]
        public void El_Desafio_Con_Longitudes_Invalidas_Se_Rechaza()
        {
            var nonce = new byte[HelloSecrets.NonceSize];
            var modulus = new byte[HelloSecrets.ModulusSize];
            modulus[0] = 1;

            // Nonce corto o módulo corto: no se puede ni codificar.
            Assert.Throws<ArgumentException>(
                () => MessageCodec.EncodeChallenge(new ChallengeMessage(new byte[15], modulus, new byte[] { 1 })));

            Assert.Throws<ArgumentException>(
                () => MessageCodec.EncodeChallenge(new ChallengeMessage(nonce, new byte[255], new byte[] { 1 })));

            // Exponente vacío o de más de 4 bytes.
            Assert.Throws<ArgumentException>(
                () => MessageCodec.EncodeChallenge(new ChallengeMessage(nonce, modulus, Array.Empty<byte>())));

            Assert.Throws<ArgumentException>(
                () => MessageCodec.EncodeChallenge(new ChallengeMessage(nonce, modulus, new byte[5])));

            byte[] valid = MessageCodec.EncodeChallenge(new ChallengeMessage(nonce, modulus, new byte[] { 1 }));

            // Un byte de más y un byte de menos.
            byte[] longer = new byte[valid.Length + 1];
            Array.Copy(valid, longer, valid.Length);
            Assert.False(MessageCodec.TryReadChallenge(longer, 0, longer.Length, out _));
            Assert.False(MessageCodec.TryReadChallenge(valid, 0, valid.Length - 1, out _));

            // Longitud del exponente 0 o 5.
            byte[] zeroExponent = (byte[])valid.Clone();
            zeroExponent[1 + HelloSecrets.NonceSize] = 0;
            Assert.False(MessageCodec.TryReadChallenge(zeroExponent, 0, zeroExponent.Length, out _));

            byte[] fiveExponent = (byte[])valid.Clone();
            fiveExponent[1 + HelloSecrets.NonceSize] = 5;
            Assert.False(MessageCodec.TryReadChallenge(fiveExponent, 0, fiveExponent.Length, out _));

            // Exponente todo a cero y módulo todo a cero.
            byte[] zeroExponentValue = (byte[])valid.Clone();
            zeroExponentValue[1 + HelloSecrets.NonceSize + 1] = 0;
            Assert.False(MessageCodec.TryReadChallenge(zeroExponentValue, 0, zeroExponentValue.Length, out _));

            byte[] zeroModulus = (byte[])valid.Clone();
            Array.Clear(zeroModulus, 1 + HelloSecrets.NonceSize + 1 + 1, HelloSecrets.ModulusSize);
            Assert.False(MessageCodec.TryReadChallenge(zeroModulus, 0, zeroModulus.Length, out _));
        }

        [Fact]
        public void La_Intencion_Va_Y_Vuelve_Igual_Con_Un_Input()
        {
            var original = new MoveInputMessage(4294967290u, -1, 1);

            byte[] bytes = MessageCodec.EncodeMoveInput(original);

            Assert.True(MessageCodec.TryReadMoveInput(bytes, 0, bytes.Length, out MoveInputMessage decoded));
            Assert.Equal(8, bytes.Length);
            Assert.Equal(original.Sequence, decoded.Sequence);
            Assert.Equal(1, decoded.Count);
            Assert.True(decoded.TryGetDirection(0, out sbyte x, out sbyte y));
            Assert.Equal((sbyte)-1, x);
            Assert.Equal((sbyte)1, y);
        }

        [Fact]
        public void La_Intencion_Va_Y_Vuelve_Igual_Con_Cuatro_Inputs()
        {
            var original = new MoveInputMessage(
                100u,
                4,
                1,
                0,
                0,
                1,
                -1,
                0,
                0,
                -1);

            byte[] bytes = MessageCodec.EncodeMoveInput(original);

            Assert.True(MessageCodec.TryReadMoveInput(bytes, 0, bytes.Length, out MoveInputMessage decoded));
            Assert.Equal(6 + (2 * 4), bytes.Length);
            Assert.Equal(original.Sequence, decoded.Sequence);
            Assert.Equal(4, decoded.Count);

            // Del más nuevo al más viejo: cada par es la secuencia Sequence - i.
            Assert.Equal((sbyte)1, GetX(decoded, 0));
            Assert.Equal((sbyte)0, GetX(decoded, 1));
            Assert.Equal((sbyte)-1, GetX(decoded, 2));
            Assert.Equal((sbyte)0, GetX(decoded, 3));
            Assert.Equal((sbyte)-1, GetY(decoded, 3));
            Assert.False(decoded.TryGetDirection(4, out _, out _));
        }

        [Fact]
        public void Un_Input_Con_Cero_O_Demasiados_No_Se_Puede_Codificar()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new MoveInputMessage(4u, 0, 0, 0, 0, 0, 0, 0, 0, 0));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new MoveInputMessage(4u, 5, 0, 0, 0, 0, 0, 0, 0, 0));
        }

        [Fact]
        public void Un_Input_Con_Menos_Secuencias_Que_Inputs_No_Se_Puede_Codificar()
        {
            // Las secuencias empiezan en 1: con n = 4 la más vieja sería Sequence - 3 = 0.
            var message = new MoveInputMessage(3u, 4, 0, 0, 0, 0, 0, 0, 0, 0);

            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeMoveInput(message));
        }

        [Fact]
        public void El_Codec_Rechaza_Un_Numero_De_Inputs_Fuera_De_Rango()
        {
            byte[] cero = RawMoveInput(4u, 0);
            Assert.False(MessageCodec.TryReadMoveInput(cero, 0, cero.Length, out _));

            byte[] cinco = RawMoveInput(4u, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            Assert.False(MessageCodec.TryReadMoveInput(cinco, 0, cinco.Length, out _));
        }

        [Fact]
        public void El_Codec_Rechaza_Una_Longitud_Que_No_Es_Seis_Mas_Dos_N()
        {
            // n = 1 con el par a medias (truncado).
            byte[] sinPar = RawMoveInput(4u, 1, 0, 0);
            Assert.False(MessageCodec.TryReadMoveInput(sinPar, 0, sinPar.Length - 1, out _));

            // n = 1 con dos bytes de propina.
            byte[] conPropina = RawMoveInput(4u, 1, 0, 0);
            Array.Resize(ref conPropina, conPropina.Length + 2);
            Assert.False(MessageCodec.TryReadMoveInput(conPropina, 0, conPropina.Length, out _));

            // n = 2 con la longitud de n = 1 (el par sobra).
            byte[] deMas = new byte[8];
            Array.Copy(RawMoveInput(4u, 2, 0, 0), deMas, deMas.Length);
            deMas[5] = 2;
            Assert.False(MessageCodec.TryReadMoveInput(deMas, 0, deMas.Length, out _));
        }

        [Fact]
        public void El_Codec_Rechaza_Una_Secuencia_Menor_Que_El_Numero_De_Inputs()
        {
            // La más vieja sería la secuencia 0, que el protocolo reserva para «ninguna».
            Assert.False(MessageCodec.TryReadMoveInput(
                RawMoveInput(3u, 4, 0, 0, 0, 0, 0, 0, 0, 0), 0, 6 + (2 * 4), out _));

            Assert.True(MessageCodec.TryReadMoveInput(
                RawMoveInput(4u, 4, 0, 0, 0, 0, 0, 0, 0, 0), 0, 6 + (2 * 4), out _));
        }

        [Fact]
        public void El_Codec_Rechaza_Una_Direccion_Invalida_En_Cualquier_Posicion()
        {
            // El par más viejo (i = 3) también se valida.
            byte[] bytes = RawMoveInput(10u, 4, 0, 0, 0, 0, 0, 0, 5, 0);

            Assert.False(MessageCodec.TryReadMoveInput(bytes, 0, bytes.Length, out _));
        }

        private static sbyte GetX(in MoveInputMessage message, int index)
        {
            Assert.True(message.TryGetDirection(index, out sbyte x, out _));
            return x;
        }

        private static sbyte GetY(in MoveInputMessage message, int index)
        {
            Assert.True(message.TryGetDirection(index, out _, out sbyte y));
            return y;
        }

        /// <summary>Construye un MoveInput a mano, sin pasar por las validaciones del codificador.</summary>
        private static byte[] RawMoveInput(uint sequence, int count, params sbyte[] directions)
        {
            var bytes = new byte[6 + (2 * count)];
            bytes[0] = (byte)MessageType.MoveInput;
            bytes[1] = (byte)(sequence & 0xFF);
            bytes[2] = (byte)((sequence >> 8) & 0xFF);
            bytes[3] = (byte)((sequence >> 16) & 0xFF);
            bytes[4] = (byte)((sequence >> 24) & 0xFF);
            bytes[5] = (byte)count;

            for (int i = 0; i < directions.Length && (6 + i) < bytes.Length; i++)
            {
                bytes[6 + i] = unchecked((byte)directions[i]);
            }

            return bytes;
        }

        [Fact]
        public void La_Bienvenida_Conserva_Los_Floats_Exactos()
        {
            var original = new WelcomeMessage(
                1,
                20,
                77,
                4242u,
                999u,
                -2.0000001f,
                -3.1415927f,
                4.4f,
                0.4f,
                0.39999998f,
                NewHash(1));

            byte[] bytes = MessageCodec.EncodeWelcome(original);

            Assert.True(MessageCodec.TryReadWelcome(bytes, 0, bytes.Length, out WelcomeMessage decoded));
            Assert.Equal(69, bytes.Length);

            // Bit a bit: la predicción del cliente depende de que la caja sea idéntica.
            Assert.Equal(original.SpawnX, decoded.SpawnX);
            Assert.Equal(original.SpawnY, decoded.SpawnY);
            Assert.Equal(original.Speed, decoded.Speed);
            Assert.Equal(original.HalfWidth, decoded.HalfWidth);
            Assert.Equal(original.HalfHeight, decoded.HalfHeight);
            Assert.Equal(original.EntityId, decoded.EntityId);
            Assert.Equal(original.ServerTick, decoded.ServerTick);
            Assert.Equal(original.ClientToken, decoded.ClientToken);
            Assert.Equal(original.MapHash, decoded.MapHash);
        }

        [Fact]
        public void El_Rechazo_Va_Y_Vuelve_Igual()
        {
            byte[] bytes = MessageCodec.EncodeReject(new RejectMessage(RejectReason.MapMismatch));

            Assert.True(MessageCodec.TryReadReject(bytes, 0, bytes.Length, out RejectMessage decoded));
            Assert.Equal(2, bytes.Length);
            Assert.Equal(RejectReason.MapMismatch, decoded.Reason);
        }

        [Fact]
        public void El_Snapshot_Va_Y_Vuelve_Igual()
        {
            var entities = new[]
            {
                new EntityState(1, 64, -128, 1, 0, EntityKind.Player, 100, EntityAction.None),
                new EntityState(9, -32768, 32767, -1, -1, EntityKind.Npc, 50, EntityAction.Attacking, 65535),
                new EntityState(10, 0, 0, 0, 0, EntityKind.Npc, 100, EntityAction.None, 2002),
            };

            var original = new SnapshotMessage(1234u, 5678u, entities);

            byte[] bytes = MessageCodec.EncodeSnapshot(original);

            Assert.True(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out SnapshotMessage decoded));
            Assert.Equal(1 + 4 + 4 + 2 + (3 * 15), bytes.Length);
            Assert.Equal(original.ServerTick, decoded.ServerTick);
            Assert.Equal(original.LastProcessedInputSequence, decoded.LastProcessedInputSequence);
            Assert.Equal(3, decoded.Entities.Length);
            Assert.Equal(1, decoded.Entities[0].Id);
            Assert.Equal((ushort)0, decoded.Entities[0].TypeId);
            Assert.Equal((ushort)65535, decoded.Entities[1].TypeId);
            Assert.Equal((ushort)2002, decoded.Entities[2].TypeId);
            Assert.Equal(64, decoded.Entities[0].X);
            Assert.Equal(-128, decoded.Entities[0].Y);
            Assert.Equal((sbyte)1, decoded.Entities[0].DirectionX);
            Assert.Equal(EntityKind.Player, decoded.Entities[0].Kind);
            Assert.Equal((byte)100, decoded.Entities[0].HealthPercent);
            Assert.Equal(EntityAction.None, decoded.Entities[0].Action);
            Assert.Equal(9, decoded.Entities[1].Id);
            Assert.Equal(short.MinValue, decoded.Entities[1].X);
            Assert.Equal(short.MaxValue, decoded.Entities[1].Y);
            Assert.Equal(EntityKind.Npc, decoded.Entities[1].Kind);
            Assert.Equal((byte)50, decoded.Entities[1].HealthPercent);
            Assert.Equal(EntityAction.Attacking, decoded.Entities[1].Action);
        }

        [Fact]
        public void El_Snapshot_Transporta_Pvp_Efectivo_Solo_En_Jugadores()
        {
            var player = new EntityState(1, 0, 0, 0, 0, EntityKind.Player, 100, EntityAction.None, pvp: true);
            byte[] bytes = MessageCodec.EncodeSnapshot(new SnapshotMessage(1, 0, new[] { player }));

            Assert.Equal((byte)0x40, bytes[23]);
            Assert.True(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out SnapshotMessage decoded));
            Assert.True(decoded.Entities[0].Pvp);

            var npc = new EntityState(2, 0, 0, 0, 0, EntityKind.Npc, 100, EntityAction.None, pvp: true);
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeSnapshot(
                new SnapshotMessage(1, 0, new[] { npc })));

            byte[] npcBytes = MessageCodec.EncodeSnapshot(new SnapshotMessage(
                1, 0, new[] { new EntityState(2, 0, 0, 0, 0, EntityKind.Npc, 100, EntityAction.None) }));
            npcBytes[23] = 0x40;
            Assert.False(MessageCodec.TryReadSnapshot(npcBytes, 0, npcBytes.Length, out _));

            bytes[23] = 0x80;
            Assert.False(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out _));
        }

        [Fact]
        public void SetPvpMode_Va_Y_Vuelve_Y_Rechaza_Modo_Invalido()
        {
            byte[] bytes = MessageCodec.EncodeSetPvpMode(new SetPvpModeMessage(PvpMode.Pvp));
            Assert.Equal(2, bytes.Length);
            Assert.True(MessageCodec.TryReadSetPvpMode(bytes, 0, bytes.Length, out SetPvpModeMessage decoded));
            Assert.Equal(PvpMode.Pvp, decoded.Mode);
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeSetPvpMode(
                new SetPvpModeMessage((PvpMode)2)));
            bytes[1] = 2;
            Assert.False(MessageCodec.TryReadSetPvpMode(bytes, 0, bytes.Length, out _));
            Assert.False(MessageCodec.TryReadSetPvpMode(bytes, 0, 1, out _));
        }

        [Fact]
        public void PvpStatus_Va_Y_Vuelve_Y_Valida_Estados_Y_Tiempos()
        {
            var original = new PvpStatusMessage(
                PvpMode.Pve, PvpForcedState.Timer, PvpChangeState.Waiting, 12, 3);
            byte[] bytes = MessageCodec.EncodePvpStatus(original);
            Assert.Equal(8, bytes.Length);
            Assert.True(MessageCodec.TryReadPvpStatus(bytes, 0, bytes.Length, out PvpStatusMessage decoded));
            Assert.Equal(original.Mode, decoded.Mode);
            Assert.Equal(original.Forced, decoded.Forced);
            Assert.Equal(original.Change, decoded.Change);
            Assert.Equal(original.ForcedSeconds, decoded.ForcedSeconds);
            Assert.Equal(original.ChangeSeconds, decoded.ChangeSeconds);
            Assert.False(MessageCodec.TryReadPvpStatus(bytes, 0, 7, out _));
            Assert.False(MessageCodec.TryReadPvpStatus(bytes, 0, 9, out _));

            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodePvpStatus(
                new PvpStatusMessage(PvpMode.Pve, PvpForcedState.None, PvpChangeState.Waiting, 1, 1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodePvpStatus(
                new PvpStatusMessage(PvpMode.Pve, PvpForcedState.Timer, PvpChangeState.Ready, 0, 0)));
            bytes[2] = 3;
            Assert.False(MessageCodec.TryReadPvpStatus(bytes, 0, bytes.Length, out _));
        }

        [Fact]
        public void PvpModeRejected_Va_Y_Vuelve_Y_Rechaza_Motivo_Invalido()
        {
            byte[] bytes = MessageCodec.EncodePvpModeRejected(
                new PvpModeRejectedMessage(PvpModeRejectReason.TooSoon));
            Assert.Equal(2, bytes.Length);
            Assert.True(MessageCodec.TryReadPvpModeRejected(
                bytes, 0, bytes.Length, out PvpModeRejectedMessage decoded));
            Assert.Equal(PvpModeRejectReason.TooSoon, decoded.Reason);
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodePvpModeRejected(
                new PvpModeRejectedMessage((PvpModeRejectReason)3)));
            bytes[1] = 3;
            Assert.False(MessageCodec.TryReadPvpModeRejected(bytes, 0, bytes.Length, out _));
        }

        [Fact]
        public void El_Protocolo_Pvp_Usa_La_Version_20()
        {
            Assert.Equal((ushort)20, ProtocolConstants.Version);
        }

        [Theory]
        [InlineData(2, 0, 0, 0, 0)]
        [InlineData(0, 3, 0, 0, 0)]
        [InlineData(0, 0, 3, 0, 0)]
        [InlineData(0, 0, 0, 1, 0)]
        [InlineData(0, 1, 0, 1, 0)]
        [InlineData(0, 2, 0, 0, 0)]
        [InlineData(0, 0, 0, 0, 1)]
        [InlineData(0, 0, 2, 0, 1)]
        [InlineData(0, 0, 1, 0, 0)]
        public void PvpStatus_Rechaza_Cada_Combinacion_Invalida(int mode, int forced, int change, int forcedSeconds, int changeSeconds)
        {
            var message = new PvpStatusMessage((PvpMode)mode, (PvpForcedState)forced,
                (PvpChangeState)change, (ushort)forcedSeconds, (ushort)changeSeconds);
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodePvpStatus(message));
            byte[] bytes = { (byte)MessageType.PvpStatus, (byte)mode, (byte)forced, (byte)change,
                (byte)forcedSeconds, 0, (byte)changeSeconds, 0 };
            Assert.False(MessageCodec.TryReadPvpStatus(bytes, 0, bytes.Length, out _));
        }

        [Fact]
        public void Mensajes_Pvp_Rechazan_Tipo_Distinto_Y_Longitudes_De_Mas_Y_Menos()
        {
            byte[] mode = MessageCodec.EncodeSetPvpMode(new SetPvpModeMessage(PvpMode.Pve));
            byte[] status = MessageCodec.EncodePvpStatus(new PvpStatusMessage(
                PvpMode.Pvp, PvpForcedState.InZone, PvpChangeState.Ready, 0, 0));
            byte[] rejected = MessageCodec.EncodePvpModeRejected(
                new PvpModeRejectedMessage(PvpModeRejectReason.NotInSafeZone));
            Assert.True(MessageCodec.TryReadSetPvpMode(mode, 0, mode.Length, out _));
            Assert.True(MessageCodec.TryReadPvpStatus(status, 0, status.Length, out _));
            Assert.True(MessageCodec.TryReadPvpModeRejected(rejected, 0, rejected.Length, out _));
            foreach (int delta in new[] { -1, 1 })
            {
                byte[] sizedMode = (byte[])mode.Clone();
                byte[] sizedStatus = (byte[])status.Clone();
                byte[] sizedRejected = (byte[])rejected.Clone();
                Array.Resize(ref sizedMode, 2 + delta);
                Array.Resize(ref sizedStatus, 8 + delta);
                Array.Resize(ref sizedRejected, 2 + delta);
                Assert.False(MessageCodec.TryReadSetPvpMode(sizedMode, 0, sizedMode.Length, out _));
                Assert.False(MessageCodec.TryReadPvpStatus(sizedStatus, 0, sizedStatus.Length, out _));
                Assert.False(MessageCodec.TryReadPvpModeRejected(sizedRejected, 0, sizedRejected.Length, out _));
            }
            mode[0] = status[0] = rejected[0] = (byte)MessageType.Attack;
            Assert.False(MessageCodec.TryReadSetPvpMode(mode, 0, mode.Length, out _));
            Assert.False(MessageCodec.TryReadPvpStatus(status, 0, status.Length, out _));
            Assert.False(MessageCodec.TryReadPvpModeRejected(rejected, 0, rejected.Length, out _));
            foreach (byte reason in new byte[] { 0, 3 })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodePvpModeRejected(
                    new PvpModeRejectedMessage((PvpModeRejectReason)reason)));
                rejected[0] = (byte)MessageType.PvpModeRejected;
                rejected[1] = reason;
                Assert.False(MessageCodec.TryReadPvpModeRejected(rejected, 0, rejected.Length, out _));
            }
        }

        [Fact]
        public void El_Snapshot_Empaqueta_Accion_Y_Rango_Sin_Cambiar_Su_Tamano()
        {
            foreach (EntityAction action in new[] { EntityAction.None, EntityAction.Attacking, EntityAction.Dead })
            {
                for (byte rank = 0; rank <= 3; rank++)
                {
                    byte[] bytes = MessageCodec.EncodeSnapshot(new SnapshotMessage(
                        1,
                        0,
                        new[] { new EntityState(7, 0, 0, 0, 0, EntityKind.Npc, 100, action, rank: rank) }));

                    Assert.Equal(26, bytes.Length);
                    Assert.Equal((byte)((byte)action | (rank << 4)), bytes[23]);
                    Assert.True(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out SnapshotMessage decoded));
                    Assert.Equal(action, decoded.Entities[0].Action);
                    Assert.Equal(rank, decoded.Entities[0].Rank);
                }
            }
        }

        [Theory]
        [InlineData(0x0f)]
        [InlineData(0x40)]
        [InlineData(0x80)]
        public void El_Snapshot_Rechaza_Accion_Desconocida_O_Bits_Reservados(byte actionByte)
        {
            byte[] bytes = MessageCodec.EncodeSnapshot(new SnapshotMessage(
                1,
                0,
                new[] { new EntityState(7, 0, 0, 0, 0, EntityKind.Npc, 100, EntityAction.None) }));
            bytes[23] = actionByte;

            Assert.False(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out _));
        }

        [Fact]
        public void El_Snapshot_No_Acepta_Rango_Imposible_Al_Codificar()
        {
            var entity = new EntityState(7, 0, 0, 0, 0, EntityKind.Npc, 100, EntityAction.None, rank: 4);

            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeSnapshot(
                new SnapshotMessage(1, 0, new[] { entity })));
        }

        [Theory]
        [InlineData(EntityKind.Player)]
        [InlineData(EntityKind.Item)]
        public void El_Snapshot_No_Acepta_Rango_En_Jugador_Ni_Objeto(EntityKind kind)
        {
            var entity = new EntityState(7, 0, 0, 0, 0, kind, 100, EntityAction.None, rank: 1);

            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeSnapshot(
                new SnapshotMessage(1, 0, new[] { entity })));
        }

        [Fact]
        public void Un_Snapshot_Acepta_Objeto_En_El_Suelo()
        {
            byte[] bytes = MessageCodec.EncodeSnapshot(new SnapshotMessage(
                1,
                0,
                new[] { new EntityState(8, 64, -64, 0, 0, EntityKind.Item, 100, EntityAction.None, 3001) }));

            Assert.True(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out SnapshotMessage snapshot));
            Assert.Equal(EntityKind.Item, snapshot.Entities[0].Kind);
            Assert.Equal((ushort)3001, snapshot.Entities[0].TypeId);
        }

        [Fact]
        public void Un_Snapshot_Vacio_Es_Valido()
        {
            byte[] bytes = MessageCodec.EncodeSnapshot(new SnapshotMessage(1u, 0u, Array.Empty<EntityState>()));

            Assert.True(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out SnapshotMessage decoded));
            Assert.Empty(decoded.Entities);
        }

        [Fact]
        public void Un_Tipo_Desconocido_Se_Rechaza()
        {
            byte[] bytes = MessageCodec.EncodeHello(new HelloMessage(1, 20, NewHash(0), 1u));
            bytes[0] = 99;

            Assert.False(MessageCodec.TryReadHello(bytes, 0, bytes.Length, out _));
        }

        [Fact]
        public void Un_Mensaje_Truncado_Se_Rechaza()
        {
            byte[] hello = MessageCodec.EncodeHello(new HelloMessage(1, 20, NewHash(0), 1u));
            byte[] input = MessageCodec.EncodeMoveInput(new MoveInputMessage(1u, 0, 0));

            Assert.False(MessageCodec.TryReadHello(hello, 0, hello.Length - 1, out _));
            Assert.False(MessageCodec.TryReadMoveInput(input, 0, input.Length - 1, out _));
            Assert.False(MessageCodec.TryReadMoveInput(input, 0, 0, out _));
        }

        [Fact]
        public void Un_Mensaje_Por_Encima_Del_Tope_Se_Rechaza()
        {
            var bytes = new byte[ProtocolConstants.MaxMessageSize + 1];
            bytes[0] = (byte)MessageType.Hello;

            Assert.False(MessageCodec.TryReadHello(bytes, 0, bytes.Length, out _));
            Assert.False(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out _));
        }

        [Fact]
        public void Un_Mensaje_De_Direccion_Contraria_Se_Rechaza()
        {
            byte[] snapshot = MessageCodec.EncodeSnapshot(new SnapshotMessage(1u, 0u, Array.Empty<EntityState>()));

            // El servidor no debe aceptar un snapshot como si fuera un input.
            Assert.False(MessageCodec.TryReadMoveInput(snapshot, 0, snapshot.Length, out _));
            Assert.False(MessageCodec.TryReadHello(snapshot, 0, snapshot.Length, out _));
        }

        [Fact]
        public void Una_Direccion_Fuera_De_Rango_Se_Rechaza()
        {
            // -1, 0 y 1 son las únicas direcciones del protocolo; el resto es mensaje malformado.
            byte[] diagonal = MessageCodec.EncodeMoveInput(new MoveInputMessage(1u, 5, 0));
            byte[] extrema = MessageCodec.EncodeMoveInput(new MoveInputMessage(2u, 0, -128));

            Assert.False(MessageCodec.TryReadMoveInput(diagonal, 0, diagonal.Length, out _));
            Assert.False(MessageCodec.TryReadMoveInput(extrema, 0, extrema.Length, out _));
        }

        [Fact]
        public void Un_Rechazo_Con_Motivo_Desconocido_Se_Rechaza()
        {
            byte[] bytes = MessageCodec.EncodeReject(new RejectMessage(RejectReason.ServerFull));
            bytes[1] = 99;

            Assert.False(MessageCodec.TryReadReject(bytes, 0, bytes.Length, out _));
        }

        [Fact]
        public void Un_Snapshot_Con_Tipo_De_Entidad_Desconocido_Se_Rechaza()
        {
            byte[] bytes = MessageCodec.EncodeSnapshot(
                new SnapshotMessage(1u, 0u, new[] { new EntityState(1, 0, 0, 0, 0, EntityKind.Player, 100, EntityAction.None) }));

            // Cabecera (1) + tick y secuencia (8) + cuenta (2) + id (4) + x e y (4).
            bytes[21] = 99;

            Assert.False(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out _));
        }

        [Fact]
        public void Un_Snapshot_Con_Accion_Desconocida_Se_Rechaza()
        {
            byte[] bytes = MessageCodec.EncodeSnapshot(
                new SnapshotMessage(1u, 0u, new[] { new EntityState(1, 0, 0, 0, 0, EntityKind.Npc, 100, EntityAction.None) }));

            // Cabecera (11) + id (4) + x e y (4) + direcciones y tipo (3) + vida (1).
            bytes[23] = 99;

            Assert.False(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out _));
        }

        [Fact]
        public void Un_Snapshot_Con_Vida_Fuera_De_Rango_Se_Rechaza()
        {
            byte[] bytes = MessageCodec.EncodeSnapshot(
                new SnapshotMessage(1u, 0u, new[] { new EntityState(1, 0, 0, 0, 0, EntityKind.Npc, 100, EntityAction.None) }));

            bytes[22] = 101;

            Assert.False(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out _));
        }

        [Fact]
        public void El_Ataque_Va_Y_Vuelve_Igual()
        {
            byte[] bytes = MessageCodec.EncodeAttack(new AttackMessage(123456));

            Assert.True(MessageCodec.TryReadAttack(bytes, 0, bytes.Length, out AttackMessage decoded));
            Assert.Equal(5, bytes.Length);
            Assert.Equal(123456, decoded.TargetId);
        }

        [Fact]
        public void CombatEvent_Va_Y_Vuelve_Con_Limites_Y_Rechaza_Entradas_Invalidas()
        {
            var one = new CombatEventMessage(new[]
            {
                new CombatEventEntry(12, 34, 65535, CombatEventFlags.Ranged | CombatEventFlags.Magical | CombatEventFlags.Killed),
            });
            byte[] oneBytes = MessageCodec.EncodeCombatEvent(one);
            Assert.Equal(13, oneBytes.Length);
            Assert.True(MessageCodec.TryReadCombatEvent(oneBytes, 0, oneBytes.Length, out CombatEventMessage oneRead));
            Assert.Equal(one.Entries[0].AttackerId, oneRead.Entries[0].AttackerId);
            Assert.Equal(one.Entries[0].TargetId, oneRead.Entries[0].TargetId);
            Assert.Equal(ushort.MaxValue, oneRead.Entries[0].Damage);
            Assert.Equal((CombatEventFlags)7, oneRead.Entries[0].Flags);

            var manyEntries = new CombatEventEntry[ProtocolConstants.MaxCombatEventsPerMessage];
            for (int i = 0; i < manyEntries.Length; i++)
            {
                manyEntries[i] = new CombatEventEntry(i, -i, ushort.MaxValue, (CombatEventFlags)7);
            }

            byte[] manyBytes = MessageCodec.EncodeCombatEvent(new CombatEventMessage(manyEntries));
            Assert.Equal(706, manyBytes.Length);
            Assert.True(MessageCodec.TryReadCombatEvent(manyBytes, 0, manyBytes.Length, out CombatEventMessage manyRead));
            Assert.Equal(64, manyRead.Entries.Length);
            Assert.Equal(63, manyRead.Entries[63].AttackerId);

            Assert.False(MessageCodec.TryReadCombatEvent(new byte[] { 28, 0 }, 0, 2, out _));
            Assert.False(MessageCodec.TryReadCombatEvent(new byte[] { 28, 65 }, 0, 2, out _));
            Assert.False(MessageCodec.TryReadCombatEvent(oneBytes, 0, oneBytes.Length - 1, out _));
            byte[] longer = new byte[oneBytes.Length + 1];
            Array.Copy(oneBytes, longer, oneBytes.Length);
            Assert.False(MessageCodec.TryReadCombatEvent(longer, 0, longer.Length, out _));
            Assert.False(MessageCodec.TryReadCombatEvent(new byte[] { 27, 1 }, 0, 2, out _));

            byte[] invalidFlags = (byte[])oneBytes.Clone();
            invalidFlags[12] = 8;
            Assert.False(MessageCodec.TryReadCombatEvent(invalidFlags, 0, invalidFlags.Length, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeCombatEvent(
                new CombatEventMessage(Array.Empty<CombatEventEntry>())));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeCombatEvent(
                new CombatEventMessage(new CombatEventEntry[65])));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeCombatEvent(
                new CombatEventMessage(new[] { new CombatEventEntry(1, 2, 3, (CombatEventFlags)8) })));
        }

        [Fact]
        public void PickUp_UseItem_E_Inventory_Van_Y_Vuelven_Igual()
        {
            byte[] pickup = MessageCodec.EncodePickUp(new PickUpMessage(123456));
            Assert.True(MessageCodec.TryReadPickUp(pickup, 0, pickup.Length, out PickUpMessage pickupRead));
            Assert.Equal(5, pickup.Length);
            Assert.Equal(123456, pickupRead.EntityId);

            byte[] use = MessageCodec.EncodeUseItem(new UseItemMessage(19));
            Assert.True(MessageCodec.TryReadUseItem(use, 0, use.Length, out UseItemMessage useRead));
            Assert.Equal(2, use.Length);
            Assert.Equal((byte)19, useRead.Slot);

            var equipment = new ushort[ProtocolConstants.EquipSlotCount];
            equipment[(int)EquipSlot.Weapon] = 3101;
            var inventory = new InventoryMessage(
                3,
                new ushort[] { 3001, 0, 3002 },
                new ushort[] { 99, 0, 1 },
                equipment,
                123456);
            byte[] encodedInventory = MessageCodec.EncodeInventory(inventory);
            Assert.True(MessageCodec.TryReadInventory(
                encodedInventory, 0, encodedInventory.Length, out InventoryMessage inventoryRead));
            Assert.Equal(38, encodedInventory.Length);
            Assert.Equal(inventory.ItemIds, inventoryRead.ItemIds);
            Assert.Equal(inventory.Counts, inventoryRead.Counts);
            Assert.Equal((ushort)3101, inventoryRead.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal(123456u, inventoryRead.Gold);
        }

        [Fact]
        public void LootGained_Admite_Cero_Y_32_Objetos_Y_Rechaza_Datos_Invalidos()
        {
            byte[] empty = MessageCodec.EncodeLootGained(new LootGainedMessage(12, 34, Array.Empty<LootItemEntry>()));
            Assert.Equal(10, empty.Length);
            Assert.True(MessageCodec.TryReadLootGained(empty, 0, empty.Length, out LootGainedMessage emptyRead));
            Assert.Equal(12u, emptyRead.Experience);
            Assert.Equal(34u, emptyRead.Gold);
            Assert.Empty(emptyRead.Items);

            var entries = new LootItemEntry[ProtocolConstants.MaxLootItemsPerMessage];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new LootItemEntry((ushort)(i + 1), ushort.MaxValue, i % 2 == 0);
            }

            byte[] full = MessageCodec.EncodeLootGained(new LootGainedMessage(123, 456, entries));
            Assert.Equal(170, full.Length);
            Assert.True(MessageCodec.TryReadLootGained(full, 0, full.Length, out LootGainedMessage fullRead));
            Assert.Equal(entries.Length, fullRead.Items.Length);
            Assert.Equal(ushort.MaxValue, fullRead.Items[0].Count);
            Assert.True(fullRead.Items[0].OnGround);
            Assert.False(fullRead.Items[1].OnGround);

            byte[] invalid = (byte[])full.Clone();
            invalid[9] = 33;
            Assert.False(MessageCodec.TryReadLootGained(invalid, 0, invalid.Length, out _));
            Assert.False(MessageCodec.TryReadLootGained(full, 0, full.Length - 1, out _));
            var extraByte = new byte[full.Length + 1];
            Array.Copy(full, extraByte, full.Length);
            Assert.False(MessageCodec.TryReadLootGained(extraByte, 0, extraByte.Length, out _));
            byte[] wrongType = new byte[10];
            wrongType[0] = 28;
            Assert.False(MessageCodec.TryReadLootGained(wrongType, 0, wrongType.Length, out _));

            byte[] invalidItemId = (byte[])full.Clone();
            invalidItemId[10] = 0;
            invalidItemId[11] = 0;
            Assert.False(MessageCodec.TryReadLootGained(invalidItemId, 0, invalidItemId.Length, out _));
            byte[] invalidItemCount = (byte[])full.Clone();
            invalidItemCount[12] = 0;
            invalidItemCount[13] = 0;
            Assert.False(MessageCodec.TryReadLootGained(invalidItemCount, 0, invalidItemCount.Length, out _));
            byte[] invalidFlags = (byte[])full.Clone();
            invalidFlags[14] = 2;
            Assert.False(MessageCodec.TryReadLootGained(invalidFlags, 0, invalidFlags.Length, out _));

            byte[] invalidGold = (byte[])empty.Clone();
            invalidGold[5] = 0x01;
            invalidGold[6] = 0x00;
            invalidGold[7] = 0xCA;
            invalidGold[8] = 0x9A;
            Assert.False(MessageCodec.TryReadLootGained(invalidGold, 0, invalidGold.Length, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeLootGained(
                new LootGainedMessage(0, 0, new LootItemEntry[33])));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeLootGained(
                new LootGainedMessage(0, 0, new[] { new LootItemEntry(0, 1, false) })));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeLootGained(
                new LootGainedMessage(0, ProtocolConstants.MaxGold + 1, Array.Empty<LootItemEntry>())));
        }

        [Fact]
        public void Inventory_Rechaza_Oro_Que_Supera_El_Maximo()
        {
            Assert.Throws<ArgumentException>(() => MessageCodec.EncodeInventory(new InventoryMessage(
                0, Array.Empty<ushort>(), Array.Empty<ushort>(), new ushort[ProtocolConstants.EquipSlotCount],
                ProtocolConstants.MaxGold + 1)));

            byte[] bytes = MessageCodec.EncodeInventory(new InventoryMessage(
                0, Array.Empty<ushort>(), Array.Empty<ushort>(), new ushort[ProtocolConstants.EquipSlotCount]));
            bytes[bytes.Length - 4] = 1;
            bytes[bytes.Length - 3] = 0;
            bytes[bytes.Length - 2] = 0xCA;
            bytes[bytes.Length - 1] = 0x9A;
            Assert.False(MessageCodec.TryReadInventory(bytes, 0, bytes.Length, out _));
        }

        [Fact]
        public void Equip_Unequip_E_ItemCatalog_Van_Y_Vuelven_Igual()
        {
            byte[] equip = MessageCodec.EncodeEquip(new EquipMessage(19));
            Assert.True(MessageCodec.TryReadEquip(equip, 0, equip.Length, out EquipMessage equipRead));
            Assert.Equal((byte)19, equipRead.Slot);
            Assert.Equal(2, equip.Length);

            byte[] unequip = MessageCodec.EncodeUnequip(new UnequipMessage(0, byte.MaxValue));
            Assert.True(MessageCodec.TryReadUnequip(unequip, 0, unequip.Length, out _));
            Assert.Equal(3, unequip.Length);

            var catalog = new ItemCatalogMessage(
                1.5f,
                10,
                1.2f,
                0.8f,
                new[]
                {
                    new ItemCatalogEntry(3001, ItemKind.Material, 99, 0, 0, 0, 0, "Colmillo"),
                    new ItemCatalogEntry(3002, ItemKind.Potion, 10, 30, 0, 0, 0, "Poción"),
                    new ItemCatalogEntry(3101, ItemKind.Weapon, 1, 0, 14, 1.2f, 0.8f, "Espada corta", 0, weaponType: "Lamina"),
                });

            byte[] bytes = MessageCodec.EncodeItemCatalog(catalog);
            Assert.True(MessageCodec.TryReadItemCatalog(bytes, 0, bytes.Length, out ItemCatalogMessage decoded));
            Assert.Equal(catalog.PickupRange, decoded.PickupRange);
            Assert.Equal(catalog.FistDamage, decoded.FistDamage);
            Assert.Equal(catalog.FistRange, decoded.FistRange);
            Assert.Equal(catalog.FistCooldownSeconds, decoded.FistCooldownSeconds);
            Assert.Equal(catalog.Items, decoded.Items);
            Assert.Equal("Poción", decoded.Items[1].Name);
            Assert.Equal(ItemKind.Weapon, decoded.Items[2].Kind);
        }

        [Fact]
        public void Equip_Unequip_E_ItemCatalog_Rechazan_Longitudes_Kind_Y_Cuenta_Invalidos()
        {
            byte[] equip = MessageCodec.EncodeEquip(new EquipMessage(1));
            byte[] unequip = MessageCodec.EncodeUnequip(new UnequipMessage(0, byte.MaxValue));
            Assert.False(MessageCodec.TryReadEquip(equip, 0, equip.Length - 1, out _));
            Assert.False(MessageCodec.TryReadEquip(equip, 0, equip.Length + 1, out _));
            Assert.False(MessageCodec.TryReadUnequip(unequip, 0, 0, out _));

            byte[] catalog = MessageCodec.EncodeItemCatalog(new ItemCatalogMessage(
                1f, 10, 1.2f, 0.8f,
                new[] { new ItemCatalogEntry(3101, ItemKind.Weapon, 1, 0, 14, 1.2f, 0.8f, "Espada", 0, weaponType: "Lamina") }));
            Assert.False(MessageCodec.TryReadItemCatalog(catalog, 0, catalog.Length - 1, out _));
            Assert.False(MessageCodec.TryReadItemCatalog(catalog, 0, catalog.Length + 1, out _));

            byte[] unknownKind = (byte[])catalog.Clone();
            unknownKind[21] = 99;
            Assert.False(MessageCodec.TryReadItemCatalog(unknownKind, 0, unknownKind.Length, out _));

            byte[] tooMany = new byte[19];
            tooMany[0] = (byte)MessageType.ItemCatalog;
            tooMany[17] = 1;
            tooMany[18] = 2;
            Assert.False(MessageCodec.TryReadItemCatalog(tooMany, 0, tooMany.Length, out _));

            byte[] oversized = new byte[ProtocolConstants.MaxMessageSize + 1];
            oversized[0] = (byte)MessageType.ItemCatalog;
            Assert.False(MessageCodec.TryReadItemCatalog(oversized, 0, oversized.Length, out _));

            Assert.Throws<ArgumentException>(() => MessageCodec.EncodeItemCatalog(
                new ItemCatalogMessage(1, 10, 1, 1,
                    new[] { new ItemCatalogEntry(1, ItemKind.Material, 1, 0, 0, 0, 0, new string('x', 49)) })));
        }

        [Fact]
        public void ItemCatalog_Se_Divide_En_Trozos_Que_Conservan_Todo_El_Orden()
        {
            var entries = new ItemCatalogEntry[300];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new ItemCatalogEntry(
                    (ushort)(i + 1), ItemKind.Material, 99, 0, 0, 0, 0, new string('x', 48));
            }

            byte[][] chunks = MessageCodec.EncodeItemCatalogChunks(new ItemCatalogMessage(
                1.5f, 10, 1.2f, 0.8f, entries));

            Assert.True(chunks.Length > 1);
            int decodedCount = 0;
            for (int i = 0; i < chunks.Length; i++)
            {
                Assert.True(chunks[i].Length <= ProtocolConstants.MaxMessageSize);
                Assert.True(MessageCodec.TryReadItemCatalog(
                    chunks[i], 0, chunks[i].Length, out ItemCatalogMessage decoded));
                Assert.Equal(1.5f, decoded.PickupRange);
                Assert.Equal(10u, decoded.FistDamage);
                for (int j = 0; j < decoded.Items.Length; j++)
                {
                    Assert.Equal((ushort)(decodedCount + 1), decoded.Items[j].Id);
                    Assert.Equal(new string('x', 48), decoded.Items[j].Name);
                    decodedCount++;
                }
            }

            Assert.Equal(entries.Length, decodedCount);
        }

        [Fact]
        public void ItemCatalog_Vacio_Genera_Un_Trozo_Vacio()
        {
            byte[][] chunks = MessageCodec.EncodeItemCatalogChunks(new ItemCatalogMessage(
                1.5f, 10, 1.2f, 0.8f, Array.Empty<ItemCatalogEntry>()));

            byte[] chunk = Assert.Single(chunks);
            Assert.True(MessageCodec.TryReadItemCatalog(chunk, 0, chunk.Length, out ItemCatalogMessage decoded));

            Assert.Empty(decoded.Items);
            Assert.Equal(19, chunks[0].Length);
        }

        [Fact]
        public void Los_Mensajes_De_Objeto_Rechazan_Longitudes_Y_Huecos_Invalidos()
        {
            byte[] pickup = MessageCodec.EncodePickUp(new PickUpMessage(7));
            byte[] use = MessageCodec.EncodeUseItem(new UseItemMessage(0));
            Assert.False(MessageCodec.TryReadPickUp(pickup, 0, pickup.Length - 1, out _));
            Assert.False(MessageCodec.TryReadPickUp(pickup, 0, pickup.Length + 1, out _));
            Assert.False(MessageCodec.TryReadUseItem(use, 0, use.Length - 1, out _));
            Assert.False(MessageCodec.TryReadUseItem(use, 0, use.Length + 1, out _));

            byte[] empty = MessageCodec.EncodeInventory(new InventoryMessage(1, new ushort[] { 0 }, new ushort[] { 0 }));
            Assert.False(MessageCodec.TryReadInventory(empty, 0, empty.Length - 1, out _));
            Assert.False(MessageCodec.TryReadInventory(empty, 0, empty.Length + 1, out _));
            empty[2] = 1;
            Assert.False(MessageCodec.TryReadInventory(empty, 0, empty.Length, out _));

            byte[] tooManySlots = { (byte)MessageType.Inventory, 61 };
            Assert.False(MessageCodec.TryReadInventory(tooManySlots, 0, tooManySlots.Length, out _));

            byte[] itemWithoutCount = MessageCodec.EncodeInventory(
                new InventoryMessage(1, new ushort[] { 3001 }, new ushort[] { 1 }));
            itemWithoutCount[4] = 0;
            itemWithoutCount[5] = 0;
            Assert.False(MessageCodec.TryReadInventory(itemWithoutCount, 0, itemWithoutCount.Length, out _));

            Assert.Throws<ArgumentException>(() => MessageCodec.EncodeInventory(
                new InventoryMessage(1, new ushort[] { 0 }, new ushort[] { 1 })));
        }

        [Fact]
        public void El_Inventario_Rechaza_Longitud_Sin_El_Campo_De_Equipo()
        {
            byte[] bytes = MessageCodec.EncodeInventory(new InventoryMessage(1, new ushort[] { 0 }, new ushort[] { 0 }));
            Assert.False(MessageCodec.TryReadInventory(bytes, 0, bytes.Length - 1, out _));
            Assert.False(MessageCodec.TryReadInventory(bytes, 0, bytes.Length + 1, out _));
        }

        [Fact]
        public void Un_Ataque_Truncado_O_Con_Bytes_De_Mas_Se_Rechaza()
        {
            byte[] bytes = MessageCodec.EncodeAttack(new AttackMessage(7));

            Assert.False(MessageCodec.TryReadAttack(bytes, 0, bytes.Length - 1, out _));
            Assert.False(MessageCodec.TryReadAttack(bytes, 0, 0, out _));

            var longer = new byte[bytes.Length + 1];
            Array.Copy(bytes, longer, bytes.Length);
            Assert.False(MessageCodec.TryReadAttack(longer, 0, longer.Length, out _));
        }

        [Fact]
        public void Un_Snapshot_Con_Direccion_Invalida_Se_Rechaza()
        {
            byte[] bytes = MessageCodec.EncodeSnapshot(
                new SnapshotMessage(1u, 0u, new[] { new EntityState(1, 0, 0, 0, 0, EntityKind.Player, 100, EntityAction.None) }));

            bytes[19] = 5;

            Assert.False(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out _));
        }

        [Fact]
        public void Todos_Los_Valores_Definidos_De_Las_Enumeraciones_Viajan()
        {
            // Si se añade un valor a cualquiera de las dos enumeraciones y no se actualiza el
            // límite del códec, esta prueba falla y avisa del olvido.
            foreach (EntityKind kind in Enum.GetValues(typeof(EntityKind)))
            {
                byte[] snapshot = MessageCodec.EncodeSnapshot(
                    new SnapshotMessage(1u, 0u, new[] { new EntityState(1, 0, 0, 0, 0, kind, 100, EntityAction.None) }));

                Assert.True(
                    MessageCodec.TryReadSnapshot(snapshot, 0, snapshot.Length, out _),
                    "El códec rechaza el EntityKind definido " + kind + ".");
            }

            foreach (EntityAction action in Enum.GetValues(typeof(EntityAction)))
            {
                byte[] snapshot = MessageCodec.EncodeSnapshot(
                    new SnapshotMessage(1u, 0u, new[] { new EntityState(1, 0, 0, 0, 0, EntityKind.Npc, 100, action) }));

                Assert.True(
                    MessageCodec.TryReadSnapshot(snapshot, 0, snapshot.Length, out _),
                    "El códec rechaza el EntityAction definido " + action + ".");
            }

            foreach (RejectReason reason in Enum.GetValues(typeof(RejectReason)))
            {
                byte[] reject = MessageCodec.EncodeReject(new RejectMessage(reason));

                Assert.True(
                    MessageCodec.TryReadReject(reject, 0, reject.Length, out _),
                    "El códec rechaza el RejectReason definido " + reason + ".");
            }
        }

        [Fact]
        public void Un_Snapshot_Con_Cuenta_Incoherente_Se_Rechaza()
        {
            byte[] bytes = MessageCodec.EncodeSnapshot(
                new SnapshotMessage(1u, 0u, new[] { new EntityState(1, 0, 0, 0, 0, EntityKind.Player, 100, EntityAction.None) }));

            // La cuenta va tras el tipo (1) y los dos uint (8).
            bytes[9] = 2;
            bytes[10] = 0;

            Assert.False(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out _));
        }

        [Fact]
        public void Las_Posiciones_Se_Cuantizan_Y_Se_Saturan()
        {
            Assert.Equal((short)0, ProtocolConstants.QuantizePosition(0f));
            Assert.Equal((short)64, ProtocolConstants.QuantizePosition(1f));
            Assert.Equal((short)96, ProtocolConstants.QuantizePosition(1.5f));
            Assert.Equal((short)-38, ProtocolConstants.QuantizePosition(-0.6f));
            Assert.Equal(1f, ProtocolConstants.DequantizePosition(64));

            // Un valor absurdo se recorta al rango del short, no da la vuelta.
            Assert.Equal(short.MaxValue, ProtocolConstants.QuantizePosition(100000f));
            Assert.Equal(short.MinValue, ProtocolConstants.QuantizePosition(-100000f));
            Assert.Equal((short)0, ProtocolConstants.QuantizePosition(float.NaN));
        }

        [Fact]
        public void Un_Snapshot_Con_Demasiadas_Entidades_No_Se_Puede_Codificar()
        {
            var entities = new EntityState[ProtocolConstants.MaxEntitiesPerSnapshot + 1];

            Assert.Throws<ArgumentOutOfRangeException>(
                () => MessageCodec.EncodeSnapshot(new SnapshotMessage(1u, 0u, entities)));
        }

        [Fact]
        public void El_Snapshot_Maximo_Cabe_En_El_Tope_De_Mensaje()
        {
            var entities = new EntityState[ProtocolConstants.MaxEntitiesPerSnapshot];

            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = new EntityState(i, 0, 0, 0, 0, EntityKind.Npc, 100, EntityAction.None);
            }

            byte[] bytes = MessageCodec.EncodeSnapshot(new SnapshotMessage(1u, 0u, entities));

            Assert.True(bytes.Length <= ProtocolConstants.MaxMessageSize);
            Assert.True(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out SnapshotMessage decoded));
            Assert.Equal(ProtocolConstants.MaxEntitiesPerSnapshot, decoded.Entities.Length);
        }

        [Fact]
        public void El_Snapshot_Maximo_Cabe_En_Un_Paquete_Sin_Fragmentos_De_LiteNetLib()
        {
            var entities = new EntityState[ProtocolConstants.MaxEntitiesPerSnapshot];

            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = new EntityState(i, 0, 0, 0, 0, EntityKind.Npc, 100, EntityAction.None);
            }

            byte[] bytes = MessageCodec.EncodeSnapshot(new SnapshotMessage(1u, 0u, entities));

            // El canal secuenciado no fragmenta: LiteNetLib rechaza con TooBigPacketException todo
            // paquete de más de 1020 B. Si este número sube, la carga con muchos PNJ tumba el hilo
            // de red, no el tick.
            Assert.True(
                bytes.Length <= 1020,
                "Un snapshot de " + entities.Length + " entidades ocupa " + bytes.Length + " B.");
        }

        [Fact]
        public void Una_Huella_De_Tamano_Incorrecto_No_Se_Puede_Codificar()
        {
            Assert.Throws<ArgumentException>(
                () => MessageCodec.EncodeHello(new HelloMessage(1, 20, new byte[3], 1u)));
        }

        [Fact]
        public void Progress_Va_Y_Vuelve_Igual_Y_Rechaza_Longitudes_Erroneas()
        {
            var original = new ProgressMessage(4, 275, 340, 145);
            byte[] bytes = MessageCodec.EncodeProgress(original);

            Assert.Equal(14, bytes.Length);
            Assert.Equal((byte)MessageType.Progress, bytes[0]);
            Assert.True(MessageCodec.TryReadProgress(bytes, 0, bytes.Length, out ProgressMessage decoded));
            Assert.Equal(original.Level, decoded.Level);
            Assert.Equal(original.Experience, decoded.Experience);
            Assert.Equal(original.ExperienceToNext, decoded.ExperienceToNext);
            Assert.Equal(original.MaxHealth, decoded.MaxHealth);
            Assert.False(MessageCodec.TryReadProgress(bytes, 0, bytes.Length - 1, out _));
            Assert.False(MessageCodec.TryReadProgress(bytes, 0, bytes.Length + 1, out _));
        }

        [Fact]
        public void AllocatePoints_Va_Y_Vuelve_Y_Usa_Cuatro_Bytes_U8()
        {
            var original = new AllocatePointsMessage(1, 2, 3, 4);
            byte[] bytes = MessageCodec.EncodeAllocatePoints(original);

            Assert.Equal(new byte[] { 9, 1, 2, 3, 4 }, bytes);
            Assert.True(MessageCodec.TryReadAllocatePoints(bytes, 0, bytes.Length, out AllocatePointsMessage decoded));
            Assert.Equal(original.Str, decoded.Str);
            Assert.Equal(original.Dex, decoded.Dex);
            Assert.Equal(original.Int, decoded.Int);
            Assert.Equal(original.Vit, decoded.Vit);
            Assert.False(MessageCodec.TryReadAllocatePoints(bytes, 0, bytes.Length - 1, out _));
            Assert.False(MessageCodec.TryReadAllocatePoints(bytes, 0, bytes.Length + 1, out _));
        }

        [Fact]
        public void ResetPoints_Va_Y_Vuelve_Sin_Cuerpo()
        {
            byte[] bytes = MessageCodec.EncodeResetPoints(new ResetPointsMessage());

            Assert.Equal(new byte[] { 19 }, bytes);
            Assert.True(MessageCodec.TryReadResetPoints(bytes, 0, bytes.Length, out _));
            Assert.False(MessageCodec.TryReadResetPoints(new byte[] { 19, 0 }, 0, 2, out _));
            Assert.False(MessageCodec.TryReadResetPoints(Array.Empty<byte>(), 0, 0, out _));
        }

        [Fact]
        public void Stats_Va_Y_Vuelve_Con_Todos_Los_Campos_Y_Rechaza_Datos_Invalidos()
        {
            var original = new StatsMessage(
                4, 25, 11, 12, 13, 14, 1, 2, 3, 4, 65, 123456, 567, 1, 321, 654, 987, 1234, 0);
            byte[] bytes = MessageCodec.EncodeStats(original);

            Assert.Equal(37, bytes.Length);
            Assert.Equal((byte)18, bytes[0]);
            Assert.True(MessageCodec.TryReadStats(bytes, 0, bytes.Length, out StatsMessage decoded));
            Assert.Equal(original.ClassId, decoded.ClassId);
            Assert.Equal(original.Level, decoded.Level);
            Assert.Equal(original.Str, decoded.Str);
            Assert.Equal(original.Dex, decoded.Dex);
            Assert.Equal(original.Int, decoded.Int);
            Assert.Equal(original.Vit, decoded.Vit);
            Assert.Equal(original.AllocStr, decoded.AllocStr);
            Assert.Equal(original.AllocDex, decoded.AllocDex);
            Assert.Equal(original.AllocInt, decoded.AllocInt);
            Assert.Equal(original.AllocVit, decoded.AllocVit);
            Assert.Equal(original.FreePoints, decoded.FreePoints);
            Assert.Equal(original.MaxHealth, decoded.MaxHealth);
            Assert.Equal(original.AttackDamage, decoded.AttackDamage);
            Assert.Equal(original.DamageType, decoded.DamageType);
            Assert.Equal(original.Defense, decoded.Defense);
            Assert.Equal(original.MagicResist, decoded.MagicResist);
            Assert.Equal(original.Resource, decoded.Resource);
            Assert.Equal(original.MaxResource, decoded.MaxResource);
            Assert.Equal(original.FreeReset, decoded.FreeReset);
            Assert.False(MessageCodec.TryReadStats(bytes, 0, bytes.Length - 1, out _));
            Assert.False(MessageCodec.TryReadStats(bytes, 0, bytes.Length + 1, out _));

            byte[] unknownDamage = (byte[])bytes.Clone();
            unknownDamage[27] = 2;
            Assert.False(MessageCodec.TryReadStats(unknownDamage, 0, unknownDamage.Length, out _));

            byte[] invalidResetFlag = (byte[])bytes.Clone();
            invalidResetFlag[36] = 2;
            Assert.False(MessageCodec.TryReadStats(invalidResetFlag, 0, invalidResetFlag.Length, out _));

            byte[] zeroClass = (byte[])bytes.Clone();
            zeroClass[1] = 0;
            Assert.False(MessageCodec.TryReadStats(zeroClass, 0, zeroClass.Length, out _));

            byte[] resourceAboveMaximum = (byte[])bytes.Clone();
            resourceAboveMaximum[32] = 0xD3;
            resourceAboveMaximum[33] = 0x04;
            Assert.False(MessageCodec.TryReadStats(resourceAboveMaximum, 0, resourceAboveMaximum.Length, out _));
        }

        [Fact]
        public void ChatSend_Va_Y_Vuelve_Y_Rechaza_Texto_Excesivo_O_Longitudes_Invalidas()
        {
            var original = new ChatSendMessage("  Hola, mundo  ");
            byte[] bytes = MessageCodec.EncodeChatSend(original);

            Assert.Equal((byte)8, bytes[0]);
            Assert.True(MessageCodec.TryReadChatSend(bytes, 0, bytes.Length, out ChatSendMessage decoded));
            Assert.Equal(original.Text, decoded.Text);
            Assert.False(MessageCodec.TryReadChatSend(bytes, 0, bytes.Length - 1, out _));

            byte[] oversized = { (byte)MessageType.ChatSend, 121, 0 };
            Array.Resize(ref oversized, 3 + 121);
            Assert.False(MessageCodec.TryReadChatSend(oversized, 0, oversized.Length, out _));

            byte[] brokenUtf8 = { (byte)MessageType.ChatSend, 1, 0, 0xFF };
            Assert.False(MessageCodec.TryReadChatSend(brokenUtf8, 0, brokenUtf8.Length, out _));

            Assert.Throws<ArgumentException>(() => MessageCodec.EncodeChatSend(
                new ChatSendMessage(new string('a', ProtocolConstants.MaxChatBytes + 1))));
        }

        [Fact]
        public void Chat_Va_Y_Vuelve_Y_Rechaza_Nombres_Textos_Y_Longitudes_Invalidos()
        {
            var original = new ChatMessage(1234, "Personaje", "¡Hola!");
            byte[] bytes = MessageCodec.EncodeChat(original);

            Assert.Equal((byte)17, bytes[0]);
            Assert.True(MessageCodec.TryReadChat(bytes, 0, bytes.Length, out ChatMessage decoded));
            Assert.Equal(original.SenderEntityId, decoded.SenderEntityId);
            Assert.Equal(original.SenderName, decoded.SenderName);
            Assert.Equal(original.Text, decoded.Text);
            Assert.False(MessageCodec.TryReadChat(bytes, 0, bytes.Length - 1, out _));

            Assert.Throws<ArgumentException>(() => MessageCodec.EncodeChat(
                new ChatMessage(1, new string('n', ProtocolConstants.MaxChatNameBytes + 1), "hola")));
            Assert.Throws<ArgumentException>(() => MessageCodec.EncodeChat(
                new ChatMessage(1, "nombre", new string('t', ProtocolConstants.MaxChatBytes + 1))));

            byte[] oversizedName = new byte[1 + 4 + 2 + 25 + 2];
            oversizedName[0] = (byte)MessageType.Chat;
            oversizedName[1] = 1;
            oversizedName[5] = 25;
            for (int i = 0; i < 25; i++)
            {
                oversizedName[7 + i] = (byte)'n';
            }
            Assert.False(MessageCodec.TryReadChat(oversizedName, 0, oversizedName.Length, out _));
            byte[] oversizedText = new byte[1 + 4 + 2 + 1 + 2 + 121];
            oversizedText[0] = (byte)MessageType.Chat;
            oversizedText[1] = 1;
            oversizedText[5] = 1;
            oversizedText[7] = (byte)'n';
            oversizedText[8] = 121;
            Assert.False(MessageCodec.TryReadChat(oversizedText, 0, oversizedText.Length, out _));
        }

        [Fact]
        public void ZoneList_Admite_Cero_Y_El_Maximo_Y_Rechaza_Campos_Invalidos()
        {
            byte[] empty = MessageCodec.EncodeZoneList(new ZoneListMessage(Array.Empty<ZoneInfo>()));
            Assert.Equal(2, empty.Length);
            Assert.True(MessageCodec.TryReadZoneList(empty, 0, empty.Length, out ZoneListMessage emptyRead));
            Assert.Empty(emptyRead.Zones);

            var zones = new ZoneInfo[ProtocolConstants.MaxZones];
            for (int i = 0; i < zones.Length; i++)
            {
                var rects = new ZoneRect[4];
                for (int r = 0; r < rects.Length; r++)
                {
                    rects[r] = new ZoneRect((short)(-100 + r), (short)(-200 - r), 1, 2);
                }

                zones[i] = new ZoneInfo((ushort)(i + 1), i % 2 == 0 ? ZoneKind.Safe : ZoneKind.Pvp, rects);
            }

            byte[] full = MessageCodec.EncodeZoneList(new ZoneListMessage(zones));
            Assert.Equal(1154, full.Length);
            Assert.True(MessageCodec.TryReadZoneList(full, 0, full.Length, out ZoneListMessage decoded));
            Assert.Equal(zones.Length, decoded.Zones.Length);
            for (int i = 0; i < zones.Length; i++)
            {
                Assert.Equal(zones[i].Id, decoded.Zones[i].Id);
                Assert.Equal(zones[i].Kind, decoded.Zones[i].Kind);
                Assert.Equal(zones[i].Rects.Length, decoded.Zones[i].Rects.Length);
                for (int r = 0; r < zones[i].Rects.Length; r++)
                {
                    Assert.Equal(zones[i].Rects[r].X, decoded.Zones[i].Rects[r].X);
                    Assert.Equal(zones[i].Rects[r].Y, decoded.Zones[i].Rects[r].Y);
                    Assert.Equal(zones[i].Rects[r].Width, decoded.Zones[i].Rects[r].Width);
                    Assert.Equal(zones[i].Rects[r].Height, decoded.Zones[i].Rects[r].Height);
                }
            }
            Assert.False(MessageCodec.TryReadZoneList(full, 0, full.Length - 1, out _));
            Assert.False(MessageCodec.TryReadZoneList(full, 0, full.Length + 1, out _));
            Assert.False(MessageCodec.TryReadZoneList(new byte[] { 29, 0 }, 0, 2, out _));

            byte[][] malformed =
            {
                new byte[] { 30, 33 },
                new byte[] { 30, 1, 1, 0, 1, 0 },
                new byte[] { 30, 1, 1, 0, 1, 17 },
                new byte[] { 30, 1, 0, 0, 0, 0 },
                new byte[] { 30, 1, 1, 0, 0, 0 },
                new byte[] { 30, 1, 1, 0, 3, 0 },
                new byte[] { 30, 1, 1, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 },
            };
            foreach (byte[] bytes in malformed)
            {
                Assert.False(MessageCodec.TryReadZoneList(bytes, 0, bytes.Length, out _));
            }

            byte[] tooManyRectangles = new byte[2 + (9 * 4) + (129 * 8)];
            tooManyRectangles[0] = (byte)MessageType.ZoneList;
            tooManyRectangles[1] = 9;
            int at = 2;
            for (int i = 0; i < 9; i++)
            {
                int count = i < 8 ? 16 : 1;
                tooManyRectangles[at++] = (byte)(i + 1);
                tooManyRectangles[at++] = 0;
                tooManyRectangles[at++] = (byte)ZoneKind.Safe;
                tooManyRectangles[at++] = (byte)count;
                for (int r = 0; r < count; r++)
                {
                    tooManyRectangles[at++] = 0;
                    tooManyRectangles[at++] = 0;
                    tooManyRectangles[at++] = 0;
                    tooManyRectangles[at++] = 0;
                    tooManyRectangles[at++] = 1;
                    tooManyRectangles[at++] = 0;
                    tooManyRectangles[at++] = 1;
                    tooManyRectangles[at++] = 0;
                }
            }
            Assert.False(MessageCodec.TryReadZoneList(tooManyRectangles, 0, tooManyRectangles.Length, out _));

            byte[] negativeWidth = (byte[])full.Clone();
            negativeWidth[10] = 0xFF;
            negativeWidth[11] = 0xFF;
            Assert.False(MessageCodec.TryReadZoneList(negativeWidth, 0, negativeWidth.Length, out _));
            byte[] negativeHeight = (byte[])full.Clone();
            negativeHeight[12] = 0xFF;
            negativeHeight[13] = 0xFF;
            Assert.False(MessageCodec.TryReadZoneList(negativeHeight, 0, negativeHeight.Length, out _));

            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeZoneList(
                new ZoneListMessage(new ZoneInfo[ProtocolConstants.MaxZones + 1])));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeZoneList(
                new ZoneListMessage(new[] { new ZoneInfo(1, ZoneKind.Safe, Array.Empty<ZoneRect>()) })));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeZoneList(
                new ZoneListMessage(new[] { new ZoneInfo(1, ZoneKind.Safe, new ZoneRect[17]) })));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeZoneList(
                new ZoneListMessage(new[] { new ZoneInfo(0, ZoneKind.Safe, new[] { new ZoneRect(0, 0, 1, 1) }) })));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeZoneList(
                new ZoneListMessage(new[] { new ZoneInfo(1, ZoneKind.Normal, new[] { new ZoneRect(0, 0, 1, 1) }) })));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeZoneList(
                new ZoneListMessage(new[] { new ZoneInfo(1, ZoneKind.Pvp, new[] { new ZoneRect(0, 0, 0, 1) }) })));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeZoneList(
                new ZoneListMessage(new[] { new ZoneInfo(1, (ZoneKind)3, new[] { new ZoneRect(0, 0, 1, 1) }) })));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeZoneList(
                new ZoneListMessage(new[] { new ZoneInfo(1, ZoneKind.Pvp, new[] { new ZoneRect(0, 0, -1, 1) }) })));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeZoneList(
                new ZoneListMessage(new[] { new ZoneInfo(1, ZoneKind.Pvp, new[] { new ZoneRect(0, 0, 1, 0) }) })));

            var encodedTooManyRects = new ZoneInfo[9];
            for (int i = 0; i < encodedTooManyRects.Length; i++)
            {
                int count = i < 8 ? 16 : 1;
                var rects = new ZoneRect[count];
                for (int r = 0; r < count; r++) rects[r] = new ZoneRect(0, 0, 1, 1);
                encodedTooManyRects[i] = new ZoneInfo((ushort)(i + 1), ZoneKind.Safe, rects);
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeZoneList(
                new ZoneListMessage(encodedTooManyRects)));
        }

        [Fact]
        public void ZoneChanged_Valida_Identificador_Y_Tipo()
        {
            foreach (ZoneChangedMessage original in new[]
            {
                new ZoneChangedMessage(1, ZoneKind.Safe),
                new ZoneChangedMessage(2, ZoneKind.Pvp),
                new ZoneChangedMessage(0, ZoneKind.Normal),
            })
            {
                byte[] bytes = MessageCodec.EncodeZoneChanged(original);
                Assert.Equal(4, bytes.Length);
                Assert.True(MessageCodec.TryReadZoneChanged(bytes, 0, bytes.Length, out ZoneChangedMessage decoded));
                Assert.Equal(original.ZoneId, decoded.ZoneId);
                Assert.Equal(original.Kind, decoded.Kind);
            }

            foreach (ZoneChangedMessage invalid in new[]
            {
                new ZoneChangedMessage(0, ZoneKind.Safe),
                new ZoneChangedMessage(1, ZoneKind.Normal),
                new ZoneChangedMessage(1, (ZoneKind)3),
            })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeZoneChanged(invalid));
            }

            Assert.False(MessageCodec.TryReadZoneChanged(new byte[] { 31, 1, 0 }, 0, 3, out _));
            Assert.False(MessageCodec.TryReadZoneChanged(new byte[] { 31, 1, 0, 1, 0 }, 0, 5, out _));
            Assert.False(MessageCodec.TryReadZoneChanged(new byte[] { 30, 1, 0, 1 }, 0, 4, out _));
            Assert.False(MessageCodec.TryReadZoneChanged(new byte[] { 31, 0, 0, 1 }, 0, 4, out _));
            Assert.False(MessageCodec.TryReadZoneChanged(new byte[] { 31, 1, 0, 0 }, 0, 4, out _));
        }
    }
}
