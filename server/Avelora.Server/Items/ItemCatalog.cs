#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avelora.Shared;

namespace Avelora.Server.Items
{
    /// <summary>
    /// Tipo de objeto: lo que define <c>items.json</c>.
    /// </summary>
    public sealed class ItemType
    {
        /// <summary>Identificador del objeto. Único y entre 1 y 65535.</summary>
        public int Id { get; set; }

        /// <summary>Nombre para informes y depuración.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Tipo de objeto: <c>material</c>, <c>potion</c>, <c>weapon</c> o <c>armor</c>.</summary>
        public string Kind { get; set; } = string.Empty;

        private string _weaponType = string.Empty;

        /// <summary>Tipo de arma que puede usar una clase; obligatorio solo para armas.</summary>
        public string WeaponType
        {
            get => _weaponType;
            set
            {
                _weaponType = value ?? string.Empty;
                HasWeaponType = true;
            }
        }

        [JsonIgnore]
        public bool HasWeaponType { get; private set; }

        private string _handedness = "one";

        /// <summary>Manos que ocupa el arma: one o two.</summary>
        public string Handedness
        {
            get => _handedness;
            set
            {
                _handedness = value ?? string.Empty;
                HasHandedness = true;
            }
        }

        [JsonIgnore]
        public bool HasHandedness { get; private set; }

        private int _requiredLevel = 1;

        /// <summary>Nivel mínimo para equipar un arma o armadura.</summary>
        public int RequiredLevel
        {
            get => _requiredLevel;
            set
            {
                _requiredLevel = value;
                HasRequiredLevel = true;
            }
        }

        [JsonIgnore]
        public bool HasRequiredLevel { get; private set; }

        /// <summary>Indica si el arma ocupa ambos huecos de mano.</summary>
        [JsonIgnore]
        public bool IsTwoHanded => string.Equals(Handedness, "two", StringComparison.Ordinal);

        private string _slot = string.Empty;

        /// <summary>Hueco de una armadura. Está prohibido en otros tipos de objeto.</summary>
        public string Slot
        {
            get => _slot;
            set
            {
                _slot = value ?? string.Empty;
                HasSlot = true;
            }
        }

        [JsonIgnore]
        public bool HasSlot { get; private set; }

        /// <summary>Defensa y resistencia mágica que aporta al equiparse.</summary>
        public int Defense { get; set; }
        public int MagicResist { get; set; }

        /// <summary>Bonus de atributos que aporta al equiparse.</summary>
        public int Str { get; set; }
        public int Dex { get; set; }
        public int Int { get; set; }
        public int Vit { get; set; }

        /// <summary>Tamaño máximo de pila (1..999).</summary>
        public int MaxStack { get; set; }

        private int _heal;

        /// <summary>Vida que cura (solo para <c>potion</c>, 1..100000).</summary>
        public int Heal
        {
            get => _heal;
            set
            {
                _heal = value;
                HasHeal = true;
            }
        }

        /// <summary>Permite distinguir un heal explícito (también 0) de un campo omitido.</summary>
        [JsonIgnore]
        public bool HasHeal { get; private set; }

        private int _damage;

        /// <summary>Daño del arma, obligatorio solo para <c>weapon</c>.</summary>
        public int Damage
        {
            get => _damage;
            set
            {
                _damage = value;
                HasDamage = true;
            }
        }

        /// <summary>Indica si se declaró explícitamente <see cref="Damage"/>.</summary>
        [JsonIgnore]
        public bool HasDamage { get; private set; }

        private float _range;

        /// <summary>Alcance del arma, obligatorio solo para <c>weapon</c>.</summary>
        public float Range
        {
            get => _range;
            set
            {
                _range = value;
                HasRange = true;
            }
        }

        /// <summary>Indica si se declaró explícitamente <see cref="Range"/>.</summary>
        [JsonIgnore]
        public bool HasRange { get; private set; }

        private double _cooldownSeconds;

        /// <summary>Recarga del arma en segundos, obligatoria solo para <c>weapon</c>.</summary>
        public double CooldownSeconds
        {
            get => _cooldownSeconds;
            set
            {
                _cooldownSeconds = value;
                HasCooldownSeconds = true;
            }
        }

        /// <summary>Indica si se declaró explícitamente <see cref="CooldownSeconds"/>.</summary>
        [JsonIgnore]
        public bool HasCooldownSeconds { get; private set; }

        /// <summary>True si es una poción.</summary>
        public bool IsPotion => string.Equals(Kind, "potion", StringComparison.OrdinalIgnoreCase);

        /// <summary>True si es un material.</summary>
        public bool IsMaterial => string.Equals(Kind, "material", StringComparison.OrdinalIgnoreCase);

        /// <summary>True si es un arma.</summary>
        public bool IsWeapon => string.Equals(Kind, "weapon", StringComparison.OrdinalIgnoreCase);

        /// <summary>True si es una armadura.</summary>
        public bool IsArmor => string.Equals(Kind, "armor", StringComparison.OrdinalIgnoreCase);

        /// <summary>Devuelve el hueco fijo que puede ocupar este objeto, o false si no se equipa.</summary>
        public bool TryGetEquipSlot(out byte slot)
        {
            if (IsWeapon)
            {
                slot = (byte)EquipSlot.Weapon;
                return true;
            }

            if (IsArmor && TryParseArmorSlot(Slot, out EquipSlot armorSlot))
            {
                slot = (byte)armorSlot;
                return true;
            }

            slot = byte.MaxValue;
            return false;
        }

        public static bool TryParseArmorSlot(string? value, out EquipSlot slot)
        {
            if (string.Equals(value, "offhand", StringComparison.OrdinalIgnoreCase)) slot = EquipSlot.Offhand;
            else if (string.Equals(value, "head", StringComparison.OrdinalIgnoreCase)) slot = EquipSlot.Head;
            else if (string.Equals(value, "body", StringComparison.OrdinalIgnoreCase)) slot = EquipSlot.Body;
            else if (string.Equals(value, "legs", StringComparison.OrdinalIgnoreCase)) slot = EquipSlot.Legs;
            else if (string.Equals(value, "hands", StringComparison.OrdinalIgnoreCase)) slot = EquipSlot.Hands;
            else if (string.Equals(value, "feet", StringComparison.OrdinalIgnoreCase)) slot = EquipSlot.Feet;
            else if (string.Equals(value, "ring", StringComparison.OrdinalIgnoreCase)) slot = EquipSlot.Ring;
            else if (string.Equals(value, "neck", StringComparison.OrdinalIgnoreCase)) slot = EquipSlot.Neck;
            else if (string.Equals(value, "accessory", StringComparison.OrdinalIgnoreCase)) slot = EquipSlot.Accessory;
            else
            {
                slot = default;
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Entrada de botín para un tipo de PNJ.
    /// </summary>
    public sealed class LootEntry
    {
        /// <summary>Identificador del objeto que puede soltar.</summary>
        public int ItemId { get; set; }

        /// <summary>Probabilidad de soltar (0, 1].</summary>
        public double Chance { get; set; }

        /// <summary>Cantidad mínima (inclusiva).</summary>
        public int Min { get; set; }

        /// <summary>Cantidad máxima (inclusiva).</summary>
        public int Max { get; set; }
    }

    /// <summary>
    /// Catálogo de objetos (<c>items.json</c>).
    /// </summary>
    /// <remarks>
    /// Se lee al arrancar y se valida. Un dato imposible impide arrancar
    /// (plan del servidor §7, «validar en la frontera»).
    /// </remarks>
    public sealed class ItemCatalog
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>Tipos de objeto declarados.</summary>
        public List<ItemType> Items { get; set; } = new List<ItemType>();

        /// <summary>Lee el archivo <c>items.json</c>.</summary>
        public static ItemCatalog Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("La ruta de items.json no puede estar vacía.", nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "No se encuentra el archivo de objetos '" + path + "'. " +
                    "Corrige itemsPath o deja itemsPath vacío para jugar sin objetos.",
                    path);
            }

            string json = File.ReadAllText(path);
            ItemCatalog? catalog = JsonSerializer.Deserialize<ItemCatalog>(json, JsonOptions);

            if (catalog == null)
            {
                throw new InvalidOperationException("items.json está vacío o no contiene un objeto.");
            }

            catalog.Items ??= new List<ItemType>();
            return catalog;
        }

        /// <summary>Busca un tipo por Id, o <c>null</c> si no existe.</summary>
        public ItemType? Find(int id)
        {
            if (Items == null)
            {
                return null;
            }

            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i] != null && Items[i].Id == id)
                {
                    return Items[i];
                }
            }

            return null;
        }

        /// <summary>Valida los datos y lanza <see cref="InvalidOperationException"/> con el primer problema.</summary>
        public void Validate()
        {
            Items ??= new List<ItemType>();

            for (int i = 0; i < Items.Count; i++)
            {
                ItemType item = Items[i];
                string where = "el objeto " + (i + 1);

                if (item == null)
                {
                    throw new InvalidOperationException("items.json: " + where + " está vacío.");
                }

                if (item.Id <= 0 || item.Id > ushort.MaxValue)
                {
                    throw new InvalidOperationException(
                        "items.json: " + where + " tiene id " + item.Id + "; debe estar entre 1 y 65535.");
                }

                for (int j = 0; j < i; j++)
                {
                    if (Items[j] != null && Items[j].Id == item.Id)
                    {
                        throw new InvalidOperationException(
                            "items.json: el id " + item.Id + " está repetido en items.");
                    }
                }

                bool isPotion = item.IsPotion;
                bool isMaterial = item.IsMaterial;
                bool isWeapon = item.IsWeapon;
                bool isArmor = item.IsArmor;
                bool isEquipable = isWeapon || isArmor;

                if (!isPotion && !isMaterial && !isWeapon && !isArmor)
                {
                    throw new InvalidOperationException(
                        "items.json: " + where + " tiene kind '" + item.Kind +
                        "'; debe ser 'material', 'potion', 'weapon' o 'armor'.");
                }

                int nameBytes = string.IsNullOrWhiteSpace(item.Name)
                    ? 0
                    : Encoding.UTF8.GetByteCount(item.Name);
                if (string.IsNullOrWhiteSpace(item.Name) || nameBytes > 48)
                {
                    throw new InvalidOperationException(
                        "items.json: " + where + " (id " + item.Id + ") debe tener un nombre no vacío de hasta 48 bytes UTF-8.");
                }

                if (item.MaxStack < 1 || item.MaxStack > 999)
                {
                    throw new InvalidOperationException(
                        "items.json: " + where + " (id " + item.Id + ") tiene maxStack " + item.MaxStack +
                        "; debe estar entre 1 y 999.");
                }

                ValidateEquipmentStats(item, where, isWeapon || isArmor);

                if (isWeapon)
                {
                    int weaponTypeBytes = string.IsNullOrEmpty(item.WeaponType)
                        ? 0
                        : Encoding.UTF8.GetByteCount(item.WeaponType);
                    if (!item.HasWeaponType || weaponTypeBytes < 1 || weaponTypeBytes > 16 ||
                        !string.Equals(item.WeaponType, item.WeaponType.Trim(), StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "items.json: " + where + " (id " + item.Id + ") requiere weaponType de 1 a 16 bytes UTF-8, sin espacios en los bordes.");
                    }

                    if (item.Handedness != "one" && item.Handedness != "two")
                    {
                        throw new InvalidOperationException(
                            "items.json: " + where + " (id " + item.Id + ") tiene handedness '" + item.Handedness + "'; debe ser 'one' o 'two'.");
                    }
                }
                else
                {
                    if (item.HasWeaponType)
                    {
                        throw new InvalidOperationException(
                            "items.json: " + where + " (id " + item.Id + ") no es arma y no puede declarar weaponType.");
                    }

                    if (item.HasHandedness)
                    {
                        throw new InvalidOperationException(
                            "items.json: " + where + " (id " + item.Id + ") no es arma y no puede declarar handedness.");
                    }
                }

                if (isEquipable)
                {
                    if (item.RequiredLevel < 1 || item.RequiredLevel > byte.MaxValue)
                    {
                        throw new InvalidOperationException(
                            "items.json: " + where + " (id " + item.Id + ") tiene requiredLevel " + item.RequiredLevel + "; debe estar entre 1 y 255.");
                    }
                }
                else if (item.HasRequiredLevel)
                {
                    throw new InvalidOperationException(
                        "items.json: " + where + " (id " + item.Id + ") no es equipable y no puede declarar requiredLevel.");
                }

                if (isArmor)
                {
                    if (!item.HasSlot)
                    {
                        throw new InvalidOperationException(
                            "items.json: " + where + " (id " + item.Id + ") es armadura y requiere slot.");
                    }

                    if (!ItemType.TryParseArmorSlot(item.Slot, out _))
                    {
                        throw new InvalidOperationException(
                            "items.json: " + where + " (id " + item.Id + ") tiene slot '" + item.Slot +
                            "'; una armadura debe usar offhand, head, body, legs, hands, feet, ring, neck o accessory (no weapon).");
                    }

                    if (item.MaxStack != 1)
                    {
                        throw new InvalidOperationException(
                            "items.json: " + where + " (id " + item.Id + ") es armadura y debe tener maxStack 1.");
                    }
                }
                else if (item.HasSlot)
                {
                    throw new InvalidOperationException(
                        "items.json: " + where + " (id " + item.Id + ") no es armadura y no puede declarar slot.");
                }

                if (isPotion)
                {
                    if (!item.HasHeal || item.Heal < 1 || item.Heal > 100000)
                    {
                        throw new InvalidOperationException(
                            "items.json: " + where + " (id " + item.Id + ") es poción y tiene heal " + item.Heal +
                            "; debe estar entre 1 y 100000.");
                    }
                }
                else
                {
                    if (item.HasHeal)
                    {
                        throw new InvalidOperationException(
                            "items.json: " + where + " (id " + item.Id + ") no es poción y tiene heal " + item.Heal +
                            "; heal está prohibido fuera de las pociones.");
                    }
                }

                if (isWeapon)
                {
                    if (item.MaxStack != 1)
                    {
                        throw new InvalidOperationException(
                            "items.json: " + where + " (id " + item.Id + ") es arma y debe tener maxStack 1.");
                    }

                    if (!item.HasDamage || item.Damage < 1 || item.Damage > 100000)
                    {
                        throw new InvalidOperationException(
                            "items.json: " + where + " (id " + item.Id + ") tiene damage " + item.Damage +
                            "; debe estar entre 1 y 100000.");
                    }

                    if (!item.HasRange || !(item.Range > 0f) || item.Range > 5f ||
                        float.IsNaN(item.Range) || float.IsInfinity(item.Range))
                    {
                        throw new InvalidOperationException(
                            "items.json: " + where + " (id " + item.Id + ") tiene range inválido; debe ser > 0 y ≤ 5.");
                    }

                    if (!item.HasCooldownSeconds || !(item.CooldownSeconds >= 0.1d) ||
                        double.IsNaN(item.CooldownSeconds) || double.IsInfinity(item.CooldownSeconds))
                    {
                        throw new InvalidOperationException(
                            "items.json: " + where + " (id " + item.Id +
                            ") tiene cooldownSeconds inválido; debe ser ≥ 0,1.");
                    }
                }
                else if (item.HasDamage || item.HasRange || item.HasCooldownSeconds)
                {
                    throw new InvalidOperationException(
                        "items.json: " + where + " (id " + item.Id +
                        ") no es arma y tiene campos de arma, que están prohibidos.");
                }
            }
        }

        private static void ValidateEquipmentStats(ItemType item, string where, bool equipable)
        {
            if (item.Defense < 0 || item.Defense > ushort.MaxValue ||
                item.MagicResist < 0 || item.MagicResist > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    "items.json: " + where + " (id " + item.Id + ") requiere defense y magicResist entre 0 y 65535.");
            }

            if (item.Str < 0 || item.Str > byte.MaxValue || item.Dex < 0 || item.Dex > byte.MaxValue ||
                item.Int < 0 || item.Int > byte.MaxValue || item.Vit < 0 || item.Vit > byte.MaxValue)
            {
                throw new InvalidOperationException(
                    "items.json: " + where + " (id " + item.Id + ") requiere str, dex, int y vit entre 0 y 255.");
            }

            if (!equipable && (item.Defense != 0 || item.MagicResist != 0 || item.Str != 0 ||
                item.Dex != 0 || item.Int != 0 || item.Vit != 0))
            {
                throw new InvalidOperationException(
                    "items.json: " + where + " (id " + item.Id + ") no es equipable y no puede dar defensa, resistencia ni bonus.");
            }
        }
    }
}
