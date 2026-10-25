#nullable enable
using System;
using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Shared;

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
        /// <summary>Ticks que dura la acción <see cref="EntityAction.Attacking"/> (6 = 0,3 s a 20 Hz).</summary>
        public const int AttackingTicks = 6;

        private readonly CombatSettings _settings;
        private readonly ItemCatalog? _catalog;

        public CombatSystem(CombatSettings settings, ItemCatalog? catalog = null)
        {
            _settings = settings;
            _catalog = catalog;
        }

        /// <summary>Ataques que han resultado válidos.</summary>
        public long AttacksOk { get; private set; }

        /// <summary>Ataques rechazados porque el objetivo no existía, no era un PNJ o estaba muerto.</summary>
        public long RejectedTarget { get; private set; }

        /// <summary>Ataques rechazados porque el objetivo estaba fuera de alcance.</summary>
        public long RejectedDistance { get; private set; }

        /// <summary>Ataques rechazados porque el jugador todavía no había recargado.</summary>
        public long RejectedCooldown { get; private set; }

        /// <summary>Ataques rechazados porque el jugador estaba muerto (I-03).</summary>
        public long RejectedDead { get; private set; }

        /// <summary>Ataques válidos realizados con un arma equipada.</summary>
        public long AttacksWithWeapon { get; private set; }

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

            // Regla 3: el objetivo existe, es un PNJ y está vivo.
            if (!entities.TryGet(intent.TargetId, out Entity? targetEntity) ||
                !(targetEntity is NpcEntity target) ||
                target.Action == EntityAction.Dead)
            {
                RejectedTarget++;
                return;
            }

            // Regla 4: la distancia entre centros cabe en alcance + tolerancia.
            float dx = target.Position.X - attacker.Position.X;
            float dy = target.Position.Y - attacker.Position.Y;
            ItemType? weapon = attacker.EquippedWeaponId != 0
                ? _catalog?.Find(attacker.EquippedWeaponId)
                : null;
            bool hasWeapon = weapon?.IsWeapon == true;
            float range = (hasWeapon ? weapon!.Range : _settings.Range) + _settings.RangeTolerance;

            if ((dx * dx) + (dy * dy) > range * range)
            {
                RejectedDistance++;
                return;
            }

            // Regla 5: recarga medida en ticks de la zona y solo desde el último ataque válido.
            long lastAttackTick = attacker.LastAttackTick;

            long cooldownTicks = hasWeapon
                ? Math.Max(1L, (long)Math.Ceiling(weapon!.CooldownSeconds * SimulationConstants.TickRate))
                : _settings.CooldownTicks;
            if (lastAttackTick >= 0 && tick - lastAttackTick < cooldownTicks)
            {
                RejectedCooldown++;
                return;
            }

            int damage = hasWeapon ? weapon!.Damage : _settings.Damage;
            target.Health = Math.Max(0, target.Health - damage);

            if (target.Health == 0)
            {
                // La baja definitiva (quitar del almacén y del interés, y programar la reaparición)
                // la hace NpcSystem en este mismo tick: aquí solo consta que ha muerto.
                target.Action = EntityAction.Dead;
            }
            else if (target.AiState == NpcAiState.Wander)
            {
                // Aggro al recibir daño (I-03): aunque esté fuera de su radio, persigue al que le
                // pegó. Un PNJ en Return sigue volviendo a casa y no cambia de objetivo.
                target.AiState = NpcAiState.Chase;
                target.TargetEntityId = attacker.Id;
            }

            attacker.LastAttackTick = tick;
            attacker.Action = EntityAction.Attacking;
            attacker.ActionUntilTick = tick + AttackingTicks;
            AttacksOk++;
            if (hasWeapon)
            {
                AttacksWithWeapon++;
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
