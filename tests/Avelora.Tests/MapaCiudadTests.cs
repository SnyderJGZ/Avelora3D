using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Avelora.EditorTools;
using Avelora.Server.Configuration;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Zones;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    public sealed class MapaCiudadTests
    {
        private const string HuellaEsperada = "4d86b0d9788c143607d664d15d93243d6634bf2192da86ad5992e397305418f6";

        [Fact]
        public void Mapa_Mide_160x120_Con_Origen_Y_Anillo_Bloqueado()
        {
            MapCollisionGrid mapa = LeerMapa();

            Assert.Equal(160, mapa.Width);
            Assert.Equal(120, mapa.Height);
            Assert.Equal(-80, mapa.OriginX);
            Assert.Equal(-60, mapa.OriginY);

            for (int y = mapa.OriginY; y < mapa.OriginY + mapa.Height; y++)
            {
                for (int x = mapa.OriginX; x < mapa.OriginX + mapa.Width; x++)
                {
                    bool borde = x < mapa.OriginX + 4 || x >= mapa.OriginX + mapa.Width - 4 ||
                        y < mapa.OriginY + 4 || y >= mapa.OriginY + mapa.Height - 4;
                    if (borde) Assert.True(mapa.IsTileBlocked(x, y), $"La casilla del anillo ({x},{y}) debe estar bloqueada.");
                }
            }
        }

        [Fact]
        public void Mapa_Coincide_Con_El_Layout_Y_Su_Huella_Esta_Fijada()
        {
            MapaCiudadLayout.Resultado primero = MapaCiudadLayout.Generar();
            MapaCiudadLayout.Resultado segundo = MapaCiudadLayout.Generar();
            MapCollisionGrid mapaLayout = Grid(primero);
            MapCollisionGrid mapaArchivo = LeerMapa();
            string huella = MapFile.ComputeContentHash(mapaLayout);

            Assert.Equal(primero.Bloqueado, segundo.Bloqueado);
            Assert.Equal(MapFile.ComputeContentHash(mapaArchivo), huella);
            Assert.Equal(HuellaEsperada, huella);
        }

        [Fact]
        public void Aparicion_Y_Spawners_Son_Casillas_Libres_Y_Conectadas()
        {
            MapCollisionGrid mapa = LeerMapa();
            ServerConfig config = ServerConfig.Load(Path.Combine(AppContext.BaseDirectory, "server.json"));
            NpcCatalog npcs = NpcCatalog.Load(Path.Combine(AppContext.BaseDirectory, "npcs.json"));
            bool[] alcanzables = CalcularAlcanzables(mapa, MapaCiudadLayout.Aparicion.CasillaX,
                MapaCiudadLayout.Aparicion.CasillaY);

            Assert.Equal(MapaCiudadLayout.Aparicion.X, config.Spawn.X);
            Assert.Equal(MapaCiudadLayout.Aparicion.Y, config.Spawn.Y);
            AssertAlcanzada(mapa, alcanzables, MapaCiudadLayout.Aparicion.CasillaX, MapaCiudadLayout.Aparicion.CasillaY);

            foreach (NpcSpawner spawner in npcs.Spawners)
            {
                int minX = (int)Math.Floor(spawner.X - spawner.WanderRadius);
                int maxX = (int)Math.Floor(spawner.X + spawner.WanderRadius);
                int minY = (int)Math.Floor(spawner.Y - spawner.WanderRadius);
                int maxY = (int)Math.Floor(spawner.Y + spawner.WanderRadius);
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++) AssertAlcanzada(mapa, alcanzables, x, y);
                }
            }

            foreach (MapaCiudadLayout.Punto puerta in MapaCiudadLayout.Puertas)
            {
                AssertAlcanzada(mapa, alcanzables, puerta.CasillaX, puerta.CasillaY);
            }

            AssertAlcanzada(mapa, alcanzables, MapaCiudadLayout.ArenaRect.CentroX, MapaCiudadLayout.ArenaRect.CentroY);
            AssertAlcanzada(mapa, alcanzables, 44, 0);
        }

        [Fact]
        public void Zonas_Ciudad_Segura_Arena_Pvp_Y_Spawners_Fuera()
        {
            MapCollisionGrid mapa = LeerMapa();
            ZoneMap zonas = ZoneMap.Load(Path.Combine(AppContext.BaseDirectory, "zones.json"), mapa);
            ItemCatalog items = ItemCatalog.Load(Path.Combine(AppContext.BaseDirectory, "items.json"));
            NpcCatalog npcs = NpcCatalog.Load(Path.Combine(AppContext.BaseDirectory, "npcs.json"));
            NpcCatalog npcsCarga = NpcCatalog.Load(RutaRepositorio("tools", "carga", "npcs-carga.json"));

            Assert.Equal(ZoneKind.Safe, zonas.KindAt(0.5f, -4.5f));
            Assert.Equal(ZoneKind.Pvp, zonas.KindAt(MapaCiudadLayout.ArenaRect.CentroX + 0.5f,
                MapaCiudadLayout.ArenaRect.CentroY + 0.5f));
            zonas.ValidateSpawners(npcs);
            npcs.Validate(mapa, items);

            ZoneInfo segura = Assert.Single(zonas.Zones, zona => zona.Kind == ZoneKind.Safe);
            foreach (NpcSpawner spawner in npcs.Spawners)
            {
                NpcType tipo = Assert.IsType<NpcType>(npcs.FindType(spawner.TypeId));
                int minX = (int)Math.Floor(spawner.X - spawner.WanderRadius - tipo.HalfWidth);
                int maxX = (int)Math.Floor(spawner.X + spawner.WanderRadius + tipo.HalfWidth);
                int minY = (int)Math.Floor(spawner.Y - spawner.WanderRadius - tipo.HalfHeight);
                int maxY = (int)Math.Floor(spawner.Y + spawner.WanderRadius + tipo.HalfHeight);
                foreach (ZoneRect rect in segura.Rects)
                {
                    Assert.True(maxX < rect.X || minX >= rect.X + rect.Width ||
                        maxY < rect.Y || minY >= rect.Y + rect.Height,
                        $"El spawner {spawner.TypeId} invade la zona segura.");
                }
            }

            Assert.Equal(npcs.Spawners.Count, npcsCarga.Spawners.Count);
            for (int i = 0; i < npcs.Spawners.Count; i++)
            {
                Assert.Equal(npcs.Spawners[i].TypeId, npcsCarga.Spawners[i].TypeId);
                Assert.Equal(npcs.Spawners[i].X, npcsCarga.Spawners[i].X);
                Assert.Equal(npcs.Spawners[i].Y, npcsCarga.Spawners[i].Y);
            }
        }

        [Fact]
        public void Layout_Usa_Solo_Piezas_Del_Catalogo_Con_Su_Tamano()
        {
            MapCollisionGrid mapa = LeerMapa();
            Assert.Equal(MapaCiudadLayout.Ancho, mapa.Width);
            Assert.Equal(MapaCiudadLayout.Alto, mapa.Height);
            var dimensiones = new Dictionary<string, (int Ancho, int Alto)>(StringComparer.Ordinal);
            foreach (string archivo in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "catalogos"), "*_catalogo.json"))
            {
                using JsonDocument catalogo = JsonDocument.Parse(File.ReadAllText(archivo));
                foreach (JsonElement pieza in catalogo.RootElement.GetProperty("piezas").EnumerateArray())
                {
                    dimensiones.Add(pieza.GetProperty("archivo").GetString()!,
                        (pieza.GetProperty("casillasAncho").GetInt32(), pieza.GetProperty("casillasAlto").GetInt32()));
                }
            }

            foreach (FieldInfo campo in typeof(MapaCiudadLayout.Piezas).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (campo.GetValue(null) is MapaCiudadLayout.Pieza pieza) ComprobarPieza(pieza, dimensiones);
                else if (campo.GetValue(null) is MapaCiudadLayout.Pieza[] piezas)
                {
                    foreach (MapaCiudadLayout.Pieza elemento in piezas) ComprobarPieza(elemento, dimensiones);
                }
            }

            MapaCiudadLayout.Resultado layout = MapaCiudadLayout.Generar();
            foreach (string archivo in layout.Suelo.Concat(layout.Camino).Where(value => value != null).Distinct())
            {
                if (archivo == "Water") continue;
                Assert.True(dimensiones.ContainsKey(archivo!), $"No está catalogado el suelo/camino {archivo}.");
                Assert.Equal((1, 1), dimensiones[archivo!]);
            }
            foreach (MapaCiudadLayout.Objeto objeto in layout.Objetos)
            {
                Assert.True(dimensiones.TryGetValue(objeto.Archivo, out var tamano), $"No está catalogado {objeto.Archivo}.");
                Assert.Equal((objeto.Ancho, objeto.Alto), tamano);
            }
        }

        [Fact]
        public void Objetos_Solidos_Bloquean_Su_Base()
        {
            MapaCiudadLayout.Resultado layout = MapaCiudadLayout.Generar();
            MapCollisionGrid mapa = LeerMapa();

            foreach (MapaCiudadLayout.Objeto objeto in layout.Objetos)
            {
                if (objeto.Solido)
                {
                    for (int x = objeto.X; x < objeto.X + objeto.Ancho; x++)
                    {
                        Assert.True(mapa.IsTileBlocked(x, objeto.Y), $"La base de {objeto.Archivo} en ({x},{objeto.Y}) debe bloquear.");
                    }
                }

                AssertNoCubre(objeto, MapaCiudadLayout.Aparicion.CasillaX, MapaCiudadLayout.Aparicion.CasillaY);
                foreach (MapaCiudadLayout.Punto puerta in MapaCiudadLayout.Puertas)
                {
                    AssertNoCubre(objeto, puerta.CasillaX, puerta.CasillaY);
                }
                foreach (MapaCiudadLayout.Claro claro in layout.Claros)
                {
                    for (int y = claro.CasillaY - 3; y <= claro.CasillaY + 3; y++)
                    {
                        for (int x = claro.CasillaX - 3; x <= claro.CasillaX + 3; x++) AssertNoCubre(objeto, x, y);
                    }
                }
            }
        }

        private static MapCollisionGrid LeerMapa() => MapFile.Load(Path.Combine(AppContext.BaseDirectory, "maps", "avelora-ciudad.avmap"));

        private static MapCollisionGrid Grid(MapaCiudadLayout.Resultado layout) =>
            new MapCollisionGrid(layout.Ancho, layout.Alto, layout.Bloqueado, layout.OrigenX, layout.OrigenY);

        private static bool[] CalcularAlcanzables(MapCollisionGrid mapa, int xInicial, int yInicial)
        {
            var alcanzables = new bool[mapa.Width * mapa.Height];
            var cola = new Queue<(int X, int Y)>();
            if (mapa.IsTileBlocked(xInicial, yInicial)) return alcanzables;
            alcanzables[Indice(mapa, xInicial, yInicial)] = true;
            cola.Enqueue((xInicial, yInicial));
            int[] dx = { 0, 0, -1, 1 };
            int[] dy = { 1, -1, 0, 0 };
            while (cola.Count > 0)
            {
                (int x, int y) = cola.Dequeue();
                for (int i = 0; i < dx.Length; i++)
                {
                    int nx = x + dx[i];
                    int ny = y + dy[i];
                    int indice = Indice(mapa, nx, ny);
                    if (indice >= 0 && !alcanzables[indice] && !mapa.IsTileBlocked(nx, ny))
                    {
                        alcanzables[indice] = true;
                        cola.Enqueue((nx, ny));
                    }
                }
            }
            return alcanzables;
        }

        private static int Indice(MapCollisionGrid mapa, int x, int y)
        {
            int localX = x - mapa.OriginX;
            int localY = y - mapa.OriginY;
            return localX < 0 || localX >= mapa.Width || localY < 0 || localY >= mapa.Height
                ? -1
                : localY * mapa.Width + localX;
        }

        private static void AssertAlcanzada(MapCollisionGrid mapa, bool[] alcanzables, int x, int y)
        {
            int indice = Indice(mapa, x, y);
            Assert.True(indice >= 0 && !mapa.IsTileBlocked(x, y) && alcanzables[indice],
                $"La casilla ({x},{y}) debe estar libre y conectada con la aparición.");
        }

        private static void ComprobarPieza(MapaCiudadLayout.Pieza pieza,
            IReadOnlyDictionary<string, (int Ancho, int Alto)> dimensiones)
        {
            if (pieza.Archivo == "Water") return;
            Assert.True(dimensiones.TryGetValue(pieza.Archivo, out var tamano), $"No está catalogada {pieza.Archivo}.");
            Assert.Equal((pieza.Ancho, pieza.Alto), tamano);
        }

        private static void AssertNoCubre(MapaCiudadLayout.Objeto objeto, int x, int y)
        {
            Assert.False(x >= objeto.X && x < objeto.X + objeto.Ancho &&
                y >= objeto.Y && y < objeto.Y + objeto.Alto,
                $"{objeto.Archivo} cubre la casilla protegida ({x},{y}).");
        }

        private static string RutaRepositorio(params string[] partes)
        {
            string ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
            return Path.GetFullPath(Path.Combine(new[] { ruta }.Concat(partes).ToArray()));
        }
    }
}
