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
                weapon?.CooldownSeconds ?? characterClass.Unarmed!.CooldownSeconds,
                stats.HastePb, _classes.MinAttackTicks);
            int basicNpcDamage = CombatMath.MitigateDamage(basicDamage, 0);
            SkillAttack[] skillAttacks = includeSkills
                ? GetSkillAttacks(characterClass.Id, level, allocStr, allocDex, allocInt, allocVit, equipped)
                : Array.Empty<SkillAttack>();
            double dps = MeasureDps(basicDamage, stats.CritChancePb, _classes.CritMultiplier,
                basicCooldown, skillAttacks, _skills.GlobalCooldownTicks,
                characterClass.Resource!.RegenPerSecond, stats.MaxResource);

            var npcResults = new List<NpcCombatResult>(_npcs.Types.Count);
            for (int i = 0; i < _npcs.Types.Count; i++)
            {
                NpcType npc = _npcs.Types[i];
                npcResults.Add(SimulateNpc(npc, stats, basicDamageType, basicDamage,
                    basicCooldown, skillAttacks, _skills.GlobalCooldownTicks,
                    characterClass.Resource.RegenPerSecond));
            }

            return new CombatSimulationResult(classId, characterClass.Name, level, dps,
                basicNpcDamage, basicCooldown, stats.CritChancePb, stats.HastePb,
                CombatMath.ExpectedDamage(basicDamage, stats.CritChancePb, _classes.CritMultiplier), npcResults);
        }

        /// <summary>Simula un duelo de ataque básico por daño esperado entre dos clases.</summary>
        public DuelResult SimulateDuel(int classA, int classB, int level, string build, string equipment)
        {
            if (equipment != "ninguno" && equipment != "franja")
                throw new ArgumentException("El equipo debe ser ninguno o franja.", nameof(equipment));

            CharacterClass a = _classes.Find(classA) ??
                throw new InvalidOperationException("No existe la clase " + classA + " en classes.json.");
            CharacterClass b = _classes.Find(classB) ??
                throw new InvalidOperationException("No existe la clase " + classB + " en classes.json.");
            var allocationA = Allocations(a, level, build);
            var allocationB = Allocations(b, level, build);
            int rawA = GetDuelDamage(a, level, allocationA, equipment, out long cooldownA);
            int rawB = GetDuelDamage(b, level, allocationB, equipment, out long cooldownB);
            DerivedCharacterStats statsA = GetStats(a, level, allocationA, equipment);
            DerivedCharacterStats statsB = GetStats(b, level, allocationB, equipment);
            string typeA = DuelDamageType(a, level, equipment);
            string typeB = DuelDamageType(b, level, equipment);
            double expectedA = CombatMath.ExpectedHit(rawA, statsA.CritChancePb,
                _classes.CritMultiplier, DefenseFor(statsB, typeA), _classes.PvpDamagePercent);
            double expectedB = CombatMath.ExpectedHit(rawB, statsB.CritChancePb,
                _classes.CritMultiplier, DefenseFor(statsA, typeB), _classes.PvpDamagePercent);
            double secondsA = KillTime(statsB.MaxHealth, expectedA, cooldownA);
            double secondsB = KillTime(statsA.MaxHealth, expectedB, cooldownB);
            double first = Math.Min(secondsA, secondsB);
            double ratio = first / Math.Max(secondsA, secondsB);
            int winner = classA == classB || Math.Abs(secondsA - secondsB) < 1e-9
                ? 0 : secondsA < secondsB ? classA : classB;
            return new DuelResult(winner, first, secondsA, secondsB, ratio, expectedA, expectedB);
        }

        /// <summary>Mide una rotación de daño de 180 segundos y el reparto por habilidad.</summary>
        public RotationResult MeasureRotation(int classId, int level, string build, string equipment)
        {
            if (equipment != "ninguno" && equipment != "franja")
                throw new ArgumentException("El equipo debe ser ninguno o franja.", nameof(equipment));
            CharacterClass characterClass = _classes.Find(classId) ??
                throw new InvalidOperationException("No existe la clase " + classId + " en classes.json.");
            ushort[] equipped = CreateEquipment(characterClass, level, equipment);
            (int str, int dex, int intelligence, int vit) allocation = Allocations(characterClass, level, build);
            DerivedCharacterStats stats = GetStats(characterClass, level, allocation, equipment);
            ItemType? weapon = _items.Find(equipped[(int)EquipSlot.Weapon]);
            int basicDamage = CombatMath.GetAttackDamage(_classes, classId, level,
                allocation.str, allocation.dex, allocation.intelligence, allocation.vit,
                weapon?.Damage, out _, _items, equipped, weapon?.DamageType);
            long basicCooldown = CombatMath.AttackCooldownTicks(
                weapon?.CooldownSeconds ?? characterClass.Unarmed!.CooldownSeconds,
                stats.HastePb, _classes.MinAttackTicks);
            SkillAttack[] skills = GetSkillAttacks(classId, level, allocation.str, allocation.dex,
                allocation.intelligence, allocation.vit, equipped);
            long[] nextSkillTicks = new long[skills.Length];
            long[] casts = new long[skills.Length];
            double[] skillDamage = new double[skills.Length];
            long nextBasicTick = 0;
            long nextGlobalSkillTick = 0;
            int mana = stats.MaxResource;
            double basicTotal = 0;
            double total = 0;
            int totalTicks = 180 * SimulationConstants.TickRate;
            for (int tick = 0; tick < totalTicks; tick++)
            {
                if (tick % SimulationConstants.TickRate == 0 && characterClass.Resource!.RegenPerSecond > 0)
                    mana = Math.Min(stats.MaxResource, mana + characterClass.Resource.RegenPerSecond);
                int best = -1;
                for (int i = 0; i < skills.Length; i++)
                {
                    SkillDefinition skill = skills[i].Definition;
                    if (tick < nextSkillTicks[i] || tick < nextGlobalSkillTick || mana < skill.ResourceCost)
                        continue;
                    if (best < 0 || skills[i].Priority > skills[best].Priority)
                        best = i;
                }
                if (best >= 0)
                {
                    SkillDefinition skill = skills[best].Definition;
                    mana -= skill.ResourceCost;
                    nextSkillTicks[best] = tick + skill.CooldownTicks;
                    nextGlobalSkillTick = tick + _skills.GlobalCooldownTicks;
                    double damage = CombatMath.ExpectedSkillDamage(skills[best].Damage, skill.Hits,
                        stats.CritChancePb, _classes.CritMultiplier, skill.CritBonus);
                    total += damage;
                    skillDamage[best] += damage;
                    casts[best]++;
                }
                else if (tick >= nextBasicTick)
                {
                    double damage = CombatMath.ExpectedDamage(basicDamage, stats.CritChancePb,
                        _classes.CritMultiplier);
                    total += damage;
                    basicTotal += damage;
                    nextBasicTick = tick + basicCooldown;
                }
            }

            var shares = new SkillShare[skills.Length];
            for (int i = 0; i < skills.Length; i++)
                shares[i] = new SkillShare(skills[i].Definition.Id, skills[i].Definition.Name,
                    casts[i], skillDamage[i], total == 0 ? 0 : skillDamage[i] * 100d / total);
            return new RotationResult(total, basicTotal, shares);
        }

        /// <summary>Simula duelos con crítico, habilidades de daño, curas y escudos.</summary>
        public DuelSeriesResult SimulateDuels(int classA, int classB, int level, string build,
            string equipment, int duels, int seed)
        {
            if (duels <= 0) throw new ArgumentOutOfRangeException(nameof(duels));
            if (equipment != "ninguno" && equipment != "franja")
                throw new ArgumentException("El equipo debe ser ninguno o franja.", nameof(equipment));
            int winsA = 0;
            int winsB = 0;
            int draws = 0;
            long healsA = 0;
            long healsB = 0;
            double totalSeconds = 0;
            for (int duel = 0; duel < duels; duel++)
            {
                Random random = new Random(seed + duel);
                Duelist a = CreateDuelist(classA, level, build, equipment);
                Duelist b = CreateDuelist(classB, level, build, equipment);
                int elapsed = 1200;
                for (int tick = 0; tick < 1200 && a.Health > 0 && b.Health > 0; tick++)
                {
                    Regenerate(a, tick);
                    Regenerate(b, tick);
                    if ((duel & 1) == 0)
                    {
                        Act(a, b, tick, random);
                        Act(b, a, tick, random);
                    }
                    else
                    {
                        Act(b, a, tick, random);
                        Act(a, b, tick, random);
                    }
                    if (a.Health <= 0 || b.Health <= 0)
                    {
                        elapsed = tick + 1;
                        break;
                    }
                }
                healsA += a.Heals;
                healsB += b.Heals;
                totalSeconds += elapsed / (double)SimulationConstants.TickRate;
                if (a.Health <= 0 && b.Health > 0) winsB++;
                else if (b.Health <= 0 && a.Health > 0) winsA++;
                else draws++;
            }
            return new DuelSeriesResult(winsA, winsB, draws,
                totalSeconds / duels, (double)Math.Max(winsA, winsB) / duels, healsA, healsB);
        }

        private Duelist CreateDuelist(int classId, int level, string build, string equipment)
        {
            CharacterClass characterClass = _classes.Find(classId) ??
                throw new InvalidOperationException("No existe la clase " + classId + " en classes.json.");
            ushort[] equipped = CreateEquipment(characterClass, level, equipment);
            (int str, int dex, int intelligence, int vit) allocation = Allocations(characterClass, level, build);
            DerivedCharacterStats stats = GetStats(characterClass, level, allocation, equipment);
            ItemType? weapon = _items.Find(equipped[(int)EquipSlot.Weapon]);
            int basicDamage = CombatMath.GetAttackDamage(_classes, classId, level,
                allocation.str, allocation.dex, allocation.intelligence, allocation.vit,
                weapon?.Damage, out string basicType, _items, equipped, weapon?.DamageType);
            long basicCooldown = CombatMath.AttackCooldownTicks(
                weapon?.CooldownSeconds ?? characterClass.Unarmed!.CooldownSeconds,
                stats.HastePb, _classes.MinAttackTicks);
            var skills = new List<SkillDefinition>();
            var damages = new List<int>();
            var shields = new List<int>();
            for (int i = 0; i < _skills.Skills!.Count; i++)
            {
                SkillDefinition skill = _skills.Skills[i];
                if (skill.ClassId != classId || skill.RequiredLevel > level) continue;
                int raw = CombatMath.GetSkillAmount(_classes, classId, level, allocation.str,
                    allocation.dex, allocation.intelligence, allocation.vit, skill.Damage,
                    skill.WeaponPercent, weapon?.Damage, _items, equipped);
                int shield = skill.StatusKind == StatusKind.Shield
                    ? CombatMath.GetSkillAmount(_classes, classId, level, allocation.str, allocation.dex,
                        allocation.intelligence, allocation.vit, skill.StatusAmount, skill.WeaponPercent,
                        weapon?.Damage, _items, equipped)
                    : 0;
                skills.Add(skill);
                damages.Add(raw);
                shields.Add(shield);
            }
            return new Duelist(characterClass, stats, basicDamage, basicType, basicCooldown,
                skills.ToArray(), damages.ToArray(), shields.ToArray());
        }

        private static void Regenerate(Duelist fighter, int tick)
        {
            if (tick % SimulationConstants.TickRate == 0 && fighter.Class.Resource!.RegenPerSecond > 0)
                fighter.Resource = Math.Min(fighter.Stats.MaxResource,
                    fighter.Resource + fighter.Class.Resource.RegenPerSecond);
            if (fighter.ShieldUntil <= tick) fighter.Shield = 0;
        }

        private void Act(Duelist attacker, Duelist target, int tick, Random random)
        {
            if (attacker.Health <= 0 || target.Health <= 0) return;
            int selected = -1;
            if (attacker.Health * 2 < attacker.Stats.MaxHealth)
            {
                for (int i = 0; i < attacker.Skills.Length; i++)
                {
                    SkillDefinition skill = attacker.Skills[i];
                    if (!skill.IsHeal || !Ready(attacker, skill, i, tick)) continue;
                    if (selected < 0 || attacker.DamageAmounts[i] > attacker.DamageAmounts[selected]) selected = i;
                }
                if (selected >= 0)
                {
                    SkillDefinition skill = attacker.Skills[selected];
                    int amount = RollCrit(attacker.DamageAmounts[selected], attacker.Stats.CritChancePb,
                        _classes.CritMultiplier + skill.CritBonus, random);
                    attacker.Health = Math.Min(attacker.Stats.MaxHealth, attacker.Health + amount);
                    attacker.Heals++;
                    if (skill.StatusKind == StatusKind.Shield)
                    {
                        attacker.Shield = attacker.ShieldAmounts[selected];
                        attacker.ShieldUntil = tick + skill.StatusTicks;
                    }
                    StartDuelCooldown(attacker, skill, selected, tick);
                    return;
                }
            }

            if (attacker.Shield <= 0)
            {
                for (int i = 0; i < attacker.Skills.Length; i++)
                {
                    SkillDefinition skill = attacker.Skills[i];
                    if (skill.StatusKind != StatusKind.Shield || !Ready(attacker, skill, i, tick)) continue;
                    if (selected < 0 || attacker.ShieldAmounts[i] > attacker.ShieldAmounts[selected]) selected = i;
                }
                if (selected >= 0)
                {
                    SkillDefinition skill = attacker.Skills[selected];
                    attacker.Shield = attacker.ShieldAmounts[selected];
                    attacker.ShieldUntil = tick + skill.StatusTicks;
                    StartDuelCooldown(attacker, skill, selected, tick);
                    return;
                }
            }

            selected = -1;
            double bestPriority = double.NegativeInfinity;
            for (int i = 0; i < attacker.Skills.Length; i++)
            {
                SkillDefinition skill = attacker.Skills[i];
                if (!skill.IsDamage || !Ready(attacker, skill, i, tick)) continue;
                double priority = skill.ResourceCost == 0 ? double.PositiveInfinity :
                    CombatMath.ExpectedSkillDamage(attacker.DamageAmounts[i], skill.Hits, 0, 100, 0) / skill.ResourceCost;
                if (selected < 0 || priority > bestPriority)
                {
                    selected = i;
                    bestPriority = priority;
                }
            }
            if (selected >= 0)
            {
                SkillDefinition skill = attacker.Skills[selected];
                StartDuelCooldown(attacker, skill, selected, tick);
                for (int hit = 0; hit < skill.Hits && target.Health > 0; hit++)
                    DealDuelDamage(attacker, target, attacker.DamageAmounts[selected], skill.DamageType,
                        _classes.CritMultiplier + skill.CritBonus, random);
                return;
            }

            if (tick >= attacker.NextBasicTick)
            {
                DealDuelDamage(attacker, target, attacker.BasicDamage, attacker.BasicDamageType,
                    _classes.CritMultiplier, random);
                attacker.NextBasicTick = tick + attacker.BasicCooldown;
            }
        }

        private static bool Ready(Duelist fighter, SkillDefinition skill, int index, int tick) =>
            fighter.Resource >= skill.ResourceCost && tick >= fighter.GlobalReadyTick &&
            tick >= fighter.SkillReadyTicks[index];

        private void StartDuelCooldown(Duelist fighter, SkillDefinition skill, int index, int tick)
        {
            fighter.Resource -= skill.ResourceCost;
            fighter.SkillReadyTicks[index] = tick + skill.CooldownTicks;
            fighter.GlobalReadyTick = tick + _skills!.GlobalCooldownTicks;
        }

        private void DealDuelDamage(Duelist attacker, Duelist target, int rawDamage, string damageType,
            int critMultiplier, Random random)
        {
            int damage = RollCrit(rawDamage, attacker.Stats.CritChancePb, critMultiplier, random);
            int defense = damageType == "physical" ? target.Stats.Defense : target.Stats.MagicResist;
            damage = CombatMath.PvpDamage(CombatMath.MitigateDamage(damage, defense), _classes.PvpDamagePercent);
            int absorbed = Math.Min(target.Shield, damage);
            target.Shield -= absorbed;
            target.Health = Math.Max(0, target.Health - (damage - absorbed));
        }

        private static int RollCrit(int damage, int critChancePb, int multiplier, Random random) =>
            critChancePb > 0 && random.Next(10000) < critChancePb
                ? CombatMath.CritDamage(damage, multiplier) : damage;

        private sealed class Duelist
        {
            public Duelist(CharacterClass characterClass, DerivedCharacterStats stats, int basicDamage,
                string basicDamageType, long basicCooldown, SkillDefinition[] skills,
                int[] damageAmounts, int[] shieldAmounts)
            {
                Class = characterClass;
                Stats = stats;
                Health = stats.MaxHealth;
                Resource = stats.MaxResource;
                BasicDamage = basicDamage;
                BasicDamageType = basicDamageType;
                BasicCooldown = basicCooldown;
                Skills = skills;
                DamageAmounts = damageAmounts;
                ShieldAmounts = shieldAmounts;
                SkillReadyTicks = new long[skills.Length];
            }
            public CharacterClass Class { get; }
            public DerivedCharacterStats Stats { get; }
            public int Health { get; set; }
            public int Resource { get; set; }
            public int BasicDamage { get; }
            public string BasicDamageType { get; }
            public long BasicCooldown { get; }
            public long NextBasicTick { get; set; }
            public SkillDefinition[] Skills { get; }
            public int[] DamageAmounts { get; }
            public int[] ShieldAmounts { get; }
            public long[] SkillReadyTicks { get; }
            public long GlobalReadyTick { get; set; }
            public int Shield { get; set; }
            public long ShieldUntil { get; set; }
            public int Heals { get; set; }
        }

        private int GetDuelDamage(CharacterClass attacker, int level,
            (int str, int dex, int intelligence, int vit) allocation, string equipment, out long cooldown)
        {
            ushort[] equipped = CreateEquipment(attacker, level, equipment);
            ItemType? weapon = _items.Find(equipped[(int)EquipSlot.Weapon]);
            cooldown = CombatMath.AttackCooldownTicks(
                weapon?.CooldownSeconds ?? attacker.Unarmed!.CooldownSeconds,
                GetStats(attacker, level, allocation, equipment).HastePb, _classes.MinAttackTicks);
            return CombatMath.GetAttackDamage(_classes, attacker.Id, level,
                allocation.str, allocation.dex, allocation.intelligence, allocation.vit,
                weapon?.Damage, out _, _items, equipped, weapon?.DamageType);
        }

        private DerivedCharacterStats GetStats(CharacterClass characterClass, int level,
            (int str, int dex, int intelligence, int vit) allocation, string equipment) =>
            CharacterStats.Calculate(_classes, characterClass.Id, level,
                allocation.str, allocation.dex, allocation.intelligence, allocation.vit,
                _items, CreateEquipment(characterClass, level, equipment));

        private (int str, int dex, int intelligence, int vit) Allocations(
            CharacterClass characterClass, int level, string build)
        {
            if (level < 1 || level > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(level));
            if (build != "puro" && build != "equilibrado")
                throw new ArgumentException("El build debe ser puro o equilibrado.", nameof(build));
            int points = checked(_levels.PointsPerLevel * level);
            int primary = build == "puro" ? points : points * 2 / 3;
            int vit = build == "puro" ? 0 : points - primary;
            return (characterClass.Primary == "str" ? primary : 0,
                characterClass.Primary == "dex" ? primary : 0,
                characterClass.Primary == "int" ? primary : 0, vit);
        }

        private string DuelDamageType(CharacterClass characterClass, int level, string equipment)
        {
            ItemType? weapon = _items.Find(CreateEquipment(characterClass, level, equipment)[(int)EquipSlot.Weapon]);
            return weapon?.DamageType ?? characterClass.Unarmed!.DamageType;
        }

        private static int DefenseFor(DerivedCharacterStats stats, string damageType) =>
            damageType == "physical"
                ? stats.Defense
                : stats.MagicResist;

        private static double KillTime(double health, double expectedHit, long cooldown)
        {
            double hits = Math.Ceiling(health / expectedHit - 1e-9);
            return ((hits - 1d) * cooldown + 1d) / SimulationConstants.TickRate;
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
                    : CombatMath.ExpectedSkillDamage(damage, skill.Hits, 0, 100, 0) / skill.ResourceCost;
                attacks[index++] = new SkillAttack(skill, damage, damageType, priority);
            }
            return attacks;
        }

        private static double MeasureDps(int basicDamage, int critChancePb, int multiplierPercent,
            long basicCooldown, SkillAttack[] skills, int globalCooldownTicks,
            int regenPerSecond, int maxResource)
        {
            if (skills.Length == 0)
                return CombatMath.ExpectedDamage(basicDamage, critChancePb, multiplierPercent) *
                    SimulationConstants.TickRate / basicCooldown;

            int mana = maxResource;
            long nextBasicTick = 0;
            long nextGlobalSkillTick = 0;
            long[] nextSkillTicks = new long[skills.Length];
            double totalDamage = 0;
            int totalTicks = MeasurementSeconds * SimulationConstants.TickRate;
            for (int tick = 0; tick < totalTicks; tick++)
            {
                if (tick % SimulationConstants.TickRate == 0 && regenPerSecond > 0)
                    mana = Math.Min(maxResource, mana + regenPerSecond);

                int best = -1;
                for (int i = 0; i < skills.Length; i++)
                {
                    SkillDefinition skill = skills[i].Definition;
                    if (tick < nextSkillTicks[i] || tick < nextGlobalSkillTick || mana < skill.ResourceCost)
                        continue;
                    if (best < 0 || skills[i].Priority > skills[best].Priority)
                        best = i;
                }

                if (best >= 0)
                {
                    SkillDefinition skill = skills[best].Definition;
                    mana -= skill.ResourceCost;
                    nextSkillTicks[best] = tick + skill.CooldownTicks;
                    nextGlobalSkillTick = tick + globalCooldownTicks;
                    totalDamage += CombatMath.ExpectedSkillDamage(skills[best].Damage,
                        skills[best].Definition.Hits, critChancePb, multiplierPercent,
                        skills[best].Definition.CritBonus);
                }
                else if (tick >= nextBasicTick)
                {
                    totalDamage += CombatMath.ExpectedDamage(basicDamage, critChancePb, multiplierPercent);
                    nextBasicTick = tick + basicCooldown;
                }
            }
            return (double)totalDamage / MeasurementSeconds;
        }

        private NpcCombatResult SimulateNpc(NpcType npc, DerivedCharacterStats stats,
            string basicDamageType, int basicDamage, long basicCooldown, SkillAttack[] skills,
            int globalCooldownTicks,
            int regenPerSecond)
        {
            double npcHealth = npc.MaxHealth;
            int playerHealth = stats.MaxHealth;
            int mana = stats.MaxResource;
            long nextBasicTick = 0;
            long nextNpcTick = 0;
            long nextGlobalSkillTick = 0;
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
                    if (tick < nextSkillTicks[i] || tick < nextGlobalSkillTick ||
                        mana < skills[i].Definition.ResourceCost)
                        continue;
                    if (selected < 0 || skills[i].Priority > skills[selected].Priority)
                        selected = i;
                }

                int outgoingDamage = 0;
                string outgoingType = basicDamageType;
                SkillAttack? outgoingSkill = null;
                if (selected >= 0)
                {
                    SkillAttack attack = skills[selected];
                    mana -= attack.Definition.ResourceCost;
                    nextSkillTicks[selected] = tick + attack.Definition.CooldownTicks;
                    nextGlobalSkillTick = tick + globalCooldownTicks;
                    outgoingDamage = attack.Damage;
                    outgoingType = attack.DamageType;
                    outgoingSkill = attack;
                }
                else if (tick >= nextBasicTick)
                {
                    outgoingDamage = basicDamage;
                    nextBasicTick = tick + basicCooldown;
                }

                if (outgoingDamage > 0)
                {
                    int defense = outgoingType == "physical" ? npc.Defense : npc.MagicResist;
                    npcHealth -= outgoingSkill is SkillAttack skillAttack
                        ? skillAttack.Definition.Hits * CombatMath.ExpectedHit(outgoingDamage,
                            stats.CritChancePb, _classes.CritMultiplier + skillAttack.Definition.CritBonus, defense)
                        : CombatMath.ExpectedHit(outgoingDamage, stats.CritChancePb,
                            _classes.CritMultiplier, defense);
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
            int firstHitDamage, long basicCooldownTicks, int critChancePb, int hastePb,
            double expectedHit, List<NpcCombatResult> npcs)
        {
            ClassId = classId;
            ClassName = className;
            Level = level;
            DamagePerSecond = dps;
            FirstHitDamage = firstHitDamage;
            BasicCooldownTicks = basicCooldownTicks;
            CritChancePb = critChancePb;
            HastePb = hastePb;
            ExpectedHit = expectedHit;
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
        /// <summary>Probabilidad crítica en puntos básicos.</summary>
        public int CritChancePb { get; }
        /// <summary>Prisa en puntos básicos.</summary>
        public int HastePb { get; }
        /// <summary>Daño esperado por golpe básico sin mitigar.</summary>
        public double ExpectedHit { get; }
        /// <summary>Resultados contra cada tipo PNJ del catálogo.</summary>
        public IReadOnlyList<NpcCombatResult> Npcs { get; }
    }

    /// <summary>Resultado por daño esperado de un duelo de ataques básicos.</summary>
    public sealed class DuelResult
    {
        internal DuelResult(int winnerClassId, double secondsToFirstKill, double secondsA,
            double secondsB, double killTimeRatio, double expectedHitA, double expectedHitB)
        {
            WinnerClassId = winnerClassId;
            SecondsToFirstKill = secondsToFirstKill;
            SecondsA = secondsA;
            SecondsB = secondsB;
            KillTimeRatio = killTimeRatio;
            ExpectedHitA = expectedHitA;
            ExpectedHitB = expectedHitB;
        }

        /// <summary>Identificador de la clase ganadora, o cero si hay empate.</summary>
        public int WinnerClassId { get; }
        /// <summary>Tiempo hasta la primera muerte.</summary>
        public double SecondsToFirstKill { get; }
        /// <summary>Tiempo de muerte de A.</summary>
        public double SecondsA { get; }
        /// <summary>Tiempo de muerte de B.</summary>
        public double SecondsB { get; }
        /// <summary>Relación entre el primer y el último tiempo de muerte.</summary>
        public double KillTimeRatio { get; }
        /// <summary>Daño esperado de A por golpe.</summary>
        public double ExpectedHitA { get; }
        /// <summary>Daño esperado de B por golpe.</summary>
        public double ExpectedHitB { get; }
    }

    /// <summary>Daño total y contribución de cada habilidad durante una rotación.</summary>
    public sealed class RotationResult
    {
        internal RotationResult(double totalDamage, double basicDamage, IReadOnlyList<SkillShare> skills)
        {
            TotalDamage = totalDamage;
            BasicDamage = basicDamage;
            Skills = skills;
        }
        /// <summary>Daño total de ataques básicos y habilidades.</summary>
        public double TotalDamage { get; }
        /// <summary>Daño de los ataques básicos.</summary>
        public double BasicDamage { get; }
        /// <summary>Daño y número de lanzamientos por habilidad desbloqueada.</summary>
        public IReadOnlyList<SkillShare> Skills { get; }
    }

    /// <summary>Contribución de una habilidad al daño de la rotación.</summary>
    public sealed class SkillShare
    {
        internal SkillShare(int skillId, string name, long casts, double damage, double percent)
        {
            SkillId = skillId;
            Name = name;
            Casts = casts;
            Damage = damage;
            Percent = percent;
        }
        /// <summary>Identificador de habilidad.</summary>
        public int SkillId { get; }
        /// <summary>Nombre declarado en el catálogo.</summary>
        public string Name { get; }
        /// <summary>Cantidad de lanzamientos en la ventana medida.</summary>
        public long Casts { get; }
        /// <summary>Daño esperado aportado por esta habilidad.</summary>
        public double Damage { get; }
        /// <summary>Porcentaje del daño total de la rotación.</summary>
        public double Percent { get; }
    }

    /// <summary>Resultados agregados de una serie de duelos simulados.</summary>
    public sealed class DuelSeriesResult
    {
        internal DuelSeriesResult(int winsA, int winsB, int draws, double meanSeconds,
            double maxWinRate, long healsA, long healsB)
        {
            WinsA = winsA;
            WinsB = winsB;
            Draws = draws;
            MeanSeconds = meanSeconds;
            MaxWinRate = maxWinRate;
            HealsA = healsA;
            HealsB = healsB;
        }
        /// <summary>Victorias del combatiente A.</summary>
        public int WinsA { get; }
        /// <summary>Victorias del combatiente B.</summary>
        public int WinsB { get; }
        /// <summary>Duelos terminados en empate al alcanzar 60 segundos.</summary>
        public int Draws { get; }
        /// <summary>Duración media, incluidos los empates de 60 segundos.</summary>
        public double MeanSeconds { get; }
        /// <summary>Mayor proporción de victorias entre A y B.</summary>
        public double MaxWinRate { get; }
        /// <summary>Curas lanzadas por A.</summary>
        public long HealsA { get; }
        /// <summary>Curas lanzadas por B.</summary>
        public long HealsB { get; }
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
