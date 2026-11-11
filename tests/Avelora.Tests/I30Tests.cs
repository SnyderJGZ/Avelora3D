using System;
using System.IO;
using System.Linq;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Shared.Maps;
using Xunit;

namespace Avelora.Tests
{
    public sealed class I30Tests
    {
        private static string Data(string file) => Path.Combine(AppContext.BaseDirectory, file);

        private static string RepositoryFile(params string[] parts)
        {
            string root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
            return Path.GetFullPath(Path.Combine(new[] { root }.Concat(parts).ToArray()));
        }

        [Fact]
        public void Armas_De_Franjas_Diez_Quince_Y_Veinte_Conservan_El_Contrato()
        {
            ItemCatalog items = ItemCatalog.Load(Data("items.json"));
            items.Validate();
            string[] types = { "Hacha", "Bastón", "Arco", "Maza" };
            int[] levels = { 10, 15, 20 };
            int[] levelFiveIds = { 3102, 3107, 3106, 3105 };

            for (int tier = 0; tier < levels.Length; tier++)
            {
                for (int weapon = 0; weapon < types.Length; weapon++)
                {
                    ItemType item = Assert.IsType<ItemType>(items.Find(3109 + tier * 4 + weapon));
                    ItemType reference = Assert.IsType<ItemType>(items.Find(levelFiveIds[weapon]));
                    Assert.True(item.IsWeapon);
                    Assert.Equal(types[weapon], item.WeaponType);
                    Assert.Equal(levels[tier], item.RequiredLevel);
                    Assert.Equal(reference.CooldownSeconds, item.CooldownSeconds);
                    Assert.Equal(reference.Range, item.Range);
                    Assert.Equal(reference.Handedness, item.Handedness);
                    Assert.Equal(reference.DamageType, item.DamageType);
                    Assert.Equal(0, item.Str);
                    Assert.Equal(0, item.Dex);
                    Assert.Equal(0, item.Int);
                    Assert.Equal(0, item.Vit);
                    if (tier > 0)
                        Assert.True(item.Damage > items.Find(3109 + (tier - 1) * 4 + weapon)!.Damage);
                }
            }
        }

        [Fact]
        public void Armaduras_De_Franjas_Tienen_Huecos_Y_Bonus_Permitidos()
        {
            ItemCatalog items = ItemCatalog.Load(Data("items.json"));
            string[] slots = { "head", "body", "legs", "hands", "feet", "offhand" };
            int[] levels = { 10, 15, 20 };
            int[] expectedDefense = { 22, 44, 94 };
            int previousDefense = 14;

            for (int tier = 0; tier < levels.Length; tier++)
            {
                int defense = 0;
                for (int piece = 0; piece < slots.Length; piece++)
                {
                    ItemType item = Assert.IsType<ItemType>(items.Find(3313 + tier * 6 + piece));
                    Assert.True(item.IsArmor);
                    Assert.Equal(slots[piece], item.Slot);
                    Assert.Equal(levels[tier], item.RequiredLevel);
                    Assert.Equal(0, item.Str);
                    Assert.Equal(0, item.Int);
                    Assert.Equal(0, item.Vit);
                    Assert.InRange(item.MagicResist, 0, 25);
                    Assert.Equal(piece == 3 ? 1 : 0, item.Dex);
                    Assert.Equal(piece == 4 ? 5 : 0, item.MoveSpeed);
                    defense += item.Defense;
                }

                Assert.Equal(expectedDefense[tier], defense);
                Assert.True(defense > previousDefense);
                previousDefense = defense;
            }
        }

        [Fact]
        public void Tipos_Nuevos_Coinciden_Con_Las_Curvas_De_Referencia()
        {
            ItemCatalog items = ItemCatalog.Load(Data("items.json"));
            NpcCatalog npcs = NpcCatalog.Load(Data("npcs.json"));
            int[,] expected =
            {
                { 10, 147, 34, 10, 0 },
                { 15, 206, 49, 15, 15 },
                { 20, 289, 84, 20, 0 },
            };
            string[] damageTypes = { "physical", "magical", "physical" };

            for (int i = 0; i < 3; i++)
            {
                NpcType npc = Assert.IsType<NpcType>(npcs.FindType(2003 + i));
                Assert.Equal(expected[i, 0], npc.Level);
                Assert.Equal(expected[i, 1], npc.MaxHealth);
                Assert.Equal(expected[i, 2], npc.AttackDamage);
                Assert.Equal(expected[i, 3], npc.Defense);
                Assert.Equal(expected[i, 4], npc.MagicResist);
                Assert.Equal(damageTypes[i], npc.DamageType);
            }

            Assert.Contains(npcs.Types, npc => npc.DamageType == "magical");
            Assert.Contains(npcs.Types, npc => npc.Defense >= 20);
            npcs.Validate(MapFile.Load(Data("maps/avelora-ciudad.avmap")), items);
        }

        [Fact]
        public void Spawners_Nuevos_Usan_Casillas_Lejanas_Y_El_Radio_Establecido()
        {
            NpcCatalog npcs = NpcCatalog.Load(Data("npcs.json"));
            (int TypeId, float X, float Y)[] expected =
            {
                (2003, -61.5f, -15.5f), (2003, -34.5f, 52.5f),
                (2004, 60.5f, -28.5f), (2004, 48.5f, -52.5f),
                (2005, 72.5f, -40.5f), (2005, 72.5f, 40.5f),
            };
            foreach (var entry in expected)
            {
                NpcSpawner spawner = Assert.Single(npcs.Spawners, value =>
                    value.TypeId == entry.TypeId && value.X == entry.X && value.Y == entry.Y);
                Assert.Equal(3f, spawner.WanderRadius);
                float dx = Math.Max(Math.Max(-30f - spawner.X, 0f), spawner.X - 30f);
                float dy = Math.Max(Math.Max(-22f - spawner.Y, 0f), spawner.Y - 22f);
                Assert.True(Math.Sqrt(dx * dx + dy * dy) > 10,
                    $"El spawner {spawner.TypeId} no queda a más de 10 casillas de la ciudad.");
            }
        }

        [Fact]
        public void Botin_De_Orcos_Y_Tipos_Nuevos_Contiene_Solo_Las_Franjas_Pedidas()
        {
            ItemCatalog items = ItemCatalog.Load(Data("items.json"));
            NpcCatalog npcs = NpcCatalog.Load(Data("npcs.json"));
            Assert.Contains(npcs.FindType(2001)!.Loot!, drop => drop.ItemId == 3108 && drop.Chance == 0.05);
            Assert.Equal(0.08, Assert.Single(npcs.FindType(2002)!.Loot!, drop => drop.ItemId == 3106).Chance);
            Assert.Equal(0.08, Assert.Single(npcs.FindType(2002)!.Loot!, drop => drop.ItemId == 3107).Chance);
            Assert.Equal(0.06, Assert.Single(npcs.FindType(2002)!.Loot!, drop => drop.ItemId == 3311).Chance);
            Assert.Equal(0.06, Assert.Single(npcs.FindType(2002)!.Loot!, drop => drop.ItemId == 3312).Chance);

            for (int tier = 0; tier < 3; tier++)
            {
                int firstWeapon = 3109 + tier * 4;
                int firstArmor = 3313 + tier * 6;
                int[] expected = Enumerable.Range(firstWeapon, 4).Concat(Enumerable.Range(firstArmor, 6))
                    .OrderBy(id => id).ToArray();
                LootEntry[] loot = npcs.FindType(2003 + tier)!.Loot!.ToArray();
                int[] allExpected = new[] { 3001, 3002 }.Concat(expected).OrderBy(id => id).ToArray();
                Assert.Equal(12, loot.Length);
                Assert.Equal(allExpected, loot.Select(drop => drop.ItemId).OrderBy(id => id).ToArray());
                Assert.All(loot.Where(drop => drop.ItemId >= 3109 && drop.ItemId <= 3330), drop =>
                {
                    Assert.Equal(1, drop.Min);
                    Assert.Equal(1, drop.Max);
                    Assert.Equal(drop.ItemId < 3313 ? 0.06 : 0.05, drop.Chance);
                });
                Assert.Contains(loot, drop => drop.ItemId == 3001 && drop.Chance == 1.0 && drop.Min == 2 && drop.Max == 3);
                Assert.Contains(loot, drop => drop.ItemId == 3002 && drop.Chance == 0.5 && drop.Min == 1 && drop.Max == 1);
            }

            npcs.Validate(MapFile.Load(Data("maps/avelora-ciudad.avmap")), items);
        }

        [Fact]
        public void Datos_De_Carga_Reflejan_Los_Catalogos_De_I30()
        {
            string items = File.ReadAllText(Data("items.json")).Replace("\r\n", "\n");
            string itemsLoad = File.ReadAllText(RepositoryFile("tools", "carga", "items-carga.json"))
                .Replace("\r\n", "\n");
            Assert.Equal(items, itemsLoad);

            ItemCatalog catalog = ItemCatalog.Load(Data("items.json"));
            NpcCatalog npcs = NpcCatalog.Load(Data("npcs.json"));
            NpcCatalog loadNpcs = NpcCatalog.Load(RepositoryFile("tools", "carga", "npcs-carga.json"));
            (int TypeId, float X, float Y, int Count)[] expectedSpawners =
            {
                (2003, -61.5f, -15.5f, 4), (2003, -34.5f, 52.5f, 4),
                (2004, 60.5f, -28.5f, 4), (2004, 48.5f, -52.5f, 4),
                (2005, 72.5f, -40.5f, 2), (2005, 72.5f, 40.5f, 2),
            };
            for (int i = 0; i < expectedSpawners.Length; i++)
            {
                NpcSpawner spawner = loadNpcs.Spawners[npcs.Spawners.Count - expectedSpawners.Length + i];
                Assert.Equal(expectedSpawners[i].TypeId, spawner.TypeId);
                Assert.Equal(expectedSpawners[i].X, spawner.X);
                Assert.Equal(expectedSpawners[i].Y, spawner.Y);
                Assert.Equal(expectedSpawners[i].Count, spawner.Count);
                Assert.Equal(3f, spawner.WanderRadius);
            }
            for (int id = 2003; id <= 2005; id++)
            {
                NpcType expected = Assert.IsType<NpcType>(npcs.FindType(id));
                NpcType actual = Assert.IsType<NpcType>(loadNpcs.FindType(id));
                Assert.Equal(expected.Level, actual.Level);
                Assert.Equal(expected.MaxHealth, actual.MaxHealth);
                Assert.Equal(expected.AttackDamage, actual.AttackDamage);
                Assert.Equal(expected.Defense, actual.Defense);
                Assert.Equal(expected.MagicResist, actual.MagicResist);
                Assert.Equal(expected.DamageType, actual.DamageType);
                Assert.Equal(expected.Loot!.Select(drop => drop.ItemId).OrderBy(itemId => itemId),
                    actual.Loot!.Select(drop => drop.ItemId).OrderBy(itemId => itemId));
            }

            Assert.Equal(30, catalog.Items.Count(item =>
                (item.Id >= 3109 && item.Id <= 3120) || (item.Id >= 3313 && item.Id <= 3330)));
        }
    }
}
