#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

        /// <summary>Tipo de objeto: <c>material</c> o <c>potion</c>.</summary>
        public string Kind { get; set; } = string.Empty;

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

        /// <summary>True si es una poción.</summary>
        public bool IsPotion => string.Equals(Kind, "potion", StringComparison.OrdinalIgnoreCase);

        /// <summary>True si es un material.</summary>
        public bool IsMaterial => string.Equals(Kind, "material", StringComparison.OrdinalIgnoreCase);
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

                if (!isPotion && !isMaterial)
                {
                    throw new InvalidOperationException(
                        "items.json: " + where + " tiene kind '" + item.Kind + "'; debe ser 'material' o 'potion'.");
                }

                if (item.MaxStack < 1 || item.MaxStack > 999)
                {
                    throw new InvalidOperationException(
                        "items.json: " + where + " (id " + item.Id + ") tiene maxStack " + item.MaxStack +
                        "; debe estar entre 1 y 999.");
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
                            "items.json: " + where + " (id " + item.Id + ") es material y tiene heal " + item.Heal +
                            "; heal está prohibido en materiales (debe ser 0 o no estar).");
                    }
                }
            }
        }
    }
}
