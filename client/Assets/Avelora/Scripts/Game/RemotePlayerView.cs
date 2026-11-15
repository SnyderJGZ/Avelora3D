using Avelora.Shared;
using Avelora.Shared.Prediction;
using UnityEngine;

namespace Avelora.Client
{
    /// <summary>Representa una entidad remota con una primitiva y muestras interpoladas.</summary>
    [DisallowMultipleComponent]
    public sealed class RemotePlayerView : MonoBehaviour
    {
        private Renderer _renderer;
        private Color _playerColor;
        private Color _npcColor;
        private Color _itemColor;
        private Color _rankColor = Color.white;
        private Vector3 _visualScale;
        private Renderer _marker;
        private Renderer _barBack;
        private Renderer _barFill;
        private bool _selected;

        /// <summary>Identificador de la entidad que representa.</summary>
        public int EntityId { get; private set; }

        /// <summary>Prepara la vista y los colores de sus primitivas.</summary>
        public void Initialize(int entityId, Renderer renderer, Color playerColor, Color npcColor, Color itemColor)
        {
            EntityId = entityId;
            _renderer = renderer;
            _visualScale = renderer != null ? renderer.transform.localScale : Vector3.one;
            _playerColor = playerColor;
            _npcColor = npcColor;
            _itemColor = itemColor;
        }

        /// <summary>Actualiza el color de un jugador según la clase que comunica el servidor.</summary>
        public void SetPlayerLook(Color color)
        {
            _playerColor = color;
            if (Kind == EntityKind.Player)
            {
                EntityVisuals.SetColor(_renderer, _playerColor);
            }
        }

        /// <summary>Rango del PNJ: 1 élite y 2 campeón.</summary>
        public void SetRank(byte rank)
        {
            if (rank == Rank)
            {
                return;
            }

            Rank = rank;
            _rankColor = rank == 1 ? new Color(1f, 0.85f, 0.45f, 1f)
                : rank >= 2 ? new Color(1f, 0.5f, 0.45f, 1f)
                : Color.white;
            float scale = rank == 1 ? 1.15f : rank >= 2 ? 1.3f : 1f;
            if (_renderer != null)
            {
                _renderer.transform.localScale = _visualScale * scale;
            }
        }

        /// <summary>Rango del PNJ: 0 normal, 1 élite, 2 campeón.</summary>
        public byte Rank { get; private set; }

        /// <summary>PvP efectivo del snapshot; solo jugadores.</summary>
        public bool Pvp { get; set; }

        /// <summary>Tipo de la última muestra aplicada.</summary>
        public EntityKind Kind { get; private set; }

        /// <summary>Vida en porcentaje de la última muestra.</summary>
        public byte HealthPercent { get; private set; } = 100;

        /// <summary>Si la última muestra dice que está muerta.</summary>
        public bool IsDead { get; private set; }

        /// <summary>Marca de selección y barra de vida para los PNJ.</summary>
        public void SetSelected(bool selected)
        {
            _selected = selected;
            RefreshOverlays();
        }

        /// <summary>Coloca la vista en la muestra interpolada del servidor.</summary>
        public void Apply(EntitySample sample)
        {
            if (_renderer == null)
            {
                return;
            }

            Vector3 position = EntityVisuals.ToWorld(sample.Position);
            transform.position = position;
            Kind = sample.Kind;
            IsDead = sample.Action == EntityAction.Dead;
            HealthPercent = sample.HealthPercent;

            Color color = Kind == EntityKind.Player ? _playerColor
                : Kind == EntityKind.Item ? _itemColor
                : _npcColor * _rankColor;
            EntityVisuals.SetColor(_renderer, color);

            if (Kind == EntityKind.Npc)
            {
                RefreshOverlays();
            }
        }

        private void RefreshOverlays()
        {
            if (Kind != EntityKind.Npc)
            {
                return;
            }

            if (_marker == null)
            {
                _marker = EntityVisuals.CreateOverlay(
                    transform,
                    "Seleccion",
                    new Vector3(0f, 0.025f, 0f),
                    new Vector3(0.9f, 0.025f, 0.9f),
                    new Color(1f, 0.9f, 0.2f, 0.55f));
                _barBack = EntityVisuals.CreateOverlay(
                    transform,
                    "VidaFondo",
                    new Vector3(0f, BarY, 0f),
                    new Vector3(BarWidth, BarHeight, 0.04f),
                    new Color(0.1f, 0.1f, 0.1f, 0.85f));
                _barFill = EntityVisuals.CreateOverlay(
                    transform,
                    "Vida",
                    new Vector3(0f, BarY, 0f),
                    new Vector3(BarWidth, BarHeight, 0.05f),
                    new Color(0.85f, 0.15f, 0.15f, 1f));
            }

            _marker.enabled = _selected && !IsDead;
            bool showBar = !IsDead && (_selected || HealthPercent < 100);
            _barBack.enabled = showBar;
            _barFill.enabled = showBar;
            float width = BarWidth * HealthPercent / 100f;
            _barFill.transform.localScale = new Vector3(width, BarHeight, 0.05f);
            _barFill.transform.localPosition = new Vector3(-BarWidth * 0.5f + width * 0.5f, BarY, 0f);
        }

        private const float BarWidth = 0.8f;
        private const float BarHeight = 0.08f;
        private const float BarY = 1.65f;
    }
}
