using Avelora.Shared;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    public sealed class I14ProtocolTests
    {
        [Fact]
        public void Huecos_De_Equipo_Mantienen_Los_Ids_De_Datos()
        {
            Assert.Equal((byte)0, (byte)EquipSlot.Weapon);
            Assert.Equal((byte)1, (byte)EquipSlot.Offhand);
            Assert.Equal((byte)2, (byte)EquipSlot.Head);
            Assert.Equal((byte)3, (byte)EquipSlot.Body);
            Assert.Equal((byte)4, (byte)EquipSlot.Legs);
            Assert.Equal((byte)5, (byte)EquipSlot.Hands);
            Assert.Equal((byte)6, (byte)EquipSlot.Feet);
            Assert.Equal((byte)7, (byte)EquipSlot.Ring);
            Assert.Equal((byte)8, (byte)EquipSlot.Neck);
            Assert.Equal((byte)9, (byte)EquipSlot.Accessory);
            Assert.Equal((ushort)27, ProtocolConstants.Version);
            Assert.Equal(10, ProtocolConstants.EquipSlotCount);
        }

        [Fact]
        public void Unequip_Valida_Hueco_Y_Destino_Y_Exige_Longitud_Exacta()
        {
            byte[] bytes = MessageCodec.EncodeUnequip(new UnequipMessage(9, 255));

            Assert.Equal(3, bytes.Length);
            Assert.True(MessageCodec.TryReadUnequip(bytes, 0, bytes.Length, out UnequipMessage decoded));
            Assert.Equal((byte)9, decoded.EquipSlot);
            Assert.Equal((byte)255, decoded.TargetSlot);
            Assert.False(MessageCodec.TryReadUnequip(bytes, 0, bytes.Length - 1, out _));
            Assert.False(MessageCodec.TryReadUnequip(bytes, 0, bytes.Length + 1, out _));

            bytes[1] = 10;
            Assert.False(MessageCodec.TryReadUnequip(bytes, 0, bytes.Length, out _));
            bytes[1] = 0;
            bytes[2] = 60;
            Assert.False(MessageCodec.TryReadUnequip(bytes, 0, bytes.Length, out _));
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                MessageCodec.EncodeUnequip(new UnequipMessage(10, 255)));
        }

        [Fact]
        public void Move_Y_Destroy_Van_Y_Vuelven_Y_Rechazan_Campos_Fuera_De_Rango()
        {
            byte[] move = MessageCodec.EncodeMoveItem(new MoveItemMessage(4, 59, 125));
            Assert.Equal(5, move.Length);
            Assert.True(MessageCodec.TryReadMoveItem(move, 0, move.Length, out MoveItemMessage moveRead));
            Assert.Equal((byte)4, moveRead.FromSlot);
            Assert.Equal((byte)59, moveRead.ToSlot);
            Assert.Equal((ushort)125, moveRead.Count);
            Assert.False(MessageCodec.TryReadMoveItem(move, 0, move.Length - 1, out _));
            move[2] = 60;
            Assert.False(MessageCodec.TryReadMoveItem(move, 0, move.Length, out _));

            byte[] destroy = MessageCodec.EncodeDestroyItem(new DestroyItemMessage(5, 0));
            Assert.True(MessageCodec.TryReadDestroyItem(destroy, 0, destroy.Length, out DestroyItemMessage destroyRead));
            Assert.Equal((byte)5, destroyRead.Slot);
            Assert.Equal((ushort)0, destroyRead.Count);
            Assert.False(MessageCodec.TryReadDestroyItem(destroy, 0, destroy.Length - 1, out _));
        }

        [Fact]
        public void Inventory_Lleva_Diez_Huecos_Equipados_Y_Valida_Su_Forma()
        {
            var equipment = new ushort[ProtocolConstants.EquipSlotCount];
            equipment[(int)EquipSlot.Head] = 3301;
            equipment[(int)EquipSlot.Weapon] = 3101;
            var message = new InventoryMessage(1, new ushort[] { 3001 }, new ushort[] { 2 }, equipment);
            byte[] bytes = MessageCodec.EncodeInventory(message);

            Assert.Equal(30, bytes.Length);
            Assert.True(MessageCodec.TryReadInventory(bytes, 0, bytes.Length, out InventoryMessage decoded));
            Assert.Equal(equipment, decoded.Equipment);
            Assert.Equal((ushort)3101, decoded.Equipment[(int)EquipSlot.Weapon]);
            Assert.False(MessageCodec.TryReadInventory(bytes, 0, bytes.Length - 1, out _));
            Assert.Throws<System.ArgumentException>(() => MessageCodec.EncodeInventory(
                new InventoryMessage(0, System.Array.Empty<ushort>(), System.Array.Empty<ushort>(), new ushort[9])));
        }

        [Fact]
        public void ItemCatalog_Transporta_Armaduras_Y_Bonus_Y_Rechaza_Kind_Desconocido()
        {
            var catalog = new ItemCatalogMessage(
                1.5f,
                10,
                1.2f,
                0.8f,
                new[]
                {
                    new ItemCatalogEntry(3305, ItemKind.Armor, 1, 0, 0, 0, 0, "Guantes", 5, 1, 0, 0, 1, 0, 0),
                    new ItemCatalogEntry(3101, ItemKind.Weapon, 1, 0, 14, 1.2f, 0.8f, "Espada", 0, 0, 0, 2, 0, 0, 0, "Lamina"),
                    new ItemCatalogEntry(3001, ItemKind.Material, 99, 0, 0, 0, 0, "Colmillo"),
                });

            byte[] bytes = MessageCodec.EncodeItemCatalog(catalog);
            Assert.True(MessageCodec.TryReadItemCatalog(bytes, 0, bytes.Length, out ItemCatalogMessage decoded));
            Assert.Equal(ItemKind.Armor, decoded.Items[0].Kind);
            Assert.Equal((byte)5, decoded.Items[0].EquipSlot);
            Assert.Equal((ushort)1, decoded.Items[0].Defense);
            Assert.Equal((byte)1, decoded.Items[0].Dex);
            Assert.Equal("Lamina", decoded.Items[1].WeaponType);
            Assert.False(decoded.Items[1].TwoHanded);
            Assert.Equal((byte)1, decoded.Items[1].RequiredLevel);
            Assert.True(decoded.Items[1].UsableByClass);
            Assert.Equal((byte)2, decoded.Items[1].Str);
            Assert.Equal(19 + 3 * (32 + 3 + 2) + 7 + 6 + 8 + 6, bytes.Length);

            bytes[21] = 99;
            Assert.False(MessageCodec.TryReadItemCatalog(bytes, 0, bytes.Length, out _));
        }
    }
}
