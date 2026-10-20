using System;
using System.IO;
using Avelora.Server.Configuration;
using Avelora.Server.Entities;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// Pruebas contra el mapa <b>de verdad</b>: el que se exporta desde la escena de Unity con
    /// «Avelora/Exportar colisión del Tilemap (.avmap)» y que viaja en <c>maps/</c>.
    /// </summary>
    /// <remarks>
    /// Estas pruebas son las que sostienen la promesa de la Etapa A: el cliente y el servidor usan
    /// el mismo mapa. Si alguien cambia la escena y no vuelve a exportar, la huella deja de
    /// coincidir y la prueba lo dice.
    /// </remarks>
    public class ServerMapTests
    {
        /// <summary>Huella verificada en la Etapa A (escena AveloraEtapaA, rejilla 19x13).</summary>
        public const string ExpectedHash = "24ec4c868c5435f7875ad7bcf76a2a44773a843f71529b8d17fabb717e68d9ed";

        private const float Speed = 4f;
        private const float Step = 0.2f;   // speed * dt = 4 * 0,05
        private static readonly Vec2 Half = new Vec2(0.4f, 0.4f);
        private static readonly Vec2 Spawn = new ServerConfig().Spawn;

        /// <summary>Dónde frena el jugador contra el tabique interior (columna x = 0).</summary>
        private const float WallStopX = -0.5f;

        private static string MapPath =>
            Path.Combine(AppContext.BaseDirectory, "maps", "avelora-etapa-a.avmap");

        [Fact]
        public void El_Mapa_Exportado_Viaja_Con_Las_Pruebas()
        {
            Assert.True(File.Exists(MapPath), "Falta el mapa exportado en " + MapPath + ".");
        }

        [Fact]
        public void El_Servidor_Carga_El_Mismo_Mapa_Que_El_Cliente()
        {
            MapCollisionGrid map = MapFile.Load(MapPath);

            Assert.Equal(19, map.Width);
            Assert.Equal(13, map.Height);
            Assert.Equal(-9, map.OriginX);
            Assert.Equal(-6, map.OriginY);
            Assert.Equal(ExpectedHash, MapFile.ComputeContentHash(map));
        }

        [Fact]
        public void El_Punto_De_Aparicion_Del_Jugador_Esta_Libre()
        {
            MapCollisionGrid map = MapFile.Load(MapPath);

            Assert.True(map.IsBoxWalkable(Spawn, Half), "El jugador nacería dentro de una pared.");
        }

        [Fact]
        public void El_Servidor_Rechaza_Un_Aparicion_Dentro_De_Una_Pared()
        {
            MapCollisionGrid map = MapFile.Load(MapPath);
            var config = new ServerConfig { SpawnX = 0f, SpawnY = -3f };

            // El tabique interior ocupa la columna x = 0: nacer ahí deja al jugador atrapado.
            Assert.Throws<InvalidOperationException>(() => config.ValidateSpawn(map));
        }

        [Fact]
        public void La_Configuracion_Por_Defecto_Aparece_En_Un_Tile_Libre()
        {
            MapCollisionGrid map = MapFile.Load(MapPath);

            new ServerConfig().ValidateSpawn(map);
        }

        [Fact]
        public void La_Zona_No_Permite_Aparecer_Dentro_De_Una_Pared()
        {
            (Zone zone, _) = CreateZone();

            // El tabique interior ocupa la columna x = 0: cualquier caja ahí se solapa. La zona
            // lo rechaza aunque el llamador se haya saltado la validación de la configuración.
            Assert.Throws<InvalidOperationException>(
                () => zone.SpawnPlayer(2, new Vec2(0f, -3f), Half, inputBufferCapacity: 8));
            Assert.Equal(1, zone.Entities.Count);
        }

        [Fact]
        public void El_Borde_De_La_Habitacion_Esta_Cerrado()
        {
            MapCollisionGrid map = MapFile.Load(MapPath);

            // La sala va de (-9,-6) a (9,6), con un tabique interior en la columna x = 0 que sube
            // desde el suelo hasta y = 0. Así que hay muro en el borde y en el tabique...
            Assert.True(map.IsTileBlocked(-9, 0));
            Assert.True(map.IsTileBlocked(9, 0));
            Assert.True(map.IsTileBlocked(0, -6));
            Assert.True(map.IsTileBlocked(0, 6));
            Assert.True(map.IsTileBlocked(0, -3));

            // ...y paso justo por dentro del borde, y por encima del tabique.
            Assert.False(map.IsTileBlocked(-8, 0));
            Assert.False(map.IsTileBlocked(8, 0));
            Assert.False(map.IsTileBlocked(0, 1));
            Assert.False(map.IsTileBlocked(0, 5));
        }

        [Fact]
        public void El_Jugador_Se_Para_Contra_La_Pared_Interior()
        {
            (Zone zone, PlayerEntity player) = CreateZone();

            Drive(zone, player, ticks: 40, dirX: 1, dirY: 0);

            Assert.Equal(WallStopX, player.Position.X, 3);
            Assert.Equal(Spawn.Y, player.Position.Y, 5);

            // El invariante que de verdad importa, por si los flotantes bailan: la caja cabe donde
            // está y no cabe un paso más allá.
            Assert.True(zone.Map.IsBoxWalkable(player.Position, player.HalfSize));
            Assert.False(zone.Map.IsBoxWalkable(player.Position + new Vec2(Step, 0f), player.HalfSize));
        }

        [Fact]
        public void Empujar_Contra_La_Pared_No_Lo_Mueve_Ni_Un_Poco()
        {
            (Zone zone, PlayerEntity player) = CreateZone();

            Drive(zone, player, ticks: 40, dirX: 1, dirY: 0);
            Vec2 pegado = player.Position;

            Drive(zone, player, ticks: 200, dirX: 1, dirY: 0, firstSequence: 41);

            Assert.Equal(pegado, player.Position);
            Assert.Equal(240u, player.LastProcessedSequence);
        }

        [Fact]
        public void Empujar_Hacia_Abajo_Tampoco_Atraviesa_El_Suelo()
        {
            (Zone zone, PlayerEntity player) = CreateZone();

            Drive(zone, player, ticks: 300, dirX: 0, dirY: -1);

            Assert.True(
                zone.Map.IsBoxWalkable(player.Position, player.HalfSize),
                "El jugador acabó dentro del suelo: " + player.Position);
            Assert.False(zone.Map.IsBoxWalkable(player.Position + new Vec2(0f, -Step), player.HalfSize));
        }

        [Fact]
        public void Se_Puede_Recorrer_La_Sala_Sin_Salirse_Ni_Atravesar_Nada()
        {
            (Zone zone, PlayerEntity player) = CreateZone();

            var random = new Random(20261019);
            uint sequence = 0;

            for (int i = 0; i < 3000; i++)
            {
                sequence++;
                zone.Inputs.TryEnqueue(new MoveIntent(
                    player.Id,
                    sequence,
                    (sbyte)random.Next(-1, 2),
                    (sbyte)random.Next(-1, 2)));

                zone.Tick();

                // Invariante de todas las etapas: la caja del jugador nunca queda dentro de un muro.
                Assert.True(
                    zone.Map.IsBoxWalkable(player.Position, player.HalfSize),
                    "El jugador acabó dentro de un muro en el tick " + zone.TickCount + ": " + player.Position);
            }

            Assert.Equal(3000, zone.TickCount);
            Assert.Equal(sequence, player.LastProcessedSequence);
        }

        private static (Zone Zone, PlayerEntity Player) CreateZone()
        {
            MapCollisionGrid map = MapFile.Load(MapPath);

            var zone = new Zone(
                1,
                map,
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(1024),
                new MovementSystem(Speed, SimulationConstants.FixedDt),
                maxIntentsPerTick: 256,
                maxIntentsPerPlayerPerTick: 4);

            PlayerEntity player = zone.SpawnPlayer(1, Spawn, Half, inputBufferCapacity: 8);
            return (zone, player);
        }

        private static void Drive(Zone zone, PlayerEntity player, int ticks, int dirX, int dirY, uint firstSequence = 1)
        {
            uint sequence = firstSequence;

            for (int i = 0; i < ticks; i++)
            {
                zone.Inputs.TryEnqueue(new MoveIntent(player.Id, sequence, (sbyte)dirX, (sbyte)dirY));
                zone.Tick();
                sequence++;
            }
        }
    }
}
