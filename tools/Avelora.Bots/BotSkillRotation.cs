using System;
using Avelora.Shared.Protocol;

namespace Avelora.Bots
{
    /// <summary>Selecciona la habilidad disponible más apropiada usando solo el catálogo recibido.</summary>
    public static class BotSkillRotation
    {
        /// <summary>Devuelve el índice de la habilidad elegida o -1 si ninguna se puede usar.</summary>
        public static int Pick(SkillCatalogEntry[] skills, byte level, int resource, long nowMs,
            long[] readyAtMs, long globalReadyAtMs, float targetDistance, bool targetIsPlayer,
            int ownHealthPercent)
        {
            if (skills == null) throw new ArgumentNullException(nameof(skills));
            if (readyAtMs == null) throw new ArgumentNullException(nameof(readyAtMs));
            if (nowMs < globalReadyAtMs) return -1;

            int best = -1;
            int bestHeal = -1;
            for (int i = 0; i < skills.Length; i++)
            {
                SkillCatalogEntry skill = skills[i];
                if (i >= readyAtMs.Length || skill.RequiredLevel > level ||
                    skill.ResourceCost > resource || nowMs < readyAtMs[i])
                    continue;

                bool hasTarget = !float.IsNaN(targetDistance);
                if (skill.Effect == 1)
                {
                    if (ownHealthPercent >= 70) continue;
                    if (bestHeal < 0 || skill.RequiredLevel > skills[bestHeal].RequiredLevel)
                        bestHeal = i;
                    continue;
                }
                if (skill.Effect == 2 && (skill.StatusKind == StatusKind.Bonus || skill.StatusKind == StatusKind.Shield))
                {
                    if (!hasTarget) continue;
                }
                else if (skill.Effect == 2 && skill.StatusKind == StatusKind.Taunt)
                {
                    if (!hasTarget || targetIsPlayer || targetDistance > skill.Range) continue;
                }
                else if (skill.Effect == 0 && skill.AreaCenter == 1)
                {
                    if (!hasTarget || targetDistance > skill.Radius) continue;
                }
                else if (skill.Effect == 0)
                {
                    if (!hasTarget || targetDistance > skill.Range) continue;
                }
                else
                {
                    continue;
                }

                if (best < 0 || skill.RequiredLevel > skills[best].RequiredLevel)
                    best = i;
            }

            return bestHeal >= 0 ? bestHeal : best;
        }
    }
}
