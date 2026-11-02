#nullable enable
using System;
using System.Collections.Generic;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Balance
{
    /// <summary>Simulador determinista de combate por ticks contra objetivos inmóviles.</summary>
    public sealed class CombatSimulator
    {
        private const int MeasurementSeconds = 60;
        private readonly ClassCatalog _classes;
        private readonly ItemCatalog _items;
        private readonly SkillCatalog _skills;
        private readonly NpcCatalog _npcs;
        private readonly LevelTable _levels;

        /// <summary>Crea el simulador con los catálogos cargados por el servidor.</summary>
        public CombatSimulator(ClassCatalog classes, ItemCatalog items, SkillCatalog skills,
            NpcCatalog npcs, LevelTable levels)
        {
            _classes = classes ?? throw new ArgumentNullException(nameof(classes));
            _items = items ?? throw new ArgumentNullException(nameof(items));
            _skills = skills ?? throw new ArgumentNullException(nameof(skills));
            _npcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
            _levels = levels ?? throw new ArgumentNullException(nameof(levels));
        }

        /// <summary>Simula DPS, primer golpe y enfrentamientos PvE para una clase y un nivel.</summary>
        public CombatSimulationResult Simulate(int classId, int level, string build, string equipment,
            bool includeSkills = true)
        {
            if (level < 1 || level > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(level));
            if (build != "puro" && build != "equilibrado")
                throw new ArgumentException("El build debe ser puro o equilibrado.", nameof(build));
            if (equipment != "ninguno" && equipment != "franja")
                throw new ArgumentException("El equipo debe ser ninguno o franja.", nameof(equipment));

            CharacterClass characterClass = _classes.Find(classId) ??
                throw new InvalidOperationException("No existe la clase " + classId + " en classes.json.");
            ushort[] equipped = CreateEquipment(characterClass, level, equipment);
            int points = checked(_levels.PointsPerLevel * level);
            int allocPrimary = build == "puro" ? points : points * 2 / 3;
            int allocVit = build == "puro" ? 0 : points - allocPrimary;
            int allocStr = characterClass.Primary == "str" ? allocPrimary : 0;
            int allocDex = characterClass.Primary == "dex" ? allocPrimary : 0;
            int allocInt = characterClass.Primary == "int" ? allocPrimary : 0;
            DerivedCharacterStats stats = CharacterStats.Calculate(_classes, classId, level,
                allocStr, allocDex, allocInt, allocVit, _items, equipped);

            ItemType? weapon = equipped[(int)EquipSlot.Weapon] == 0
                ? null
                : _items.Find(equipped[(int)EquipSlot.Weapon]);
            int basicDamage = CombatMath.GetAttackDamage(_classes, classId, level, allocStr, allocDex,
                allocInt, allocVit, weapon?.Damage, out string basicDamageType, _items, equipped,
                weapon?.DamageType);
            long basicCooldown = CombatMath.AttackCooldownTicks(
                weapon?.CooldownSeconds ?? characterClass.Unarmed!.CooldownSeconds);
            int basicNpcDamage = CombatMath.MitigateDamage(basicDamage, 0);
            SkillAttack[] skillAttacks = includeSkills
                ? GetSkillAttacks(characterClass.Id, level, allocStr, allocDex, allocInt, allocVit, equipped)
                : Array.Empty<SkillAttack>();
            double dps = MeasureDps(basicDamage, basicCooldown, skillAttacks,
                characterClass.Resource!.RegenPerSecond, stats.MaxResource);

            var npcResults = new List<NpcCombatResult>(_npcs.Types.Count);
            for (int i = 0; i < _npcs.Types.Count; i++)
            {
                NpcType npc = _npcs.Types[i];
                npcResults.Add(SimulateNpc(npc, stats, basicDamageType, basicDamage,
                    basicCooldown, skillAttacks, characterClass.Resource.RegenPerSecond));
            }

            return new CombatSimulationResult(classId, characterClass.Name, level, dps,
                basicNpcDamage, basicCooldown, npcResults);
        }

        private ushort[] CreateEquipment(CharacterClass characterClass, int level, string equipment)
        {
            var selected = new ushort[ProtocolConstants.EquipSlotCount];
            if (equipment == "ninguno")
            {
                ItemType? ownWeapon = FindFirstWeapon(characterClass);
                if (ownWeapon != null && ownWeapon.RequiredLevel <= level)
                    selected[(int)EquipSlot.Weapon] = (ushort)ownWeapon.Id;
                return selected;
            }

            for (int slot = 0; slot < selected.Length; slot++)
            {
                ItemType? best = null;
                for (int i = 0; i < _items.Items.Count; i++)
                {
                    ItemType item = _items.Items[i];
                    if (!item.TryGetEquipSlot(out byte itemSlot) || itemSlot != slot || item.RequiredLevel > level)
                        continue;
                    if (item.IsWeapon && !CanUseWeapon(characterClass, item))
                        continue;
                    if (best == null || item.RequiredLevel > best.RequiredLevel ||
                        (item.RequiredLevel == best.RequiredLevel && item.Id > best.Id))
                        best = item;
                }
                if (best != null)
                    selected[slot] = (ushort)best.Id;
            }

            ItemType? equippedWeapon = _items.Find(selected[(int)EquipSlot.Weapon]);
            if (equippedWeapon?.IsTwoHanded == true)
                selected[(int)EquipSlot.Offhand] = 0;
            return selected;
        }

        private ItemType? FindFirstWeapon(CharacterClass characterClass)
        {
            for (int i = 0; i < _items.Items.Count; i++)
            {
                ItemType item = _items.Items[i];
                if (item.IsWeapon && CanUseWeapon(characterClass, item))
                    return item;
            }
            return null;
        }

        private static bool CanUseWeapon(CharacterClass characterClass, ItemType item)
        {
            if (characterClass.WeaponTypes == null)
                return false;
            for (int i = 0; i < characterClass.WeaponTypes.Count; i++)
            {
                if (string.Equals(characterClass.WeaponTypes[i], item.WeaponType,
                    StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private SkillAttack[] GetSkillAttacks(int classId, int level, int allocStr, int allocDex,
            int allocInt, int allocVit, ushort[] equipment)
        {
            int eligible = 0;
            for (int i = 0; i < _skills.Skills!.Count; i++)
            {
                SkillDefinition skill = _skills.Skills[i];
                if (skill.ClassId == classId && skill.RequiredLevel <= level && skill.IsDamage)
                    eligible++;
            }
            var attacks = new SkillAttack[eligible];
            int index = 0;
            for (int i = 0; i < _skills.Skills!.Count; i++)
            {
                SkillDefinition skill = _skills.Skills[i];
                if (skill.ClassId != classId || skill.RequiredLevel > level || !skill.IsDamage)
                    continue;
                ItemType? weapon = _items.Find(equipment[(int)EquipSlot.Weapon]);
                int damage = CombatMath.GetSkillAmount(_classes, classId, level, allocStr, allocDex,
                    allocInt, allocVit, skill.Damage, skill.WeaponPercent, weapon?.Damage, _items, equipment);
                string damageType = skill.DamageType;
                double priority = skill.ResourceCost == 0
                    ? double.PositiveInfinity
                    : (double)damage / skill.ResourceCost;
                attacks[index++] = new SkillAttack(skill, damage, damageType, priority);
            }
            return attacks;
        }

        private static double MeasureDps(int basicDamage, long basicCooldown, SkillAttack[] skills,
            int regenPerSecond, int maxResource)
        {
            if (skills.Length == 0)
                return basicDamage * (double)SimulationConstants.TickRate / basicCooldown;

            int mana = maxResource;
            long nextBasicTick = 0;
            long[] nextSkillTicks = new long[skills.Length];
            long totalDamage = 0;
            int totalTicks = MeasurementSeconds * SimulationConstants.TickRate;
            for (int tick = 0; tick < totalTicks; tick++)
            {
                if (tick % SimulationConstants.TickRate == 0 && regenPerSecond > 0)
                    mana = Math.Min(maxResource, mana + regenPerSecond);

                int best = -1;
                for (int i = 0; i < skills.Length; i++)
                {
                    SkillDefinition skill = skills[i].Definition;
                    if (tick < nextSkillTicks[i] || mana < skill.ResourceCost)
                        continue;
                    if (best < 0 || skills[i].Priority > skills[best].Priority)
                        best = i;
                }

                if (best >= 0)
                {
                    SkillDefinition skill = skills[best].Definition;
                    mana -= skill.ResourceCost;
                    nextSkillTicks[best] = tick + skill.CooldownTicks;
                    totalDamage += skills[best].Damage;
                }
                else if (tick >= nextBasicTick)
                {
                    totalDamage += basicDamage;
                    nextBasicTick = tick + basicCooldown;
                }
            }
            return (double)totalDamage / MeasurementSeconds;
        }

        private NpcCombatResult SimulateNpc(NpcType npc, DerivedCharacterStats stats,
            string basicDamageType, int basicDamage, long basicCooldown, SkillAttack[] skills,
            int regenPerSecond)
        {
            int npcHealth = npc.MaxHealth;
            int playerHealth = stats.MaxHealth;
            int mana = stats.MaxResource;
            long nextBasicTick = 0;
            long nextNpcTick = 0;
            long[] nextSkillTicks = new long[skills.Length];
            long maxTicks = Math.Max(1L, (long)npc.MaxHealth * SimulationConstants.TickRate);
            long elapsedTicks = maxTicks;

            for (long tick = 0; tick < maxTicks && npcHealth > 0 && playerHealth > 0; tick++)
            {
                if (tick % SimulationConstants.TickRate == 0 && regenPerSecond > 0)
                    mana = Math.Min(stats.MaxResource, mana + regenPerSecond);

                int selected = -1;
                for (int i = 0; i < skills.Length; i++)
                {
                    if (tick < nextSkillTicks[i] || mana < skills[i].Definition.ResourceCost)
                        continue;
                    if (selected < 0 || skills[i].Priority > skills[selected].Priority)
                        selected = i;
                }

                int outgoingDamage = 0;
                string outgoingType = basicDamageType;
                if (selected >= 0)
                {
                    SkillAttack attack = skills[selected];
                    mana -= attack.Definition.ResourceCost;
                    nextSkillTicks[selected] = tick + attack.Definition.CooldownTicks;
                    outgoingDamage = attack.Damage;
                    outgoingType = attack.DamageType;
                }
                else if (tick >= nextBasicTick)
                {
                    outgoingDamage = basicDamage;
                    nextBasicTick = tick + basicCooldown;
                }

                if (outgoingDamage > 0)
                {
                    int defense = outgoingType == "physical" ? npc.Defense : npc.MagicResist;
                    npcHealth -= CombatMath.MitigateDamage(outgoingDamage, defense);
                    if (npcHealth <= 0)
                    {
                        elapsedTicks = tick + 1;
                        break;
                    }
                }

                if (tick >= nextNpcTick)
                {
                    int defense = npc.DamageType == "magical" ? stats.MagicResist : stats.Defense;
                    playerHealth -= CombatMath.MitigateDamage(npc.AttackDamage, defense);
                    nextNpcTick = tick + npc.AttackCooldownTicks;
                    if (playerHealth <= 0)
                        elapsedTicks = tick + 1;
                }
            }

            bool killed = npcHealth <= 0;
            double lostPercent = (stats.MaxHealth - Math.Max(0, playerHealth)) * 100d / stats.MaxHealth;
            return new NpcCombatResult(npc.Id, npc.Name,
                killed ? (double)elapsedTicks / SimulationConstants.TickRate : (double?)null, killed, lostPercent);
        }

        private readonly struct SkillAttack
        {
            public SkillAttack(SkillDefinition definition, int damage, string damageType, double priority)
            {
                Definition = definition;
                Damage = damage;
                DamageType = damageType;
                Priority = priority;
            }

            public SkillDefinition Definition { get; }
            public int Damage { get; }
            public string DamageType { get; }
            public double Priority { get; }
        }
    }

    /// <summary>Resultados de una simulación para una clase y un nivel.</summary>
    public sealed class CombatSimulationResult
    {
        internal CombatSimulationResult(int classId, string className, int level, double dps,
            int firstHitDamage, long basicCooldownTicks, List<NpcCombatResult> npcs)
        {
            ClassId = classId;
            ClassName = className;
            Level = level;
            DamagePerSecond = dps;
            FirstHitDamage = firstHitDamage;
            BasicCooldownTicks = basicCooldownTicks;
            Npcs = npcs;
        }

        /// <summary>Identificador de clase.</summary>
        public int ClassId { get; }
        /// <summary>Nombre declarado en el catálogo.</summary>
        public string ClassName { get; }
        /// <summary>Nivel simulado.</summary>
        public int Level { get; }
        /// <summary>Daño medio por segundo durante la ventana de simulación.</summary>
        public double DamagePerSecond { get; }
        /// <summary>Daño mitigado del primer ataque básico contra un objetivo sin defensa.</summary>
        public int FirstHitDamage { get; }
        /// <summary>Recarga del ataque básico en ticks.</summary>
        public long BasicCooldownTicks { get; }
        /// <summary>Resultados contra cada tipo PNJ del catálogo.</summary>
        public IReadOnlyList<NpcCombatResult> Npcs { get; }
    }

    /// <summary>Tiempo hasta derrotar un PNJ y porcentaje de vida perdido por el jugador.</summary>
    public sealed class NpcCombatResult
    {
        internal NpcCombatResult(int npcId, string npcName, double? timeToKillSeconds, bool defeated, double healthLostPercent)
        {
            NpcId = npcId;
            NpcName = npcName;
            TimeToKillSeconds = timeToKillSeconds;
            Defeated = defeated;
            HealthLostPercent = healthLostPercent;
        }

        /// <summary>Identificador de tipo de PNJ.</summary>
        public int NpcId { get; }
        /// <summary>Nombre declarado en el catálogo.</summary>
        public string NpcName { get; }
        /// <summary>Tiempo en segundos hasta derrotar el PNJ; null si no ocurri?.</summary>
        public double? TimeToKillSeconds { get; }
        /// <summary>false si el jugador muri? o se agot? el tiempo.</summary>
        public bool Defeated { get; }
        /// <summary>Porcentaje de vida máxima perdido durante el combate.</summary>
        public double HealthLostPercent { get; }
    }
}
