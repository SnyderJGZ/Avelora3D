using System;
using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Xunit;

namespace Avelora.Tests
{
    public class MovementSystemTests
    {
        private const float Speed = 4f;
        private static readonly float Dt = SimulationConstants.FixedDt;
        private static readonly Vec2 Half = new Vec2(0.4f, 0.4f);

        private static (Zone Zone, PlayerEntity Player) Build(string[] rows, int startX = 3, int startY = 3)
        {
            var map = MapCollisionGrid.FromRows(rows);
            var zone = new Zone(
                1,
                map,
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(64),
                new MovementSystem(Speed, Dt),
                maxIntentsPerTick: 32,
                maxIntentsPerPlayerPerTick: 4);

            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(startX, startY), Half, inputBufferCapacity: 4);
            return (zone, player);
        }

        private static void Send(Zone zone, PlayerEntity player, uint sequence, int dirX, int dirY)
        {
            zone.Inputs.TryEnqueue(new MoveIntent(player.Id, sequence, (sbyte)dirX, (sbyte)dirY));
        }

        [Fact]
        public void Un_Input_Es_Un_Paso()
        {
            var (zone, player) = Build(new[]
            {
                ".....",
                ".....",
                ".....",
                ".....",
                ".....",
            });

            Send(zone, player, 1, 1, 0);
            zone.Tick();

            Assert.Equal(3f + (Speed * Dt), player.Position.X, 4);
            Assert.Equal(3f, player.Position.Y, 4);
            Assert.Equal(1u, player.LastProcessedSequence);
        }

        [Fact]
        public void Sin_Input_No_Se_Mueve()
        {
            var (zone, player) = Build(new[]
            {
                ".....",
                ".....",
                ".....",
                ".....",
                ".....",
            });

            Vec2 before = player.Position;

            zone.Tick();
            zone.Tick();

            Assert.Equal(before, player.Position);
            Assert.Equal(0u, player.LastProcessedSequence);
            Assert.Equal(0, player.DirectionX);
            Assert.Equal(0, player.DirectionY);
        }

        [Fact]
        public void Dos_Inputs_En_Un_Tick_Se_Aplican_Uno_Y_El_Otro_Espera()
        {
            var (zone, player) = Build(new[]
            {
                ".......",
                ".......",
                ".......",
                ".......",
                ".......",
            });

            Send(zone, player, 1, 1, 0);
            Send(zone, player, 2, 1, 0);

            zone.Tick();
            Assert.Equal(3f + (Speed * Dt), player.Position.X, 4);
            Assert.Equal(1u, player.LastProcessedSequence);

            zone.Tick();
            Assert.Equal(3f + (2f * Speed * Dt), player.Position.X, 4);
            Assert.Equal(2u, player.LastProcessedSequence);
        }

        [Fact]
        public void El_Exceso_Por_Encima_Del_Margen_Se_Descarta_Y_Se_Cuenta()
        {
            // Margen de 2 inputs por jugador y tick: el resto se rechaza en la propia zona.
            var map = MapCollisionGrid.AllWalkable(16, 16);
            var zone = new Zone(
                1,
                map,
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(64),
                new MovementSystem(Speed, Dt),
                maxIntentsPerTick: 32,
                maxIntentsPerPlayerPerTick: 2);

            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(5f, 5f), Half, inputBufferCapacity: 8);

            for (uint i = 1; i <= 6; i++)
            {
                Send(zone, player, i, 1, 0);
            }

            zone.Tick();

            Assert.Equal(4, zone.RejectedPerPlayerLimit);
            Assert.Equal(2, player.Input.Count + 1);   // se aplicó uno y queda otro en la cola
        }

        [Fact]
        public void Un_Input_De_Una_Entidad_Que_No_Existe_Se_Descarta()
        {
            var (zone, player) = Build(new[]
            {
                ".....",
                ".....",
                ".....",
                ".....",
                ".....",
            });

            Send(zone, player, 1, 1, 0);
            zone.Inputs.TryEnqueue(new MoveIntent(9999, 1, 1, 0));

            zone.Tick();

            Assert.Equal(1, zone.RejectedUnknownEntity);
            Assert.Equal(1u, player.LastProcessedSequence);
        }

        [Fact]
        public void Se_Desliza_Pegado_A_Una_Pared()
        {
            // Columna x = 4 bloqueada de arriba abajo.
            var (zone, player) = Build(new[]
            {
                "......",
                "....#.",
                "....#.",
                "....#.",
                "....#.",
                "....#.",
            }, startX: 3, startY: 2);

            // En diagonal hacia la pared: X se frena y Y sigue avanzando.
            for (uint i = 1; i <= 5; i++)
            {
                Send(zone, player, i, 1, 1);
            }

            for (int i = 0; i < 5; i++)
            {
                zone.Tick();
            }

            // La caja no llega a tocar la columna bloqueada (x = 4)...
            Assert.True(player.Position.X + Half.X < 4f, "No debería haber tocado la pared.");
            // ...pero sí ha avanzado hacia ella y ha seguido subiendo.
            Assert.True(player.Position.X > 3.5f, "Debería haberse acercado a la pared.");
            Assert.True(player.Position.Y > 2f, "Debería haberse deslizado hacia arriba.");
        }

        [Fact]
        public void Un_Jugador_No_Puede_Salir_Del_Mapa()
        {
            var (zone, player) = Build(new[]
            {
                ".....",
                ".....",
                ".....",
                ".....",
                ".....",
            }, startX: 2, startY: 2);

            for (uint i = 1; i <= 200; i++)
            {
                Send(zone, player, i, 1, 0);
            }

            for (int i = 0; i < 200; i++)
            {
                zone.Tick();
            }

            // El borde del mapa cuenta como bloqueado: la caja no puede salirse de la rejilla.
            Assert.True(zone.Map.IsBoxWalkable(player.Position, player.HalfSize));
            Assert.True(player.Position.X < 5f);
        }

        [Fact]
        public void La_Direccion_Invalida_Nunca_Llega_Al_Jugador()
        {
            var (zone, player) = Build(new[]
            {
                ".....",
                ".....",
                ".....",
                ".....",
                ".....",
            });

            Assert.Equal(InputQueueResult.InvalidDirection, zone.Inputs.TryEnqueue(new MoveIntent(player.Id, 1, 7, 0)));

            zone.Tick();

            Assert.Equal(0, zone.Inputs.Pending);
            Assert.Equal(0u, player.LastProcessedSequence);
            Assert.Equal(0, player.DirectionX);
        }

        [Fact]
        public void Cliente_Que_No_Manda_Nada_Se_Queda_Quieto()
        {
            var (zone, player) = Build(new[]
            {
                ".....",
                ".....",
                ".....",
                ".....",
                ".....",
            });
            Vec2 start = player.Position;

            for (int i = 0; i < 50; i++)
            {
                zone.Tick();
            }

            Assert.Equal(start, player.Position);
            Assert.Equal(50, zone.TickCount);
        }

        [Fact]
        public void Solo_Se_Mueven_Los_Jugadores()
        {
            var map = MapCollisionGrid.AllWalkable(8, 8);
            var store = new EntityStore();
            var zone = new Zone(
                1,
                map,
                store,
                new EntityIdAllocator(),
                new InputQueue(16),
                new MovementSystem(Speed, Dt),
                16,
                2);

            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(2f, 2f), Half, 4);

            var npc = new Entity(999, EntityKind.Npc) { MapId = 1, Position = new Vec2(5f, 5f), HalfSize = Half };
            store.Add(npc);

            // Un input dirigido al PNJ no debe moverlo: el movimiento es cosa de los jugadores.
            zone.Inputs.TryEnqueue(new MoveIntent(npc.Id, 1, 1, 0));
            Send(zone, player, 1, 1, 0);

            zone.Tick();

            Assert.Equal(new Vec2(5f, 5f), npc.Position);
            Assert.Equal(1, zone.RejectedUnknownEntity);
        }

        [Fact]
        public void El_Movimiento_Es_Determinista_Entre_Dos_Zonas_Iguales()
        {
            var first = RunRandomWalk();
            var second = RunRandomWalk();

            Assert.Equal(first, second);
        }

        private static List<Vec2> RunRandomWalk()
        {
            var map = MapCollisionGrid.FromRows(new[]
            {
                "#######",
                "#.....#",
                "#..#..#",
                "#.....#",
                "#######",
            });

            var zone = new Zone(
                1,
                map,
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(64),
                new MovementSystem(Speed, Dt),
                64,
                4);

            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(2f, 2f), Half, 8);
            var random = new Random(1234);
            var trail = new List<Vec2>();

            for (uint sequence = 1; sequence <= 300; sequence++)
            {
                Send(zone, player, sequence, random.Next(-1, 2), random.Next(-1, 2));
                zone.Tick();
                trail.Add(player.Position);
            }

            return trail;
        }
    }
}
