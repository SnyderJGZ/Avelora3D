using System;
using System.IO;
using Avelora.Shared;
using Avelora.Shared.Maps;
using UnityEngine;

namespace Avelora.Client
{
    /// <summary>Carga desde StreamingAssets el mismo mapa de colisión que usa el servidor.</summary>
    [DisallowMultipleComponent]
    public sealed class AveloraMap : MonoBehaviour
    {
        [Tooltip("Nombre del mapa en StreamingAssets/Maps, sin extensión.")]
        [SerializeField] private string mapFileName = "avelora-ciudad";

        [Tooltip("Escribe en la consola las dimensiones y la huella del mapa al cargarlo.")]
        [SerializeField] private bool debugMode;

        /// <summary>Rejilla compartida; permanece nula si no se pudo cargar el mapa.</summary>
        public MapCollisionGrid Grid { get; private set; }

        /// <summary>Huella del mapa para detectar desajustes con el servidor.</summary>
        public string ContentHash { get; private set; } = "(sin cargar)";

        /// <summary>Huella SHA-256 binaria que viaja en el saludo.</summary>
        public byte[] ContentHashBytes { get; private set; }

        private bool _loadAttempted;

        private void Awake()
        {
            Cargar();
        }

        /// <summary>Carga una sola vez el archivo .avmap de StreamingAssets/Maps.</summary>
        public bool Cargar()
        {
            if (Grid != null)
            {
                return true;
            }

            if (_loadAttempted)
            {
                return false;
            }

            _loadAttempted = true;
            try
            {
                string path = Path.Combine(
                    Application.streamingAssetsPath,
                    "Maps",
                    mapFileName + MapFile.Extension);
                Grid = MapFile.Load(path);
                ContentHashBytes = MapFile.ComputeContentHashBytes(Grid);
                ContentHash = MapFile.ToHexString(ContentHashBytes);

                if (debugMode)
                {
                    Debug.Log(
                        "[Avelora] Mapa " + Grid.Width + "x" + Grid.Height +
                        " (origen " + Grid.OriginX + "," + Grid.OriginY + "), huella " + ContentHash,
                        this);
                }

                return true;
            }
            catch (Exception exception)
            {
                Grid = null;
                ContentHashBytes = null;
                ContentHash = "(sin cargar)";
                Debug.LogError("[Avelora] No se pudo cargar el mapa de colisión: " + exception.Message, this);
                return false;
            }
        }
    }
}
