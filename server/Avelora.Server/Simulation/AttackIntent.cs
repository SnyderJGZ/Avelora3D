#nullable enable
namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Una intención de ataque tal como la dejó la red (I-02): quién ataca y a quién. Igual que
    /// <see cref="MoveIntent"/>, es solo una petición; el servidor decide si vale.
    /// </summary>
    public readonly struct AttackIntent
    {
        public AttackIntent(int attackerEntityId, int targetId)
        {
            AttackerEntityId = attackerEntityId;
            TargetId = targetId;
        }

        /// <summary>Entidad del jugador que ataca, según la conexión que mandó el mensaje.</summary>
        public int AttackerEntityId { get; }

        /// <summary>Entidad a la que apunta el ataque. El cliente nunca manda posiciones ni daño.</summary>
        public int TargetId { get; }
    }
}
