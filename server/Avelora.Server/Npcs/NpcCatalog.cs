#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avelora.Shared;

namespace Avelora.Server.Npcs
{
    /// <summary>
    /// Un tipo de enemigo: lo que comparten todos los que salen de un mismo spawner.
    /// </summary>
    public sealed class NpcType
    {
        /// <summary>Identificador del tipo. Único y mayor que 0.</summary>
        public int Id { get; set; }

        /// <summary>Nombre para informes y depuración.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Velocidad de deambular, en unidades por segundo (0 &lt; v ≤ 10).</summary>
        public float Speed { get; set; }

        /// <summary>Semiancho de la caja de colisión (0,05 ≤ v ≤ 2).</summary>
        public float HalfWidth { get; set; }

        /// <summary>Semialto de la caja de colisión (0,05 ≤ v ≤ 2).</summary>
        public float HalfHeight { get; set; }

        /// <summary>Caja de colisión del tipo.</summary>
        public Vec2 HalfSize => new Vec2(HalfWidth, HalfHeight);
    }

    /// <summary>
    /// Un punto de aparición: cuántos enemigos de un tipo salen ahí y hasta dónde pueden alejarse.
    /// </summary>
    public sealed class NpcSpawner
    {
        /// <summary>Tipo que se coloca. Debe existir en <see cref="NpcCatalog.Types"/>.</summary>
        public int TypeId { get; set; }

        /// <summary>Coordenada X del punto de aparición.</summary>
        public float X { get; set; }

        /// <summary>Coordenada Y del punto de aparición.</summary>
        public float Y { get; set; }

        /// <summary>Enemigos que se colocan en el punto (1..50).</summary>
        public int Count { get; set; }

        /// <summary>Radio de deambular alrededor del punto, en tiles (&gt; 0).</summary>
        public float WanderRadius { get; set; }

        /// <summary>Punto de aparición y «casa» de los enemigos.</summary>
        public Vec2 Position => new Vec2(X, Y);
    }

    /// <summary>
    /// Datos de los enemigos (<c>npcs.json</c>): tipos y puntos de aparición.
    /// </summary>
    /// <remarks>
    /// Se lee al arrancar, antes de crear la zona, y se valida contra el mapa real. Un dato
    /// imposible (un tipo que no existe, un punto dentro de una pared, una velocidad absurda)
    /// impide arrancar en vez de aparecer como un fallo raro en mitad de la partida
    /// (plan del servidor §7, «validar en la frontera»).
    /// </remarks>
    public sealed class NpcCatalog
    {
        /// <summary>Tope de enemigos por zona.</summary>
        public const int MaxTotalEnemies = 500;

        /// <summary>Tope de enemigos por spawner.</summary>
        public const int MaxPerSpawner = 50;

        /// <summary>Velocidad máxima admitida para un tipo, en unidades por segundo.</summary>
        public const float MaxSpeed = 10f;

        /// <summary>Semiancho/semialto mínimo de la caja de un tipo.</summary>
        public const float MinHalfSize = 0.05f;

        /// <summary>Semiancho/semialto máximo de la caja de un tipo.</summary>
        public const float MaxHalfSize = 2f;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>Tipos de enemigo declarados.</summary>
        public List<NpcType> Types { get; set; } = new List<NpcType>();

        /// <summary>Puntos de aparición declarados.</summary>
        public List<NpcSpawner> Spawners { get; set; } = new List<NpcSpawner>();

        /// <summary>Suma de los <see cref="NpcSpawner.Count"/> de todos los spawners.</summary>
        public int TotalEnemies
        {
            get
            {
                int total = 0;

                if (Spawners != null)
                {
                    for (int i = 0; i < Spawners.Count; i++)
                    {
                        total += Spawners[i].Count;
                    }
                }

                return total;
            }
        }

        /// <summary>Lee el archivo <c>npcs.json</c>.</summary>
        /// <exception cref="FileNotFoundException">No existe la ruta indicada.</exception>
        /// <exception cref="JsonException">El archivo no es JSON válido.</exception>
        public static NpcCatalog Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("La ruta de npcs.json no puede estar vacía.", nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "No se encuentra el archivo de enemigos '" + path + "'. " +
                    "Corrige npcsPath o deja npcsPath vacío para jugar sin enemigos.",
                    path);
            }

            string json = File.ReadAllText(path);
            NpcCatalog? catalog = JsonSerializer.Deserialize<NpcCatalog>(json, JsonOptions);

            if (catalog == null)
            {
                throw new InvalidOperationException("npcs.json está vacío o no contiene un objeto.");
            }

            catalog.Types ??= new List<NpcType>();
            catalog.Spawners ??= new List<NpcSpawner>();
            return catalog;
        }

        /// <summary>Busca un tipo por Id, o <c>null</c> si no existe.</summary>
        public NpcType? FindType(int id)
        {
            if (Types == null)
            {
                return null;
            }

            for (int i = 0; i < Types.Count; i++)
            {
                if (Types[i].Id == id)
                {
                    return Types[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Comprueba que los datos son utilizables en <paramref name="map"/> y lanza una
        /// <see cref="InvalidOperationException"/> con el primer problema que encuentre.
        /// </summary>
        public void Validate(MapCollisionGrid map)
        {
            if (map is null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            Types ??= new List<NpcType>();
            Spawners ??= new List<NpcSpawner>();

            ValidateTypes();

            int total = 0;

            for (int i = 0; i < Spawners.Count; i++)
            {
                NpcSpawner spawner = Spawners[i];
                string where = "el spawner " + (i + 1);

                if (spawner == null)
                {
                    throw new InvalidOperationException("npcs.json: " + where + " está vacío.");
                }

                total += spawner.Count;

                if (FindType(spawner.TypeId) == null)
                {
                    throw new InvalidOperationException(
                        "npcs.json: " + where + " usa el typeId " + spawner.TypeId +
                        ", que no existe en types.");
                }

                if (spawner.Count < 1 || spawner.Count > MaxPerSpawner)
                {
                    throw new InvalidOperationException(
                        "npcs.json: " + where + " tiene count " + spawner.Count +
                        "; debe estar entre 1 y " + MaxPerSpawner + ".");
                }

                if (!(spawner.WanderRadius > 0f) || float.IsInfinity(spawner.WanderRadius))
                {
                    throw new InvalidOperationException(
                        "npcs.json: " + where + " tiene wanderRadius " + spawner.WanderRadius +
                        "; debe ser un número positivo.");
                }

                NpcType type = FindType(spawner.TypeId)!;

                if (!map.IsBoxWalkable(spawner.Position, type.HalfSize))
                {
                    throw new InvalidOperationException(
                        "npcs.json: " + where + " (tipo " + type.Id + ") nace en " +
                        spawner.Position + ", dentro de una pared: su caja no cabe. " +
                        "Mueve el punto a una zona transitable.");
                }
            }

            if (total > MaxTotalEnemies)
            {
                throw new InvalidOperationException(
                    "npcs.json: hay " + total + " enemigos y el máximo por zona es " +
                    MaxTotalEnemies + ".");
            }
        }

        private void ValidateTypes()
        {
            for (int i = 0; i < Types.Count; i++)
            {
                NpcType type = Types[i];
                string where = "el tipo " + (i + 1);

                if (type == null)
                {
                    throw new InvalidOperationException("npcs.json: " + where + " está vacío.");
                }

                if (type.Id <= 0)
                {
                    throw new InvalidOperationException(
                        "npcs.json: " + where + " tiene id " + type.Id + "; debe ser mayor que 0.");
                }

                for (int j = 0; j < i; j++)
                {
                    if (Types[j] != null && Types[j].Id == type.Id)
                    {
                        throw new InvalidOperationException(
                            "npcs.json: el id " + type.Id + " está repetido en types.");
                    }
                }

                if (!(type.Speed > 0f) || type.Speed > MaxSpeed)
                {
                    throw new InvalidOperationException(
                        "npcs.json: el tipo " + type.Id + " tiene speed " + type.Speed +
                        "; debe ser mayor que 0 y como mucho " + MaxSpeed + ".");
                }

                if (!(type.HalfWidth >= MinHalfSize) || type.HalfWidth > MaxHalfSize ||
                    !(type.HalfHeight >= MinHalfSize) || type.HalfHeight > MaxHalfSize)
                {
                    throw new InvalidOperationException(
                        "npcs.json: el tipo " + type.Id + " tiene halfWidth/halfHeight (" +
                        type.HalfWidth + "; " + type.HalfHeight + "); deben estar entre " +
                        MinHalfSize + " y " + MaxHalfSize + ".");
                }
            }
        }
    }
}
