#nullable enable
using Avelora.Server.Entities;
using Avelora.Server.Npcs;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Simulation
{
    /// <summary>Caduca, limpia y valida los estados temporales de las entidades (I-18).</summary>
    public sealed class StatusSystem
    {
        /// <summary>Estados retirados por caducidad.</summary>
        public long Expired { get; private set; }

        /// <summary>Estados retirados por muerte.</summary>
        public long ClearedByDeath { get; private set; }

        /// <summary>Provocaciones interrumpidas por retorno o cambio de objetivo.</summary>
        public long TauntsBroken { get; private set; }

        /// <summary>Actualiza todos los estados al inicio del tick.</summary>
        public void Tick(EntityStore entities, long tick)
        {
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i];
                if (entity.Action == EntityAction.Dead)
                {
                    ClearedByDeath += entity.Statuses.Clear();
                    continue;
                }

                Expired += entity.Statuses.Expire(tick);
                if (entity is NpcEntity npc && entity.Statuses.IsActive(StatusKind.Taunt))
                {
                    ActiveStatus taunt = entity.Statuses.Get(StatusKind.Taunt);
                    if ((npc.AiState == NpcAiState.Return || npc.TargetEntityId != taunt.SourceId) &&
                        entity.Statuses.Remove(StatusKind.Taunt))
                    {
                        TauntsBroken++;
                    }
                }
            }
        }
    }
}
