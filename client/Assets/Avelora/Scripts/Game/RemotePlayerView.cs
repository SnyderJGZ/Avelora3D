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
        public void SetNpcSheets(Sprite[] idle, Sprite[] walk, Sprite[] hurt, Sprite[] death, Sprite[] attack)
        {
            if (IsSheet(idle) && IsSheet(walk))
            {
                _idle = idle;
                _walk = walk;
                _hurt = IsSheet(hurt) ? hurt : null;
                _death = IsSheet(death) ? death : null;
                _attack = IsSheet(attack) ? attack : null;
            }
        }

        /// <summary>Un objeto del suelo (I-04b): cuadrado pequeño teñido con el color del tipo.</summary>
        public void SetItemLook(Color color)
        {
            _itemColor = color;
            transform.localScale = new Vector3(ItemScale, ItemScale, 1f);
        }

        /// <summary>Tipo de la última muestra aplicada.</summary>
        public EntityKind Kind { get; private set; }

        /// <summary>Vida en porcentaje de la última muestra.</summary>
        public byte HealthPercent { get; private set; } = 100;

        /// <summary>Si la última muestra dice que está muerta (I-02).</summary>
        public bool IsDead { get; private set; }

        /// <summary>Marca la entidad como objetivo seleccionado: marca bajo los pies y barra visible.</summary>
        public void SetSelected(bool selected)
        {
            _selected = selected;
            RefreshOverlays();
        }

        /// <summary>Coloca la vista en la muestra de este frame.</summary>
        public void Apply(EntitySample sample)
        {
            if (_renderer == null)
            {
                return;
            }

            // Se camina si la posición cambia, no por la dirección: un orco que ataca sigue mirando
            // a su objetivo (dirección distinta de cero) pero está quieto (I-03b).
            var position = new Vector3(sample.Position.X, sample.Position.Y, 0f);
            _moving = (position - transform.position).sqrMagnitude > MovingEpsilonSq;
            transform.position = position;
            Kind = sample.Kind;
            IsDead = sample.Action == EntityAction.Dead;

            bool attacking = sample.Action == EntityAction.Attacking;
            if (attacking && !_wasAttacking)
            {
                _attackStartedAt = Time.time;
            }

            _wasAttacking = attacking;

            // La primera muestra fija la vida sin animar daño: un orco que entra herido al área de
            // interés no debe parpadear como si le acabasen de pegar.
            if (_hasHealth && sample.HealthPercent < HealthPercent)
            {
                _hurtStartedAt = Time.time;
            }

            HealthPercent = sample.HealthPercent;
            _hasHealth = true;

            if (IsDead && _deathStartedAt < 0f)
            {
                _deathStartedAt = Time.time;
            }

            if (_walk != null)
            {
                Animate(sample.DirectionX, sample.DirectionY);
            }
            else
            {
                _renderer.color = sample.Kind == EntityKind.Player ? _playerColor
                    : sample.Kind == EntityKind.Item ? _itemColor
                    : _npcColor;

                if (sample.DirectionX != 0)
                {
                    _renderer.flipX = sample.DirectionX < 0;
                }
            }

            if (sample.Kind == EntityKind.Npc)
            {
                RefreshOverlays();
            }
        }

        private void Animate(sbyte directionX, sbyte directionY)
        {
            bool moving = !IsDead && _moving;
            if (!IsDead && (directionX != 0 || directionY != 0))
            {
                // En diagonal manda el eje horizontal: la hoja no tiene filas diagonales.
                _facing = directionX < 0 ? FacingLeft
                    : directionX > 0 ? FacingRight
                    : directionY > 0 ? FacingUp
                    : FacingDown;
            }

            _renderer.color = Color.white;
            _renderer.flipX = false;

            // Una sola pasada: la muerte se queda en su último cuadro hasta que el servidor retira
            // la entidad; el daño vuelve a caminar o a reposo al acabar.
            if (IsDead && _death != null)
            {
                _renderer.sprite = OneShotFrame(_death, Time.time - _deathStartedAt, DeathFps, out _);
                return;
            }

            if (_attack != null && _attackStartedAt >= 0f)
            {
                Sprite frame = OneShotFrame(_attack, Time.time - _attackStartedAt, AttackFps, out bool finished);
                if (!finished)
                {
                    _renderer.sprite = frame;
                    return;
                }

                _attackStartedAt = -1f;
            }

            if (_hurt != null && _hurtStartedAt >= 0f)
            {
                Sprite frame = OneShotFrame(_hurt, Time.time - _hurtStartedAt, HurtFps, out bool finished);
                if (!finished)
                {
                    _renderer.sprite = frame;
                    return;
                }

                _hurtStartedAt = -1f;
            }

            Sprite[] sheet = moving ? _walk : _idle;
            int frames = sheet.Length / 4;
            int loopFrame = (int)(Time.time * (moving ? WalkFps : IdleFps)) % frames;
            _renderer.sprite = sheet[(_facing * frames) + loopFrame];
        }

        private Sprite OneShotFrame(Sprite[] sheet, float elapsed, float fps, out bool finished)
        {
            int frames = sheet.Length / 4;
            int frame = (int)(elapsed * fps);
            finished = frame >= frames;
            return sheet[(_facing * frames) + Mathf.Min(frame, frames - 1)];
        }

        private void RefreshOverlays()
        {
            if (Kind != EntityKind.Npc)
            {
                return;
            }

            if (_marker == null)
            {
                CreateOverlays();
            }

            _marker.enabled = _selected && !IsDead;

            bool showBar = !IsDead && (_selected || HealthPercent < 100);
            _barBack.enabled = showBar;
            _barFill.enabled = showBar;
            _barFill.transform.localScale = new Vector3(BarWidth * HealthPercent / 100f, BarHeight, 1f);
        }

        private void CreateOverlays()
        {
            _marker = CreateQuad("Seleccion", CenterSprite, new Vector3(0f, -0.3f, 0f),
                new Vector3(0.8f, 0.25f, 1f), new Color(1f, 0.9f, 0.2f, 0.45f), -1);
            _barBack = CreateQuad("VidaFondo", CenterSprite, new Vector3(0f, BarY, 0f),
                new Vector3(BarWidth, BarHeight, 1f), new Color(0.1f, 0.1f, 0.1f, 0.8f), 1);
            _barFill = CreateQuad("Vida", LeftSprite, new Vector3(-BarWidth / 2f, BarY, 0f),
                new Vector3(BarWidth, BarHeight, 1f), new Color(0.85f, 0.15f, 0.15f, 1f), 2);
        }

        private SpriteRenderer CreateQuad(string name, Sprite sprite, Vector3 position, Vector3 scale, Color color, int orderOffset)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;

            var quad = go.AddComponent<SpriteRenderer>();
            quad.sprite = sprite;
            quad.color = color;
            quad.sharedMaterial = _renderer.sharedMaterial;
            quad.sortingLayerID = _renderer.sortingLayerID;
            quad.sortingOrder = _renderer.sortingOrder + orderOffset;
            quad.enabled = false;
            return quad;
        }

        private static bool IsSheet(Sprite[] sheet) =>
            sheet != null && sheet.Length >= 4 && sheet.Length % 4 == 0;

        // Sprites blancos de 1×1 unidad creados en ejecución: la marca y la barra no necesitan arte.
        private static Sprite CenterSprite => _centerSprite != null ? _centerSprite
            : (_centerSprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 4f));

        private static Sprite LeftSprite => _leftSprite != null ? _leftSprite
            : (_leftSprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 4f, 4f), new Vector2(0f, 0.5f), 4f));

        private static Sprite _centerSprite;
        private static Sprite _leftSprite;

        private const int FacingDown = 0;
        private const int FacingUp = 1;
        private const int FacingLeft = 2;
        private const int FacingRight = 3;
        private const float WalkFps = 10f;
        private const float IdleFps = 6f;
        private const float HurtFps = 12f;
        private const float DeathFps = 10f;
        private const float AttackFps = 16f;
        private const float ItemScale = 0.4f;
        private const float MovingEpsilonSq = 0.0001f * 0.0001f;
        private const float BarWidth = 0.8f;
        private const float BarHeight = 0.08f;
        private const float BarY = 0.55f;

        private Sprite[] _idle;
        private Sprite[] _walk;
        private Sprite[] _hurt;
        private Sprite[] _death;
        private Sprite[] _attack;
        private bool _moving;
        private Color _itemColor = Color.white;
        private bool _wasAttacking;
        private float _attackStartedAt = -1f;
        private int _facing = FacingDown;
        private bool _selected;
        private bool _hasHealth;
        private float _hurtStartedAt = -1f;
        private float _deathStartedAt = -1f;
        private SpriteRenderer _marker;
        private SpriteRenderer _barBack;
        private SpriteRenderer _barFill;
    }
}
