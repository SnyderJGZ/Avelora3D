#nullable enable
using System;
using Avelora.Server.Entities;
using Avelora.Shared;

namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Aplica las intenciones aceptadas y avanza un paso fijo a cada jugador.
    /// </summary>
    /// <remarks>
    /// Es deliberadamente corto: el movimiento en sí lo hace <see cref="MovementSimulator"/>, el
    /// mismo código que el cliente usa para predecir. Aquí solo están las <b>reglas del servidor</b>
    /// (plan del servidor §5): un input por tick, la velocidad la pone el servidor, y sin input no
    /// hay movimiento.
    ///
    /// La última regla es la que mantiene cliente y servidor avanzando en la misma proporción: el
    /// cliente predice un paso por cada input que envía, así que el servidor debe dar exactamente
    /// un paso por cada input que recibe. Si el servidor siguiera moviendo con el último input
    /// conocido, se adelantaría al cliente y la reconciliación corregiría en cada snapshot.
    /// </remarks>
    public sealed class MovementSystem
    {
        private readonly float _speed;
        private readonly float _dt;

        /// <param name="speed">Velocidad en unidades por segundo. Es dato del servidor.</param>
        /// <param name="dt">Paso fijo en segundos; debe ser el compartido con el cliente.</param>
        public MovementSystem(float speed, float dt)
        {
            if (speed <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(speed), speed, "La velocidad debe ser positiva.");
            }

            if (dt <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(dt), dt, "El paso de simulación debe ser positivo.");
            }

            _speed = speed;
            _dt = dt;
        }

        /// <summary>Velocidad aplicada, en unidades por segundo.</summary>
        public float Speed => _speed;

        /// <summary>Paso de simulación, en segundos.</summary>
        public float Dt => _dt;

        /// <summary>Avanza un paso a todos los jugadores del almacén.</summary>
        public void Tick(EntityStore entities, MapCollisionGrid map)
        {
            if (entities is null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            if (map is null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            for (int i = 0; i < entities.Count; i++)
            {
                if (entities[i] is PlayerEntity player)
                {
                    Step(player, map);
                }
            }
        }

        private void Step(PlayerEntity player, MapCollisionGrid map)
        {
            if (!player.Input.TryDequeue(out MoveIntent intent))
            {
                // Sin input este tick el jugador no se mueve. Un cliente que no manda nada se
                // queda quieto, y uno que solo manda (0,0) tampoco avanza.
                player.DirectionX = 0;
                player.DirectionY = 0;
                return;
            }

            player.DirectionX = intent.DirectionX;
            player.DirectionY = intent.DirectionY;
            player.LastProcessedSequence = intent.Sequence;

            // Un muerto sigue consumiendo inputs (la secuencia avanza y el cliente no se
            // desincroniza), pero no se mueve (I-03).
            if (player.IsDead)
            {
                player.DirectionX = 0;
                player.DirectionY = 0;
                return;
            }

            player.Position = MovementSimulator.Step(
                player.Position,
                player.DirectionX,
                player.DirectionY,
                _speed,
                _dt,
                player.HalfSize,
                map);
        }
    }
}
