#nullable enable
namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Una intención de ataque o habilidad tal como la dejó la red (I-02, I-16): quién ataca, a quién
    /// y qué habilidad usa (0 = ataque básico). Igual que <see cref="MoveIntent"/>, es solo una
    /// petición; el servidor decide si vale.
    /// </summary>
    public readonly struct AttackIntent
    {
        public AttackIntent(int attackerEntityId, int targetId, ushort skillId = 0)
        {
            AttackerEntityId = attackerEntityId;
            TargetId = targetId;
            SkillId = skillId;
        }

        /// <summary>Entidad del jugador que ataca, según la conexión que mandó el mensaje.</summary>
        public int AttackerEntityId { get; }

        /// <summary>Entidad a la que apunta el ataque o habilidad. El cliente nunca manda posiciones ni daño.</summary>
        public int TargetId { get; }

        /// <summary>Identificador de la habilidad; 0 significa ataque básico.</summary>
        public ushort SkillId { get; }
    }
}
