using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Avelora.Shared.Protocol;
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
                 WeaponType = "TestBlade",
                MaxStack = stack,
                Damage = damage,
                Range = range,
                CooldownSeconds = cooldownSeconds,
            };

        private static ItemType Armor(
            int id = 3301,
            string slot = "head",
            int stack = 1,
            int defense = 2,
            int magicResist = 0,
            int str = 0,
            int dex = 0,
            int intelligence = 0,
            int vit = 0) =>
            new ItemType
            {
                Id = id,
                Name = "Casco",
                Kind = "armor",
                Slot = slot,
                MaxStack = stack,
                Defense = defense,
                MagicResist = magicResist,
                Str = str,
                Dex = dex,
                Int = intelligence,
                Vit = vit,
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
                  { "id": 3101, "name": "Espada corta", "kind": "weapon", "weaponType": "Lamina", "maxStack": 1, "damage": 14, "range": 1.2, "cooldownSeconds": 0.8 }
                ] }
                """);

            ItemCatalog catalog = ItemCatalog.Load(path);
            catalog.Validate();
            Assert.Equal(3002, catalog.Find(3002)!.Id);
            Assert.True(catalog.Find(3002)!.IsPotion);
            Assert.True(catalog.Find(3101)!.IsWeapon);
            Assert.Equal(14, catalog.Find(3101)!.Damage);
        }

        [Fact]
        public void Armor_Valida_Hueco_Y_Stats_Y_Rechaza_Datos_Inválidos()
        {
            ItemType armor = Armor(dex: 1, vit: 2);
            Catalog(armor).Validate();
            Assert.True(armor.TryGetEquipSlot(out byte slot));
            Assert.Equal(2, slot);

            Assert.Throws<InvalidOperationException>(() => Catalog(new ItemType
            {
                Id = 3301, Name = "Casco", Kind = "armor", MaxStack = 1,
            }).Validate());
            Assert.Throws<InvalidOperationException>(() => Catalog(Armor(slot: "weapon")).Validate());
            Assert.Throws<InvalidOperationException>(() => Catalog(Armor(slot: "unknown")).Validate());
            Assert.Throws<InvalidOperationException>(() => Catalog(Armor(stack: 2)).Validate());
            Assert.Throws<InvalidOperationException>(() => Catalog(Armor(str: 256)).Validate());
            Assert.Throws<InvalidOperationException>(() => Catalog(Armor(defense: -1)).Validate());
            Assert.Throws<InvalidOperationException>(() => Catalog(WeaponWithSlot()).Validate());
        }

        [Fact]
        public void DamageType_Desconocido_En_Arma_Se_Rechaza()
        {
            ItemType weapon = Weapon();
            weapon.DamageType = "fuego";

            Assert.Throws<InvalidOperationException>(() => Catalog(weapon).Validate());
        }

        [Fact]
        public void DamageType_En_No_Arma_Se_Rechaza()
        {
            var potion = new ItemType
            {
                Id = 3002,
                Name = "Poción",
                Kind = "potion",
                MaxStack = 10,
                Heal = 30,
                DamageType = "magical",
            };

            Assert.Throws<InvalidOperationException>(() => Catalog(potion).Validate());
        }

        [Fact]
        public void DamageType_Valido_Pasa_La_Validacion()
        {
            ItemType physicalWeapon = Weapon();
            physicalWeapon.DamageType = "physical";
            ItemType magicalWeapon = Weapon(id: 3102);
            magicalWeapon.DamageType = "magical";

            Catalog(physicalWeapon, magicalWeapon).Validate();
        }

        [Fact]
        public void JSON_Valida_Armor_Sin_Slot_Obligatorio()
        {
            string path = Path.Combine(_folder, "armor-sin-slot.json");
            File.WriteAllText(path, """
                { "items": [ { "id": 3301, "name": "Casco", "kind": "armor", "maxStack": 1 } ] }
                """);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => ItemCatalog.Load(path).Validate());
            Assert.Contains("requiere slot", error.Message);
        }

        private static ItemType WeaponWithSlot()
        {
            ItemType item = Weapon();
            item.Slot = "head";
            return item;
        }

        [Fact]
        public void Datos_De_I09_Declaran_La_Piedra_Y_El_Drop_Del_Orco_Guerrero()
        {
            ItemCatalog items = ItemCatalog.Load(Path.Combine(AppContext.BaseDirectory, "items.json"));
            items.Validate();
            ItemType stone = Assert.IsType<ItemType>(items.Find(3201));
            Assert.Equal("Piedra de reajuste", stone.Name);
            Assert.True(stone.IsMaterial);
            Assert.Equal(10, stone.MaxStack);

            NpcCatalog npcs = NpcCatalog.Load(Path.Combine(AppContext.BaseDirectory, "npcs.json"));
            npcs.Validate(MapFile.Load(Path.Combine(AppContext.BaseDirectory, "maps", "avelora-valle.avmap")), items);
            LootEntry drop = Assert.Single(npcs.FindType(2002)!.Loot!, loot => loot.ItemId == 3201);
            Assert.Equal(0.03, drop.Chance);
            Assert.Equal(1, drop.Min);
            Assert.Equal(1, drop.Max);
        }

        [Fact]
        public void Items_I27_Declara_Las_Doce_Armaduras_Y_Los_Objetos_Nuevos_No_Entran_En_El_Botin()
        {
            ItemCatalog items = ItemCatalog.Load(Path.Combine(AppContext.BaseDirectory, "items.json"));
            items.Validate();
            (int Id, string Slot, int Defense, int Resist, int Str, int Dex, int Int, int Vit)[] expected =
            {
                (3301, "head", 1, 0, 0, 0, 0, 0),
                (3302, "head", 2, 1, 0, 0, 0, 0),
                (3303, "body", 2, 1, 0, 0, 0, 0),
                (3304, "legs", 1, 0, 0, 0, 0, 0),
                (3305, "hands", 1, 0, 0, 1, 0, 0),
                (3306, "feet", 1, 0, 0, 0, 0, 0),
                (3307, "offhand", 2, 0, 0, 0, 0, 0),
                (3308, "ring", 0, 0, 0, 0, 0, 2),
                (3309, "neck", 0, 1, 2, 0, 0, 0),
                (3310, "accessory", 0, 2, 0, 0, 2, 0),
                (3311, "body", 5, 1, 0, 0, 0, 0),
                (3312, "legs", 3, 0, 0, 0, 0, 0),
            };

            foreach (var expectedItem in expected)
            {
                ItemType item = Assert.IsType<ItemType>(items.Find(expectedItem.Id));
                Assert.True(item.IsArmor);
                Assert.Equal(expectedItem.Slot, item.Slot);
                Assert.Equal(1, item.MaxStack);
                Assert.Equal(expectedItem.Defense, item.Defense);
                Assert.Equal(expectedItem.Resist, item.MagicResist);
                Assert.Equal(expectedItem.Str, item.Str);
                Assert.Equal(expectedItem.Dex, item.Dex);
                Assert.Equal(expectedItem.Int, item.Int);
                Assert.Equal(expectedItem.Vit, item.Vit);
            }

            NpcCatalog npcs = NpcCatalog.Load(Path.Combine(AppContext.BaseDirectory, "npcs.json"));
            npcs.Validate(MapFile.Load(Path.Combine(AppContext.BaseDirectory, "maps", "avelora-valle.avmap")), items);
            int[] orcDrops = npcs.FindType(2001)!.Loot!
                .Where(drop => drop.ItemId >= 3301 && drop.ItemId <= 3305)
                .Select(drop => drop.ItemId)
                .OrderBy(id => id)
                .ToArray();
            int[] warriorDrops = npcs.FindType(2002)!.Loot!
                .Where(drop => drop.ItemId >= 3306 && drop.ItemId <= 3310)
                .Select(drop => drop.ItemId)
                .OrderBy(id => id)
                .ToArray();
            Assert.Equal(new[] { 3301, 3302, 3303, 3304, 3305 }, orcDrops);
            Assert.Equal(new[] { 3306, 3307, 3308, 3309, 3310 }, warriorDrops);
            Assert.All(npcs.Types.SelectMany(type => type.Loot!).Where(drop => drop.ItemId >= 3301),
                drop => Assert.Equal(0.06, drop.Chance));
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
        [InlineData(8.01f)]
        public void Range_De_Arma_Fuera_De_Rango_Se_Rechaza(float range) =>
            Assert.Throws<InvalidOperationException>(() => Catalog(Weapon(range: range)).Validate());

        [Fact]
        public void Range_Maximo_De_Arma_Es_8_Y_El_Error_Declara_El_Tope()
        {
            Catalog(Weapon(range: 8f)).Validate();
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => Catalog(Weapon(range: 8.01f)).Validate());
            Assert.Contains("≤ 8", error.Message);
        }

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
                Id = 3101, Name = "Espada", Kind = "weapon", WeaponType = "TestBlade", MaxStack = 1,
                Damage = 14, Range = 1.2f,
            }).Validate());

            Assert.Throws<InvalidOperationException>(() => Catalog(new ItemType
            {
                Id = 3101, Name = "Espada", Kind = "weapon", WeaponType = "TestBlade", MaxStack = 1,
                Damage = 14, CooldownSeconds = 0.8d,
            }).Validate());

            Assert.Throws<InvalidOperationException>(() => Catalog(new ItemType
            {
                Id = 3101, Name = "Espada", Kind = "weapon", WeaponType = "TestBlade", MaxStack = 1,
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
        public void Catalogo_Grande_Es_Valido_Y_Nombre_Que_Supera_El_Tope_Se_Rechaza()
        {
            var tooMany = new List<ItemType>();
            for (int i = 1; i <= 513; i++)
            {
                tooMany.Add(Material(id: i));
            }

            Catalog(tooMany.ToArray()).Validate();

            var tooLarge = new List<ItemType>();
            for (int i = 1; i <= 20; i++)
            {
                tooLarge.Add(Material(id: i));
                tooLarge[i - 1].Name = new string('x', 48);
            }

            Catalog(tooLarge.ToArray()).Validate();

            ItemType invalidName = Material(id: 1000);
            invalidName.Name = new string('x', 49);
            Assert.Throws<InvalidOperationException>(() => Catalog(invalidName).Validate());
        }

        [Fact]
        public void Catalogo_Real_Se_Codifica_En_Trozos_Dentro_Del_Tope()
        {
            ItemCatalog catalog = ItemCatalog.Load(Path.Combine(AppContext.BaseDirectory, "items.json"));
            ClassCatalog classes = ClassCatalog.Load(Path.Combine(AppContext.BaseDirectory, "classes.json"));
            classes.ValidateWeaponTypes(catalog);
            Assert.Equal(23, catalog.Items.Count);
            var entries = new ItemCatalogEntry[catalog.Items.Count];
            for (int i = 0; i < entries.Length; i++)
            {
                ItemType item = catalog.Items[i];
                ItemKind kind = item.IsPotion
                    ? ItemKind.Potion
                    : item.IsWeapon ? ItemKind.Weapon
                    : item.IsArmor ? ItemKind.Armor : ItemKind.Material;
                item.TryGetEquipSlot(out byte equipSlot);
                bool usableByClass = !item.IsWeapon;
                if (item.IsWeapon)
                {
                    for (int typeIndex = 0; typeIndex < classes.Find(1)!.WeaponTypes!.Count; typeIndex++)
                    {
                        if (string.Equals(classes.Find(1)!.WeaponTypes![typeIndex], item.WeaponType,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            usableByClass = true;
                            break;
                        }
                    }
                }

                entries[i] = new ItemCatalogEntry(
                    (ushort)item.Id,
                    kind,
                    (ushort)item.MaxStack,
                    item.IsPotion ? (uint)item.Heal : 0u,
                    item.IsWeapon ? (uint)item.Damage : 0u,
                    item.IsWeapon ? item.Range : 0f,
                    item.IsWeapon ? (float)item.CooldownSeconds : 0f,
                    item.Name,
                    equipSlot,
                    checked((ushort)item.Defense),
                    checked((ushort)item.MagicResist),
                    checked((byte)item.Str),
                    checked((byte)item.Dex),
                    checked((byte)item.Int),
                    checked((byte)item.Vit),
                    item.IsWeapon ? item.WeaponType : string.Empty,
                    item.IsTwoHanded,
                    (byte)item.RequiredLevel,
                    usableByClass);
            }

            UnarmedAttack unarmed = classes.Find(1)!.Unarmed!;
            int fistDamage = CharacterStats.GetAttackDamage(
                classes, 1, 1, 0, 0, 0, 0, null, out _, catalog, new ushort[ProtocolConstants.EquipSlotCount]);
            byte[][] chunks = MessageCodec.EncodeItemCatalogChunks(new ItemCatalogMessage(
                1.5f, (uint)fistDamage, unarmed.Range, (float)unarmed.CooldownSeconds, entries));

            Assert.NotEmpty(chunks);
            Assert.Single(chunks);
            Assert.Equal(1253, chunks[0].Length);
            Assert.All(chunks, chunk => Assert.True(chunk.Length <= ProtocolConstants.MaxMessageSize));
            int decodedCount = 0;
            for (int i = 0; i < chunks.Length; i++)
            {
                Assert.True(MessageCodec.TryReadItemCatalog(
                    chunks[i], 0, chunks[i].Length, out ItemCatalogMessage decoded));
                for (int j = 0; j < decoded.Items.Length; j++)
                {
                    Assert.Equal((ushort)catalog.Items[decodedCount].Id, decoded.Items[j].Id);
                    decodedCount++;
                }
            }

            Assert.Equal(catalog.Items.Count, decodedCount);
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
