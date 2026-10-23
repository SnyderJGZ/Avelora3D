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

        /// <summary>
        /// Da a un PNJ sus hojas de animación (4 filas: abajo, arriba, izquierda, derecha). Si una
        /// hoja no se reparte en 4 filas iguales, se ignora y queda el cuadrado teñido.
        /// </summary>
        public void SetNpcSheets(Sprite[] idle, Sprite[] walk)
        {
            if (idle != null && walk != null && idle.Length >= 4 && walk.Length >= 4 &&
                idle.Length % 4 == 0 && walk.Length % 4 == 0)
            {
                _idle = idle;
                _walk = walk;
            }
        }

        /// <summary>Coloca la vista en la muestra de este frame.</summary>
        public void Apply(EntitySample sample)
        {
            if (_renderer == null)
            {
                return;
            }

            transform.position = new Vector3(sample.Position.X, sample.Position.Y, 0f);

            if (_walk != null)
            {
                Animate(sample.DirectionX, sample.DirectionY);
                return;
            }

            _renderer.color = sample.Kind == EntityKind.Player ? _playerColor : _npcColor;

            if (sample.DirectionX != 0)
            {
                _renderer.flipX = sample.DirectionX < 0;
            }
        }

        private void Animate(sbyte directionX, sbyte directionY)
        {
            bool moving = directionX != 0 || directionY != 0;
            if (moving)
            {
                // En diagonal manda el eje horizontal: la hoja no tiene filas diagonales.
                _facing = directionX < 0 ? FacingLeft
                    : directionX > 0 ? FacingRight
                    : directionY > 0 ? FacingUp
                    : FacingDown;
            }

            Sprite[] sheet = moving ? _walk : _idle;
            int frames = sheet.Length / 4;
            int frame = (int)(Time.time * (moving ? WalkFps : IdleFps)) % frames;

            _renderer.color = Color.white;
            _renderer.flipX = false;
            _renderer.sprite = sheet[(_facing * frames) + frame];
        }

        private const int FacingDown = 0;
        private const int FacingUp = 1;
        private const int FacingLeft = 2;
        private const int FacingRight = 3;
        private const float WalkFps = 10f;
        private const float IdleFps = 6f;

        private Sprite[] _idle;
        private Sprite[] _walk;
        private int _facing = FacingDown;
    }
}
