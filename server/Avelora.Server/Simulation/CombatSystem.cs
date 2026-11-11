#nullable enable
using System;
using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Progression;
using Avelora.Server.Zones;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Resuelve los ataques de los jugadores (I-02) y el uso de habilidades (I-16): el cliente pide
    /// y el servidor decide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Un ataque solo vale si el atacante es un jugador de la zona, el objetivo existe, es un PNJ
    /// vivo, está dentro del alcance efectivo y el jugador ha respetado su recarga. La recarga se
    /// cuenta en ticks de la zona, nunca con el reloj del cliente ni con el del servidor. Las
    /// habilidades comparten las mismas reglas de objetivo y alcance, pero tienen su propia recarga
    /// y gastan el recurso de la clase.
    /// </para>
    /// <para>
    /// Corre en el hilo de simulación, dentro de <see cref="Zone.Tick"/>, después de mover a los
    /// jugadores y antes de la IA de los enemigos. No reserva memoria por tick: la lista de
    /// intenciones la reutiliza la zona.
    /// </para>
    /// </remarks>
    public sealed class CombatSystem
    {
        /// <summary>Alcance base mínimo para marcar un evento como ataque a distancia.</summary>
        public const float RangedMinRange = 2f;

        /// <summary>Ticks que dura la acción <see cref="EntityAction.Attacking"/> (6 = 0,3 s a 20 Hz).</summary>
        public const int AttackingTicks = 6;

        private readonly CombatSettings _settings;
        private readonly ItemCatalog? _catalog;
        private readonly ClassCatalog _classes;
        private readonly MapCollisionGrid? _map;
        private readonly CombatEventBuffer? _events;
        private readonly ZoneMap _zones;
        private readonly SkillCatalog? _skills;
        private readonly Random _random;
        private readonly int[] _areaIds = new int[SkillCatalog.MaxAreaTargets];
        private readonly float[] _areaDistSq = new float[SkillCatalog.MaxAreaTargets];
        private readonly long[] _skillRejected = new long[13];
        private readonly long[] _statusApplied = new long[5];

        public CombatSystem(
            CombatSettings settings,
            ItemCatalog? catalog = null,
            ClassCatalog? classes = null,
            MapCollisionGrid? map = null,
            CombatEventBuffer? events = null,
            ZoneMap? zones = null,
            SkillCatalog? skills = null,
            Random? random = null)
        {
            _settings = settings;
            _catalog = catalog;
            _classes = classes ?? ClassCatalog.Default;
            _map = map;
            _events = events;
            _zones = zones ?? ZoneMap.Empty;
            _skills = skills;
            _random = random ?? new Random(settings.CombatSeed);
        }

        /// <summary>Ataques que han resultado válidos.</summary>
        public long AttacksOk { get; private set; }

        public long PvpAttacksOk { get; private set; }
        public long PvpKills { get; private set; }
        public long RejectedPvpSafeZone { get; private set; }
        public long RejectedPvpAttackerPve { get; private set; }
        public long RejectedPvpTargetPve { get; private set; }
        public long RejectedPvpTargetDead { get; private set; }
        public long RejectedPvpSelf { get; private set; }

        /// <summary>Ataques rechazados porque el objetivo no existía, no era un PNJ o estaba muerto.</summary>
        public long RejectedTarget { get; private set; }

        /// <summary>Ataques rechazados porque el objetivo estaba fuera de alcance.</summary>
        public long RejectedDistance { get; private set; }

        /// <summary>Ataques rechazados porque el jugador todavía no había recargado.</summary>
        public long RejectedCooldown { get; private set; }

        /// <summary>Ataques rechazados porque el jugador estaba muerto (I-03).</summary>
        public long RejectedDead { get; private set; }

        /// <summary>Ataques rechazados porque una pared bloqueaba la vista.</summary>
        public long RejectedLineOfSight { get; private set; }

        /// <summary>Ataques rechazados porque el PNJ estaba regresando a casa.</summary>
        public long RejectedEvading { get; private set; }

        /// <summary>Ataques rechazados porque el atacante o el objetivo estaban en zona segura.</summary>
        public long RejectedSafeZone { get; private set; }

        /// <summary>Ataques válidos realizados con un arma equipada.</summary>
        public long AttacksWithWeapon { get; private set; }

        /// <summary>Ataques acertados cuyo alcance base supera dos tiles.</summary>
        public long AttacksRanged { get; private set; }

        /// <summary>Cambios de una marca de botín ya asignada a otro jugador.</summary>
        public long LootOwnerChanges { get; private set; }

        /// <summary>Secundarios golpeados por áreas.</summary>
        public long AreaTargetsHit { get; private set; }
        /// <summary>Áreas con exceso de candidatos válidos.</summary>
        public long AreaCapped { get; private set; }
        /// <summary>Curas realizadas.</summary>
        public long HealsDone { get; private set; }
        /// <summary>Vida realmente curada.</summary>
        public long HealAmount { get; private set; }

        /// <summary>Estados no aplicados por ser más débiles que el activo.</summary>
        public long StatusesIgnoredWeaker { get; private set; }

        /// <summary>Daño absorbido por escudos.</summary>
        public long ShieldAbsorbed { get; private set; }

        /// <summary>Estados aplicados o refrescados por tipo.</summary>
        public long StatusesApplied(StatusKind kind)
        {
            int index = (int)kind;
            return index > 0 && index < _statusApplied.Length ? _statusApplied[index] : 0;
        }
        /// <summary>Habilidades que han resultado válidas (I-16).</summary>
        public long SkillsUsed { get; private set; }

        /// <summary>Contador de rechazos de una habilidad por motivo (I-16).</summary>
        public long SkillsRejected(SkillRejectReason reason)
        {
            int index = (int)reason;
            return index >= 0 && index < _skillRejected.Length ? _skillRejected[index] : 0;
        }

        /// <summary>El servidor aceptó una habilidad; el cliente cuenta la recarga (I-16).</summary>
        public event Action<PlayerEntity, ushort, int>? SkillUsed;

        /// <summary>El servidor rechazó una habilidad; el cliente recibe el motivo (I-16).</summary>
        public event Action<PlayerEntity, ushort, SkillRejectReason>? SkillRejected;

        /// <summary>Aplica las intenciones del tick y limpia las acciones que ya han caducado.</summary>
        /// <param name="entities">Entidades vivas de la zona.</param>
        /// <param name="intents">Ataques que la red dejó en la cola, en orden de llegada.</param>
        /// <param name="tick">Tick de la zona que se está simulando.</param>
        public void Tick(EntityStore entities, List<AttackIntent> intents, long tick)
        {
            if (entities is null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            if (intents is null)
            {
                throw new ArgumentNullException(nameof(intents));
            }

            ClearFinishedAttacks(entities, tick);

            // Regeneración del recurso (I-16): entera, una vez por segundo y solo con catálogo.
            if (_skills != null && tick % SimulationConstants.TickRate == 0)
            {
                RegenerateResources(entities);
            }

            for (int i = 0; i < intents.Count; i++)
            {
                if (intents[i].SkillId == 0)
                {
                    Resolve(entities, intents[i], tick);
                }
                else
                {
                    ResolveSkill(entities, intents[i], tick);
                }
            }
        }

        private int? WeaponDamage(PlayerEntity player)
        {
            ItemType? weapon = player.EquippedWeaponId != 0
                ? _catalog?.Find(player.EquippedWeaponId)
                : null;
            return weapon?.IsWeapon == true ? weapon.Damage : null;
        }

        private void Resolve(EntityStore entities, in AttackIntent intent, long tick)
        {
            // Regla 1: el atacante tiene que ser un jugador de la zona. Un mensaje de una conexión
            // que ya se fue no hace nada; no hay a quién cobrarle la recarga.
            if (!entities.TryGet(intent.AttackerEntityId, out Entity? attackerEntity) ||
                !(attackerEntity is PlayerEntity attacker))
            {
                return;
            }

            // Regla 2: un jugador muerto no puede atacar (I-03). Se comprueba antes del objetivo
            // para que el motivo sea siempre «muerto» mientras dure la muerte.
            if (attacker.IsDead)
            {
                RejectedDead++;
                return;
            }

            TargetCheck targetCheck = CheckTarget(entities, attacker, intent.TargetId, out Entity? target, out PlayerEntity? victim, out NpcEntity? npc);
            switch (targetCheck)
            {
                case TargetCheck.Ok:
                    break;
                case TargetCheck.Missing:
                case TargetCheck.NotNpc:
                    RejectedTarget++;
                    return;
                case TargetCheck.Self:
                    RejectedPvpSelf++;
                    return;
                case TargetCheck.TargetDead:
                    RejectedPvpTargetDead++;
                    return;
                case TargetCheck.PvpSafeZone:
                    RejectedPvpSafeZone++;
                    return;
                case TargetCheck.AttackerPve:
                    RejectedPvpAttackerPve++;
                    return;
                case TargetCheck.TargetPve:
                    RejectedPvpTargetPve++;
                    return;
                case TargetCheck.Evading:
                    RejectedEvading++;
                    return;
                case TargetCheck.NpcSafeZone:
                    RejectedSafeZone++;
                    return;
                default:
                    return;
            }

            Entity hitTarget = target!;

            ItemType? weapon = attacker.EquippedWeaponId != 0
                ? _catalog?.Find(attacker.EquippedWeaponId)
                : null;
            bool hasWeapon = weapon?.IsWeapon == true;
            CharacterClass characterClass = _classes.Find(attacker.ClassId)
                ?? throw new InvalidOperationException("No existe la clase " + attacker.ClassId + " en classes.json.");
            float baseRange = hasWeapon ? weapon!.Range : characterClass.Unarmed!.Range;
            DerivedCharacterStats attackerStats = CharacterStats.Calculate(
                _classes, attacker.ClassId, attacker.Level, attacker.AllocStr, attacker.AllocDex,
                attacker.AllocInt, attacker.AllocVit, _catalog, attacker.Equipment);

            // Regla 4: la distancia entre centros cabe en alcance + tolerancia y hay línea de visión.
            ReachCheck reach = CheckReach(attacker, hitTarget, baseRange);
            if (reach == ReachCheck.Distance)
            {
                RejectedDistance++;
                return;
            }

            if (reach == ReachCheck.LineOfSight)
            {
                RejectedLineOfSight++;
                return;
            }

            // Regla 5: recarga medida en ticks de la zona y solo desde el último ataque válido.
            long lastAttackTick = attacker.LastAttackTick;

            long cooldownTicks = hasWeapon
                ? CombatMath.AttackCooldownTicks(weapon!.CooldownSeconds, attackerStats.HastePb, _classes.MinAttackTicks)
                : CombatMath.AttackCooldownTicks(characterClass.Unarmed!.CooldownSeconds, attackerStats.HastePb, _classes.MinAttackTicks);
            if (lastAttackTick >= 0 && tick - lastAttackTick < cooldownTicks)
            {
                RejectedCooldown++;
                return;
            }

            int damage = CombatMath.GetAttackDamage(
                _classes,
                attacker.ClassId,
                attacker.Level,
                attacker.AllocStr,
                attacker.AllocDex,
                attacker.AllocInt,
                attacker.AllocVit,
                hasWeapon ? weapon!.Damage : null,
                out string damageType,
                _catalog,
                attacker.Equipment,
                hasWeapon ? weapon!.DamageType : null,
                attacker.Statuses.Bonus);

            ApplyDamage(entities, attacker, hitTarget, victim, npc, damage, damageType,
                baseRange > RangedMinRange, attackerStats.CritChancePb, _classes.CritMultiplier, tick);

            attacker.LastAttackTick = tick;
            AttacksOk++;
            if (victim != null)
            {
                PvpAttacksOk++;
            }
            if (hasWeapon)
            {
                AttacksWithWeapon++;
            }

            if (baseRange > RangedMinRange)
            {
                AttacksRanged++;
            }
        }

        private void ResolveSkill(EntityStore entities, in AttackIntent intent, long tick)
        {
            // Sin atacante no hay a quién contestar, igual que en Resolve.
            if (!entities.TryGet(intent.AttackerEntityId, out Entity? attackerEntity) ||
                !(attackerEntity is PlayerEntity caster))
            {
                return;
            }

            SkillDefinition? skill = _skills?.Find(intent.SkillId);
            if (skill == null)
            {
                Reject(caster, intent.SkillId, SkillRejectReason.UnknownSkill);
                return;
            }

            if (skill.ClassId != caster.ClassId)
            {
                Reject(caster, intent.SkillId, SkillRejectReason.WrongClass);
                return;
            }

            if (caster.Level < skill.RequiredLevel)
            {
                Reject(caster, intent.SkillId, SkillRejectReason.LevelTooLow);
                return;
            }

            if (caster.IsDead)
            {
                Reject(caster, intent.SkillId, SkillRejectReason.Dead);
                return;
            }

            if (skill.IsHeal)
            {
                ResolveHeal(entities, caster, skill, intent, tick);
                return;
            }

            if (skill.IsBeneficial)
            {
                ResolveBeneficial(entities, caster, skill, intent, tick);
                return;
            }

            if (skill.AreaSelf)
            {
                ResolveSelfArea(entities, caster, skill, intent, tick);
                return;
            }

            TargetCheck targetCheck = CheckTarget(entities, caster, intent.TargetId, out Entity? target, out PlayerEntity? victim, out NpcEntity? npc);
            switch (targetCheck)
            {
                case TargetCheck.Ok:
                    break;
                case TargetCheck.PvpSafeZone:
                case TargetCheck.NpcSafeZone:
                    Reject(caster, intent.SkillId, SkillRejectReason.SafeZone);
                    return;
                case TargetCheck.AttackerPve:
                case TargetCheck.TargetPve:
                    Reject(caster, intent.SkillId, SkillRejectReason.PvpNotAllowed);
                    return;
                default:
                    Reject(caster, intent.SkillId, SkillRejectReason.InvalidTarget);
                    return;
            }

            Entity hitTarget = target!;
            if (skill.StatusKind == StatusKind.Taunt && npc == null)
            {
                Reject(caster, intent.SkillId, SkillRejectReason.InvalidTarget);
                return;
            }

            ReachCheck reach = CheckReach(caster, hitTarget, skill.Range);
            if (reach == ReachCheck.Distance)
            {
                Reject(caster, intent.SkillId, SkillRejectReason.OutOfRange);
                return;
            }

            if (reach == ReachCheck.LineOfSight)
            {
                Reject(caster, intent.SkillId, SkillRejectReason.NoLineOfSight);
                return;
            }

            if (OnCooldown(caster, skill, tick))
            {
                Reject(caster, intent.SkillId, SkillRejectReason.Cooldown);
                return;
            }

            if (caster.Mana < skill.ResourceCost)
            {
                Reject(caster, intent.SkillId, SkillRejectReason.NotEnoughResource);
                return;
            }

            caster.Mana -= skill.ResourceCost;
            StartCooldown(caster, skill, tick);

            if (skill.IsDamage)
            {
                int found = skill.Radius > 0f
                    ? CollectAreaTargets(entities, caster, hitTarget, skill, skill.MaxTargets - 1)
                    : 0;
                int damage = CombatMath.GetSkillAmount(
                    _classes,
                    caster.ClassId,
                    caster.Level,
                    caster.AllocStr,
                    caster.AllocDex,
                    caster.AllocInt,
                    caster.AllocVit,
                    skill.Damage,
                    skill.WeaponPercent,
                    WeaponDamage(caster),
                    _catalog,
                    caster.Equipment,
                    caster.Statuses.Bonus);
                string damageType = skill.DamageType;
                int critChancePb = CharacterStats.Calculate(_classes, caster.ClassId, caster.Level,
                    caster.AllocStr, caster.AllocDex, caster.AllocInt, caster.AllocVit,
                    _catalog, caster.Equipment).CritChancePb;

                for (int hit = 0; hit < skill.Hits; hit++)
                {
                    if (hit > 0 && hitTarget.Action == EntityAction.Dead) break;
                    ApplyDamage(entities, caster, hitTarget, victim, npc, damage, damageType,
                        skill.Range > RangedMinRange, critChancePb, _classes.CritMultiplier + skill.CritBonus, tick);
                }

                for (int k = 0; k < found; k++)
                {
                    if (CheckTarget(entities, caster, _areaIds[k], out Entity? t, out PlayerEntity? v, out NpcEntity? n) != TargetCheck.Ok)
                        continue;
                    ApplyDamage(entities, caster, t!, v, n, damage, damageType,
                        skill.Range > RangedMinRange, critChancePb, _classes.CritMultiplier + skill.CritBonus, tick);
                }
                AreaTargetsHit += found;
            }
            else
            {
                caster.Action = EntityAction.Attacking;
                caster.ActionUntilTick = tick + AttackingTicks;
            }

            if (skill.StatusKind != StatusKind.None)
                ApplyHarmfulStatus(caster, hitTarget, npc, skill, tick);
            SkillsUsed++;
            SkillUsed?.Invoke(caster, (ushort)skill.Id, hitTarget.Id);
        }

        private void ApplyHarmfulStatus(PlayerEntity caster, Entity target, NpcEntity? npc, SkillDefinition skill, long tick)
        {
            if (target.Action == EntityAction.Dead) return;
            StatusKind kind = skill.StatusKind;
            bool applied = target.Statuses.Apply(kind, (ushort)skill.Id, caster.Id, skill.StatusAmount,
                tick + skill.StatusTicks);
            if (applied) _statusApplied[(int)kind]++;
            else StatusesIgnoredWeaker++;

            if (npc == null) return;
            if (kind == StatusKind.Taunt && applied &&
                (npc.AiState == NpcAiState.Wander || npc.TargetEntityId != caster.Id))
            {
                npc.AiState = NpcAiState.Chase;
                npc.TargetEntityId = caster.Id;
            }
            else if (kind != StatusKind.Taunt && npc.AiState == NpcAiState.Wander)
            {
                npc.AiState = NpcAiState.Chase;
                npc.TargetEntityId = caster.Id;
            }
        }

        private void ResolveSelfArea(EntityStore entities, PlayerEntity caster, SkillDefinition skill,
            in AttackIntent intent, long tick)
        {
            if (OnCooldown(caster, skill, tick))
            {
                Reject(caster, intent.SkillId, SkillRejectReason.Cooldown);
                return;
            }
            if (caster.Mana < skill.ResourceCost)
            {
                Reject(caster, intent.SkillId, SkillRejectReason.NotEnoughResource);
                return;
            }

            int found = CollectAreaTargets(entities, caster, caster, skill, skill.MaxTargets);
            if (found == 0)
            {
                Reject(caster, intent.SkillId, SkillRejectReason.InvalidTarget);
                return;
            }

            caster.Mana -= skill.ResourceCost;
            StartCooldown(caster, skill, tick);
            int damage = CombatMath.GetSkillAmount(_classes, caster.ClassId, caster.Level,
                caster.AllocStr, caster.AllocDex, caster.AllocInt, caster.AllocVit, skill.Damage,
                skill.WeaponPercent, WeaponDamage(caster), _catalog, caster.Equipment, caster.Statuses.Bonus);
            int critChancePb = CharacterStats.Calculate(_classes, caster.ClassId, caster.Level,
                caster.AllocStr, caster.AllocDex, caster.AllocInt, caster.AllocVit,
                _catalog, caster.Equipment).CritChancePb;
            for (int k = 0; k < found; k++)
            {
                if (CheckTarget(entities, caster, _areaIds[k], out Entity? target,
                    out PlayerEntity? victim, out NpcEntity? npc) != TargetCheck.Ok)
                    continue;
                ApplyDamage(entities, caster, target!, victim, npc, damage, skill.DamageType,
                    false, critChancePb, _classes.CritMultiplier + skill.CritBonus, tick);
            }
            AreaTargetsHit += found;
            SkillsUsed++;
            SkillUsed?.Invoke(caster, (ushort)skill.Id, caster.Id);
        }

        private int CollectAreaTargets(EntityStore entities, PlayerEntity caster, Entity center,
            SkillDefinition skill, int limit)
        {
            int count = 0;
            bool capped = false;
            float r2 = skill.Radius * skill.Radius;
            for (int i = 0; i < entities.Count; i++)
            {
                Entity e = entities[i];
                if (e.Id == center.Id) continue;
                float dx = e.Position.X - center.Position.X;
                float dy = e.Position.Y - center.Position.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 > r2 || CheckTarget(entities, caster, e.Id, out _, out _, out _) != TargetCheck.Ok) continue;
                if (_map != null && !_map.HasLineOfSight(center.Position, e.Position)) continue;
                if (count == limit)
                {
                    capped = true;
                    if (d2 >= _areaDistSq[count - 1]) continue;
                }
                else count++;
                int slot = count - 1;
                while (slot > 0 && d2 < _areaDistSq[slot - 1])
                {
                    _areaIds[slot] = _areaIds[slot - 1];
                    _areaDistSq[slot] = _areaDistSq[slot - 1];
                    slot--;
                }
                _areaIds[slot] = e.Id;
                _areaDistSq[slot] = d2;
            }
            if (capped) AreaCapped++;
            return count;
        }

        private void ResolveHeal(EntityStore entities, PlayerEntity caster, SkillDefinition skill, in AttackIntent intent, long tick)
        {
            if (skill.Radius > 0)
            {
                ResolveAreaHeal(entities, caster, skill, intent, tick);
                return;
            }
            if (!CheckFriendlyTarget(entities, caster, skill, intent, out PlayerEntity? target)) return;
            PlayerEntity healTarget = target!;
            if (healTarget.Health >= healTarget.MaxHealth)
            {
                Reject(caster, intent.SkillId, SkillRejectReason.FullHealth);
                return;
            }
            if (OnCooldown(caster, skill, tick))
            {
                Reject(caster, intent.SkillId, SkillRejectReason.Cooldown);
                return;
            }
            if (caster.Mana < skill.ResourceCost)
            {
                Reject(caster, intent.SkillId, SkillRejectReason.NotEnoughResource);
                return;
            }
            caster.Mana -= skill.ResourceCost;
            StartCooldown(caster, skill, tick);
            int raw = CombatMath.GetSkillAmount(_classes, caster.ClassId, caster.Level, caster.AllocStr,
                caster.AllocDex, caster.AllocInt, caster.AllocVit, skill.Damage, skill.WeaponPercent,
                WeaponDamage(caster), _catalog, caster.Equipment);
            int critChancePb = CharacterStats.Calculate(_classes, caster.ClassId, caster.Level,
                caster.AllocStr, caster.AllocDex, caster.AllocInt, caster.AllocVit,
                _catalog, caster.Equipment).CritChancePb;
            bool crit = critChancePb > 0 && _random.Next(10000) < critChancePb;
            if (crit) raw = CombatMath.CritDamage(raw, _classes.CritMultiplier + skill.CritBonus);
            int healed = Math.Min(raw, healTarget.MaxHealth - healTarget.Health);
            healTarget.Health += healed;
            HealsDone++;
            HealAmount += healed;
            CombatEventFlags flags = CombatEventFlags.Heal;
            if (crit) flags |= CombatEventFlags.Critical;
            _events?.Add(new CombatEventEntry(caster.Id, healTarget.Id,
                (ushort)Math.Min(healed, ushort.MaxValue), flags));
            SkillsUsed++;
            SkillUsed?.Invoke(caster, (ushort)skill.Id, healTarget.Id);
        }

        private void ResolveAreaHeal(EntityStore entities, PlayerEntity caster, SkillDefinition skill,
            in AttackIntent intent, long tick)
        {
            int found = CollectAreaAllies(entities, caster, skill);
            if (skill.StatusKind == StatusKind.None)
            {
                bool injured = false;
                for (int i = 0; i < found; i++)
                    if (entities.TryGet(_areaIds[i], out Entity? entity) && entity is PlayerEntity player &&
                        player.Health < player.MaxHealth)
                    {
                        injured = true;
                        break;
                    }
                if (!injured)
                {
                    Reject(caster, intent.SkillId, SkillRejectReason.FullHealth);
                    return;
                }
            }
            if (OnCooldown(caster, skill, tick))
            {
                Reject(caster, intent.SkillId, SkillRejectReason.Cooldown);
                return;
            }
            if (caster.Mana < skill.ResourceCost)
            {
                Reject(caster, intent.SkillId, SkillRejectReason.NotEnoughResource);
                return;
            }

            caster.Mana -= skill.ResourceCost;
            StartCooldown(caster, skill, tick);
            int raw = CombatMath.GetSkillAmount(_classes, caster.ClassId, caster.Level, caster.AllocStr,
                caster.AllocDex, caster.AllocInt, caster.AllocVit, skill.Damage, skill.WeaponPercent,
                WeaponDamage(caster), _catalog, caster.Equipment);
            int critChancePb = CharacterStats.Calculate(_classes, caster.ClassId, caster.Level,
                caster.AllocStr, caster.AllocDex, caster.AllocInt, caster.AllocVit,
                _catalog, caster.Equipment).CritChancePb;
            int shieldAmount = skill.StatusKind == StatusKind.Shield
                ? CombatMath.GetSkillAmount(_classes, caster.ClassId, caster.Level, caster.AllocStr,
                    caster.AllocDex, caster.AllocInt, caster.AllocVit, skill.StatusAmount,
                    skill.WeaponPercent, WeaponDamage(caster), _catalog, caster.Equipment)
                : 0;

            for (int i = 0; i < found; i++)
            {
                if (!entities.TryGet(_areaIds[i], out Entity? entity) || !(entity is PlayerEntity ally))
                    continue;
                bool crit = critChancePb > 0 && _random.Next(10000) < critChancePb;
                int amount = crit ? CombatMath.CritDamage(raw, _classes.CritMultiplier + skill.CritBonus) : raw;
                int healed = Math.Min(amount, ally.MaxHealth - ally.Health);
                if (healed > 0)
                {
                    ally.Health += healed;
                    HealsDone++;
                    HealAmount += healed;
                    CombatEventFlags flags = CombatEventFlags.Heal;
                    if (crit) flags |= CombatEventFlags.Critical;
                    _events?.Add(new CombatEventEntry(caster.Id, ally.Id,
                        (ushort)Math.Min(healed, ushort.MaxValue), flags));
                }
                if (skill.StatusKind == StatusKind.Shield)
                {
                    bool applied = ally.Statuses.Apply(skill.StatusKind, (ushort)skill.Id, caster.Id,
                        shieldAmount, tick + skill.StatusTicks);
                    if (applied) _statusApplied[(int)skill.StatusKind]++;
                    else StatusesIgnoredWeaker++;
                }
            }
            SkillsUsed++;
            SkillUsed?.Invoke(caster, (ushort)skill.Id, caster.Id);
        }

        private int CollectAreaAllies(EntityStore entities, PlayerEntity caster, SkillDefinition skill)
        {
            int count = 1;
            int limit = skill.MaxTargets;
            float radiusSquared = skill.Radius * skill.Radius;
            _areaIds[0] = caster.Id;
            _areaDistSq[0] = 0;
            bool capped = false;
            ZoneKind casterZone = _zones.KindAt(caster.Position.X, caster.Position.Y);
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i];
                if (!(entity is PlayerEntity player) || player.Id == caster.Id || player.IsDead) continue;
                float dx = player.Position.X - caster.Position.X;
                float dy = player.Position.Y - caster.Position.Y;
                float distanceSquared = dx * dx + dy * dy;
                if (distanceSquared > radiusSquared) continue;
                ZoneKind playerZone = _zones.KindAt(player.Position.X, player.Position.Y);
                if (player.IsPvpEffective(playerZone) && !caster.IsPvpEffective(casterZone)) continue;
                if (_map != null && !_map.HasLineOfSight(caster.Position, player.Position)) continue;
                if (count == limit)
                {
                    capped = true;
                    if (distanceSquared >= _areaDistSq[count - 1]) continue;
                }
                else count++;
                int slot = count - 1;
                while (slot > 0 && distanceSquared < _areaDistSq[slot - 1])
                {
                    _areaIds[slot] = _areaIds[slot - 1];
                    _areaDistSq[slot] = _areaDistSq[slot - 1];
                    slot--;
                }
                _areaIds[slot] = player.Id;
                _areaDistSq[slot] = distanceSquared;
            }
            if (capped) AreaCapped++;
            return count;
        }

        private bool CheckFriendlyTarget(EntityStore entities, PlayerEntity caster, SkillDefinition skill,
            in AttackIntent intent, out PlayerEntity? target)
        {
            target = null;
            if (!entities.TryGet(intent.TargetId, out Entity? entity) || !(entity is PlayerEntity player) || player.IsDead)
            {
                Reject(caster, intent.SkillId, SkillRejectReason.InvalidTarget);
                return false;
            }
            if (player != caster)
            {
                ZoneKind casterZone = _zones.KindAt(caster.Position.X, caster.Position.Y);
                ZoneKind targetZone = _zones.KindAt(player.Position.X, player.Position.Y);
                if (player.IsPvpEffective(targetZone) && !caster.IsPvpEffective(casterZone))
                {
                    Reject(caster, intent.SkillId, SkillRejectReason.PvpNotAllowed);
                    return false;
                }
                ReachCheck reach = CheckReach(caster, player, skill.Range);
                if (reach != ReachCheck.Ok)
                {
                    Reject(caster, intent.SkillId, reach == ReachCheck.Distance
                        ? SkillRejectReason.OutOfRange : SkillRejectReason.NoLineOfSight);
                    return false;
                }
            }
            target = player;
            return true;
        }

        private void ResolveBeneficial(EntityStore entities, PlayerEntity caster, SkillDefinition skill,
            in AttackIntent intent, long tick)
        {
            PlayerEntity? target;
            if (skill.AreaSelf) target = caster;
            else if (!CheckFriendlyTarget(entities, caster, skill, intent, out target)) return;
            if (OnCooldown(caster, skill, tick))
            {
                Reject(caster, intent.SkillId, SkillRejectReason.Cooldown);
                return;
            }
            if (caster.Mana < skill.ResourceCost)
            {
                Reject(caster, intent.SkillId, SkillRejectReason.NotEnoughResource);
                return;
            }

            caster.Mana -= skill.ResourceCost;
            StartCooldown(caster, skill, tick);
            int amount = skill.StatusKind == StatusKind.Shield
                ? CombatMath.GetSkillAmount(_classes, caster.ClassId, caster.Level, caster.AllocStr,
                    caster.AllocDex, caster.AllocInt, caster.AllocVit, skill.StatusAmount,
                    skill.WeaponPercent, WeaponDamage(caster), _catalog, caster.Equipment)
                : skill.StatusAmount;
            bool applied = target!.Statuses.Apply(skill.StatusKind, (ushort)skill.Id, caster.Id, amount,
                tick + skill.StatusTicks);
            if (applied) _statusApplied[(int)skill.StatusKind]++;
            else StatusesIgnoredWeaker++;
            SkillsUsed++;
            SkillUsed?.Invoke(caster, (ushort)skill.Id, target.Id);
        }

        private void Reject(PlayerEntity caster, ushort skillId, SkillRejectReason reason)
        {
            _skillRejected[(int)reason]++;
            SkillRejected?.Invoke(caster, skillId, reason);
        }

        private bool OnCooldown(PlayerEntity caster, SkillDefinition skill, long tick) =>
            tick < caster.GlobalSkillReadyTick ||
            (caster.SkillReadyTicks.TryGetValue((ushort)skill.Id, out long readyAt) && tick < readyAt);

        private void StartCooldown(PlayerEntity caster, SkillDefinition skill, long tick)
        {
            caster.SkillReadyTicks[(ushort)skill.Id] = tick + skill.CooldownTicks;
            caster.GlobalSkillReadyTick = tick + _skills!.GlobalCooldownTicks;
        }

        private enum TargetCheck
        {
            Ok,
            Missing,
            Self,
            TargetDead,
            PvpSafeZone,
            AttackerPve,
            TargetPve,
            NotNpc,
            Evading,
            NpcSafeZone,
        }

        /// <summary>
        /// Reglas de objetivo compartidas por el ataque básico y las habilidades: existencia, PvP
        /// efectivo, zonas seguras y estados del PNJ.
        /// </summary>
        private TargetCheck CheckTarget(
            EntityStore entities,
            PlayerEntity attacker,
            int targetId,
            out Entity? target,
            out PlayerEntity? victim,
            out NpcEntity? npc)
        {
            target = null;
            victim = null;
            npc = null;

            if (!entities.TryGet(targetId, out Entity? targetEntity))
            {
                return TargetCheck.Missing;
            }

            target = targetEntity;
            victim = targetEntity as PlayerEntity;
            npc = targetEntity as NpcEntity;
            if (victim != null)
            {
                if (victim.Id == attacker.Id)
                {
                    return TargetCheck.Self;
                }

                if (victim.IsDead)
                {
                    return TargetCheck.TargetDead;
                }

                ZoneKind attackerZone = _zones.KindAt(attacker.Position.X, attacker.Position.Y);
                ZoneKind victimZone = _zones.KindAt(victim.Position.X, victim.Position.Y);
                if (attackerZone == ZoneKind.Safe || victimZone == ZoneKind.Safe)
                {
                    return TargetCheck.PvpSafeZone;
                }

                if (!attacker.IsPvpEffective(attackerZone))
                {
                    return TargetCheck.AttackerPve;
                }

                if (!victim.IsPvpEffective(victimZone))
                {
                    return TargetCheck.TargetPve;
                }
            }
            else if (npc == null || npc.Action == EntityAction.Dead)
            {
                return TargetCheck.NotNpc;
            }

            if (npc != null && npc.AiState == NpcAiState.Return)
            {
                return TargetCheck.Evading;
            }

            if (victim == null &&
                (_zones.KindAt(attacker.Position.X, attacker.Position.Y) == ZoneKind.Safe ||
                 _zones.KindAt(target.Position.X, target.Position.Y) == ZoneKind.Safe))
            {
                return TargetCheck.NpcSafeZone;
            }

            return TargetCheck.Ok;
        }

        private enum ReachCheck
        {
            Ok,
            Distance,
            LineOfSight,
        }

        /// <summary>Distancia efectiva (alcance base + tolerancia) y línea de visión, compartidas.</summary>
        private ReachCheck CheckReach(Entity attacker, Entity target, float baseRange)
        {
            float dx = target.Position.X - attacker.Position.X;
            float dy = target.Position.Y - attacker.Position.Y;
            float range = baseRange + _settings.RangeTolerance;

            if ((dx * dx) + (dy * dy) > range * range)
            {
                return ReachCheck.Distance;
            }

            if (_map != null && !_map.HasLineOfSight(attacker.Position, target.Position))
            {
                return ReachCheck.LineOfSight;
            }

            return ReachCheck.Ok;
        }

        /// <summary>
        /// Mitigación, vida, muerte, botín, aggro y evento de combate, compartidos por el ataque
        /// básico y las habilidades. El daño ya viene calculado y sin mitigar.
        /// </summary>
        private void ApplyDamage(
            EntityStore entities,
            PlayerEntity attacker,
            Entity target,
            PlayerEntity? victim,
            NpcEntity? npc,
            int rawDamage,
            string damageType,
            bool ranged,
            int critChancePb,
            int critMultiplier,
            long tick)
        {
            int damage = rawDamage;
            bool crit = critChancePb > 0 && _random.Next(10000) < critChancePb;
            if (crit)
            {
                damage = CombatMath.CritDamage(damage, critMultiplier);
            }
            int defense;
            if (victim != null)
            {
                DerivedCharacterStats victimStats = CharacterStats.Calculate(
                    _classes,
                    victim.ClassId,
                    victim.Level,
                    victim.AllocStr,
                    victim.AllocDex,
                    victim.AllocInt,
                    victim.AllocVit,
                    _catalog,
                    victim.Equipment);
                defense = (damageType == "physical" ? victimStats.Defense : victimStats.MagicResist) + victim.Statuses.Bonus;
            }
            else
            {
                NpcEntity targetNpc = npc!;
                defense = damageType == "physical" ? targetNpc.Defense : targetNpc.MagicResist;
            }

            damage = CombatMath.MitigateDamage(damage, defense);
            if (victim != null)
            {
                damage = CombatMath.PvpDamage(damage, _classes.PvpDamagePercent);
            }
            int absorbed = target.Statuses.Absorb(damage);
            damage -= absorbed;
            ShieldAbsorbed += absorbed;
            if (victim != null)
            {
                victim.Health = Math.Max(0, victim.Health - damage);
                victim.LastDamageTick = tick;
                victim.RegenAccumulator = 0d;
                if (victim.Health == 0)
                {
                    victim.Action = EntityAction.Dead;
                    if (victim.DeathTick < 0)
                    {
                        victim.DeathTick = tick;
                    }

                    PvpKills++;
                }
            }
            else
            {
                NpcEntity targetNpc = npc!;
                targetNpc.Health = Math.Max(0, targetNpc.Health - damage);
                targetNpc.LastAttackerPlayerId = attacker.Id;
                if (targetNpc.LootOwnerId == attacker.Id)
                {
                    targetNpc.LootOwnerLastHitTick = tick;
                }

                else if (targetNpc.LootOwnerId <= 0 ||
                    tick - targetNpc.LootOwnerLastHitTick >= _settings.LootTagTimeoutTicks ||
                    !entities.TryGet(targetNpc.LootOwnerId, out Entity? ownerEntity) || ownerEntity is not PlayerEntity)
                {
                    if (targetNpc.LootOwnerId > 0)
                    {
                        LootOwnerChanges++;
                    }

                    targetNpc.LootOwnerId = attacker.Id;
                    targetNpc.LootOwnerLastHitTick = tick;
                }

                if (targetNpc.Health == 0)
                {
                    // La baja definitiva y la reaparición las resuelve NpcSystem en este tick.
                    targetNpc.Action = EntityAction.Dead;
                    targetNpc.KillerPlayerId = attacker.Id;
                }
                else if (targetNpc.AiState == NpcAiState.Wander)
                {
                    // Aggro al recibir daño (I-03); un PNJ en Return no cambia de objetivo.
                    targetNpc.AiState = NpcAiState.Chase;
                    targetNpc.TargetEntityId = attacker.Id;
                }
            }

            attacker.Action = EntityAction.Attacking;
            attacker.ActionUntilTick = tick + AttackingTicks;

            if (_events != null)
            {
                if (absorbed > 0)
                {
                    _events.Add(new CombatEventEntry(attacker.Id, target.Id,
                        (ushort)Math.Min(absorbed, ushort.MaxValue), CombatEventFlags.Absorbed));
                }

                CombatEventFlags flags = ranged ? CombatEventFlags.Ranged : CombatEventFlags.None;
                if (damageType == "magical")
                {
                    flags |= CombatEventFlags.Magical;
                }
                if (crit)
                {
                    flags |= CombatEventFlags.Critical;
                }

                if ((victim?.Health ?? npc!.Health) == 0)
                {
                    flags |= CombatEventFlags.Killed;
                }

                if (damage > 0)
                {
                    _events.Add(new CombatEventEntry(
                        attacker.Id,
                        target.Id,
                        (ushort)Math.Min(damage, ushort.MaxValue),
                        flags));
                }
            }
        }

        /// <summary>Suma el recurso de la clase una vez por segundo a los jugadores vivos (I-16).</summary>
        private void RegenerateResources(EntityStore entities)
        {
            for (int i = 0; i < entities.Count; i++)
            {
                if (!(entities[i] is PlayerEntity player) || player.IsDead)
                {
                    continue;
                }

                CharacterClass? characterClass = _classes.Find(player.ClassId);
                if (characterClass?.Resource == null)
                {
                    continue;
                }

                int regen = characterClass.Resource.RegenPerSecond;
                if (regen <= 0 || player.Mana >= player.MaxMana)
                {
                    continue;
                }

                player.Mana = Math.Min(player.MaxMana, player.Mana + regen);
            }
        }

        /// <summary>Devuelve a <see cref="EntityAction.None"/> lo que ya no está atacando.</summary>
        private static void ClearFinishedAttacks(EntityStore entities, long tick)
        {
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i];

                if (entity.Action == EntityAction.Attacking && tick >= entity.ActionUntilTick)
                {
                    entity.Action = EntityAction.None;
                }
            }
        }
    }
}
