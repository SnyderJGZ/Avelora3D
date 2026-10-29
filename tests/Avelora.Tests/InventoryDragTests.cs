using Avelora.Shared;
using Avelora.Shared.Inventory;
using Xunit;

namespace Avelora.Tests
{
    public class InventoryDragTests
    {
        private const int Helmet = InventoryDrag.EquipSource + (int)EquipSlot.Head;
        private const int Weapon = InventoryDrag.EquipSource + (int)EquipSlot.Weapon;
        private const byte NotEquippable = 255;

        [Theory]
        [InlineData(0, 5, false, DragResult.Move)]
        [InlineData(0, 0, false, DragResult.None)]
        [InlineData(0, -1, false, DragResult.Destroy)]
        [InlineData(0, -1, true, DragResult.None)]
        [InlineData(Helmet, 3, false, DragResult.Unequip)]
        [InlineData(Helmet, -1, false, DragResult.None)]
        [InlineData(Helmet, Weapon, false, DragResult.None)]
        [InlineData(-1, 3, false, DragResult.None)]
        public void Resuelve_Origen_Y_Destino(int from, int to, bool overPanel, DragResult expected)
        {
            Assert.Equal(expected, InventoryDrag.Resolve(from, to, overPanel, 3301, (byte)EquipSlot.Head));
        }

        [Fact]
        public void Equipa_Solo_En_Su_Hueco()
        {
            Assert.Equal(DragResult.Equip, InventoryDrag.Resolve(2, Helmet, false, 3301, (byte)EquipSlot.Head));
            Assert.Equal(DragResult.WrongSlot, InventoryDrag.Resolve(2, Weapon, false, 3301, (byte)EquipSlot.Head));
            Assert.Equal(DragResult.WrongSlot, InventoryDrag.Resolve(2, Helmet, false, 3001, NotEquippable));
        }

        [Fact]
        public void Sin_Objeto_No_Hace_Nada()
        {
            Assert.Equal(DragResult.None, InventoryDrag.Resolve(0, 5, false, 0, NotEquippable));
            Assert.Equal(DragResult.None, InventoryDrag.Resolve(0, -1, false, 0, NotEquippable));
        }

        [Theory]
        [InlineData(10, true, 5)]
        [InlineData(9, true, 4)]
        [InlineData(1, true, 0)]
        [InlineData(10, false, 0)]
        public void Shift_Mueve_Media_Pila(ushort stack, bool half, ushort expected)
        {
            Assert.Equal(expected, InventoryDrag.MoveCount(stack, half));
        }

        [Theory]
        [InlineData(0, 10, 1)]
        [InlineData(-5, 10, 1)]
        [InlineData(4, 10, 4)]
        [InlineData(99, 10, 10)]
        [InlineData(1, 1, 1)]
        public void La_Cantidad_A_Destruir_Queda_Entre_Uno_Y_La_Pila(int requested, ushort stack, ushort expected)
        {
            Assert.Equal(expected, InventoryDrag.ClampDestroyCount(requested, stack));
        }
    }
}
