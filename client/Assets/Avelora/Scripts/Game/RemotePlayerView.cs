using Avelora.Shared;
using Avelora.Shared.Prediction;
using UnityEngine;

namespace Avelora.Client
{
    /// <summary>
    /// Dibuja una entidad remota (otro jugador o un PNJ) a partir de una muestra interpolada.
    /// </summary>
    /// <remarks>
    /// No simula nada: la posición ya viene decidida por el servidor y suavizada por
    /// <see cref="SnapshotTimeline"/>. Aquí solo se copia al Transform y se elige el tinte.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class RemotePlayerView : MonoBehaviour
    {
        private SpriteRenderer _renderer;
        private Color _playerColor;
        private Color _npcColor;

        /// <summary>Identificador de la entidad que representa.</summary>
        public int EntityId { get; private set; }

        /// <summary>Prepara el SpriteRenderer copiando el aspecto del jugador local.</summary>
        public void Initialize(
            int entityId,
            Sprite sprite,
            Material material,
            int sortingLayerId,
            int sortingOrder,
            Color playerColor,
            Color npcColor)
        {
            EntityId = entityId;
            _playerColor = playerColor;
            _npcColor = npcColor;

            _renderer = GetComponent<SpriteRenderer>();
            if (_renderer == null)
            {
                _renderer = gameObject.AddComponent<SpriteRenderer>();
            }

            _renderer.sprite = sprite;

            if (material != null)
            {
                // El jugador local usa el material de URP 2D; uno nuevo usaría el sprite Unlit y
                // se vería distinto bajo las luces de la escena.
                _renderer.sharedMaterial = material;
            }

            _renderer.sortingLayerID = sortingLayerId;
            _renderer.sortingOrder = sortingOrder;
        }

        /// <summary>Coloca la vista en la muestra de este frame.</summary>
        public void Apply(EntitySample sample)
        {
            if (_renderer == null)
            {
                return;
            }

            transform.position = new Vector3(sample.Position.X, sample.Position.Y, 0f);
            _renderer.color = sample.Kind == EntityKind.Player ? _playerColor : _npcColor;

            if (sample.DirectionX != 0)
            {
                _renderer.flipX = sample.DirectionX < 0;
            }
        }
    }
}
