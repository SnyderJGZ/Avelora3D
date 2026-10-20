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

        [Fact]
        public void El_Saludo_Va_Y_Vuelve_Igual()
        {
            var original = new HelloMessage(
                3,
                20,
                NewHash(7),
                123456u,
                "cuenta",
                "contraseña",
                "personaje",
                "invitacion");

            byte[] bytes = MessageCodec.EncodeHello(original);

            Assert.True(MessageCodec.TryReadHello(bytes, 0, bytes.Length, out HelloMessage decoded));
            Assert.True(bytes.Length > 41, "El saludo de la Etapa F lleva las cuatro cadenas.");
            Assert.Equal(original.ProtocolVersion, decoded.ProtocolVersion);
            Assert.Equal(original.TickRate, decoded.TickRate);
            Assert.Equal(original.ClientToken, decoded.ClientToken);
            Assert.Equal(original.MapHash, decoded.MapHash);
            Assert.Equal(original.AccountName, decoded.AccountName);
            Assert.Equal(original.Password, decoded.Password);
            Assert.Equal(original.CharacterName, decoded.CharacterName);
            Assert.Equal(original.InvitationCode, decoded.InvitationCode);
        }

        [Fact]
        public void Un_Saludo_Con_Una_Cadena_Demasiado_Larga_No_Se_Puede_Codificar()
        {
            var hello = new HelloMessage(
                2,
                20,
                NewHash(0),
                1u,
                new string('a', ProtocolConstants.MaxAccountNameBytes + 1),
                "avelora-dev",
                "dev",
                "avelora-dev");

            Assert.Throws<ArgumentException>(() => MessageCodec.EncodeHello(hello));
        }

        [Fact]
        public void Un_Saludo_Con_Bytes_De_Mas_Se_Rechaza()
        {
            byte[] bytes = MessageCodec.EncodeHello(new HelloMessage(
                2,
                20,
                NewHash(0),
                1u,
                "dev",
                "avelora-dev",
                "dev",
                "avelora-dev"));

            byte[] withExtra = new byte[bytes.Length + 1];
            Array.Copy(bytes, withExtra, bytes.Length);

            Assert.False(MessageCodec.TryReadHello(withExtra, 0, withExtra.Length, out _));
        }

        [Fact]
        public void La_Intencion_Va_Y_Vuelve_Igual()
        {
            var original = new MoveInputMessage(4294967290u, -1, 1);

            byte[] bytes = MessageCodec.EncodeMoveInput(original);

            Assert.True(MessageCodec.TryReadMoveInput(bytes, 0, bytes.Length, out MoveInputMessage decoded));
            Assert.Equal(7, bytes.Length);
            Assert.Equal(original.Sequence, decoded.Sequence);
            Assert.Equal(original.DirectionX, decoded.DirectionX);
            Assert.Equal(original.DirectionY, decoded.DirectionY);
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
                new EntityState(1, 64, -128, 1, 0, EntityKind.Player),
                new EntityState(9, -32768, 32767, -1, -1, EntityKind.Npc),
            };

            var original = new SnapshotMessage(1234u, 5678u, entities);

            byte[] bytes = MessageCodec.EncodeSnapshot(original);

            Assert.True(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out SnapshotMessage decoded));
            Assert.Equal(1 + 4 + 4 + 2 + (2 * 11), bytes.Length);
            Assert.Equal(original.ServerTick, decoded.ServerTick);
            Assert.Equal(original.LastProcessedInputSequence, decoded.LastProcessedInputSequence);
            Assert.Equal(2, decoded.Entities.Length);
            Assert.Equal(1, decoded.Entities[0].Id);
            Assert.Equal(64, decoded.Entities[0].X);
            Assert.Equal(-128, decoded.Entities[0].Y);
            Assert.Equal((sbyte)1, decoded.Entities[0].DirectionX);
            Assert.Equal(EntityKind.Player, decoded.Entities[0].Kind);
            Assert.Equal(9, decoded.Entities[1].Id);
            Assert.Equal(short.MinValue, decoded.Entities[1].X);
            Assert.Equal(short.MaxValue, decoded.Entities[1].Y);
            Assert.Equal(EntityKind.Npc, decoded.Entities[1].Kind);
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
                new SnapshotMessage(1u, 0u, new[] { new EntityState(1, 0, 0, 0, 0, EntityKind.Player) }));

            // Cabecera (1) + tick y secuencia (8) + cuenta (2) + id (4) + x e y (4).
            bytes[21] = 99;

            Assert.False(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out _));
        }

        [Fact]
        public void Un_Snapshot_Con_Direccion_Invalida_Se_Rechaza()
        {
            byte[] bytes = MessageCodec.EncodeSnapshot(
                new SnapshotMessage(1u, 0u, new[] { new EntityState(1, 0, 0, 0, 0, EntityKind.Player) }));

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
                    new SnapshotMessage(1u, 0u, new[] { new EntityState(1, 0, 0, 0, 0, kind) }));

                Assert.True(
                    MessageCodec.TryReadSnapshot(snapshot, 0, snapshot.Length, out _),
                    "El códec rechaza el EntityKind definido " + kind + ".");
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
                new SnapshotMessage(1u, 0u, new[] { new EntityState(1, 0, 0, 0, 0, EntityKind.Player) }));

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
                entities[i] = new EntityState(i, 0, 0, 0, 0, EntityKind.Npc);
            }

            byte[] bytes = MessageCodec.EncodeSnapshot(new SnapshotMessage(1u, 0u, entities));

            Assert.True(bytes.Length <= ProtocolConstants.MaxMessageSize);
            Assert.True(MessageCodec.TryReadSnapshot(bytes, 0, bytes.Length, out SnapshotMessage decoded));
            Assert.Equal(ProtocolConstants.MaxEntitiesPerSnapshot, decoded.Entities.Length);
        }

        [Fact]
        public void Una_Huella_De_Tamano_Incorrecto_No_Se_Puede_Codificar()
        {
            Assert.Throws<ArgumentException>(
                () => MessageCodec.EncodeHello(new HelloMessage(1, 20, new byte[3], 1u)));
        }
    }
}
