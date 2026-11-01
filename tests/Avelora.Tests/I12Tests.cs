using System;
using System.Collections.Generic;
using System.IO;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    public sealed class I12Tests
    {
        [Fact]
        public void EquipRules_Compara_WeaponType_Sin_Distinguir_Mayusculas_Y_Valida_Nivel()
        {
            var sword = new ItemType
            {
                Id = 4101, Name = "Hoja", Kind = "weapon", WeaponType = "Lamina",
                MaxStack = 1, Damage = 10, Range = 1, CooldownSeconds = 1, RequiredLevel = 3,
            };
            var characterClass = new CharacterClass { WeaponTypes = new List<string> { "lAmInA" } };

            Assert.Equal(EquipCheck.Ok, EquipRules.Check(sword, characterClass, 3));
            Assert.Equal(EquipCheck.LowLevel, EquipRules.Check(sword, characterClass, 2));
            Assert.Equal(EquipCheck.WrongClass, EquipRules.Check(sword,
                new CharacterClass { WeaponTypes = new List<string> { "Varita" } }, 1));
            Assert.Equal(EquipCheck.WrongClass, EquipRules.Check(sword,
                new CharacterClass { WeaponTypes = new List<string>() }, 10));

            ItemType armor = Armor(4201, "head");
            armor.RequiredLevel = 3;
            Assert.Equal(EquipCheck.LowLevel, EquipRules.Check(armor, characterClass, 2));
            Assert.Equal(EquipCheck.Ok, EquipRules.Check(armor,
                new CharacterClass { WeaponTypes = new List<string>() }, 3));
        }

        [Fact]
        public void WeaponType_Y_RequiredLevel_Se_Validan_Desde_Json()
        {
            string path = Path.Combine(Path.GetTempPath(), "i12-items-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, """
                    { "items": [
                      { "id": 4101, "name": "Hoja", "kind": "weapon", "weaponType": "Lamina", "maxStack": 1, "damage": 10, "range": 1, "cooldownSeconds": 1, "handedness": "two", "requiredLevel": 255 },
                      { "id": 4201, "name": "Coraza", "kind": "armor", "slot": "body", "maxStack": 1, "requiredLevel": 5 }
                    ] }
                    """);

                ItemCatalog catalog = ItemCatalog.Load(path);
                catalog.Validate();
                Assert.True(catalog.Find(4101)!.IsTwoHanded);
                Assert.Equal(255, catalog.Find(4101)!.RequiredLevel);
                Assert.Equal(5, catalog.Find(4201)!.RequiredLevel);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Catalogo_De_Clases_Valida_Tipos_De_Arma_Contra_Items()
        {
            ClassCatalog classes = LoadClasses("[\"Lamina\"]");
            var items = new ItemCatalog
            {
                Items = new List<ItemType>
                {
                    new ItemType
                    {
                        Id = 4101, Name = "Hoja", Kind = "weapon", WeaponType = "lamina",
                        MaxStack = 1, Damage = 10, Range = 1, CooldownSeconds = 1,
                    },
                },
            };

            classes.ValidateWeaponTypes(items);

            ClassCatalog invalid = LoadClasses("[\"Báculo\"]");
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => invalid.ValidateWeaponTypes(items));
            Assert.Contains("Báculo", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Items_Rechaza_WeaponType_Ausente_Vacio_Largo_O_Con_Bordes()
        {
            AssertItemInvalid(WeaponJson("", includeType: false), "weaponType");
            AssertItemInvalid(WeaponJson("", includeType: true), "weaponType");
            AssertItemInvalid(WeaponJson(new string('x', 17)), "weaponType");
            AssertItemInvalid(WeaponJson(" borde"), "weaponType");
            AssertItemInvalid(WeaponJson("borde "), "weaponType");
        }

        [Fact]
        public void Items_Valida_Manos_Por_Defecto_Y_Niveles_En_Armas_Y_Armaduras()
        {
            ItemCatalog defaultWeapon = LoadItems(WeaponJson("Lamina", includeType: true));
            Assert.Equal("one", defaultWeapon.Items[0].Handedness);
            Assert.False(defaultWeapon.Items[0].IsTwoHanded);
            Assert.Equal(1, defaultWeapon.Items[0].RequiredLevel);

            ItemCatalog twoHanded = LoadItems(WeaponJson("Lamina", includeType: true, handedness: "two", requiredLevel: 255));
            Assert.True(twoHanded.Items[0].IsTwoHanded);
            Assert.Equal(255, twoHanded.Items[0].RequiredLevel);
            Assert.Equal(5, LoadItems("""
                { "id": 4201, "name": "Pieza", "kind": "armor", "slot": "body", "maxStack": 1, "requiredLevel": 5 }
                """).Items[0].RequiredLevel);
        }

        [Theory]
        [InlineData("three")]
        [InlineData("")]
        public void Items_Rechaza_Handedness_Invalido(string handedness) =>
            AssertItemInvalid(WeaponJson("Lamina", includeType: true, handedness: handedness), "handedness");

        [Fact]
        public void Items_Prohibe_Campos_De_Arma_Y_Nivel_En_No_Equipables()
        {
            AssertItemInvalid("""{ "id": 3001, "name": "Pieza", "kind": "material", "maxStack": 1, "weaponType": "Lamina" }""", "weaponType");
            AssertItemInvalid("""{ "id": 3001, "name": "Pieza", "kind": "material", "maxStack": 1, "handedness": "one" }""", "handedness");
            AssertItemInvalid("""{ "id": 3001, "name": "Pieza", "kind": "material", "maxStack": 1, "requiredLevel": 2 }""", "requiredLevel");
            AssertItemInvalid("""{ "id": 3002, "name": "Poción", "kind": "potion", "maxStack": 1, "heal": 1, "requiredLevel": 2 }""", "requiredLevel");
            AssertItemInvalid(WeaponJson("Lamina", includeType: true, requiredLevel: 0), "requiredLevel");
            AssertItemInvalid(WeaponJson("Lamina", includeType: true, requiredLevel: 256), "requiredLevel");
            AssertItemInvalid("""{ "id": 4201, "name": "Pieza", "kind": "armor", "slot": "body", "maxStack": 1, "handedness": "two" }""", "handedness");
        }

        [Fact]
        public void Classes_Valida_WeaponTypes_Obligatorio_Limite_Y_Repetidos()
        {
            AssertClassInvalid(null, "weaponTypes");
            Assert.NotNull(LoadClasses("[]").Classes);
            AssertClassInvalid("[\"Tipo\", \"tIPO\"]", "repetidos");
            AssertClassInvalid("[\"T1\",\"T2\",\"T3\",\"T4\",\"T5\",\"T6\",\"T7\",\"T8\",\"T9\"]", "8");
            AssertClassInvalid("[\"\"]", "weaponTypes");
            AssertClassInvalid("[\"" + new string('x', 17) + "\"]", "16 bytes");
            AssertClassInvalid("[\" borde\"]", "weaponTypes");
        }

        [Fact]
        public void Equip_Rechaza_Clase_Y_Nivel_Sin_Mutar_Y_Equipa_Dos_Manos_Atomico()
        {
            Zone wrongClassZone = NewZone(new[] { Weapon(4101, "OtroTipo", twoHanded: true) });
            PlayerEntity wrongClassPlayer = SpawnWithItems(wrongClassZone, new InventorySlot(4101, 1));
            int wrongClassChanges = 0;
            wrongClassZone.InventoryChanged += _ => wrongClassChanges++;
            EnqueueEquip(wrongClassZone, wrongClassPlayer, 0);
            Assert.True(wrongClassZone.RejectedEquipClass == 1,
                "clase=" + wrongClassZone.RejectedEquipClass + " nivel=" + wrongClassZone.RejectedEquipLevel +
                " item=" + wrongClassZone.RejectedEquipItem + " vacío=" + wrongClassZone.RejectedEquipEmpty +
                " muerto=" + wrongClassZone.RejectedEquipDead);
            Assert.Equal((ushort)4101, wrongClassPlayer.Inventory[0].ItemId);
            Assert.Equal((ushort)0, wrongClassPlayer.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal(0, wrongClassChanges);

            Zone lowLevelZone = NewZone(new[] { Weapon(4102, "TipoA", requiredLevel: 2) });
            PlayerEntity lowLevelPlayer = SpawnWithItems(lowLevelZone, new InventorySlot(4102, 1));
            EnqueueEquip(lowLevelZone, lowLevelPlayer, 0);
            Assert.Equal(1, lowLevelZone.RejectedEquipLevel);
            Assert.Equal((ushort)4102, lowLevelPlayer.Inventory[0].ItemId);

            Zone equipZone = NewZone(new[]
            {
                Weapon(4103, "TipoA", twoHanded: true),
                Armor(4201, "offhand"),
            });
            PlayerEntity player = SpawnWithItems(equipZone, new InventorySlot(4103, 1), new InventorySlot(0, 0));
            player.Equipment[(int)EquipSlot.Offhand] = 4201;
            int changes = 0;
            equipZone.InventoryChanged += _ => changes++;
            EnqueueEquip(equipZone, player, 0);
            Assert.Equal((ushort)4103, player.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal((ushort)0, player.Equipment[(int)EquipSlot.Offhand]);
            Assert.Equal((ushort)4201, player.Inventory[0].ItemId);
            Assert.Equal(1, changes);
        }

        [Fact]
        public void Equip_Dos_Manos_Con_Inventario_Lleno_Rechaza_Y_Offhand_Se_Bloquea()
        {
            Zone zone = NewZone(new[]
            {
                Weapon(4101, "TipoA", twoHanded: true),
                Weapon(4102, "TipoA"),
                Armor(4201, "offhand"),
            }, slots: 1);
            PlayerEntity player = SpawnWithItems(zone, new InventorySlot(4101, 1));
            player.Equipment[(int)EquipSlot.Weapon] = 4102;
            player.Equipment[(int)EquipSlot.Offhand] = 4201;
            EnqueueEquip(zone, player, 0);
            Assert.Equal(1, zone.RejectedEquipFull);
            Assert.Equal((ushort)4102, player.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal((ushort)4201, player.Equipment[(int)EquipSlot.Offhand]);
            Assert.Equal((ushort)4101, player.Inventory[0].ItemId);

            Zone offhandZone = NewZone(new[] { Weapon(4103, "TipoA", twoHanded: true), Armor(4202, "offhand") });
            PlayerEntity offhandPlayer = SpawnWithItems(offhandZone, new InventorySlot(4202, 1));
            offhandPlayer.Equipment[(int)EquipSlot.Weapon] = 4103;
            EnqueueEquip(offhandZone, offhandPlayer, 0);
            Assert.Equal(1, offhandZone.RejectedEquipTwoHand);
            Assert.Equal((ushort)4202, offhandPlayer.Inventory[0].ItemId);
            Assert.Equal((ushort)0, offhandPlayer.Equipment[(int)EquipSlot.Offhand]);

            Zone noWeaponZone = NewZone(new[] { Weapon(4104, "TipoA", twoHanded: true), Armor(4203, "offhand") }, slots: 1);
            PlayerEntity noWeaponPlayer = SpawnWithItems(noWeaponZone, new InventorySlot(4104, 1));
            noWeaponPlayer.Equipment[(int)EquipSlot.Offhand] = 4203;
            EnqueueEquip(noWeaponZone, noWeaponPlayer, 0);
            Assert.Equal((ushort)4104, noWeaponPlayer.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal((ushort)0, noWeaponPlayer.Equipment[(int)EquipSlot.Offhand]);
            Assert.Equal((ushort)4203, noWeaponPlayer.Inventory[0].ItemId);
        }

        [Fact]
        public void Unequip_Intercambio_Rechaza_Clase_Nivel_Y_Arma_Dos_Manos_Sin_Cambios()
        {
            Zone classZone = NewZone(new[] { Weapon(4101, "TipoA"), Weapon(4102, "OtroTipo") });
            PlayerEntity classPlayer = SpawnWithItems(classZone, new InventorySlot(4102, 1));
            classPlayer.Equipment[(int)EquipSlot.Weapon] = 4101;
            EnqueueUnequip(classZone, classPlayer, (byte)EquipSlot.Weapon, 0);
            Assert.Equal(1, classZone.RejectedEquipClass);
            Assert.Equal((ushort)4101, classPlayer.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal((ushort)4102, classPlayer.Inventory[0].ItemId);

            ItemType highLevelWeapon = Weapon(4104, "TipoA", requiredLevel: 2);
            Zone levelZone = NewZone(new[] { Weapon(4103, "TipoA"), highLevelWeapon });
            PlayerEntity levelPlayer = SpawnWithItems(levelZone, new InventorySlot(4104, 1));
            levelPlayer.Equipment[(int)EquipSlot.Weapon] = 4103;
            EnqueueUnequip(levelZone, levelPlayer, (byte)EquipSlot.Weapon, 0);
            Assert.Equal(1, levelZone.RejectedEquipLevel);
            Assert.Equal((ushort)4103, levelPlayer.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal((ushort)4104, levelPlayer.Inventory[0].ItemId);

            Zone handsZone = NewZone(new[]
            {
                Weapon(4105, "TipoA"), Weapon(4106, "TipoA", twoHanded: true), Armor(4205, "offhand"),
            }, slots: 1);
            PlayerEntity handsPlayer = SpawnWithItems(handsZone, new InventorySlot(4106, 1));
            handsPlayer.Equipment[(int)EquipSlot.Weapon] = 4105;
            handsPlayer.Equipment[(int)EquipSlot.Offhand] = 4205;
            EnqueueUnequip(handsZone, handsPlayer, (byte)EquipSlot.Weapon, 0);
            Assert.Equal(1, handsZone.RejectedEquipFull);
            Assert.Equal((ushort)4105, handsPlayer.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal((ushort)4205, handsPlayer.Equipment[(int)EquipSlot.Offhand]);
            Assert.Equal((ushort)4106, handsPlayer.Inventory[0].ItemId);
        }

        [Fact]
        public void Equip_Dos_Manos_Con_Arma_Previa_Y_Offhand_Libre_Abre_Otra_Vez_El_Hueco()
        {
            Zone zone = NewZone(new[]
            {
                Weapon(4101, "TipoA", twoHanded: true),
                Weapon(4102, "TipoA"),
                Armor(4201, "offhand"),
            });
            PlayerEntity player = SpawnWithItems(zone,
                new InventorySlot(4101, 1), new InventorySlot(0, 0), new InventorySlot(0, 0));
            player.Equipment[(int)EquipSlot.Weapon] = 4102;
            player.Equipment[(int)EquipSlot.Offhand] = 4201;

            EnqueueEquip(zone, player, 0);

            Assert.Equal((ushort)4101, player.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal((ushort)4102, player.Inventory[0].ItemId);
            Assert.Equal((ushort)4201, player.Inventory[10].ItemId);
            Assert.Equal((ushort)0, player.Equipment[(int)EquipSlot.Offhand]);

            EnqueueEquip(zone, player, 0);
            Assert.Equal((ushort)4102, player.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal((ushort)4101, player.Inventory[0].ItemId);
            EnqueueEquip(zone, player, 10);
            Assert.Equal((ushort)4201, player.Equipment[(int)EquipSlot.Offhand]);
            Assert.Equal((ushort)0, player.Inventory[10].ItemId);
        }

        [Fact]
        public void EnforceEquipment_Clase_Desconocida_Mueve_El_Arma_Al_Inventario()
        {
            Zone zone = NewZone(new[] { Weapon(4101, "TipoA") });
            PlayerEntity player = SpawnWithItems(zone, new InventorySlot(0, 0));
            player.ClassId = 250;
            player.Equipment[(int)EquipSlot.Weapon] = 4101;

            Assert.Equal(1, zone.EnforceEquipment(player));
            Assert.Equal((ushort)4101, player.Inventory[10].ItemId);
            Assert.Equal((ushort)0, player.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal(1, zone.EquipmentCorrectedOnLoad);
        }

        [Fact]
        public void EnforceEquipment_Repara_Clase_Nivel_Y_Dos_Manos_Sin_Perder_Con_Inventario_Lleno()
        {
            Zone classZone = NewZone(new[] { Weapon(4101, "OtroTipo") });
            PlayerEntity classPlayer = SpawnWithItems(classZone, new InventorySlot(0, 0));
            classPlayer.CharacterName = "PersonajePrueba";
            classPlayer.Equipment[(int)EquipSlot.Weapon] = 4101;
            Assert.Equal(1, classZone.EnforceEquipment(classPlayer));
            Assert.Equal((ushort)4101, classPlayer.Inventory[10].ItemId);
            Assert.Equal((ushort)0, classPlayer.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal(1, classZone.EquipmentCorrectedOnLoad);

            ItemType highLevelArmor = Armor(4201, "head");
            highLevelArmor.RequiredLevel = 2;
            Zone levelZone = NewZone(new[] { highLevelArmor });
            PlayerEntity levelPlayer = SpawnWithItems(levelZone, new InventorySlot(0, 0));
            levelPlayer.Equipment[(int)EquipSlot.Head] = 4201;
            Assert.Equal(1, levelZone.EnforceEquipment(levelPlayer));
            Assert.Equal((ushort)4201, levelPlayer.Inventory[10].ItemId);

            Zone handsZone = NewZone(new[] { Weapon(4102, "TipoA", twoHanded: true), Armor(4202, "offhand") });
            PlayerEntity handsPlayer = SpawnWithItems(handsZone, new InventorySlot(0, 0));
            handsPlayer.Equipment[(int)EquipSlot.Weapon] = 4102;
            handsPlayer.Equipment[(int)EquipSlot.Offhand] = 4202;
            Assert.Equal(1, handsZone.EnforceEquipment(handsPlayer));
            Assert.Equal((ushort)4202, handsPlayer.Inventory[10].ItemId);
            Assert.Equal((ushort)0, handsPlayer.Equipment[(int)EquipSlot.Offhand]);

            Zone fullZone = NewZone(new[] { Weapon(4103, "OtroTipo") }, slots: 1);
            PlayerEntity fullPlayer = SpawnWithItems(fullZone, new InventorySlot(4301, 1));
            fullPlayer.CharacterName = "PersonajeLleno";
            fullPlayer.Equipment[(int)EquipSlot.Weapon] = 4103;
            var stderr = new StringWriter();
            TextWriter previousError = Console.Error;
            try
            {
                Console.SetError(stderr);
                Assert.Equal(0, fullZone.EnforceEquipment(fullPlayer));
            }
            finally
            {
                Console.SetError(previousError);
            }

            Assert.Equal((ushort)4103, fullPlayer.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal(1, fullZone.EquipmentLoadWarnings);
            Assert.Contains("PersonajeLleno", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("4103", stderr.ToString(), StringComparison.Ordinal);

            Zone validZone = NewZone(new[] { Weapon(4104, "TipoA") });
            PlayerEntity validPlayer = SpawnWithItems(validZone, new InventorySlot(0, 0));
            validPlayer.Equipment[(int)EquipSlot.Weapon] = 4104;
            Assert.Equal(0, validZone.EnforceEquipment(validPlayer));
            Assert.Equal((ushort)4104, validPlayer.Equipment[(int)EquipSlot.Weapon]);
        }

        [Fact]
        public void ItemCatalog_Transporta_Reglas_Y_Rechaza_Campos_Invalidos()
        {
            Assert.Equal((ushort)21, ProtocolConstants.Version);
            var items = new[]
            {
                new ItemCatalogEntry(4101, ItemKind.Weapon, 1, 0, 20, 2, 1, "Arma doble", 0,
                    weaponType: "TipoLargo", twoHanded: true, requiredLevel: 3, usableByClass: false),
                new ItemCatalogEntry(4102, ItemKind.Weapon, 1, 0, 10, 1, 1, "Arma simple", 0,
                    weaponType: "TipoCorto"),
                new ItemCatalogEntry(4201, ItemKind.Armor, 1, 0, 0, 0, 0, "Armadura", (byte)EquipSlot.Head,
                    requiredLevel: 5),
                new ItemCatalogEntry(3001, ItemKind.Potion, 10, 20, 0, 0, 0, "Poción"),
            };
            byte[] encoded = MessageCodec.EncodeItemCatalog(new ItemCatalogMessage(1, 10, 1, 1, items));
            Assert.True(MessageCodec.TryReadItemCatalog(encoded, 0, encoded.Length, out ItemCatalogMessage decoded));
            Assert.Equal(items.Length, decoded.Items.Length);
            Assert.Equal("TipoLargo", decoded.Items[0].WeaponType);
            Assert.True(decoded.Items[0].TwoHanded);
            Assert.Equal((byte)3, decoded.Items[0].RequiredLevel);
            Assert.False(decoded.Items[0].UsableByClass);
            Assert.Equal("TipoCorto", decoded.Items[1].WeaponType);
            Assert.False(decoded.Items[1].TwoHanded);
            Assert.Equal((byte)5, decoded.Items[2].RequiredLevel);
            Assert.Equal(string.Empty, decoded.Items[3].WeaponType);

            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeItemCatalog(new ItemCatalogMessage(
                1, 10, 1, 1, new[] { new ItemCatalogEntry(1, ItemKind.Weapon, 1, 0, 1, 1, 1, "", 0, weaponType: new string('x', 17)) })));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeItemCatalog(new ItemCatalogMessage(
                1, 10, 1, 1, new[] { new ItemCatalogEntry(1, ItemKind.Potion, 1, 1, 0, 0, 0, "", weaponType: "Tipo") })));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeItemCatalog(new ItemCatalogMessage(
                1, 10, 1, 1, new[] { new ItemCatalogEntry(1, ItemKind.Weapon, 1, 0, 1, 1, 1, "", 0) })));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeItemCatalog(new ItemCatalogMessage(
                1, 10, 1, 1, new[] { new ItemCatalogEntry(1, ItemKind.Material, 1, 0, 0, 0, 0, "", requiredLevel: 0) })));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.EncodeItemCatalog(new ItemCatalogMessage(
                1, 10, 1, 1, new[] { new ItemCatalogEntry(1, ItemKind.Armor, 1, 0, 0, 0, 0, "", 2, usableByClass: false) })));

            byte[] invalidFlags = (byte[])encoded.Clone();
            invalidFlags[49] = 2;
            Assert.False(MessageCodec.TryReadItemCatalog(invalidFlags, 0, invalidFlags.Length, out _));
            invalidFlags = (byte[])encoded.Clone();
            invalidFlags[51] = 2;
            Assert.False(MessageCodec.TryReadItemCatalog(invalidFlags, 0, invalidFlags.Length, out _));
        }

        [Fact]
        public void Catalogo_Extenso_Se_Trocea_A_1400_Bytes_Y_Recompone_En_Orden()
        {
            var items = new ItemCatalogEntry[60];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = new ItemCatalogEntry(
                    (ushort)(i + 1), ItemKind.Weapon, 1, 0, 1, 1, 1, new string('n', 48), 0,
                    weaponType: new string('t', 16), twoHanded: i % 2 == 0, requiredLevel: 255);
            }

            byte[][] chunks = MessageCodec.EncodeItemCatalogChunks(
                new ItemCatalogMessage(1, 1, 1, 1, items));
            Assert.Equal(5, chunks.Length);
            Assert.Equal(1332, chunks[0].Length);
            Assert.All(chunks, chunk => Assert.True(chunk.Length <= ProtocolConstants.MaxMessageSize));

            int next = 1;
            foreach (byte[] chunk in chunks)
            {
                Assert.True(MessageCodec.TryReadItemCatalog(chunk, 0, chunk.Length, out ItemCatalogMessage decoded));
                foreach (ItemCatalogEntry entry in decoded.Items)
                {
                    Assert.Equal((ushort)next++, entry.Id);
                    Assert.Equal(new string('t', 16), entry.WeaponType);
                    Assert.Equal((byte)255, entry.RequiredLevel);
                }
            }

            Assert.Equal(items.Length + 1, next);
        }

        private static ClassCatalog LoadClasses(string? weaponTypes)
        {
            string path = WriteClassesJson(weaponTypes);
            try { return ClassCatalog.Load(path); }
            finally { File.Delete(path); }
        }

        private static string WriteClassesJson(string? weaponTypes)
        {
            string path = Path.Combine(Path.GetTempPath(), "i12-classes-" + Guid.NewGuid().ToString("N") + ".json");
            string declaredTypes = weaponTypes == null ? string.Empty : ", \"weaponTypes\": " + weaponTypes;
            File.WriteAllText(path, """
                {
                  "baseHealth": 50,
                  "damagePerPoint": 0,
                  "classes": [
                    { "id": 1, "name": "Clase Uno", "primary": "str",
                      "base": { "str": 1, "dex": 1, "int": 1, "vit": 1 },
                      "healthPerVit": 1, "defense": 0, "magicResist": 0,
                      "resource": { "name": "Recurso", "base": 0, "perInt": 0 },
                      "unarmed": { "damage": 1, "damageType": "physical", "range": 1, "cooldownSeconds": 1 }__DECLARED_TYPES__ }
                  ]
                }
                """.Replace("__DECLARED_TYPES__", declaredTypes));
            return path;
        }

        private static string WeaponJson(
            string weaponType,
            bool includeType = true,
            string? handedness = null,
            int? requiredLevel = null)
        {
            string json = "{ \"id\": 4101, \"name\": \"Hoja\", \"kind\": \"weapon\"";
            if (includeType) json += ", \"weaponType\": \"" + weaponType + "\"";
            if (handedness != null) json += ", \"handedness\": \"" + handedness + "\"";
            if (requiredLevel.HasValue) json += ", \"requiredLevel\": " + requiredLevel.Value;
            return json + ", \"maxStack\": 1, \"damage\": 10, \"range\": 1, \"cooldownSeconds\": 1 }";
        }

        private static ItemCatalog LoadItems(string itemJson)
        {
            string path = Path.Combine(Path.GetTempPath(), "i12-items-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{ \"items\": [" + itemJson + "] }");
                ItemCatalog catalog = ItemCatalog.Load(path);
                catalog.Validate();
                return catalog;
            }
            finally { File.Delete(path); }
        }

        private static void AssertItemInvalid(string itemJson, string expected)
        {
            string path = Path.Combine(Path.GetTempPath(), "i12-invalid-items-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{ \"items\": [" + itemJson + "] }");
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => ItemCatalog.Load(path).Validate());
                Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally { File.Delete(path); }
        }

        private static void AssertClassInvalid(string? weaponTypes, string expected)
        {
            if (weaponTypes == null)
            {
                string path = WriteClassesJson(null);
                try
                {
                    InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => ClassCatalog.Load(path));
                    Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
                }
                finally { File.Delete(path); }
            }
            else
            {
                string path = WriteClassesJson(weaponTypes);
                try
                {
                    InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => ClassCatalog.Load(path));
                    Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
                }
                finally { File.Delete(path); }
            }
        }

        private static ItemType Weapon(int id, string type, bool twoHanded = false, int requiredLevel = 1) => new ItemType
        {
            Id = id, Name = "Objeto de prueba", Kind = "weapon", WeaponType = type,
            Handedness = twoHanded ? "two" : "one", RequiredLevel = requiredLevel,
            MaxStack = 1, Damage = 10, Range = 1, CooldownSeconds = 1,
        };

        private static ItemType Armor(int id, string slot) => new ItemType
        {
            Id = id, Name = "Protección de prueba", Kind = "armor", Slot = slot, MaxStack = 1,
        };

        private static Zone NewZone(ItemType[] items, int slots = 13) => new Zone(
            1, MapCollisionGrid.AllWalkable(10, 10), new EntityStore(), new EntityIdAllocator(),
            new InputQueue(16), new MovementSystem(4, SimulationConstants.FixedDt), 16, 4,
            items: new ItemCatalog { Items = new List<ItemType>(items) },
            itemActions: new ItemActionQueue(16), inventorySlots: slots,
            classes: new ClassCatalog
            {
                BaseHealth = 50,
                DamagePerPoint = 0.5m,
                Classes = new List<CharacterClass>
                {
                    new CharacterClass
                    {
                        Id = 1, Name = "Clase de prueba", Primary = "str",
                        Base = new CharacterAttributes { Str = 1, Dex = 1, Int = 1, Vit = 1 },
                        HealthPerVit = 1, Resource = new CharacterResource { Name = "Recurso", Base = 0, PerInt = 0 },
                        Unarmed = new UnarmedAttack { Damage = 1, DamageType = "physical", Range = 1, CooldownSeconds = 1 },
                        WeaponTypes = new List<string> { "TipoA" },
                    },
                },
            });

        private static PlayerEntity SpawnWithItems(Zone zone, params InventorySlot[] inventory)
        {
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(2, 2), new Vec2(0.4f, 0.4f), 8);
            for (int i = 0; i < inventory.Length && i < player.Inventory.Length; i++) player.Inventory[i] = inventory[i];
            return player;
        }

        private static void EnqueueEquip(Zone zone, PlayerEntity player, byte slot)
        {
            Assert.True(zone.ItemActions!.TryEnqueue(ItemAction.Equip(player.Id, slot)));
            zone.Tick();
        }

        private static void EnqueueUnequip(Zone zone, PlayerEntity player, byte equipSlot, byte targetSlot)
        {
            Assert.True(zone.ItemActions!.TryEnqueue(ItemAction.Unequip(player.Id, equipSlot, targetSlot)));
            zone.Tick();
        }
    }
}
