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
    /// Resuelve los ataques de los jugadores (I-02): el cliente pide y el servidor decide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Un ataque solo vale si el atacante es un jugador de la zona, el objetivo existe, es un PNJ
    /// vivo, está dentro del alcance efectivo y el jugador ha respetado su recarga. La recarga se
    /// cuenta en ticks de la zona, nunca con el reloj del cliente ni con el del servidor.
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

        public CombatSystem(
            CombatSettings settings,
            ItemCatalog? catalog = null,
            ClassCatalog? classes = null,
            MapCollisionGrid? map = null,
            CombatEventBuffer? events = null,
            ZoneMap? zones = null)
        {
            _settings = settings;
            _catalog = catalog;
            _classes = classes ?? ClassCatalog.Default;
            _map = map;
            _events = events;
            _zones = zones ?? ZoneMap.Empty;
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

            for (int i = 0; i < intents.Count; i++)
            {
                Resolve(entities, intents[i], tick);
            }
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

            if (!entities.TryGet(intent.TargetId, out Entity? targetEntity))
            {
                RejectedTarget++;
                return;
            }

            Entity target = targetEntity;
            PlayerEntity? victim = targetEntity as PlayerEntity;
            NpcEntity? npc = targetEntity as NpcEntity;
            if (victim != null)
            {
                if (victim.Id == attacker.Id)
                {
                    RejectedPvpSelf++;
                    return;
                }

                if (victim.IsDead)
                {
                    RejectedPvpTargetDead++;
                    return;
                }

                ZoneKind attackerZone = _zones.KindAt(attacker.Position.X, attacker.Position.Y);
                ZoneKind victimZone = _zones.KindAt(victim.Position.X, victim.Position.Y);
                if (attackerZone == ZoneKind.Safe || victimZone == ZoneKind.Safe)
                {
                    RejectedPvpSafeZone++;
                    return;
                }

                if (!attacker.IsPvpEffective(attackerZone))
                {
                    RejectedPvpAttackerPve++;
                    return;
                }

                if (!victim.IsPvpEffective(victimZone))
                {
                    RejectedPvpTargetPve++;
                    return;
                }
            }
            else if (npc == null || npc.Action == EntityAction.Dead)
            {
                RejectedTarget++;
                return;
            }

            if (npc != null && npc.AiState == NpcAiState.Return)
            {
                RejectedEvading++;
                return;
            }

            if (victim == null &&
                (_zones.KindAt(attacker.Position.X, attacker.Position.Y) == ZoneKind.Safe ||
                 _zones.KindAt(target.Position.X, target.Position.Y) == ZoneKind.Safe))
            {
                RejectedSafeZone++;
                return;
            }

            // Regla 4: la distancia entre centros cabe en alcance + tolerancia.
            float dx = target.Position.X - attacker.Position.X;
            float dy = target.Position.Y - attacker.Position.Y;
            ItemType? weapon = attacker.EquippedWeaponId != 0
                ? _catalog?.Find(attacker.EquippedWeaponId)
                : null;
            bool hasWeapon = weapon?.IsWeapon == true;
            CharacterClass characterClass = _classes.Find(attacker.ClassId)
                ?? throw new InvalidOperationException("No existe la clase " + attacker.ClassId + " en classes.json.");
            float range = (hasWeapon ? weapon!.Range : characterClass.Unarmed!.Range) + _settings.RangeTolerance;
            float baseRange = hasWeapon ? weapon!.Range : characterClass.Unarmed!.Range;

            if ((dx * dx) + (dy * dy) > range * range)
            {
                RejectedDistance++;
                return;
            }

            if (_map != null && !_map.HasLineOfSight(attacker.Position, target.Position))
            {
                RejectedLineOfSight++;
                return;
            }

            // Regla 5: recarga medida en ticks de la zona y solo desde el último ataque válido.
            long lastAttackTick = attacker.LastAttackTick;

            long cooldownTicks = hasWeapon
                ? Math.Max(1L, (long)Math.Ceiling(weapon!.CooldownSeconds * SimulationConstants.TickRate))
                : Math.Max(1L, (long)Math.Ceiling(characterClass.Unarmed!.CooldownSeconds * SimulationConstants.TickRate));
            if (lastAttackTick >= 0 && tick - lastAttackTick < cooldownTicks)
            {
                RejectedCooldown++;
                return;
            }

            int damage = CharacterStats.GetAttackDamage(
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
                hasWeapon ? weapon!.DamageType : null);
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
                defense = damageType == "physical" ? victimStats.Defense : victimStats.MagicResist;
            }
            else
            {
                NpcEntity targetNpc = npc!;
                defense = damageType == "physical" ? targetNpc.Defense : targetNpc.MagicResist;
            }

            damage = CharacterStats.MitigateDamage(damage, defense);
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

            attacker.LastAttackTick = tick;
            attacker.Action = EntityAction.Attacking;
            attacker.ActionUntilTick = tick + AttackingTicks;
            AttacksOk++;
            if (victim != null)
            {
                PvpAttacksOk++;
            }
            if (hasWeapon)
            {
                AttacksWithWeapon++;
            }

            bool ranged = baseRange > RangedMinRange;
            if (ranged)
            {
                AttacksRanged++;
            }

            if (_events != null)
            {
                CombatEventFlags flags = ranged ? CombatEventFlags.Ranged : CombatEventFlags.None;
                if (damageType == "magical")
                {
                    flags |= CombatEventFlags.Magical;
                }

                if ((victim?.Health ?? npc!.Health) == 0)
                {
                    flags |= CombatEventFlags.Killed;
                }

                _events.Add(new CombatEventEntry(
                    attacker.Id,
                    target.Id,
                    (ushort)Math.Min(damage, ushort.MaxValue),
                    flags));
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
