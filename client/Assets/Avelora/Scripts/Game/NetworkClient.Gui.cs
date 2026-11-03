using System;
using System.Collections.Generic;
using Avelora.Net;
using Avelora.Shared;
using Avelora.Shared.Inventory;
using Avelora.Shared.Protocol;
using UnityEngine;

namespace Avelora.Client
{
    /// <summary>
    /// Interfaz IMGUI del cliente: HUD, barra rápida, inventario, panel de personaje, fichas, chat
    /// y el arrastre de objetos. Solo dibuja y pide; el estado y la red viven en NetworkClient.cs.
    /// </summary>
    public sealed partial class NetworkClient
    {
        private Rect _statsPanel;
        private Rect _inventoryPanel;
        private Rect _hotbarPanel;
        private Rect _skillsPanel;
        private Vector2? _skillsPosition;
        private HotbarShortcut _shortcutDrag;
        private int _shortcutDragFrom = -1;
        private bool _shortcutDragging;
        private Vector2 _shortcutPressAt;
        private int _hotbarHover = -1;

        // Arte oscuro de la interfaz (Resources/GUI/Dark) y sus estilos de texto.
        private Texture2D _darkWindow;
        private Texture2D _darkSlot;
        private Texture2D _darkSlotFill;
        private Texture2D _darkHotbar;
        private Texture2D _darkCharacter;
        private Texture2D _darkTooltip;
        private readonly Texture2D[] _statIcons = new Texture2D[9];
        private GUIStyle _darkTextStyle;
        private GUIStyle _darkRightStyle;
        private GUIStyle _darkRedStyle;
        private GUIStyle _darkRedRightStyle;
        private GUIStyle _darkSmallStyle;
        private GUIStyle _darkTitleStyle;
        private GUIStyle _darkButtonStyle;
        private GUIStyle _characterStatusStyle;
        private GUIStyle _damageNumberStyle;
        private GUIStyle _critNumberStyle;
        private Rect _characterPanel;
        private string _deleteControlName = "avelora-character-delete";
        private string _createControlName = "avelora-character-create";

        // Arte de la interfaz (Resources/GUI). Los iconos van por id de objeto (GUI/Items/<id>):
        // el servidor manda los datos y el cliente solo pone la imagen; null = sin icono todavía.
        private readonly Dictionary<ushort, Texture2D> _itemIcons = new Dictionary<ushort, Texture2D>();
        private GUIStyle _barStyle;
        private static readonly string[] StatusMaskTexts =
        {
            "", "B", "E", "B E", "L", "B L", "E L", "B E L",
            "P", "B P", "E P", "B E P", "L P", "B L P", "E L P", "B E L P",
        };

        private void OnGUI()
        {
            if (connectOnStart && !_welcomeReceived && (_connecting || _selectionActive))
            {
                EnsureOverlayStyles();
                EnsureGuiArt();
                DrawCharacterSelection();
                return;
            }

            DrawCombatEffects();

            if (!showOverlay)
            {
                return;
            }

            EnsureOverlayStyles();

            RemotePlayerView selected = SelectedTarget();
            string rank = selected == null || selected.Rank == 0 ? string.Empty
                : selected.Rank == 1 ? " (élite)" : " (campeón)";
            string combat = "Objetivo " + (_selectedNpcId != 0 ? _selectedNpcId + rank : "ninguno") +
                (_autoAttack ? " (auto · Esc cancela)" : string.Empty) +
                " · Ataques " + _attacksSent +
                (Time.unscaledTime < _combatHintUntil ? " · " + _combatHint : string.Empty);

            GUI.Box(new Rect(10f, 10f, 540f, 130f), GUIContent.none);
            GUI.Label(
                new Rect(20f, 16f, 520f, 118f),
                combat + "\n" +
                "Avelora · " + host + ":" + port + "\n" +
                _status + "\n" +
                "Entidad " + _entityId +
                " · Pendientes " + PendingInputs +
                " · Snapshots " + _snapshotsReceived +
                " · Correcciones " + Corrections +
                " · Saltos " + Snaps + "\n" +
                "Input " + LastInputSequence +
                " · Confirmado " + LastProcessedInputSequence +
                " · Tick " + _lastServerTick +
                " · Remotos " + RemoteViewCount +
                (_transport == null ? " · Pulsa R para reconectar" : string.Empty),
                _overlayStyle);

            if (_welcomeReceived)
            {
                _dropTarget = -1;
                DrawOwnHealth();
                DrawOwnStatuses();
                DrawZoneLabel();
                DrawPvpStatus();
                DrawPvpMarks();
                DrawStatusMarks();
                DrawMinimap();
                DrawChat();
            }
        }

        private void DrawZoneLabel()
        {
            string text;
            Color color;
            if (_zoneKind == ZoneKind.Safe)
            {
                text = "Zona segura";
                color = new Color(0.45f, 0.9f, 0.5f);
            }
            else if (_zoneKind == ZoneKind.Pvp)
            {
                text = "Zona PvP";
                color = new Color(1f, 0.4f, 0.35f);
            }
            else
            {
                return;
            }

            Color previous = GUI.color;
            GUI.color = color;
            GUI.Label(
                new Rect((Screen.width - 240f) / 2f, Screen.height - 150f, 240f, 22f),
                text,
                _damageNumberStyle);
            GUI.color = previous;
        }

        private void DrawPvpStatus()
        {
            _pvpButtonRect = Rect.zero;
            if (!_pvpStatusReceived)
            {
                return;
            }

            string text;
            Color color;
            if (_pvpStatus.Forced == PvpForcedState.InZone)
            {
                text = "PvP activo";
                color = new Color(1f, 0.4f, 0.35f);
            }
            else if (_pvpStatus.Forced == PvpForcedState.Timer)
            {
                if (_pvpStatus.Mode == PvpMode.Pve)
                {
                    text = "PvP activo " + Clock(_pvpForcedEndsAt);
                    color = new Color(1f, 0.6f, 0.3f);
                }
                else
                {
                    text = "PvP activo";
                    color = new Color(1f, 0.4f, 0.35f);
                }
            }
            else if (_pvpStatus.Mode == PvpMode.Pvp)
            {
                text = "PvP activo";
                color = new Color(1f, 0.4f, 0.35f);
            }
            else
            {
                text = "PvE";
                color = new Color(0.45f, 0.9f, 0.5f);
            }

            Color previousColor = GUI.color;
            GUI.color = color;
            GUI.Label(
                new Rect((Screen.width - 240f) / 2f, Screen.height - 172f, 240f, 22f),
                text,
                _damageNumberStyle);
            GUI.color = previousColor;

            if (_pvpStatus.Change == PvpChangeState.Unavailable)
            {
                return;
            }

            _pvpButtonRect = new Rect((Screen.width - 240f) / 2f, Screen.height - 198f, 240f, 24f);
            if (_pvpStatus.Change == PvpChangeState.Waiting && Time.unscaledTime < _pvpChangeReadyAt)
            {
                bool previousEnabled = GUI.enabled;
                GUI.enabled = false;
                GUI.Button(_pvpButtonRect, "Cambio de modo en " + Clock(_pvpChangeReadyAt));
                GUI.enabled = previousEnabled;
                return;
            }

            string buttonText = _pvpStatus.Mode == PvpMode.Pvp ? "Pasar a PvE (P)" : "Pasar a PvP (P)";
            if (GUI.Button(_pvpButtonRect, buttonText))
            {
                RequestPvpModeToggle();
            }
        }

        private string Clock(float endsAt)
        {
            int seconds = Mathf.Max(0, Mathf.CeilToInt(endsAt - Time.unscaledTime));
            return (seconds / 60) + ":" + (seconds % 60).ToString("00");
        }

        private void DrawPvpMarks()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }

            Color previousColor = GUI.color;
            GUI.color = new Color(1f, 0.4f, 0.35f);
            foreach (KeyValuePair<int, RemotePlayerView> pair in _remoteViews)
            {
                RemotePlayerView view = pair.Value;
                if (view == null || view.Kind != EntityKind.Player || !view.Pvp || view.IsDead)
                {
                    continue;
                }

                Vector3 screen = cam.WorldToScreenPoint(view.transform.position + Vector3.up * 0.9f);
                if (screen.z < 0f)
                {
                    continue;
                }

                GUI.Label(
                    new Rect(screen.x - 24f, Screen.height - screen.y - 12f, 48f, 18f),
                    "PvP",
                    _damageNumberStyle);
            }

            GUI.color = previousColor;
        }

        private void DrawStatusMarks()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Color previousColor = GUI.color;
            GUI.color = new Color(0.6f, 0.9f, 1f);
            foreach (KeyValuePair<int, RemotePlayerView> pair in _remoteViews)
            {
                RemotePlayerView view = pair.Value;
                if (view == null || view.IsDead || !_statusMasks.TryGetValue(pair.Key, out byte mask) ||
                    mask >= StatusMaskTexts.Length)
                {
                    continue;
                }

                Vector3 screen = cam.WorldToScreenPoint(view.transform.position + Vector3.up * 1.2f);
                if (screen.z < 0f) continue;
                GUI.Label(new Rect(screen.x - 32f, Screen.height - screen.y - 9f, 64f, 18f),
                    StatusMaskTexts[mask], _damageNumberStyle);
            }

            if (_statusMasks.TryGetValue(_entityId, out byte ownMask) && ownMask < StatusMaskTexts.Length)
            {
                Vector3 screen = cam.WorldToScreenPoint(transform.position + Vector3.up * 1.2f);
                if (screen.z >= 0f)
                {
                    GUI.Label(new Rect(screen.x - 32f, Screen.height - screen.y - 9f, 64f, 18f),
                        StatusMaskTexts[ownMask], _damageNumberStyle);
                }
            }

            GUI.color = previousColor;
        }

        private void DrawMinimap()
        {
            if (_map == null || _map.Grid == null)
            {
                return;
            }

            MapCollisionGrid grid = _map.Grid;
            if (_minimapDirty || _minimapTexture == null)
            {
                if (_minimapTexture != null)
                {
                    Destroy(_minimapTexture);
                }

                _minimapTexture = new Texture2D(grid.Width, grid.Height, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };

                var pixels = new Color32[grid.Width * grid.Height];
                var wallColor = new Color32(20, 20, 24, 255);
                var floorColor = new Color32(70, 82, 70, 255);
                for (int y = 0; y < grid.Height; y++)
                {
                    for (int x = 0; x < grid.Width; x++)
                    {
                        int worldX = grid.OriginX + x;
                        int worldY = grid.OriginY + y;
                        pixels[y * grid.Width + x] = grid.IsTileBlocked(worldX, worldY)
                            ? wallColor
                            : floorColor;
                    }
                }

                var pvpColor = new Color32(170, 60, 55, 255);
                var safeColor = new Color32(70, 170, 90, 255);
                for (int kindPass = 0; kindPass < 2; kindPass++)
                {
                    ZoneKind kind = kindPass == 0 ? ZoneKind.Pvp : ZoneKind.Safe;
                    Color32 color = kindPass == 0 ? pvpColor : safeColor;
                    for (int zoneIndex = 0; zoneIndex < _zones.Length; zoneIndex++)
                    {
                        ZoneInfo zone = _zones[zoneIndex];
                        if (zone.Kind != kind)
                        {
                            continue;
                        }

                        ZoneRect[] rects = zone.Rects;
                        for (int rectIndex = 0; rectIndex < rects.Length; rectIndex++)
                        {
                            ZoneRect zoneRect = rects[rectIndex];
                            int startX = Mathf.Max(0, zoneRect.X - grid.OriginX);
                            int startY = Mathf.Max(0, zoneRect.Y - grid.OriginY);
                            int endX = Mathf.Min(grid.Width, zoneRect.X + zoneRect.Width - grid.OriginX);
                            int endY = Mathf.Min(grid.Height, zoneRect.Y + zoneRect.Height - grid.OriginY);
                            for (int y = startY; y < endY; y++)
                            {
                                for (int x = startX; x < endX; x++)
                                {
                                    int worldX = grid.OriginX + x;
                                    int worldY = grid.OriginY + y;
                                    if (!grid.IsTileBlocked(worldX, worldY))
                                    {
                                        pixels[y * grid.Width + x] = color;
                                    }
                                }
                            }
                        }
                    }
                }

                _minimapTexture.SetPixels32(pixels);
                _minimapTexture.Apply(false);
                _minimapDirty = false;
            }

            float scale = Mathf.Min(200f / grid.Width, 150f / grid.Height);
            var rect = new Rect(
                Screen.width - grid.Width * scale - 12f,
                12f,
                grid.Width * scale,
                grid.Height * scale);
            GUI.Box(new Rect(rect.x - 4f, rect.y - 4f, rect.width + 8f, rect.height + 8f), GUIContent.none);
            GUI.DrawTexture(rect, _minimapTexture);

            if (TryGetEntityPosition(_entityId, out Vector3 self))
            {
                Color previous = GUI.color;
                GUI.color = Color.white;
                GUI.DrawTexture(
                    new Rect(
                        rect.x + (self.x - grid.OriginX) * scale - 2f,
                        rect.yMax - (self.y - grid.OriginY) * scale - 2f,
                        4f,
                        4f),
                    Texture2D.whiteTexture);
                GUI.color = previous;
            }
        }

        private void EnsureOverlayStyles()
        {
            if (_overlayStyle == null)
            {
                _overlayStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    wordWrap = true,
                };
                _overlayStyle.normal.textColor = Color.white;
                // Sin texto enriquecido: un <size=400> escrito en el chat taparía la pantalla de todos.
                _chatLineStyle = new GUIStyle(_overlayStyle) { wordWrap = false, clipping = TextClipping.Clip, richText = false };
            }

            if (_damageNumberStyle == null)
            {
                _damageNumberStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 16,
                    fontStyle = FontStyle.Bold,
                    richText = false,
                };
                _damageNumberStyle.normal.textColor = Color.white;
                _critNumberStyle = new GUIStyle(_damageNumberStyle)
                {
                    fontSize = 24,
                    fontStyle = FontStyle.Bold,
                };
            }
        }

        private void DrawCombatEffects()
        {
            float now = Time.unscaledTime;
            for (int i = _areaFlashes.Count - 1; i >= 0; i--)
            {
                if (now - _areaFlashes[i].StartTime >= 0.35f)
                {
                    _areaFlashes.RemoveAt(i);
                }
            }
            for (int i = _damageNumbers.Count - 1; i >= 0; i--)
            {
                float lifetime = _damageNumbers[i].Critical ? 1.2f : 0.9f;
                if (now - _damageNumbers[i].StartTime >= lifetime)
                {
                    _damageNumbers.RemoveAt(i);
                }
            }

            for (int i = _lootTexts.Count - 1; i >= 0; i--)
            {
                if (now - _lootTexts[i].StartTime >= 2.5f)
                {
                    _lootTexts.RemoveAt(i);
                }
            }

            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }

            if (_damageNumbers.Count > 0 || _lootTexts.Count > 0)
            {
                EnsureOverlayStyles();
            }

            Color previousColor = GUI.color;
            for (int i = 0; i < _projectiles.Count; i++)
            {
                CombatProjectile projectile = _projectiles[i];
                if (!TryGetEntityPosition(projectile.TargetId, out Vector3 destination))
                {
                    continue;
                }

                float progress = Mathf.Clamp01((now - projectile.StartTime) / projectile.Duration);
                Vector3 screen = cam.WorldToScreenPoint(Vector3.Lerp(projectile.From, destination, progress));
                if (screen.z < 0f)
                {
                    continue;
                }

                Vector2 gui = new Vector2(screen.x, Screen.height - screen.y);
                GUI.color = (projectile.Flags & CombatEventFlags.Magical) != 0
                    ? new Color(0.5f, 0.8f, 1f)
                    : new Color(0.95f, 0.85f, 0.5f);
                GUI.DrawTexture(new Rect(gui.x - 3f, gui.y - 3f, 6f, 6f), Texture2D.whiteTexture);
            }

            for (int i = 0; i < _areaFlashes.Count; i++)
            {
                AreaFlash flash = _areaFlashes[i];
                GUI.color = new Color(1f, 0.55f, 0.2f, 1f - (now - flash.StartTime) / 0.35f);
                for (int k = 0; k < 24; k++)
                {
                    float a = k * 2f * Mathf.PI / 24f;
                    Vector3 world = flash.Center + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * flash.Radius;
                    Vector3 screen = cam.WorldToScreenPoint(world);
                    if (screen.z < 0f) continue;
                    GUI.DrawTexture(new Rect(screen.x - 2.5f, Screen.height - screen.y - 2.5f, 5f, 5f),
                        Texture2D.whiteTexture);
                }
            }

            for (int i = 0; i < _damageNumbers.Count; i++)
            {
                DamageNumber number = _damageNumbers[i];
                float elapsed = now - number.StartTime;
                float lifetime = number.Critical ? 1.2f : 0.9f;
                Vector3 world = number.Position + Vector3.up * (0.8f * elapsed / lifetime);
                Vector3 screen = cam.WorldToScreenPoint(world);
                if (screen.z < 0f)
                {
                    continue;
                }

                Vector2 gui = new Vector2(screen.x, Screen.height - screen.y);
                Color color = number.Critical && !number.Heal && !number.Absorbed ? new Color(1f, 0.7f, 0.15f)
                    : number.Heal ? new Color(0.35f, 1f, 0.4f)
                    : number.Absorbed ? new Color(0.45f, 0.85f, 1f)
                    : (number.OnSelf ? new Color(1f, 0.35f, 0.3f) : Color.white);
                color.a = 1f - elapsed / lifetime;
                GUI.color = color;
                GUI.Label(
                    number.Critical ? new Rect(gui.x - 48f, gui.y - 28f, 96f, 32f) : new Rect(gui.x - 32f, gui.y - 20f, 64f, 24f),
                    number.Text,
                    number.Critical ? _critNumberStyle : _damageNumberStyle);
            }

            if (_lootTexts.Count > 0 && TryGetEntityPosition(_entityId, out Vector3 self))
            {
                for (int i = 0; i < _lootTexts.Count; i++)
                {
                    LootText loot = _lootTexts[i];
                    float elapsed = now - loot.StartTime;
                    Vector3 world = self + Vector3.up * (1.0f + 1.0f * elapsed / 2.5f);
                    Vector3 screen = cam.WorldToScreenPoint(world);
                    if (screen.z < 0f)
                    {
                        continue;
                    }

                    Vector2 gui = new Vector2(screen.x, Screen.height - screen.y);
                    gui.y += loot.Line * 18f;
                    Color color = loot.Color;
                    color.a = 1f - elapsed / 2.5f;
                    GUI.color = color;
                    GUI.Label(new Rect(gui.x - 120f, gui.y - 20f, 240f, 22f), loot.Text, _damageNumberStyle);
                }
            }

            GUI.color = previousColor;
        }

        private void DrawCharacterSelection()
        {
            if (!_characterListReceived)
            {
                Rect waiting = new Rect((Screen.width - 440f) / 2f, (Screen.height - 76f) / 2f, 440f, 76f);
                GUI.DrawTexture(waiting, _darkSlotFill);
                GUI.Label(new Rect(waiting.x + 16f, waiting.y + 12f, waiting.width - 32f, 52f), _status, _darkTextStyle);
                return;
            }

            CharacterListEntry[] characters = _characterList.Characters;
            CharacterClassEntry[] classes = _characterList.Classes;
            int classRows = (classes.Length + 1) / 2;
            float contentHeight = 56f + (characters.Length * 32f) + 92f + (classRows * 36f) + 58f;
            float width = Mathf.Min(560f, Screen.width - 24f);
            float height = Mathf.Min(Screen.height - 24f, contentHeight + 82f);
            _characterPanel = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
            GUI.DrawTexture(_characterPanel, _darkSlotFill);
            GUI.Label(new Rect(_characterPanel.x + 16f, _characterPanel.y + 10f, width - 32f, 28f),
                "Selección de personaje", _darkTitleStyle);

            if (_confirmDelete)
            {
                DrawDeleteConfirmation();
                DrawCharacterStatus(width);
                return;
            }

            Rect viewport = new Rect(_characterPanel.x + 12f, _characterPanel.y + 42f, width - 24f, height - 82f);
            Rect content = new Rect(0f, 0f, viewport.width - 18f, contentHeight);
            _characterScroll = GUI.BeginScrollView(viewport, _characterScroll, content, false, true);
            float y = 0f;

            GUI.Label(new Rect(4f, y, content.width - 8f, 24f),
                "Personajes (" + characters.Length + "/" + _characterList.MaxCharacters + ")", _darkTextStyle);
            y += 25f;

            for (int i = 0; i < characters.Length; i++)
            {
                CharacterListEntry character = characters[i];
                var row = new Rect(4f, y, content.width - 8f, 30f);
                if (character.CharacterId == _selectedCharacterId)
                {
                    GUI.color = new Color(0.34f, 0.4f, 0.48f, 0.9f);
                    GUI.DrawTexture(row, Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }

                int clickCount = Event.current.clickCount;
                if (LeftButton(row, GUIContent.none, GUIStyle.none))
                {
                    _selectedCharacterId = character.CharacterId;
                    if (clickCount >= 2)
                    {
                        SelectCharacter(character.CharacterId);
                    }
                }

                DrawClassSwatch(new Rect(row.x + 7f, row.y + 7f, 16f, 16f), character.ClassId);
                GUI.Label(new Rect(row.x + 32f, row.y + 2f, row.width - 40f, 26f),
                    character.Name + " · " + ClassName(classes, character.ClassId) + " · Nv " + character.Level,
                    _darkTextStyle);
                y += 32f;
            }

            if (characters.Length == 0)
            {
                GUI.Label(new Rect(8f, y, content.width - 16f, 26f), "Todavía no tienes personajes", _darkSmallStyle);
                y += 28f;
            }

            bool canEnter = _selectedCharacterId != 0 && !_characterSelectionPending && !_characterOperationPending;
            GUI.enabled = canEnter;
            if (LeftButton(new Rect(8f, y, 110f, 30f), "Entrar", _darkButtonStyle))
            {
                SelectCharacter(_selectedCharacterId);
            }

            GUI.enabled = _selectedCharacterId != 0 && !_characterOperationPending && !_characterSelectionPending;
            if (LeftButton(new Rect(126f, y, 110f, 30f), "Borrar", _darkButtonStyle))
            {
                _deleteConfirmationName = string.Empty;
                _confirmDelete = true;
            }

            GUI.enabled = true;
            y += 40f;
            if (characters.Length < _characterList.MaxCharacters)
            {
                GUI.Label(new Rect(4f, y, content.width - 8f, 22f), "Crear personaje", _darkTextStyle);
                y += 23f;

                GUI.SetNextControlName(_createControlName);
                _newCharacterName = GUI.TextField(new Rect(8f, y, Mathf.Min(260f, content.width - 16f), 26f),
                    _newCharacterName, 32);
                y += 32f;

                for (int i = 0; i < classes.Length; i++)
                {
                    int column = i % 2;
                    int row = i / 2;
                    float cellWidth = (content.width - 20f) / 2f;
                    var button = new Rect(8f + (column * (cellWidth + 4f)), y + (row * 34f), cellWidth, 30f);
                    if (classes[i].ClassId == _createClassId)
                    {
                        GUI.color = new Color(0.34f, 0.4f, 0.48f, 0.9f);
                        GUI.DrawTexture(button, Texture2D.whiteTexture);
                        GUI.color = Color.white;
                    }

                    if (LeftButton(button, GUIContent.none, GUIStyle.none))
                    {
                        _createClassId = classes[i].ClassId;
                    }

                    DrawClassSwatch(new Rect(button.x + 6f, button.y + 7f, 16f, 16f), classes[i].ClassId);
                    GUI.Label(new Rect(button.x + 28f, button.y + 2f, button.width - 34f, 26f), classes[i].Name, _darkSmallStyle);
                }

                y += classRows * 34f + 4f;
                bool canCreate = classes.Length > 0 && _createClassId != 0 &&
                    !_characterOperationPending && !_characterSelectionPending;
                GUI.enabled = canCreate;
                if (LeftButton(new Rect(8f, y, 150f, 30f), "Crear", _darkButtonStyle))
                {
                    CreateCharacter(_createClassId);
                }

                GUI.enabled = true;
            }
            else
            {
                GUI.Label(new Rect(8f, y, content.width - 16f, 26f), "Ya tienes el máximo de personajes", _darkSmallStyle);
            }

            GUI.EndScrollView();

            DrawCharacterStatus(width);
        }

        private void DrawCharacterStatus(float width)
        {
            if (_characterStatusStyle == null)
            {
                _characterStatusStyle = new GUIStyle(_darkSmallStyle)
                {
                    wordWrap = false,
                    clipping = TextClipping.Clip,
                    richText = false,
                };
            }

            GUI.Label(new Rect(_characterPanel.x + 14f, _characterPanel.yMax - 34f, width - 28f, 24f), _status, _characterStatusStyle);
        }

        private void DrawDeleteConfirmation()
        {
            CharacterListEntry selected = FindCharacter(_selectedCharacterId);
            if (selected.CharacterId == 0)
            {
                _confirmDelete = false;
                return;
            }

            const float width = 390f;
            const float height = 154f;
            var dialog = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
            GUI.DrawTexture(dialog, _darkSlotFill);
            GUI.Label(new Rect(dialog.x + 14f, dialog.y + 10f, width - 28f, 28f),
                "Escribe el nombre para borrar a " + selected.Name, _darkTextStyle);
            GUI.SetNextControlName(_deleteControlName);
            _deleteConfirmationName = GUI.TextField(
                new Rect(dialog.x + 14f, dialog.y + 48f, width - 28f, 28f), _deleteConfirmationName, 32);

            GUI.enabled = !_characterOperationPending && !_characterSelectionPending;
            if (LeftButton(new Rect(dialog.x + 42f, dialog.y + 100f, 140f, 32f), "Confirmar", _darkButtonStyle))
            {
                DeleteCharacter(selected.CharacterId, _deleteConfirmationName);
            }

            GUI.enabled = true;
            if (LeftButton(new Rect(dialog.x + 208f, dialog.y + 100f, 140f, 32f), "Cancelar", _darkButtonStyle))
            {
                _confirmDelete = false;
            }
        }

        private void DrawClassSwatch(Rect rect, byte classId)
        {
            Color previous = GUI.color;
            GUI.color = ClassColor(classId);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private static string ClassName(CharacterClassEntry[] classes, byte classId)
        {
            for (int i = 0; i < classes.Length; i++)
            {
                if (classes[i].ClassId == classId)
                {
                    return classes[i].Name;
                }
            }

            return "Clase " + classId;
        }

        private CharacterListEntry FindCharacter(int characterId)
        {
            CharacterListEntry[] characters = _characterList.Characters;
            for (int i = 0; i < characters.Length; i++)
            {
                if (characters[i].CharacterId == characterId)
                {
                    return characters[i];
                }
            }

            return default;
        }

        /// <summary>
        /// Últimos mensajes de la zona abajo a la izquierda y caja para escribir (I-07b). Las teclas
        /// se leen aquí, antes que el TextField, para que Enter y Esc no los consuma él.
        /// </summary>
        private void DrawChat()
        {
            Event e = Event.current;
            bool keyDown = e.type == EventType.KeyDown;
            bool enter = keyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);

            if (!_chatOpen && enter)
            {
                _chatOpen = true;
                _chatDraft = string.Empty;
                e.Use();
            }
            else if (_chatOpen && enter)
            {
                SendChat(_chatDraft);
                _chatOpen = false;
                e.Use();
            }
            else if (_chatOpen && keyDown && e.keyCode == KeyCode.Escape)
            {
                _chatOpen = false;
                e.Use();
            }

            const float width = 420f;
            const float lineHeight = 18f;
            float logHeight = (ChatLogLines * lineHeight) + 10f;
            var log = new Rect(10f, Screen.height - 100f - logHeight, width, logHeight);

            if (_chatLog.Count > 0 || _chatOpen)
            {
                GUI.Box(log, GUIContent.none);
                float y = log.yMax - 5f - (_chatLog.Count * lineHeight);
                for (int i = 0; i < _chatLog.Count; i++)
                {
                    GUI.Label(new Rect(log.x + 6f, y + (i * lineHeight), width - 12f, lineHeight), _chatLog[i], _chatLineStyle);
                }
            }

            if (_chatOpen)
            {
                GUI.SetNextControlName(ChatControl);
                _chatDraft = GUI.TextField(new Rect(log.x, log.yMax + 4f, width, 22f), _chatDraft, ProtocolConstants.MaxChatBytes);
                GUI.FocusControl(ChatControl);
            }
            else if (_chatLog.Count == 0)
            {
                GUI.Label(new Rect(log.x, log.yMax - 20f, width, 20f), "Enter: chat", _overlayStyle);
            }
        }

        /// <summary>El servidor valida y limita el ritmo; aquí solo se evita mandar lo que seguro rechazaría.</summary>
        private void SendChat(string text)
        {
            text = text.Trim();
            if (text.Length == 0 || _transport == null)
            {
                return;
            }

            if (System.Text.Encoding.UTF8.GetByteCount(text) > ProtocolConstants.MaxChatBytes)
            {
                ShowHint("mensaje demasiado largo");
                return;
            }

            byte[] bytes = MessageCodec.EncodeChatSend(new ChatSendMessage(text));
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
        }

        // Medidas en píxeles de las imágenes de Resources/GUI/Dark (recortadas de Art/GUI/Inventario y
        // Art/GUI/panel_stats): todo se dibuja escalado y se coloca con estas coordenadas.
        private const float WindowWidth = 838f, WindowHeight = 711f;
        private const float CharacterWidth = 916f, CharacterHeight = 791f;
        private const float TooltipWidth = 476f, TooltipHeight = 555f;
        private const float HotbarWidth = 1466f, HotbarHeight = 152f;

        /// <summary>Rectángulo de la imagen original (en píxeles) colocado sobre un panel escalado.</summary>
        private static Rect Part(Rect panel, float scale, float x, float y, float width, float height) =>
            new Rect(panel.x + (x * scale), panel.y + (y * scale), width * scale, height * scale);

        /// <summary>GUI.Button que solo responde al clic izquierdo (IMGUI acepta cualquier botón del ratón).</summary>
        private static bool LeftButton(Rect rect, GUIContent content, GUIStyle style) =>
            GUI.Button(rect, content, style) && Event.current.button == 0;

        private static bool LeftButton(Rect rect, string text, GUIStyle style) =>
            LeftButton(rect, new GUIContent(text), style);

        // Paneles movibles (como en Lineage 2): se arrastran con el clic izquierdo por la barra del
        // título y la posición se guarda en PlayerPrefs. 0 = ninguno, 1 = personaje, 2 = inventario, 3 = habilidades.
        private int _draggedPanel;
        private Vector2 _dragOffset;
        private Vector2? _statsPosition;
        private Vector2? _inventoryPosition;

        /// <summary>
        /// Coloca un panel: en su sitio guardado (o el de por defecto) y, si se arrastra por la barra
        /// del título (alto <paramref name="titleHeight"/>, sin la X de la derecha), lo mueve sin
        /// dejar que se salga de la pantalla.
        /// </summary>
        private Rect PlacePanel(int id, ref Vector2? position, Vector2 fallback, Vector2 size, float titleHeight)
        {
            string key = "avelora.panel" + id;
            if (position == null)
            {
                position = PlayerPrefs.HasKey(key + ".x")
                    ? new Vector2(PlayerPrefs.GetFloat(key + ".x"), PlayerPrefs.GetFloat(key + ".y"))
                    : fallback;
            }

            Event e = Event.current;
            var title = new Rect(position.Value.x, position.Value.y, size.x - titleHeight - 10f, titleHeight);
            if (e.type == EventType.MouseDown && e.button == 0 && title.Contains(e.mousePosition))
            {
                _draggedPanel = id;
                _dragOffset = e.mousePosition - position.Value;
                e.Use();
            }
            else if (_draggedPanel == id && e.type == EventType.MouseDrag)
            {
                position = e.mousePosition - _dragOffset;
                e.Use();
            }
            else if (_draggedPanel == id && e.rawType == EventType.MouseUp)
            {
                _draggedPanel = 0;
                PlayerPrefs.SetFloat(key + ".x", position.Value.x);
                PlayerPrefs.SetFloat(key + ".y", position.Value.y);
            }

            // Siempre entero en pantalla, también si la ventana se achica después de moverlo.
            position = new Vector2(
                Mathf.Clamp(position.Value.x, 0f, Mathf.Max(0f, Screen.width - size.x)),
                Mathf.Clamp(position.Value.y, 0f, Mathf.Max(0f, Screen.height - size.y)));
            return new Rect(position.Value, size);
        }

        /// <summary>
        /// Inventario (I): ventana con la rejilla de 5 columnas; cantidad abajo a la derecha y la ficha
        /// del objeto al pasar el ratón. La X o la tecla I lo cierran.
        /// </summary>
        private void DrawInventory()
        {
            const int columns = 5;
            const float scale = 0.62f;

            EnsureGuiArt();

            // Por defecto, centrado en la mitad derecha de la pantalla (el personaje va en la izquierda).
            float panelWidth = WindowWidth * scale;
            _inventoryPanel = PlacePanel(
                2,
                ref _inventoryPosition,
                new Vector2((Screen.width * 0.5f) + Mathf.Max(10f, ((Screen.width * 0.5f) - panelWidth) / 2f), 80f),
                new Vector2(panelWidth, WindowHeight * scale),
                75f * scale);
            ClearCoveredDropTargets(_inventoryPanel);
            GUI.DrawTexture(_inventoryPanel, _darkWindow);
            if (LeftButton(Part(_inventoryPanel, scale, 775f, 12f, 50f, 50f), GUIContent.none, GUIStyle.none))
            {
                _showInventory = false;
            }

            // Zona útil de la ventana: x 17–827, y 75–695 en la imagen original.
            Rect content = Part(_inventoryPanel, scale, 17f, 75f, 810f, 620f);
            int rows = Mathf.Max(1, (_inventoryIds.Length + columns - 1) / columns);
            // Abajo quedan 34 px para la línea de ayuda.
            float cell = Mathf.Min((content.width - 40f) / columns, (content.height - 72f - ((rows - 1) * 6f)) / rows);
            float gap = 6f;
            float gridWidth = (columns * cell) + ((columns - 1) * gap);
            float left = content.x + ((content.width - gridWidth) / 2f);
            float top = content.y + 10f;

            ushort hovered = 0;
            Vector2 mouse = Event.current.mousePosition;
            for (int i = 0; i < _inventoryIds.Length; i++)
            {
                var slot = new Rect(left + ((i % columns) * (cell + gap)), top + ((i / columns) * (cell + gap)), cell, cell);
                SlotInput(slot, i, _inventoryIds[i]);
                if (DrawItemSlot(slot, _inventoryIds[i], _inventoryCounts[i]) && slot.Contains(mouse))
                {
                    hovered = _inventoryIds[i];
                }
            }

            GUI.Label(
                new Rect(content.x + 12f, content.yMax - 48f, content.width - 24f, 20f),
                "Oro: " + _gold.ToString("N0"),
                _darkSmallStyle);

            GUI.Label(
                new Rect(content.x + 12f, content.yMax - 26f, content.width - 24f, 20f),
                "Arrastra para mover (Shift: media pila) · doble clic usar · clic dcho. equipar · fuera: destruir",
                _darkSmallStyle);

            if (hovered != 0)
            {
                DrawItemTooltip(hovered, mouse);
            }
        }

        /// <summary>
        /// Barra rápida abajo al centro: diez accesos independientes con las teclas 1–0.
        /// </summary>
        private void DrawHotbar()
        {
            _hotbarHover = -1;
            EnsureGuiArt();

            const float scale = 0.42f; // tamaño fijo: el texto no escala, así que los paneles tampoco
            _hotbarPanel = new Rect((Screen.width - (HotbarWidth * scale)) / 2f, Screen.height - (HotbarHeight * scale) - 8f, HotbarWidth * scale, HotbarHeight * scale);
            GUI.DrawTexture(_hotbarPanel, _darkHotbar);

            ushort hovered = 0;
            Vector2 mouse = Event.current.mousePosition;
            int count = _hotbar.Length;
            for (int i = 0; i < count; i++)
            {
                // Huecos de 123×120 cada 132 px desde x = 77, y = 16 en la imagen original.
                Rect slot = Part(_hotbarPanel, scale, 77f + (i * 132f) + 13.5f, 16f + 12f, 96f, 96f);
                if (slot.Contains(mouse)) _hotbarHover = i;
                HotbarShortcut shortcut = _hotbar[i];
                ShortcutInput(slot, i);
                if (shortcut.Kind == ShortcutKind.Skill && TryGetSkill(shortcut.Id, out SkillCatalogEntry skill))
                {
                    DrawSkillOnSlot(slot, i, skill);
                }
                else if (shortcut.Kind == ShortcutKind.Item)
                {
                    int total = HotbarItemCount(shortcut.Id);
                    Color previous = GUI.color;
                    if (total == 0) GUI.color = new Color(0.4f, 0.4f, 0.4f, 1f);
                    DrawItemSlot(slot, shortcut.Id, 1, drawFrame: false);
                    GUI.Label(new Rect(slot.x, slot.yMax - 20f, slot.width - 6f, 18f), total.ToString(), _darkRightStyle);
                    GUI.color = previous;
                    if (slot.Contains(mouse)) hovered = shortcut.Id;
                }
            }

            if (hovered != 0)
            {
                DrawItemTooltip(hovered, mouse);
            }
        }

        private void DrawSkills()
        {
            const float scale = 0.62f;
            float panelWidth = WindowWidth * scale;
            _skillsPanel = PlacePanel(3, ref _skillsPosition,
                new Vector2(Mathf.Max(10f, Screen.width * 0.5f - panelWidth - 20f), 80f),
                new Vector2(panelWidth, WindowHeight * scale), 75f * scale);
            ClearCoveredDropTargets(_skillsPanel);
            GUI.DrawTexture(_skillsPanel, _darkWindow);
            if (LeftButton(Part(_skillsPanel, scale, 775f, 12f, 50f, 50f), GUIContent.none, GUIStyle.none))
            {
                _showSkills = false;
            }

            Rect content = Part(_skillsPanel, scale, 17f, 75f, 810f, 620f);
            SkillCatalogEntry[] skills = _skillCatalog.Skills;
            int count = skills == null ? 0 : skills.Length;
            float rowHeight = count == 0 ? 48f : Mathf.Min(48f, (content.height - 30f) / count);
            Event e = Event.current;
            for (int i = 0; i < count; i++)
            {
                SkillCatalogEntry skill = skills[i];
                var row = new Rect(content.x, content.y + i * rowHeight, content.width, rowHeight);
                Color previous = GUI.color;
                bool locked = skill.RequiredLevel > _level;
                if (locked) GUI.color = new Color(0.5f, 0.5f, 0.5f, 1f);
                GUI.Label(new Rect(row.x, row.y, row.width * 0.34f, row.height),
                    skill.Name + " · " + SkillKindText(skill), _darkSmallStyle);
                GUI.Label(new Rect(row.x + row.width * 0.34f, row.y, row.width * 0.3f, row.height),
                    "Coste " + skill.ResourceCost + " " + _skillCatalog.ResourceName, _darkSmallStyle);
                GUI.Label(new Rect(row.x + row.width * 0.64f, row.y, row.width * 0.24f, row.height),
                    "Recarga " + (skill.CooldownTicks / (float)SimulationConstants.TickRate).ToString("0.#") + " s", _darkSmallStyle);
                GUI.Label(new Rect(row.x + row.width * 0.88f, row.y, row.width * 0.12f, row.height),
                    "Nv " + skill.RequiredLevel, _darkSmallStyle);
                GUI.color = previous;
                if (!locked && e.type == EventType.MouseDown && e.button == 0 && row.Contains(e.mousePosition))
                {
                    _shortcutDrag = new HotbarShortcut(ShortcutKind.Skill, skill.Id);
                    _shortcutDragFrom = -1;
                    _shortcutPressAt = e.mousePosition;
                    e.Use();
                }
            }
            GUI.Label(new Rect(content.x + 12f, content.yMax - 26f, content.width - 24f, 20f),
                "Arrastra una habilidad a la barra · clic dcho. en el hueco: quitar", _darkSmallStyle);
        }

        private static string SkillKindText(SkillCatalogEntry skill)
        {
            string kind = skill.Effect == 1 ? "cura"
                : skill.Effect == 2 ? string.Empty
                : skill.Radius > 0f ? "área " + skill.Radius.ToString("0.#") + " m"
                : "un objetivo";
            string status = StatusText(skill);
            if (kind.Length == 0) return status;
            if (status.Length == 0) return kind;
            return kind + " + " + status;
        }

        private static string StatusText(SkillCatalogEntry skill)
        {
            if (skill.StatusKind == StatusKind.None) return string.Empty;
            string seconds = (skill.StatusTicks / (float)SimulationConstants.TickRate).ToString("0.#");
            switch (skill.StatusKind)
            {
                case StatusKind.Bonus: return "bonus +" + skill.StatusAmount + " · " + seconds + " s";
                case StatusKind.Shield: return "escudo " + skill.StatusAmount + "+ · " + seconds + " s";
                case StatusKind.Slow: return "ralentiza " + skill.StatusAmount + " % · " + seconds + " s";
                case StatusKind.Taunt: return "provoca · " + seconds + " s";
                default: return string.Empty;
            }
        }

        private void DrawSkillOnSlot(Rect slot, int index, SkillCatalogEntry skill)
        {
            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(slot, Texture2D.whiteTexture);
            GUI.color = ResourceCurrent < skill.ResourceCost || _level < skill.RequiredLevel
                ? new Color(0.6f, 0.6f, 0.6f, 1f) : previous;
            string name = skill.Name.Length > 6 ? skill.Name.Substring(0, 6) : skill.Name;
            GUI.Label(new Rect(slot.x, slot.y + slot.height * 0.35f, slot.width, slot.height * 0.65f), name, _overlayStyle);
            GUI.Label(new Rect(slot.x + 2f, slot.y, slot.width, 18f), ((index + 1) % 10).ToString(), _overlayStyle);
            float seconds = SkillSecondsLeft(skill.Id);
            if (seconds > 0f)
            {
                float fraction = Mathf.Clamp01(seconds / (skill.CooldownTicks / (float)SimulationConstants.TickRate));
                GUI.color = new Color(0f, 0f, 0f, 0.6f);
                GUI.DrawTexture(new Rect(slot.x, slot.yMax - slot.height * fraction, slot.width, slot.height * fraction), Texture2D.whiteTexture);
                GUI.color = previous;
                GUI.Label(slot, Mathf.CeilToInt(seconds).ToString(), _damageNumberStyle);
            }
            GUI.color = previous;
        }

        private void ShortcutInput(Rect slot, int index)
        {
            Event e = Event.current;
            if (e.type != EventType.MouseDown || !slot.Contains(e.mousePosition)) return;
            if ((_showStats && _statsReceived && _statsPanel.Contains(e.mousePosition)) ||
                (_showInventory && _inventoryPanel.Contains(e.mousePosition)) ||
                (_showSkills && _skillCatalogReceived && _skillsPanel.Contains(e.mousePosition))) return;
            if (e.button == 0 && _hotbar[index].Kind != ShortcutKind.Empty)
            {
                _shortcutDrag = _hotbar[index];
                _shortcutDragFrom = index;
                _shortcutPressAt = e.mousePosition;
                e.Use();
            }
            else if (e.button == 1)
            {
                _hotbar[index] = default;
                SaveHotbar();
                e.Use();
            }
        }

        private void HandleShortcutDrag()
        {
            if (_shortcutDrag.Kind == ShortcutKind.Empty) return;
            Event e = Event.current;
            if (!_shortcutDragging && e.type == EventType.MouseDrag && (e.mousePosition - _shortcutPressAt).sqrMagnitude > 36f)
            {
                _shortcutDragging = true;
            }
            if (_shortcutDragging && e.type == EventType.Repaint)
            {
                var rect = new Rect(e.mousePosition.x - 22f, e.mousePosition.y - 22f, 44f, 44f);
                if (_shortcutDrag.Kind == ShortcutKind.Item)
                    DrawItemSlot(rect, _shortcutDrag.Id, 1, drawFrame: false);
                else if (TryGetSkill(_shortcutDrag.Id, out SkillCatalogEntry skill))
                {
                    Color previous = GUI.color;
                    GUI.color = new Color(0f, 0f, 0f, 0.55f);
                    GUI.DrawTexture(rect, Texture2D.whiteTexture);
                    GUI.color = previous;
                    GUI.Label(rect, skill.Name.Length > 6 ? skill.Name.Substring(0, 6) : skill.Name, _overlayStyle);
                }
            }
            if (e.rawType != EventType.MouseUp || e.button != 0) return;
            bool overPanel = (_showStats && _statsReceived && _statsPanel.Contains(e.mousePosition)) ||
                (_showInventory && _inventoryPanel.Contains(e.mousePosition)) ||
                (_showSkills && _skillCatalogReceived && _skillsPanel.Contains(e.mousePosition));
            if (_shortcutDragging && !overPanel)
            {
                if (_hotbarHover >= 0)
                {
                    PlaceHotbarShortcut(_hotbarHover, _shortcutDragFrom, _shortcutDrag);
                }
                else if (_shortcutDragFrom >= 0 && !_hotbarPanel.Contains(e.mousePosition))
                {
                    _hotbar[_shortcutDragFrom] = default;
                    SaveHotbar();
                }
            }
            _shortcutDrag = default;
            _shortcutDragFrom = -1;
            _shortcutDragging = false;
        }

        // Inventario interactivo (I-14b). Un «origen» es un hueco del inventario (0…) o del equipo
        // (EquipSource + EquipSlot). El cliente solo pide; el servidor valida y manda el Inventory nuevo.
        private const int EquipSource = InventoryDrag.EquipSource;
        private int _pressSource = -1;
        private Vector2 _pressAt;
        private bool _dragging;
        private int _dropTarget = -1;
        private int _discardSlot = -1;
        private ushort _discardItemId;
        private int _discardAmount;
        private Rect _discardDialog;

        private void ClearCoveredDropTargets(Rect panel)
        {
            // Cada panel tapa los destinos anteriores; sus huecos visibles se registran después.
            if (!panel.Contains(Event.current.mousePosition)) return;
            _dropTarget = -1;
            _hotbarHover = -1;
        }

        /// <summary>
        /// Clics sobre un hueco: izquierdo empieza a arrastrar (doble clic usa o quita), derecho equipa
        /// o quita. Además apunta qué hueco hay bajo el ratón para cuando se suelte lo arrastrado.
        /// </summary>
        private void SlotInput(Rect rect, int source, ushort id)
        {
            Event e = Event.current;
            if (!rect.Contains(e.mousePosition))
            {
                return;
            }

            _dropTarget = source;
            if (e.type != EventType.MouseDown || id == 0 || _transport == null)
            {
                return;
            }

            if (e.button == 0 && e.clickCount == 2)
            {
                _pressSource = -1;
                ActivateSlot(source);
                e.Use();
            }
            else if (e.button == 0)
            {
                _pressSource = source;
                _pressAt = e.mousePosition;
                e.Use();
            }
            else if (e.button == 1)
            {
                if (source >= EquipSource ||
                    (_catalog.TryGetValue(id, out ItemCatalogEntry item) && (item.Kind == ItemKind.Weapon || item.Kind == ItemKind.Armor)))
                {
                    ActivateSlot(source);
                }
                else
                {
                    ShowHint("no se equipa");
                }

                e.Use();
            }
        }

        /// <summary>Hueco del inventario: usar o equipar. Hueco del equipo: devolverlo al inventario.</summary>
        private void ActivateSlot(int source)
        {
            if (source >= EquipSource)
            {
                SendUnequip(source - EquipSource, byte.MaxValue);
            }
            else
            {
                UseSlot(source);
            }
        }

        /// <summary>
        /// Arrastre en curso: dibuja el icono bajo el ratón y, al soltar, pide mover, dividir (Shift),
        /// equipar o quitar. Soltar fuera de los paneles pregunta si destruirlo.
        /// </summary>
        private void HandleDrag()
        {
            Event e = Event.current;
            if (_pressSource < 0)
            {
                return;
            }

            if (!_dragging && e.type == EventType.MouseDrag && (e.mousePosition - _pressAt).sqrMagnitude > 36f)
            {
                _dragging = true;
            }

            int from = _pressSource;
            ushort id = from >= EquipSource ? _equipment[from - EquipSource] : from < _inventoryIds.Length ? _inventoryIds[from] : (ushort)0;
            if (_dragging && e.type == EventType.Repaint && id != 0)
            {
                DrawItemSlot(new Rect(e.mousePosition.x - 24f, e.mousePosition.y - 24f, 48f, 48f), id, 1, drawFrame: false);
            }

            if (e.rawType != EventType.MouseUp || e.button != 0)
            {
                return;
            }

            _pressSource = -1;
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            if (_transport == null)
            {
                return;
            }

            if (_hotbarHover >= 0)
            {
                if (from < EquipSource && id != 0)
                    PlaceHotbarShortcut(_hotbarHover, -1, new HotbarShortcut(ShortcutKind.Item, id));
                return;
            }

            int to = _dropTarget;
            bool overPanel = (_showStats && _statsPanel.Contains(e.mousePosition)) ||
                (_showInventory && _inventoryPanel.Contains(e.mousePosition)) ||
                (_showSkills && _skillsPanel.Contains(e.mousePosition)) ||
                _hotbarPanel.Contains(e.mousePosition);
            byte equipSlot = _catalog.TryGetValue(id, out ItemCatalogEntry item) ? item.EquipSlot : byte.MaxValue;
            switch (InventoryDrag.Resolve(from, to, overPanel, id, equipSlot))
            {
                case DragResult.Move:
                    SendMoveItem(from, to, InventoryDrag.MoveCount(_inventoryCounts[from], e.shift));
                    break;
                case DragResult.Equip:
                    UseSlot(from);
                    break;
                case DragResult.Unequip:
                    SendUnequip(from - EquipSource, to);
                    break;
                case DragResult.Destroy:
                    _discardSlot = from;
                    _discardItemId = id;
                    _discardAmount = _inventoryCounts[from];
                    break;
                case DragResult.WrongSlot:
                    ShowHint("no va en ese hueco");
                    break;
            }
        }

        /// <summary>
        /// Aviso al soltar un objeto fuera de los paneles: tirarlo lo destruye (I-21; los jugadores no
        /// dejan objetos en el suelo, así nadie llena el tope de botín de la zona). Con una pila se
        /// elige cuántos con el deslizador o −/+ (sin teclado: los números son la barra rápida).
        /// </summary>
        private void DrawDiscardDialog()
        {
            // Si el hueco cambió mientras tanto (lo recogió otro mensaje), se olvida.
            if (_discardSlot >= _inventoryIds.Length || _inventoryIds[_discardSlot] != _discardItemId)
            {
                _discardSlot = -1;
                return;
            }

            string name = _catalog.TryGetValue(_discardItemId, out ItemCatalogEntry item) ? item.Name : "Objeto " + _discardItemId;
            ushort count = _inventoryCounts[_discardSlot];
            _discardAmount = InventoryDrag.ClampDestroyCount(_discardAmount, count);
            bool stack = count > 1;
            _discardDialog = new Rect((Screen.width - 380f) / 2f, (Screen.height / 2f) - 70f, 380f, stack ? 150f : 110f);
            GUI.DrawTexture(_discardDialog, _darkSlotFill);
            GUI.Label(
                new Rect(_discardDialog.x + 14f, _discardDialog.y + 12f, 352f, 30f),
                "¿Destruir " + name + (stack ? " ×" + _discardAmount + " de " + count : string.Empty) + "? No se puede deshacer.",
                _darkTextStyle);

            float buttons = _discardDialog.y + 60f;
            if (stack)
            {
                float row = _discardDialog.y + 52f;
                if (LeftButton(new Rect(_discardDialog.x + 14f, row, 34f, 28f), "−", _darkButtonStyle))
                {
                    _discardAmount--;
                }

                _discardAmount = Mathf.RoundToInt(GUI.HorizontalSlider(
                    new Rect(_discardDialog.x + 58f, row + 8f, 190f, 16f), _discardAmount, 1f, count));

                if (LeftButton(new Rect(_discardDialog.x + 258f, row, 34f, 28f), "+", _darkButtonStyle))
                {
                    _discardAmount++;
                }

                if (LeftButton(new Rect(_discardDialog.x + 300f, row, 66f, 28f), "Todo", _darkButtonStyle))
                {
                    _discardAmount = count;
                }

                _discardAmount = InventoryDrag.ClampDestroyCount(_discardAmount, count);
                buttons = _discardDialog.y + 100f;
            }

            if (LeftButton(new Rect(_discardDialog.x + 70f, buttons, 110f, 34f), "Destruir", _darkButtonStyle) &&
                _transport != null)
            {
                byte[] bytes = MessageCodec.EncodeDestroyItem(new DestroyItemMessage((byte)_discardSlot, (ushort)_discardAmount));
                _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
                _discardSlot = -1;
            }

            if (LeftButton(new Rect(_discardDialog.x + 200f, buttons, 110f, 34f), "Cancelar", _darkButtonStyle))
            {
                _discardSlot = -1;
            }
        }

        /// <summary>Dibuja un hueco con su objeto. Devuelve si hay objeto.</summary>
        private bool DrawItemSlot(Rect slot, ushort id, ushort count, bool drawFrame = true)
        {
            if (drawFrame)
            {
                GUI.DrawTexture(slot, _darkSlot);
            }

            if (id == 0)
            {
                return false;
            }

            Rect inner = new Rect(slot.x + (slot.width * 0.16f), slot.y + (slot.height * 0.16f), slot.width * 0.68f, slot.height * 0.68f);
            Texture2D icon = ItemIcon(id);
            string name = _catalog.TryGetValue(id, out ItemCatalogEntry entry) ? entry.Name : "Objeto " + id;
            if (icon != null)
            {
                GUI.DrawTexture(inner, icon, ScaleMode.ScaleToFit);
            }
            else
            {
                // Sin arte todavía: las primeras letras del nombre.
                GUI.Label(inner, name.Substring(0, Mathf.Min(4, name.Length)), _darkSmallStyle);
            }

            if (count > 1)
            {
                GUI.Label(new Rect(slot.x, slot.yMax - 20f, slot.width - 6f, 18f), count.ToString(), _darkRightStyle);
            }

            return true;
        }

        /// <summary>Ficha del objeto (panel de tooltip): icono, nombre, tipo y sus números.</summary>
        private void DrawItemTooltip(ushort id, Vector2 mouse)
        {
            if (_dragging || !_catalog.TryGetValue(id, out ItemCatalogEntry item))
            {
                return;
            }

            const float scale = 0.55f;
            float width = TooltipWidth * scale;
            float height = TooltipHeight * scale;
            float x = mouse.x + 16f + width > Screen.width ? mouse.x - width - 16f : mouse.x + 16f;
            float y = Mathf.Min(mouse.y + 12f, Screen.height - height - 4f);
            var panel = new Rect(x, y, width, height);
            GUI.DrawTexture(panel, _darkTooltip);

            Texture2D icon = ItemIcon(id);
            if (icon != null)
            {
                GUI.DrawTexture(Part(panel, scale, 33f, 55f, 104f, 110f), _darkSlotFill);
                GUI.DrawTexture(Part(panel, scale, 45f, 67f, 80f, 86f), icon, ScaleMode.ScaleToFit);
            }

            string kind = item.Kind == ItemKind.Weapon ? "Arma – " + item.WeaponType
                : item.Kind == ItemKind.Armor ? EquipSlotNames[item.EquipSlot]
                : item.Kind == ItemKind.Potion ? "Poción" : "Material";
            GUI.Label(Part(panel, scale, 155f, 62f, 300f, 50f), item.Name, _darkTitleStyle);
            GUI.Label(Part(panel, scale, 155f, 112f, 300f, 40f), kind, _darkSmallStyle);

            var lines = new List<KeyValuePair<string, string>>();
            int unusableClassRow = -1;
            int levelRow = -1;
            if (item.Kind == ItemKind.Weapon && !item.UsableByClass)
            {
                unusableClassRow = lines.Count;
                lines.Add(new KeyValuePair<string, string>("Tu clase no la usa", string.Empty));
            }

            if (item.Kind == ItemKind.Weapon && item.TwoHanded)
            {
                lines.Add(new KeyValuePair<string, string>("A dos manos", string.Empty));
            }

            if ((item.Kind == ItemKind.Weapon || item.Kind == ItemKind.Armor) && item.RequiredLevel > 1)
            {
                levelRow = lines.Count;
                lines.Add(new KeyValuePair<string, string>("Nivel", item.RequiredLevel.ToString()));
            }

            if (item.Kind == ItemKind.Weapon)
            {
                lines.Add(new KeyValuePair<string, string>("Daño", item.Damage.ToString()));
                lines.Add(new KeyValuePair<string, string>("Alcance", item.Range.ToString("0.0")));
                lines.Add(new KeyValuePair<string, string>("Recarga", item.CooldownSeconds.ToString("0.0") + " s"));
            }
            else if (item.Kind == ItemKind.Potion)
            {
                lines.Add(new KeyValuePair<string, string>("Cura", item.Heal + " de vida"));
            }

            AddStatLine(lines, "Defensa", item.Defense);
            AddStatLine(lines, "Res. mágica", item.MagicResist);
            AddStatLine(lines, "Fuerza", item.Str);
            AddStatLine(lines, "Destreza", item.Dex);
            AddStatLine(lines, "Inteligencia", item.Int);
            AddStatLine(lines, "Vitalidad", item.Vit);
            if (item.MaxStack > 1)
            {
                lines.Add(new KeyValuePair<string, string>("Pila máxima", item.MaxStack.ToString()));
            }

            for (int i = 0; i < lines.Count && i < 11; i++)
            {
                Rect row = Part(panel, scale, 33f, 192f + (i * 32f), 410f, 30f);
                bool red = i == unusableClassRow || (i == levelRow && _level > 0 && _level < item.RequiredLevel);
                GUI.Label(row, lines[i].Key, red ? _darkRedStyle : _darkTextStyle);
                GUI.Label(row, lines[i].Value, red ? _darkRedRightStyle : _darkRightStyle);
            }
        }

        /// <summary>Evita enviar equipamientos que el catálogo y el estado local saben que serán rechazados.</summary>
        private bool CanEquipHere(int inventorySlot, out string reason)
        {
            reason = string.Empty;
            if (inventorySlot < 0 || inventorySlot >= _inventoryIds.Length ||
                !_catalog.TryGetValue(_inventoryIds[inventorySlot], out ItemCatalogEntry item) ||
                (item.Kind != ItemKind.Weapon && item.Kind != ItemKind.Armor))
            {
                return true;
            }

            if (item.Kind == ItemKind.Weapon && !item.UsableByClass)
            {
                reason = "Tu clase no usa: " + item.WeaponType;
                return false;
            }

            if (_level > 0 && _level < item.RequiredLevel)
            {
                reason = "Requiere nivel " + item.RequiredLevel;
                return false;
            }

            if (item.EquipSlot == (byte)EquipSlot.Offhand &&
                _catalog.TryGetValue(_equipment[(int)EquipSlot.Weapon], out ItemCatalogEntry equippedWeapon) &&
                equippedWeapon.TwoHanded)
            {
                reason = "Quita primero el arma a dos manos";
                return false;
            }

            if (item.Kind == ItemKind.Weapon && item.TwoHanded &&
                _equipment[(int)EquipSlot.Offhand] != 0 &&
                _equipment[(int)EquipSlot.Weapon] != 0)
            {
                bool hasEmptyInventorySlot = false;
                for (int i = 0; i < _inventoryIds.Length; i++)
                {
                    if (_inventoryIds[i] == 0)
                    {
                        hasEmptyInventorySlot = true;
                        break;
                    }
                }

                if (!hasEmptyInventorySlot)
                {
                    reason = "Libera un hueco para guardar la mano secundaria";
                    return false;
                }
            }

            return true;
        }

        private static void AddStatLine(List<KeyValuePair<string, string>> lines, string label, int value)
        {
            if (value != 0)
            {
                lines.Add(new KeyValuePair<string, string>(label, "+" + value));
            }
        }

        // En el orden de EquipSlot (I-14).
        private static readonly string[] EquipSlotNames =
        {
            "Arma", "Escudo / mano secundaria", "Casco", "Armadura", "Pantalones",
            "Guantes", "Botas", "Anillo", "Collar", "Accesorio",
        };

        // Huecos del panel de personaje (91×91 en la imagen original), en el orden de EquipSlot. El
        // arte trae nueve; el accesorio va debajo del arma, donde no hay dibujo.
        private static readonly Vector2[] EquipSlotPositions =
        {
            new Vector2(328f, 449f), new Vector2(328f, 340f), new Vector2(44f, 123f), new Vector2(44f, 231f),
            new Vector2(44f, 340f), new Vector2(44f, 449f), new Vector2(44f, 557f), new Vector2(328f, 231f),
            new Vector2(328f, 123f), new Vector2(328f, 557f),
        };

        /// <summary>
        /// Panel del personaje (C, I-09b): equipo a la izquierda, nivel, experiencia, vida y números a
        /// la derecha, y abajo el reparto de puntos. «+» aparta un punto; Confirmar lo manda y el
        /// servidor decide. Todos los números vienen del último Stats.
        /// </summary>
        private void DrawStats()
        {
            EnsureGuiArt();

            // Tamaño fijo; por defecto, centrado en la mitad izquierda de la pantalla.
            const float scale = 0.62f;
            float panelWidth = CharacterWidth * scale;
            _statsPanel = PlacePanel(
                1,
                ref _statsPosition,
                new Vector2(Mathf.Max(10f, ((Screen.width * 0.5f) - panelWidth) / 2f), 80f),
                new Vector2(panelWidth, CharacterHeight * scale),
                75f * scale);
            ClearCoveredDropTargets(_statsPanel);
            GUI.DrawTexture(_statsPanel, _darkCharacter);
            if (LeftButton(Part(_statsPanel, scale, 848f, 17f, 51f, 49f), GUIContent.none, GUIStyle.none))
            {
                _showStats = false;
            }

            Vector2 mouse = Event.current.mousePosition;

            // Huecos de equipo: el objeto equipado tapa la silueta dibujada.
            ushort hoveredEquipment = 0;
            for (int e = 0; e < _equipment.Length && e < EquipSlotPositions.Length; e++)
            {
                Rect equipSlot = Part(_statsPanel, scale, EquipSlotPositions[e].x, EquipSlotPositions[e].y, 91f, 91f);
                SlotInput(equipSlot, EquipSource + e, _equipment[e]);
                if (e == (int)EquipSlot.Accessory)
                {
                    GUI.DrawTexture(equipSlot, _darkSlot);
                }

                if (_equipment[e] != 0)
                {
                    GUI.DrawTexture(new Rect(equipSlot.x + 3f, equipSlot.y + 3f, equipSlot.width - 6f, equipSlot.height - 6f), _darkSlotFill);
                    DrawItemSlot(equipSlot, _equipment[e], 1, drawFrame: false);
                    if (equipSlot.Contains(mouse))
                    {
                        hoveredEquipment = _equipment[e];
                    }
                }
            }

            // Columna derecha: «NIVEL» y «EXP» vienen pintados; aquí van los números y el relleno.
            GUI.Label(Part(_statsPanel, scale, 565f, 104f, 120f, 30f), _stats.Level.ToString(), _darkTitleStyle);
            // Primera barra: vida (con su valor en la fila de NIVEL); segunda: experiencia (fila de EXP).
            int health = Mathf.RoundToInt(_stats.MaxHealth * _ownHealthPercent / 100f);
            GUI.Label(Part(_statsPanel, scale, 620f, 104f, 243f, 30f), "Vida " + health + " / " + _stats.MaxHealth, _darkRightStyle);
            FillBar(Part(_statsPanel, scale, 479f, 144f, 384f, 10f), _ownHealthPercent / 100f, new Color(0.8f, 0.15f, 0.15f, 1f));
            GUI.Label(
                Part(_statsPanel, scale, 560f, 168f, 303f, 26f),
                _experienceToNext == 0 ? "máx." : _experience + " / " + _experienceToNext,
                _darkRightStyle);
            float xpFill = _experienceToNext == 0 ? 1f : (float)_experience / _experienceToNext;
            FillBar(Part(_statsPanel, scale, 479f, 205f, 384f, 10f), xpFill, new Color(0.95f, 0.8f, 0.2f, 1f));

            int pending = _pendingPoints[0] + _pendingPoints[1] + _pendingPoints[2] + _pendingPoints[3];
            int free = _stats.FreePoints - pending;
            float y = 232f;
            const float line = 38f;
            StatLine(scale, y, 0, "Vida máxima", _stats.MaxHealth.ToString());
            StatLine(scale, y += line, 1, "Recurso", _stats.Resource + " / " + _stats.MaxResource);
            StatLine(scale, y += line, 2, "Daño", _stats.AttackDamage + (_stats.DamageType == 1 ? " mágico" : " físico"));
            StatLine(scale, y += line, 3, "Defensa", _stats.Defense.ToString());
            StatLine(scale, y += line, 8, "Res. mágica", _stats.MagicResist.ToString());
            StatLine(scale, y += line, 2, "Crít / Prisa",
                (_stats.CritChance / 100f).ToString("0.0") + " % / " + (_stats.Haste / 100f).ToString("0.0") + " %");

            string[] names = { "Fuerza", "Destreza", "Inteligencia", "Vitalidad" };
            ushort[] totals = { _stats.Str, _stats.Dex, _stats.Int, _stats.Vit };
            y += line + 8f;
            for (int i = 0; i < 4; i++, y += line)
            {
                Rect row = Part(_statsPanel, scale, 479f, y, 330f, 34f);
                GUI.Label(row, names[i], _darkTextStyle);
                GUI.Label(row, (totals[i] + _pendingPoints[i]) + (_pendingPoints[i] > 0 ? "  (+" + _pendingPoints[i] + ")" : string.Empty), _darkRightStyle);

                GUI.enabled = free > 0 && !_ownDead;
                if (LeftButton(Part(_statsPanel, scale, 825f, y + 2f, 38f, 30f), "+", _darkButtonStyle))
                {
                    _pendingPoints[i]++;
                }

                GUI.enabled = true;
            }

            // Recuadro inferior (x 31–886, y 673–773): puntos libres y botones.
            GUI.Label(Part(_statsPanel, scale, 45f, 686f, 270f, 34f), "Clase " + _stats.ClassId + " · Libres: " + free, _darkTextStyle);
            bool anyAllocated = _stats.AllocStr + _stats.AllocDex + _stats.AllocInt + _stats.AllocVit > 0;
            GUI.enabled = pending > 0 && !_ownDead;
            if (LeftButton(Part(_statsPanel, scale, 330f, 682f, 170f, 40f), "Confirmar", _darkButtonStyle))
            {
                SendAllocatePoints();
            }

            if (LeftButton(Part(_statsPanel, scale, 510f, 682f, 160f, 40f), "Cancelar", _darkButtonStyle))
            {
                System.Array.Clear(_pendingPoints, 0, _pendingPoints.Length);
            }

            GUI.enabled = anyAllocated && pending == 0 && !_ownDead;
            if (LeftButton(Part(_statsPanel, scale, 680f, 682f, 195f, 40f), _stats.FreeReset != 0 ? "Reajustar" : "Reajustar (piedra)", _darkButtonStyle))
            {
                byte[] bytes = MessageCodec.EncodeResetPoints(new ResetPointsMessage());
                _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
            }

            GUI.enabled = true;
            GUI.Label(
                Part(_statsPanel, scale, 45f, 728f, 830f, 34f),
                _stats.FreeReset != 0 ? "Reajuste gratis hasta el nivel 10" : "Reajustar cuesta una piedra de reajuste",
                _darkSmallStyle);

            if (hoveredEquipment != 0)
            {
                DrawItemTooltip(hoveredEquipment, mouse);
            }
        }

        private void StatLine(float scale, float y, int icon, string label, string value)
        {
            GUI.DrawTexture(Part(_statsPanel, scale, 479f, y + 2f, 30f, 30f), _statIcons[icon], ScaleMode.ScaleToFit);
            Rect row = Part(_statsPanel, scale, 520f, y, 343f, 34f);
            GUI.Label(row, label, _darkTextStyle);
            GUI.Label(row, value, _darkRightStyle);
        }

        private static void FillBar(Rect bar, float fill, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(fill), bar.height), Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private void SendAllocatePoints()
        {
            byte[] bytes = MessageCodec.EncodeAllocatePoints(new AllocatePointsMessage(
                (byte)_pendingPoints[0],
                (byte)_pendingPoints[1],
                (byte)_pendingPoints[2],
                (byte)_pendingPoints[3]));
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);

            // Si el servidor lo acepta llega un Stats con los números nuevos; si lo rechaza, al limpiar
            // aquí el jugador vuelve a ver sus puntos libres reales.
            System.Array.Clear(_pendingPoints, 0, _pendingPoints.Length);
        }

        /// <summary>
        /// Carga una vez el arte de Resources/GUI: el marco de la barra de vida (Tiny Swords, dibujado en
        /// 9 partes) y las piezas oscuras de Resources/GUI/Dark.
        /// </summary>
        private void EnsureGuiArt()
        {
            if (_barStyle != null)
            {
                return;
            }

            _barStyle = new GUIStyle { border = new RectOffset(24, 24, 0, 0) };
            _barStyle.normal.background = Resources.Load<Texture2D>("GUI/Bar");

            _darkWindow = Resources.Load<Texture2D>("GUI/Dark/Window");
            _darkSlot = Resources.Load<Texture2D>("GUI/Dark/Slot");
            _darkHotbar = Resources.Load<Texture2D>("GUI/Dark/Hotbar");
            _darkCharacter = Resources.Load<Texture2D>("GUI/Dark/Character");
            _darkTooltip = Resources.Load<Texture2D>("GUI/Dark/Tooltip");
            for (int i = 0; i < _statIcons.Length; i++)
            {
                _statIcons[i] = Resources.Load<Texture2D>("GUI/Dark/StatIcon_" + i);
            }

            // Fondo liso del color de los huecos, para tapar las siluetas pintadas del panel.
            _darkSlotFill = new Texture2D(1, 1);
            _darkSlotFill.SetPixel(0, 0, new Color(0.09f, 0.11f, 0.13f, 1f));
            _darkSlotFill.Apply();

            var light = new Color(0.86f, 0.88f, 0.9f);
            _darkTextStyle = new GUIStyle(_overlayStyle) { fontSize = 14, wordWrap = false, alignment = TextAnchor.MiddleLeft };
            _darkTextStyle.normal.textColor = light;
            _darkRightStyle = new GUIStyle(_darkTextStyle) { alignment = TextAnchor.MiddleRight };
            _darkRedStyle = new GUIStyle(_darkTextStyle);
            _darkRedStyle.normal.textColor = Color.red;
            _darkRedRightStyle = new GUIStyle(_darkRightStyle);
            _darkRedRightStyle.normal.textColor = Color.red;
            _darkSmallStyle = new GUIStyle(_darkTextStyle) { fontSize = 11, wordWrap = true };
            _darkSmallStyle.normal.textColor = new Color(0.68f, 0.71f, 0.75f);
            _darkTitleStyle = new GUIStyle(_darkTextStyle) { fontSize = 16, fontStyle = FontStyle.Bold };
            // Botón plano con borde de 1 px: el hueco de 121 px no cabe en 9 partes en un botón de 20 px.
            _darkButtonStyle = new GUIStyle
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(1, 1, 1, 1),
                padding = new RectOffset(2, 2, 0, 0),
            };
            _darkButtonStyle.normal.background = FlatButton(new Color(0.15f, 0.18f, 0.22f), new Color(0.55f, 0.58f, 0.63f));
            _darkButtonStyle.hover.background = FlatButton(new Color(0.22f, 0.26f, 0.31f), new Color(0.8f, 0.82f, 0.86f));
            _darkButtonStyle.active.background = FlatButton(new Color(0.1f, 0.12f, 0.15f), new Color(0.95f, 0.8f, 0.2f));
            _darkButtonStyle.normal.textColor = light;
            _darkButtonStyle.hover.textColor = Color.white;
            _darkButtonStyle.active.textColor = new Color(0.95f, 0.8f, 0.2f);
        }

        private static Texture2D FlatButton(Color fill, Color edge)
        {
            var texture = new Texture2D(3, 3) { filterMode = FilterMode.Point };
            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 3; x++)
                {
                    texture.SetPixel(x, y, x == 1 && y == 1 ? fill : edge);
                }
            }

            texture.Apply();
            return texture;
        }

        private Texture2D ItemIcon(ushort itemId)
        {
            if (itemId == 0)
            {
                return null;
            }

            if (!_itemIcons.TryGetValue(itemId, out Texture2D icon))
            {
                icon = Resources.Load<Texture2D>("GUI/Items/" + itemId);
                _itemIcons[itemId] = icon;
            }

            return icon;
        }

        /// <summary>Barra de vida propia abajo al centro y aviso de muerte con cuenta atrás (I-03b).</summary>
        private void DrawOwnHealth()
        {
            const float width = 300f;
            const float height = 16f;
            // Encima de la barra rápida (unos 64 px de alto abajo del todo).
            var back = new Rect((Screen.width - width) / 2f, Screen.height - 112f, width, height);

            // Marco de madera tallada alrededor de las dos barras.
            EnsureGuiArt();
            GUI.Box(new Rect(back.x - 16f, back.y - 12f, width + 32f, height + 33f), GUIContent.none, _barStyle);

            Color previous = GUI.color;
            GUI.color = new Color(0.1f, 0.1f, 0.1f, 0.85f);
            GUI.DrawTexture(back, Texture2D.whiteTexture);
            GUI.color = new Color(0.8f, 0.15f, 0.15f, 1f);
            GUI.DrawTexture(new Rect(back.x, back.y, width * _ownHealthPercent / 100f, height), Texture2D.whiteTexture);
            // Barra de experiencia (I-06) justo debajo; llena en el nivel máximo.
            var xp = new Rect(back.x, back.y + height + 3f, width, 6f);
            GUI.color = new Color(0.1f, 0.1f, 0.1f, 0.85f);
            GUI.DrawTexture(xp, Texture2D.whiteTexture);
            GUI.color = new Color(0.95f, 0.8f, 0.2f, 1f);
            float xpFill = _experienceToNext == 0 ? 1f : (float)_experience / _experienceToNext;
            GUI.DrawTexture(new Rect(xp.x, xp.y, width * xpFill, xp.height), Texture2D.whiteTexture);
            if (ResourceMax > 0)
            {
                var res = new Rect(back.x, xp.y + xp.height + 3f, width, 6f);
                GUI.color = new Color(0.1f, 0.1f, 0.1f, 0.85f);
                GUI.DrawTexture(res, Texture2D.whiteTexture);
                GUI.color = new Color(0.25f, 0.55f, 1f, 1f);
                GUI.DrawTexture(new Rect(res.x, res.y, width * ResourceCurrent / (float)ResourceMax, res.height), Texture2D.whiteTexture);
            }
            GUI.color = previous;

            // El snapshot solo trae el porcentaje; la vida absoluta es aproximada.
            string health = _maxHealth > 0
                ? "Vida " + Mathf.RoundToInt(_maxHealth * _ownHealthPercent / 100f) + "/" + _maxHealth
                : "Vida " + _ownHealthPercent + " %";
            string level = _level > 0
                ? "Nv " + _level + " · " + (_experienceToNext == 0 ? "máx." : _experience + "/" + _experienceToNext + " XP") + " · "
                : string.Empty;
            string resource = _skillCatalogReceived && ResourceMax > 0
                ? " · " + _skillCatalog.ResourceName + " " + ResourceCurrent + "/" + ResourceMax : string.Empty;
            GUI.Label(new Rect(back.x + 6f, back.y - 1f, width, height + 2f), level + health + resource, _overlayStyle);

            DrawHotbar();

            if (_showStats && _statsReceived)
            {
                DrawStats();
            }

            if (_showInventory)
            {
                DrawInventory();
            }

            if (_showSkills && _skillCatalogReceived)
            {
                DrawSkills();
            }

            HandleDrag();
            HandleShortcutDrag();
            if (_discardSlot >= 0)
            {
                DrawDiscardDialog();
            }

            if (_ownDead)
            {
                float left = Mathf.Max(0f, RespawnSeconds - (Time.unscaledTime - _ownDeathAt));
                var box = new Rect((Screen.width - 320f) / 2f, Screen.height / 2f - 30f, 320f, 60f);
                GUI.Box(box, GUIContent.none);
                GUI.Label(new Rect(box.x + 16f, box.y + 10f, 300f, 44f),
                    "Has muerto. Reapareces en " + Mathf.CeilToInt(left) + " s.", _overlayStyle);
            }
        }

        private void DrawOwnStatuses()
        {
            int visibleCount = 0;
            for (int i = 0; i < _ownStatusCount; i++)
            {
                float left = _ownStatuses[i].RemainingTicks / (float)SimulationConstants.TickRate -
                    (Time.unscaledTime - _ownStatusReceivedAt);
                if (left > 0f) visibleCount++;
            }

            if (visibleCount == 0) return;
            const float width = 120f;
            const float height = 20f;
            const float gap = 4f;
            float x = (Screen.width - visibleCount * width - (visibleCount - 1) * gap) / 2f;
            float y = Screen.height - 146f;
            for (int i = 0; i < _ownStatusCount; i++)
            {
                StatusEffectEntry entry = _ownStatuses[i];
                float left = Mathf.Max(0f, entry.RemainingTicks / (float)SimulationConstants.TickRate -
                    (Time.unscaledTime - _ownStatusReceivedAt));
                if (left <= 0f) continue;

                string text;
                Color color;
                switch (entry.Kind)
                {
                    case StatusKind.Bonus:
                        text = "Bonus +" + entry.Amount + " · " + left.ToString("0") + " s";
                        color = new Color(1f, 0.85f, 0.25f);
                        break;
                    case StatusKind.Shield:
                        text = "Escudo " + entry.Amount + " · " + left.ToString("0") + " s";
                        color = new Color(0.45f, 0.85f, 1f);
                        break;
                    case StatusKind.Slow:
                        text = "Lento " + entry.Amount + " % · " + left.ToString("0") + " s";
                        color = new Color(0.45f, 0.65f, 1f);
                        break;
                    case StatusKind.Taunt:
                        text = "Provocado · " + left.ToString("0") + " s";
                        color = new Color(1f, 0.4f, 0.4f);
                        break;
                    default:
                        continue;
                }

                var box = new Rect(x, y, width, height);
                Color previousColor = GUI.color;
                GUI.color = new Color(0.08f, 0.08f, 0.1f, 0.88f);
                GUI.DrawTexture(box, Texture2D.whiteTexture);
                GUI.color = color;
                GUI.Label(new Rect(box.x + 3f, box.y, box.width - 6f, box.height), text, _damageNumberStyle);
                GUI.color = previousColor;
                x += width + gap;
            }
        }
    }
}
