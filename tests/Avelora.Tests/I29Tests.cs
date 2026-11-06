using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    public sealed class I29Tests
    {
        private static string Data(string file) => Path.Combine(AppContext.BaseDirectory, file);

        private static ClassCatalog Classes() => ClassCatalog.Load(Data("classes.json"));

        private static Zone NewZone(ItemCatalog items, ClassCatalog? classes = null) => new Zone(
            1, MapCollisionGrid.AllWalkable(30, 20), new EntityStore(), new EntityIdAllocator(),
            new InputQueue(128), new MovementSystem(4f, SimulationConstants.FixedDt), 128, 4,
            new InterestSettings(30f, 8f, 0f), null, 12345, new AttackQueue(128),
            new CombatSettings(1f), items: items, itemActions: new ItemActionQueue(64),
            classes: classes ?? Classes());

        private static ItemType Armor(int id, string slot, int moveSpeed) => new ItemType
        {
            Id = id, Name = "Armadura de prueba", Kind = "armor", Slot = slot,
            MaxStack = 1, MoveSpeed = moveSpeed,
        };

        private static ItemCatalog Items(params ItemType[] items) =>
            new ItemCatalog { Items = new List<ItemType>(items) };

        private static PlayerEntity AddPlayer(Zone zone) => zone.SpawnPlayer(
            1, new Vec2(8, 10), new Vec2(0.4f, 0.4f), 8);

        private static string WriteTemp(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), "avelora-i29-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            return path;
        }

        [Theory]
        [InlineData(0, 115, 100)]
        [InlineData(5, 115, 105)]
        [InlineData(10, 115, 110)]
        [InlineData(15, 115, 115)]
        [InlineData(50, 115, 115)]
        public void MoveSpeedPercent_Aplica_El_Tope(int equipMoveSpeed, int moveCap, int expected) =>
            Assert.Equal(expected, CombatMath.MoveSpeedPercent(equipMoveSpeed, moveCap));

        [Fact]
        public void Botas_Y_Ralentizacion_Se_Combinan_En_La_Velocidad_Del_Jugador()
        {
            Zone zone = NewZone(Items(Armor(7001, "feet", 10)));
            PlayerEntity player = AddPlayer(zone);
            player.Equipment[(int)EquipSlot.Feet] = 7001;
            zone.RecalculateEquipmentStats(player);
            Assert.True(player.Statuses.Apply(StatusKind.Slow, 4008, player.Id, 40, 1000));

            Assert.Equal(110, player.MoveSpeedPercent);
            Assert.Equal(66, player.TotalSpeedPercent);
            Vec2 initial = player.Position;
            Assert.Equal(InputQueueResult.Accepted, zone.Inputs.TryEnqueue(new MoveIntent(player.Id, 1, 1, 0)));
            zone.Tick();

            Assert.InRange(Math.Abs((player.Position.X - initial.X) - (4f * 0.66f / 20f)), 0f, 0.0001f);
        }

        [Fact]
        public void Dos_Armaduras_Respetan_El_Tope_De_Velocidad()
        {
            Zone zone = NewZone(Items(Armor(7001, "feet", 10), Armor(7002, "head", 10)));
            PlayerEntity player = AddPlayer(zone);
            player.Equipment[(int)EquipSlot.Feet] = 7001;
            player.Equipment[(int)EquipSlot.Head] = 7002;
            zone.RecalculateEquipmentStats(player);

            Assert.Equal(115, player.MoveSpeedPercent);
        }

        [Fact]
        public void Sin_Equipo_Ni_Ralentizacion_Conserva_La_Velocidad_Base()
        {
            Zone zone = NewZone(Items());
            PlayerEntity player = AddPlayer(zone);
            Vec2 initial = player.Position;
            Assert.Equal(100, player.TotalSpeedPercent);
            Assert.Equal(InputQueueResult.Accepted, zone.Inputs.TryEnqueue(new MoveIntent(player.Id, 1, 1, 0)));
            zone.Tick();

            Assert.InRange(Math.Abs((player.Position.X - initial.X) - (4f / 20f)), 0f, 0.0001f);
        }

        [Fact]
        public void MoveSpeed_Se_Valida_En_Rango_Y_Solo_En_Armaduras()
        {
            foreach (int value in new[] { -1, 11 })
            {
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                    () => Items(Armor(7001, "feet", value)).Validate());
                Assert.Contains("moveSpeed", error.Message, StringComparison.OrdinalIgnoreCase);
            }

            var material = new ItemType
            {
                Id = 7001, Name = "Material", Kind = "material", MaxStack = 1, MoveSpeed = 5,
            };
            InvalidOperationException nonArmorError = Assert.Throws<InvalidOperationException>(
                () => Items(material).Validate());
            Assert.Contains("moveSpeed", nonArmorError.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(99)]
        [InlineData(201)]
        public void MoveCap_Fuera_De_Rango_Se_Rechaza(int moveCap)
        {
            string json = File.ReadAllText(Data("classes.json"));
            JsonObject catalog = JsonNode.Parse(json)!.AsObject();
            catalog["moveCap"] = moveCap;
            string path = WriteTemp(catalog.ToJsonString());
            try
            {
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                    () => ClassCatalog.Load(path));
                Assert.Contains("moveCap", error.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Classes_Sin_MoveCap_Usan_El_Valor_Neutral()
        {
            JsonObject catalog = JsonNode.Parse(File.ReadAllText(Data("classes.json")))!.AsObject();
            catalog.Remove("moveCap");
            string path = WriteTemp(catalog.ToJsonString());
            try
            {
                Assert.Equal(100, ClassCatalog.Load(path).MoveCap);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void StatusEffects_Acepta_Hasta_200_Por_Ciento_Sin_Cambiar_El_Formato()
        {
            var message = new StatusEffectsMessage(200,
                new[] { new StatusEffectEntry(StatusKind.Slow, 4008, 40, 40) });
            byte[] encoded = MessageCodec.EncodeStatusEffects(message);

            Assert.Equal(3 + 7 * message.Effects.Length, encoded.Length);
            Assert.True(MessageCodec.TryReadStatusEffects(encoded, 0, encoded.Length, out StatusEffectsMessage decoded));
            Assert.Equal((byte)200, decoded.SpeedPercent);
            Assert.Single(decoded.Effects);

            foreach (byte speedPercent in new byte[] { 0, 201 })
            {
                byte[] invalid = (byte[])encoded.Clone();
                invalid[1] = speedPercent;
                Assert.False(MessageCodec.TryReadStatusEffects(invalid, 0, invalid.Length, out _));
            }

            Assert.Equal((ushort)24, ProtocolConstants.Version);
        }

        [Fact]
        public void Datos_De_Velocidad_Coinciden_Con_La_Tarea()
        {
            ItemCatalog items = ItemCatalog.Load(Data("items.json"));
            ClassCatalog classes = Classes();
            NpcCatalog npcs = NpcCatalog.Load(Data("npcs.json"));

            Assert.Equal(5, items.Find(3306)!.MoveSpeed);
            Assert.Equal(115, classes.MoveCap);
            Assert.InRange(Math.Abs(npcs.Types.Find(type => type.Id == 2001)!.Speed - 3.4f), 0f, 0.001f);
            Assert.InRange(Math.Abs(npcs.Types.Find(type => type.Id == 2002)!.Speed - 3.0f), 0f, 0.001f);
        }
    }
}
