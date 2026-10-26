using System;
using System.Collections.Generic;
using System.IO;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Shared;
using Xunit;

namespace Avelora.Tests
{
    public sealed class ItemDataTests : IDisposable
    {
        private readonly string _folder = Path.Combine(
            Path.GetTempPath(), "avelora-item-data-" + Guid.NewGuid().ToString("N"));

        public ItemDataTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            try { Directory.Delete(_folder, true); }
            catch (IOException) { }
        }

        private static ItemCatalog Catalog(params ItemType[] items) => new ItemCatalog
        {
            Items = new List<ItemType>(items),
        };

        private static ItemType Material(int id = 3001, int stack = 99, int? heal = null)
        {
            var item = new ItemType
            {
                Id = id,
                Name = "Material",
                Kind = "material",
                MaxStack = stack,
            };
            if (heal.HasValue)
            {
                item.Heal = heal.Value;
            }

            return item;
        }

        private static ItemType Weapon(
            int id = 3101,
            string name = "Espada corta",
            int stack = 1,
            int damage = 14,
            float range = 1.2f,
            double cooldownSeconds = 0.8d) =>
            new ItemType
            {
                Id = id,
                Name = name,
                Kind = "weapon",
                MaxStack = stack,
                Damage = damage,
                Range = range,
                CooldownSeconds = cooldownSeconds,
            };

        private static NpcCatalog Npcs(params LootEntry[] loot) => new NpcCatalog
        {
            Types = new List<NpcType>
            {
                new NpcType
                {
                    Id = 2001,
                    Name = "Orco",
                    Speed = 2,
                    HalfWidth = 0.35f,
                    HalfHeight = 0.35f,
                    MaxHealth = 30,
                    Experience = 20,
                    Level = 1,
                    RespawnSeconds = 10,
                    AggroRadius = 0,
                    LeashRadius = 8,
                    AttackDamage = 8,
                    AttackRange = 1,
                    AttackCooldownSeconds = 1.5f,
                    Loot = new List<LootEntry>(loot),
                },
            },
            Spawners = new List<NpcSpawner>(),
        };

        [Fact]
        public void Items_Json_Valido_Se_Carga_Y_Se_Valida()
        {
            string path = Path.Combine(_folder, "items.json");
            File.WriteAllText(path, """
                { "items": [
                  { "id": 3001, "name": "Colmillo", "kind": "material", "maxStack": 99 },
                  { "id": 3002, "name": "Poción", "kind": "potion", "maxStack": 10, "heal": 30 },
                  { "id": 3101, "name": "Espada corta", "kind": "weapon", "maxStack": 1, "damage": 14, "range": 1.2, "cooldownSeconds": 0.8 }
                ] }
                """);

            ItemCatalog catalog = ItemCatalog.Load(path);
            catalog.Validate();
            Assert.Equal(3002, catalog.Find(3002)!.Id);
            Assert.True(catalog.Find(3002)!.IsPotion);
            Assert.True(catalog.Find(3101)!.IsWeapon);
            Assert.Equal(14, catalog.Find(3101)!.Damage);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(65536)]
        public void ItemId_Fuera_De_Rango_Se_Rechaza(int id) =>
            Assert.Throws<InvalidOperationException>(() => Catalog(Material(id)).Validate());

        [Fact]
        public void ItemId_Duplicado_Se_Rechaza() =>
            Assert.Throws<InvalidOperationException>(() => Catalog(Material(), Material()).Validate());

        [Theory]
        [InlineData("arma")]
        [InlineData("")]
        public void Kind_Desconocido_Se_Rechaza(string kind)
        {
            ItemType item = Material();
            item.Kind = kind;
            Assert.Throws<InvalidOperationException>(() => Catalog(item).Validate());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1000)]
        public void MaxStack_Fuera_De_Rango_Se_Rechaza(int maxStack) =>
            Assert.Throws<InvalidOperationException>(() => Catalog(Material(stack: maxStack)).Validate());

        [Theory]
        [InlineData(0)]
        [InlineData(100001)]
        public void Heal_De_Pocion_Fuera_De_Rango_Se_Rechaza(int heal)
        {
            var potion = new ItemType { Id = 3002, Name = "Poción", Kind = "potion", MaxStack = 10, Heal = heal };
            Assert.Throws<InvalidOperationException>(() => Catalog(potion).Validate());
        }

        [Fact]
        public void Potion_Sin_Heal_Y_Material_Con_Heal_Se_Rechazan()
        {
            var potion = new ItemType { Id = 3002, Name = "Poción", Kind = "potion", MaxStack = 10 };
            Assert.Throws<InvalidOperationException>(() => Catalog(potion).Validate());
            Assert.Throws<InvalidOperationException>(() => Catalog(Material(heal: 2)).Validate());
            Assert.Throws<InvalidOperationException>(() => Catalog(Material(heal: 0)).Validate());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(100001)]
        public void Damage_De_Arma_Fuera_De_Rango_Se_Rechaza(int damage) =>
            Assert.Throws<InvalidOperationException>(() => Catalog(Weapon(damage: damage)).Validate());

        [Theory]
        [InlineData(0f)]
        [InlineData(5.01f)]
        public void Range_De_Arma_Fuera_De_Rango_Se_Rechaza(float range) =>
            Assert.Throws<InvalidOperationException>(() => Catalog(Weapon(range: range)).Validate());

        [Theory]
        [InlineData(0.09d)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void Cooldown_De_Arma_Invalido_Se_Rechaza(double cooldownSeconds) =>
            Assert.Throws<InvalidOperationException>(() =>
                Catalog(Weapon(cooldownSeconds: cooldownSeconds)).Validate());

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public void Arma_Con_MaxStack_Distinto_De_Uno_Se_Rechaza(int maxStack) =>
            Assert.Throws<InvalidOperationException>(() => Catalog(Weapon(stack: maxStack)).Validate());

        [Fact]
        public void Arma_Debe_Tener_Los_Tres_Campos_De_Combate()
        {
            Assert.Throws<InvalidOperationException>(() => Catalog(new ItemType
            {
                Id = 3101, Name = "Espada", Kind = "weapon", MaxStack = 1,
                Damage = 14, Range = 1.2f,
            }).Validate());

            Assert.Throws<InvalidOperationException>(() => Catalog(new ItemType
            {
                Id = 3101, Name = "Espada", Kind = "weapon", MaxStack = 1,
                Damage = 14, CooldownSeconds = 0.8d,
            }).Validate());

            Assert.Throws<InvalidOperationException>(() => Catalog(new ItemType
            {
                Id = 3101, Name = "Espada", Kind = "weapon", MaxStack = 1,
                Range = 1.2f, CooldownSeconds = 0.8d,
            }).Validate());
        }

        [Fact]
        public void Campos_De_Arma_En_Pocion_O_Material_Se_Rechazan()
        {
            ItemType[] invalidItems =
            {
                MakeNonWeaponWithDamage(potion: false),
                MakeNonWeaponWithRange(potion: false),
                MakeNonWeaponWithCooldown(potion: false),
                MakeNonWeaponWithDamage(potion: true),
                MakeNonWeaponWithRange(potion: true),
                MakeNonWeaponWithCooldown(potion: true),
            };

            foreach (ItemType item in invalidItems)
            {
                Assert.Throws<InvalidOperationException>(() => Catalog(item).Validate());
            }
        }

        private static ItemType MakeNonWeaponWithDamage(bool potion)
        {
            ItemType item = NonWeapon(potion);
            item.Damage = 1;
            return item;
        }

        private static ItemType MakeNonWeaponWithRange(bool potion)
        {
            ItemType item = NonWeapon(potion);
            item.Range = 1;
            return item;
        }

        private static ItemType MakeNonWeaponWithCooldown(bool potion)
        {
            ItemType item = NonWeapon(potion);
            item.CooldownSeconds = 1;
            return item;
        }

        private static ItemType NonWeapon(bool potion) => potion
            ? new ItemType { Id = 3002, Name = "Poción", Kind = "potion", MaxStack = 10, Heal = 30 }
            : Material();

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Nombre_Vacio_Se_Rechaza(string name)
        {
            ItemType item = Material();
            item.Name = name;
            Assert.Throws<InvalidOperationException>(() => Catalog(item).Validate());
        }

        [Fact]
        public void Nombre_De_Mas_De_48_Bytes_UTF8_Se_Rechaza()
        {
            ItemType item = Weapon(name: new string('ñ', 25));
            Assert.Throws<InvalidOperationException>(() => Catalog(item).Validate());
        }

        [Fact]
        public void Catalogo_Que_Supera_La_Cantidad_O_El_Tope_De_Mensaje_Se_Rechaza()
        {
            var tooMany = new List<ItemType>();
            for (int i = 1; i <= 513; i++)
            {
                tooMany.Add(Material(id: i));
            }

            Assert.Throws<InvalidOperationException>(() => Catalog(tooMany.ToArray()).Validate());

            var tooLarge = new List<ItemType>();
            for (int i = 1; i <= 20; i++)
            {
                tooLarge.Add(Material(id: i));
                tooLarge[i - 1].Name = new string('x', 48);
            }

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => Catalog(tooLarge.ToArray()).Validate());
            Assert.Contains("ItemCatalog", error.Message);
        }

        [Fact]
        public void Loot_Con_ItemId_Inexistente_Se_Rechaza()
        {
            var items = Catalog(Material());
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                Npcs(new LootEntry { ItemId = 3002, Chance = 1, Min = 1, Max = 1 })
                    .Validate(MapCollisionGrid.AllWalkable(20, 20), items));
            Assert.Contains("no existe", error.Message);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(65536)]
        public void Loot_ItemId_Fuera_De_Rango_Se_Rechaza(int itemId)
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                Npcs(new LootEntry { ItemId = itemId, Chance = 1, Min = 1, Max = 1 })
                    .Validate(MapCollisionGrid.AllWalkable(20, 20), Catalog(Material())));
            Assert.Contains("itemId", error.Message);
        }

        [Fact]
        public void Loot_Rechaza_Chance_Minimo_Maximo_Y_MaxStack_Invalidos()
        {
            var items = Catalog(Material());
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(20, 20);
            LootEntry[] invalid =
            {
                new LootEntry { ItemId = 3001, Chance = 0, Min = 1, Max = 1 },
                new LootEntry { ItemId = 3001, Chance = 1.1, Min = 1, Max = 1 },
                new LootEntry { ItemId = 3001, Chance = double.NaN, Min = 1, Max = 1 },
                new LootEntry { ItemId = 3001, Chance = 1, Min = 2, Max = 1 },
                new LootEntry { ItemId = 3001, Chance = 1, Min = 1, Max = 100 },
            };

            foreach (LootEntry entry in invalid)
            {
                Assert.Throws<InvalidOperationException>(() => Npcs(entry).Validate(map, items));
            }
        }
    }
}
