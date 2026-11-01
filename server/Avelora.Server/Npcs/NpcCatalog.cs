#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avelora.Server.Items;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Npcs
{
    /// <summary>Multiplicadores declarados para un rango de enemigos.</summary>
    public sealed class NpcRank
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public double Chance { get; set; }

        public double Health { get; set; } = 1d;

        public double Damage { get; set; } = 1d;

        public double Experience { get; set; } = 1d;

        public int LootRolls { get; set; } = 1;
    }

    /// <summary>
    /// Un tipo de enemigo: lo que comparten todos los que salen de un mismo spawner.
    /// </summary>
    public sealed class NpcType
    {
        /// <summary>Identificador del tipo. Único y mayor que 0.</summary>
        public int Id { get; set; }

        /// <summary>Nombre para informes y depuración.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Velocidad de deambular, en unidades por segundo (0 &lt; v ≤ 10).</summary>
        public float Speed { get; set; }

        /// <summary>Semiancho de la caja de colisión (0,05 ≤ v ≤ 2).</summary>
        public float HalfWidth { get; set; }

        /// <summary>Semialto de la caja de colisión (0,05 ≤ v ≤ 2).</summary>
        public float HalfHeight { get; set; }

        /// <summary>Caja de colisión del tipo.</summary>
        public Vec2 HalfSize => new Vec2(HalfWidth, HalfHeight);

        /// <summary>Vida máxima del tipo (1..100000).</summary>
        public int MaxHealth { get; set; }

        /// <summary>Experiencia que gana quien asesta el golpe mortal (0..1000000, I-06).</summary>
        public int Experience { get; set; } = -1;

        /// <summary>Oro mínimo por tirada de botín (I-25).</summary>
        public int GoldMin { get; set; }

        /// <summary>Oro máximo por tirada de botín (I-25).</summary>
        public int GoldMax { get; set; }

        /// <summary>Nivel del enemigo, obligatorio para escalar experiencia (I-08).</summary>
        public int Level { get; set; } = -1;

        /// <summary>Defensa física del tipo (0..100000, I-08).</summary>
        public int Defense { get; set; }

        /// <summary>Resistencia mágica del tipo (0..100000, I-08).</summary>
        public int MagicResist { get; set; }

        /// <summary>Segundos que tarda el spawner en reponer un enemigo muerto (0,5..3600).</summary>
        public float RespawnSeconds { get; set; }

        /// <summary>
        /// Radio en el que el PNJ detecta y persigue a un jugador vivo (0..20; 0 = pasivo, nunca
        /// persigue por su cuenta).
        /// </summary>
        public float AggroRadius { get; set; }

        /// <summary>
        /// Distancia máxima a su casa mientras persigue (≥ el <c>wanderRadius</c> de cada spawner
        /// que lo usa, y ≤ 40). Al pasarla, vuelve a casa.
        /// </summary>
        public float LeashRadius { get; set; }

        /// <summary>Vida que quita cada golpe del PNJ (1..100000).</summary>
        public int AttackDamage { get; set; }

        /// <summary>Alcance del golpe, en tiles (&gt; 0 y ≤ 5).</summary>
        public float AttackRange { get; set; }

        /// <summary>Segundos entre dos golpes del PNJ (≥ 0,2).</summary>
        public float AttackCooldownSeconds { get; set; }

        /// <summary>Botín que puede soltar al morir (I-04).</summary>
        public List<LootEntry> Loot { get; set; } = new List<LootEntry>();

        /// <summary>Si este tipo puede aparecer con un rango declarado.</summary>
        public bool Ranks { get; set; } = true;

        /// <summary>Recarga del golpe en ticks, redondeando hacia arriba (I-03).</summary>
        public long AttackCooldownTicks => Math.Max(
            1L,
            (long)Math.Ceiling(AttackCooldownSeconds * SimulationConstants.TickRate));

        /// <summary>
        /// Segundos de reaparición convertidos a ticks de simulación, redondeando hacia arriba:
        /// nada de relojes, todo se mide en ticks de la zona (I-02).
        /// </summary>
        public long RespawnTicks =>
            (long)Math.Ceiling(RespawnSeconds * SimulationConstants.TickRate);
    }

    /// <summary>
    /// Un punto de aparición: cuántos enemigos de un tipo salen ahí y hasta dónde pueden alejarse.
    /// </summary>
    public sealed class NpcSpawner
    {
        /// <summary>Tipo que se coloca. Debe existir en <see cref="NpcCatalog.Types"/>.</summary>
        public int TypeId { get; set; }

        /// <summary>Coordenada X del punto de aparición.</summary>
        public float X { get; set; }

        /// <summary>Coordenada Y del punto de aparición.</summary>
        public float Y { get; set; }

        /// <summary>Enemigos que se colocan en el punto (1..50).</summary>
        public int Count { get; set; }

        /// <summary>Radio de deambular alrededor del punto, en tiles (&gt; 0).</summary>
        public float WanderRadius { get; set; }

        /// <summary>Punto de aparición y «casa» de los enemigos.</summary>
        public Vec2 Position => new Vec2(X, Y);
    }

    /// <summary>
    /// Datos de los enemigos (<c>npcs.json</c>): tipos y puntos de aparición.
    /// </summary>
    /// <remarks>
    /// Se lee al arrancar, antes de crear la zona, y se valida contra el mapa real. Un dato
    /// imposible (un tipo que no existe, un punto dentro de una pared, una velocidad absurda)
    /// impide arrancar en vez de aparecer como un fallo raro en mitad de la partida
    /// (plan del servidor §7, «validar en la frontera»).
    /// </remarks>
    public sealed class NpcCatalog
    {
        /// <summary>Mayor id de rango que cabe en el campo compartido del snapshot.</summary>
        public const int MaxRankId = ProtocolConstants.MaxEntityRank;

        /// <summary>Tope de enemigos por zona.</summary>
        public const int MaxTotalEnemies = 500;

        /// <summary>Tope de enemigos por spawner.</summary>
        public const int MaxPerSpawner = 50;

        public const int MaxGoldPerRoll = 10000;

        public const int MaxLootEntries = 16;

        /// <summary>Velocidad máxima admitida para un tipo, en unidades por segundo.</summary>
        public const float MaxSpeed = 10f;

        /// <summary>Semiancho/semialto mínimo de la caja de un tipo.</summary>
        public const float MinHalfSize = 0.05f;

        /// <summary>Semiancho/semialto máximo de la caja de un tipo.</summary>
        public const float MaxHalfSize = 2f;

        /// <summary>Vida máxima admitida para un tipo.</summary>
        public const int MaxHealthLimit = 100000;

        /// <summary>Segundos de reaparición mínimos admitidos.</summary>
        public const float MinRespawnSeconds = 0.5f;

        /// <summary>Segundos de reaparición máximos admitidos.</summary>
        public const float MaxRespawnSeconds = 3600f;

        /// <summary>Radio de aggro máximo admitido (I-03).</summary>
        public const float MaxAggroRadius = 20f;

        /// <summary>Correa máxima admitida (I-03).</summary>
        public const float MaxLeashRadius = 40f;

        /// <summary>Alcance máximo del golpe de un PNJ (I-03).</summary>
        public const float MaxAttackRange = 5f;

        /// <summary>Recarga mínima del golpe de un PNJ (I-03).</summary>
        public const float MinAttackCooldownSeconds = 0.2f;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>Tipos de enemigo declarados.</summary>
        public List<NpcType> Types { get; set; } = new List<NpcType>();

        /// <summary>Rangos de aparición declarados; el rango 0 (normal) no necesita datos.</summary>
        public List<NpcRank> Ranks { get; set; } = new List<NpcRank>();

        /// <summary>Puntos de aparición declarados.</summary>
        public List<NpcSpawner> Spawners { get; set; } = new List<NpcSpawner>();

        /// <summary>Suma de los <see cref="NpcSpawner.Count"/> de todos los spawners.</summary>
        public int TotalEnemies
        {
            get
            {
                int total = 0;

                if (Spawners != null)
                {
                    for (int i = 0; i < Spawners.Count; i++)
                    {
                        total += Spawners[i].Count;
                    }
                }

                return total;
            }
        }

        /// <summary>Lee el archivo <c>npcs.json</c>.</summary>
        /// <exception cref="FileNotFoundException">No existe la ruta indicada.</exception>
        /// <exception cref="JsonException">El archivo no es JSON válido.</exception>
        public static NpcCatalog Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("La ruta de npcs.json no puede estar vacía.", nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "No se encuentra el archivo de enemigos '" + path + "'. " +
                    "Corrige npcsPath o deja npcsPath vacío para jugar sin enemigos.",
                    path);
            }

            string json = File.ReadAllText(path);
            NpcCatalog? catalog = JsonSerializer.Deserialize<NpcCatalog>(json, JsonOptions);

            if (catalog == null)
            {
                throw new InvalidOperationException("npcs.json está vacío o no contiene un objeto.");
            }

            catalog.Types ??= new List<NpcType>();
            catalog.Ranks ??= new List<NpcRank>();
            catalog.Spawners ??= new List<NpcSpawner>();
            return catalog;
        }

        /// <summary>Busca un tipo por Id, o <c>null</c> si no existe.</summary>
        public NpcType? FindType(int id)
        {
            if (Types == null)
            {
                return null;
            }

            for (int i = 0; i < Types.Count; i++)
            {
                if (Types[i].Id == id)
                {
                    return Types[i];
                }
            }

            return null;
        }

        /// <summary>Busca un rango por id, o <c>null</c> si no está declarado.</summary>
        public NpcRank? FindRank(int id)
        {
            if (Ranks == null)
            {
                return null;
            }

            for (int i = 0; i < Ranks.Count; i++)
            {
                if (Ranks[i]?.Id == id)
                {
                    return Ranks[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Comprueba que los datos son utilizables en <paramref name="map"/> y lanza una
        /// <see cref="InvalidOperationException"/> con el primer problema que encuentre.
        /// </summary>
        public void Validate(MapCollisionGrid map, ItemCatalog? items = null)
        {
            if (map is null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            Types ??= new List<NpcType>();
            Ranks ??= new List<NpcRank>();
            Spawners ??= new List<NpcSpawner>();

            ValidateRanks();
            ValidateTypes(items);

            int total = 0;

            for (int i = 0; i < Spawners.Count; i++)
            {
                NpcSpawner spawner = Spawners[i];
                string where = "el spawner " + (i + 1);

                if (spawner == null)
                {
                    throw new InvalidOperationException("npcs.json: " + where + " está vacío.");
                }

                total += spawner.Count;

                if (FindType(spawner.TypeId) == null)
                {
                    throw new InvalidOperationException(
                        "npcs.json: " + where + " usa el typeId " + spawner.TypeId +
                        ", que no existe en types.");
                }

                if (spawner.Count < 1 || spawner.Count > MaxPerSpawner)
                {
                    throw new InvalidOperationException(
                        "npcs.json: " + where + " tiene count " + spawner.Count +
                        "; debe estar entre 1 y " + MaxPerSpawner + ".");
                }

                if (!(spawner.WanderRadius > 0f) || float.IsInfinity(spawner.WanderRadius))
                {
                    throw new InvalidOperationException(
                        "npcs.json: " + where + " tiene wanderRadius " + spawner.WanderRadius +
                        "; debe ser un número positivo.");
                }

                NpcType type = FindType(spawner.TypeId)!;

                if (type.LeashRadius < spawner.WanderRadius)
                {
                    throw new InvalidOperationException(
                        "npcs.json: " + where + " tiene wanderRadius " + spawner.WanderRadius +
                        ", mayor que el leashRadius " + type.LeashRadius + " del tipo " + type.Id +
                        "; la correa debe cubrir el deambular.");
                }

                if (!map.IsBoxWalkable(spawner.Position, type.HalfSize))
                {
                    throw new InvalidOperationException(
                        "npcs.json: " + where + " (tipo " + type.Id + ") nace en " +
                        spawner.Position + ", dentro de una pared: su caja no cabe. " +
                        "Mueve el punto a una zona transitable.");
                }
            }

            if (total > MaxTotalEnemies)
            {
                throw new InvalidOperationException(
                    "npcs.json: hay " + total + " enemigos y el máximo por zona es " +
                    MaxTotalEnemies + ".");
            }
        }

        private void ValidateRanks()
        {
            double totalChance = 0d;

            for (int i = 0; i < Ranks.Count; i++)
            {
                NpcRank rank = Ranks[i];
                string where = "el rango " + (i + 1);

                if (rank == null)
                {
                    throw new InvalidOperationException("npcs.json: " + where + " está vacío.");
                }

                if (rank.Id < 1 || rank.Id > MaxRankId)
                {
                    throw new InvalidOperationException(
                        "npcs.json: " + where + " tiene id " + rank.Id + "; debe estar entre 1 y " + MaxRankId + ".");
                }

                for (int previous = 0; previous < i; previous++)
                {
                    if (Ranks[previous] != null && Ranks[previous].Id == rank.Id)
                    {
                        throw new InvalidOperationException(
                            "npcs.json: el id " + rank.Id + " está repetido en ranks.");
                    }
                }

                if (string.IsNullOrWhiteSpace(rank.Name) || rank.Name.Length > 24)
                {
                    throw new InvalidOperationException(
                        "npcs.json: el name del rango " + rank.Id + " debe tener entre 1 y 24 caracteres.");
                }

                if (rank.Chance < 0d || rank.Chance > 1d ||
                    double.IsNaN(rank.Chance) || double.IsInfinity(rank.Chance))
                {
                    throw new InvalidOperationException(
                        "npcs.json: la chance del rango " + rank.Id + " debe estar entre 0 y 1.");
                }

                totalChance += rank.Chance;

                ValidateRankMultiplier(rank.Id, "health", rank.Health);
                ValidateRankMultiplier(rank.Id, "damage", rank.Damage);
                ValidateRankMultiplier(rank.Id, "experience", rank.Experience);

                if (rank.LootRolls < 1 || rank.LootRolls > 5)
                {
                    throw new InvalidOperationException(
                        "npcs.json: lootRolls del rango " + rank.Id + " debe estar entre 1 y 5.");
                }
            }

            if (totalChance > 1d)
            {
                throw new InvalidOperationException(
                    "npcs.json: la suma de chance en ranks no puede superar 1.");
            }
        }

        private static void ValidateRankMultiplier(int rankId, string field, double value)
        {
            if (value < 1d || value > 20d || double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new InvalidOperationException(
                    "npcs.json: " + field + " del rango " + rankId + " debe estar entre 1 y 20.");
            }
        }

        private void ValidateTypes(ItemCatalog? items)
        {
            for (int i = 0; i < Types.Count; i++)
            {
                NpcType type = Types[i];
                string where = "el tipo " + (i + 1);

                if (type == null)
                {
                    throw new InvalidOperationException("npcs.json: " + where + " está vacío.");
                }

                // El id viaja en el snapshot como ushort (I-03c).
                if (type.Id <= 0 || type.Id > ushort.MaxValue)
                {
                    throw new InvalidOperationException(
                        "npcs.json: " + where + " tiene id " + type.Id + "; debe estar entre 1 y 65535.");
                }

                for (int j = 0; j < i; j++)
                {
                    if (Types[j] != null && Types[j].Id == type.Id)
                    {
                        throw new InvalidOperationException(
                            "npcs.json: el id " + type.Id + " está repetido en types.");
                    }
                }

                if (!(type.Speed > 0f) || type.Speed > MaxSpeed)
                {
                    throw new InvalidOperationException(
                        "npcs.json: el tipo " + type.Id + " tiene speed " + type.Speed +
                        "; debe ser mayor que 0 y como mucho " + MaxSpeed + ".");
                }

                if (!(type.HalfWidth >= MinHalfSize) || type.HalfWidth > MaxHalfSize ||
                    !(type.HalfHeight >= MinHalfSize) || type.HalfHeight > MaxHalfSize)
                {
                    throw new InvalidOperationException(
                        "npcs.json: el tipo " + type.Id + " tiene halfWidth/halfHeight (" +
                        type.HalfWidth + "; " + type.HalfHeight + "); deben estar entre " +
                        MinHalfSize + " y " + MaxHalfSize + ".");
                }

                if (type.MaxHealth < 1 || type.MaxHealth > MaxHealthLimit)
                {
                    throw new InvalidOperationException(
                        "npcs.json: el tipo " + type.Id + " tiene maxHealth " + type.MaxHealth +
                        "; debe estar entre 1 y " + MaxHealthLimit + ".");
                }

                if (type.Experience < 0 || type.Experience > 1_000_000)
                {
                    throw new InvalidOperationException(
                        "npcs.json: el tipo " + type.Id + " tiene experience " + type.Experience +
                        "; debe estar entre 0 y 1000000 (el campo es obligatorio).");
                }

                if (type.Level < 1 || type.Level > byte.MaxValue)
                {
                    throw new InvalidOperationException(
                        "npcs.json: el tipo " + type.Id + " tiene level " + type.Level +
                        "; debe estar entre 1 y 255.");
                }

                if (type.Defense < 0 || type.Defense > 100_000)
                {
                    throw new InvalidOperationException(
                        "npcs.json: el tipo " + type.Id + " tiene defense; debe estar entre 0 y 100000.");
                }

                if (type.MagicResist < 0 || type.MagicResist > 100_000)
                {
                    throw new InvalidOperationException(
                        "npcs.json: el tipo " + type.Id + " tiene magicResist; debe estar entre 0 y 100000.");
                }

                if (!(type.RespawnSeconds >= MinRespawnSeconds) ||
                    type.RespawnSeconds > MaxRespawnSeconds ||
                    float.IsNaN(type.RespawnSeconds) ||
                    float.IsInfinity(type.RespawnSeconds))
                {
                    throw new InvalidOperationException(
                        "npcs.json: el tipo " + type.Id + " tiene respawnSeconds " + type.RespawnSeconds +
                        "; debe estar entre " + MinRespawnSeconds + " y " + MaxRespawnSeconds + ".");
                }

                if (type.AggroRadius < 0f || type.AggroRadius > MaxAggroRadius ||
                    float.IsNaN(type.AggroRadius) || float.IsInfinity(type.AggroRadius))
                {
                    throw new InvalidOperationException(
                        "npcs.json: el tipo " + type.Id + " tiene aggroRadius " + type.AggroRadius +
                        "; debe estar entre 0 y " + MaxAggroRadius + " (0 = pasivo).");
                }

                if (type.LeashRadius < 0f || type.LeashRadius > MaxLeashRadius ||
                    float.IsNaN(type.LeashRadius) || float.IsInfinity(type.LeashRadius))
                {
                    throw new InvalidOperationException(
                        "npcs.json: el tipo " + type.Id + " tiene leashRadius " + type.LeashRadius +
                        "; debe estar entre 0 y " + MaxLeashRadius + ".");
                }

                if (type.AttackDamage < 1 || type.AttackDamage > MaxHealthLimit)
                {
                    throw new InvalidOperationException(
                        "npcs.json: el tipo " + type.Id + " tiene attackDamage " + type.AttackDamage +
                        "; debe estar entre 1 y " + MaxHealthLimit + ".");
                }

                if (!(type.AttackRange > 0f) || type.AttackRange > MaxAttackRange ||
                    float.IsNaN(type.AttackRange) || float.IsInfinity(type.AttackRange))
                {
                    throw new InvalidOperationException(
                        "npcs.json: el tipo " + type.Id + " tiene attackRange " + type.AttackRange +
                        "; debe ser mayor que 0 y como mucho " + MaxAttackRange + ".");
                }

                if (!(type.AttackCooldownSeconds >= MinAttackCooldownSeconds) ||
                    float.IsNaN(type.AttackCooldownSeconds) ||
                    float.IsInfinity(type.AttackCooldownSeconds))
                {
                    throw new InvalidOperationException(
                        "npcs.json: el tipo " + type.Id + " tiene attackCooldownSeconds " +
                        type.AttackCooldownSeconds + "; debe ser >= " + MinAttackCooldownSeconds + ".");
                }

                // Validar botín (I-04)
                if (type.GoldMin < 0 || type.GoldMin > type.GoldMax || type.GoldMax > MaxGoldPerRoll)
                {
                    throw new InvalidOperationException(
                        "npcs.json: " + where + " tiene goldMin=" + type.GoldMin + ", goldMax=" +
                        type.GoldMax + "; debe ser 0 ≤ goldMin ≤ goldMax ≤ " + MaxGoldPerRoll + ".");
                }

                if (type.Loot != null)
                {
                    if (type.Loot.Count > MaxLootEntries)
                    {
                        throw new InvalidOperationException(
                            "npcs.json: " + where + " tiene " + type.Loot.Count +
                            " entradas de loot; máximo " + MaxLootEntries + ".");
                    }

                    for (int l = 0; l < type.Loot.Count; l++)
                    {
                        LootEntry loot = type.Loot[l];
                        string lootWhere = where + " > loot " + (l + 1);

                        if (loot == null)
                        {
                            throw new InvalidOperationException("npcs.json: " + lootWhere + " está vacío.");
                        }

                        if (loot.ItemId <= 0 || loot.ItemId > ushort.MaxValue)
                        {
                            throw new InvalidOperationException(
                                "npcs.json: " + lootWhere + " tiene itemId " + loot.ItemId +
                                "; debe estar entre 1 y 65535.");
                        }

                        if (!(loot.Chance > 0d) || loot.Chance > 1d ||
                            double.IsNaN(loot.Chance) || double.IsInfinity(loot.Chance))
                        {
                            throw new InvalidOperationException(
                                "npcs.json: " + lootWhere + " tiene chance " + loot.Chance +
                                "; debe estar en (0, 1].");
                        }

                        if (loot.Min < 1 || loot.Max < loot.Min)
                        {
                            throw new InvalidOperationException(
                                "npcs.json: " + lootWhere + " tiene min=" + loot.Min + ", max=" + loot.Max +
                                "; debe ser 1 ≤ min ≤ max.");
                        }

                        ItemType? item = items?.Find(loot.ItemId);
                        if (item == null)
                        {
                            throw new InvalidOperationException(
                                "npcs.json: " + lootWhere + " usa itemId " + loot.ItemId +
                                ", que no existe en items.json.");
                        }

                        if (loot.Max > item.MaxStack)
                        {
                            throw new InvalidOperationException(
                                "npcs.json: " + lootWhere + " tiene max " + loot.Max +
                                ", mayor que maxStack " + item.MaxStack + " del objeto " + loot.ItemId + ".");
                        }
                    }
                }
            }
        }
    }
}
