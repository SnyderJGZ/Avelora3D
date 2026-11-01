# I-15pb: modo PvE/PvP, indicador, marca PvP y ataque a jugadores (cliente, protocolo v20)

**Base:** el servidor de I-15p (`docs/agentes/tareas/I-15p.md` «Reglas», «Decisiones tomadas», §1 y §7, y su informe
`docs/agentes/informes/I-15p.md`). Léelos antes: ahí están `PvpMode` (`Pve`, `Pvp`), `PvpForcedState` (`None`,
`InZone`, `Timer`), `PvpChangeState` (`Unavailable`, `Waiting`, `Ready`), `PvpModeRejectReason` (`NotInSafeZone`,
`TooSoon`), `SetPvpModeMessage`, `PvpStatusMessage` (`Mode`, `Forced`, `Change`, `ForcedSeconds`, `ChangeSeconds`),
`PvpModeRejectedMessage.Reason`, `EntityState.Pvp`, `MessageType.SetPvpMode`/`PvpStatus`/`PvpModeRejected` y
`MessageCodec.EncodeSetPvpMode`/`TryReadPvpStatus`/`TryReadPvpModeRejected` (comprueba los nombres exactos en `shared/`).

- **Solo** `client/Assets/Avelora/Scripts/Game/NetworkClient.cs`, `NetworkClient.Gui.cs` y `RemotePlayerView.cs`.
  Nada de `shared/` (lo hizo I-15p), ni archivos nuevos (así no hace falta ningún `.meta`), ni prefabs, ni la escena.
- **No borres ni regeneres `.meta`.**
- Al terminar ejecuta `.\tools\sync-shared-to-unity.ps1 -ProyectoUnity 'E:\Unity\Nueva carpeta\My project (1)'`
  y anota solo cuántos archivos copió. **No puedes compilar Unity**: lo hará el supervisor. Escribe C# 9
  compatible con Unity y comprueba a mano que cada símbolo existe.
- **Modo sin red** (`connectOnStart = false`, `LocalPlayerController`): debe seguir igual.
- Arte **de prueba**: `GUI` y `Texture2D.whiteTexture`; sin sprites, fuentes ni materiales nuevos.
- **El servidor decide:** el cliente no valida si el ataque a un jugador es legal (zonas, modos); solo evita
  seleccionar jugadores sin la marca PvP. Ninguna zona ni clase escrita en el código.
- **Informe:** `docs/agentes/informes/I-15pb.md`, escrito desde el principio.

Fuera de alcance, no lo hagas: sonidos, lista de jugadores, nombres sobre los jugadores, colores por bando, menú de
confirmación, tocar el servidor o los bots.

## 1. Estado (`NetworkClient.cs`)

Campos junto a `_zoneKind` (~179):

```csharp
private PvpStatusMessage _pvpStatus;        // default: Pve, None, Unavailable
private bool _pvpStatusReceived;
private float _pvpChangeReadyAt;            // Time.unscaledTime en que Waiting pasa a poder cambiar
private float _pvpForcedEndsAt;             // Time.unscaledTime en que acaba el forzado con temporizador
private Rect _pvpButtonRect;
```

En los dos sitios donde se pone `_zoneKind = ZoneKind.Normal;` (~367 al desconectar y ~1435 en `HandleWelcome`):
`_pvpStatus = default; _pvpStatusReceived = false;`.

## 2. Recibir (`NetworkClient.cs`, en el `switch` junto a `case MessageType.ZoneChanged:` ~1230)

```csharp
case MessageType.PvpStatus:
    if (_welcomeReceived && MessageCodec.TryReadPvpStatus(buffer, offset, length, out PvpStatusMessage pvpStatus))
    {
        _pvpStatus = pvpStatus;
        _pvpStatusReceived = true;
        _pvpChangeReadyAt = Time.unscaledTime + pvpStatus.ChangeSeconds;
        _pvpForcedEndsAt = Time.unscaledTime + pvpStatus.ForcedSeconds;
    }

    break;

case MessageType.PvpModeRejected:
    if (_welcomeReceived && MessageCodec.TryReadPvpModeRejected(buffer, offset, length, out PvpModeRejectedMessage rejected))
    {
        ShowHint(rejected.Reason == PvpModeRejectReason.NotInSafeZone
            ? "el modo solo se cambia en zona segura"
            : "aún no: espera en la zona segura");
    }

    break;
```

## 3. Marca sobre los jugadores (`RemotePlayerView.cs` y `NetworkClient.cs`)

- `RemotePlayerView` (junto a `Rank` ~110): `public bool Pvp { get; set; }` con comentario («PvP efectivo, del
  snapshot; solo jugadores»).
- `NetworkClient` (~1577, junto a `view.SetRank(state.Rank);`): `view.Pvp = state.Pvp;`.
- Dibujo: método nuevo `DrawPvpMarks()` en `NetworkClient.Gui.cs`, llamado justo después de `DrawPvpStatus()` (§5).
  `Camera cam = Camera.main;` si es nula sale. Por cada vista de `_remoteViews` con `Kind == Player`, `Pvp` y
  `!IsDead`: `Vector3 screen = cam.WorldToScreenPoint(view.transform.position + Vector3.up * 0.9f);` si
  `screen.z < 0f` la salta; etiqueta `"PvP"` en `new Rect(screen.x - 24f, Screen.height - screen.y - 12f, 48f, 18f)`
  con `_damageNumberStyle` y `GUI.color = new Color(1f, 0.4f, 0.35f)` (restaura el color al final). Copia la forma de
  los números de daño (`DrawCombatEffects` ~318). Sin reservas por fotograma (sin LINQ ni listas nuevas).
- Comprueba si el propio jugador tiene vista en `_remoteViews` (en ~1559 el estado con `state.Id == _entityId` se
  trata aparte). Si no la tiene, la marca propia la da el indicador de §5; dilo en el informe.

## 4. Atacar a jugadores con el clic actual (`HandleCombatInput`, `NetworkClient.cs` ~589)

- En el clic (~610): `int picked = PickEntityAt(world, EntityKind.Npc, PickRadius);` y, **si es 0**,
  `picked = PickPvpPlayerAt(world, PickRadius);`. Así un PNJ tiene prioridad si están solapados.
- Método nuevo `PickPvpPlayerAt(Vector3 world, float radius)` copiando `PickEntityAt` (~880): solo vistas con
  `Kind == EntityKind.Player`, `Pvp == true`, `!IsDead` y `pair.Key != _entityId`.
- `SelectedTarget()` (~905): además, si la vista es un jugador y `!view.Pvp`, devuelve `null` (perdió la marca: se
  deselecciona solo con el código de ~631). El resto (doble clic, espacio, alcance, línea de visión, `EncodeAttack`)
  no cambia.
- Cambia el texto `"sin objetivo: haz clic en un enemigo"` por `"sin objetivo: haz clic en un enemigo o jugador PvP"`.

## 5. Indicador y botón (`NetworkClient.Gui.cs`)

Método nuevo `DrawPvpStatus()`, llamado en `OnGUI` dentro de `if (_welcomeReceived)` justo después de
`DrawZoneLabel();` (~96). Si `!_pvpStatusReceived`, no dibuja nada (servidor sin zonas).

- **Indicador** en `new Rect((Screen.width - 240f) / 2f, Screen.height - 172f, 240f, 22f)` (encima del rótulo de
  zona) con `_damageNumberStyle`, guardando y restaurando `GUI.color`:
  - `Forced == InZone` → `"PvP forzado"`, rojo `new Color(1f, 0.4f, 0.35f)`.
  - `Forced == Timer` → `"PvP forzado " + Clock(_pvpForcedEndsAt)`, naranja `new Color(1f, 0.6f, 0.3f)`.
  - si no, `Mode == Pvp` → `"PvP"` rojo; `Pve` → `"PvE"` verde `new Color(0.45f, 0.9f, 0.5f)`.
  - `Clock(float endsAt)`: `int s = Mathf.Max(0, Mathf.CeilToInt(endsAt - Time.unscaledTime));` → `"m:ss"`
    (`(s / 60) + ":" + (s % 60).ToString("00")`).
- **Botón**, solo si `Change != Unavailable`: `_pvpButtonRect = new Rect((Screen.width - 240f) / 2f,
  Screen.height - 198f, 240f, 24f)`; si no, `_pvpButtonRect = Rect.zero`.
  - `Waiting` y `Time.unscaledTime < _pvpChangeReadyAt` → `GUI.enabled = false;` botón
    `"Cambio de modo en " + Clock(_pvpChangeReadyAt)`; restaura `GUI.enabled = true`.
  - `Ready`, o `Waiting` con la cuenta ya a 0 → botón `"Pasar a PvP (P)"` o `"Pasar a PvE (P)"` según `Mode`; al
    pulsarlo, `RequestPvpModeToggle()`. (Si la cuenta local llega a 0 antes que el `Ready` del servidor, el servidor
    puede rechazar con `TooSoon`; es correcto y sale la pista.)

`RequestPvpModeToggle()` en `NetworkClient.cs`: si `_transport == null || !_pvpStatusReceived` sale; envía
`MessageCodec.EncodeSetPvpMode(new SetPvpModeMessage(_pvpStatus.Mode == PvpMode.Pvp ? PvpMode.Pve : PvpMode.Pvp))`
con `DeliveryMode.ReliableOrdered`. Tecla **P**: en `HandleItemInput` (~686), junto a las demás teclas,
`if (keyboard.pKey.wasPressedThisFrame && _pvpStatus.Change != PvpChangeState.Unavailable) RequestPvpModeToggle();`
(P no está usada; compruébalo). Fuera de la zona segura P no hace nada.

En el cálculo de `overPanel` (~597-603) añade `|| _pvpButtonRect.Contains(gui)` para que pulsar el botón no
seleccione objetivo.

## 6. Comprobación manual (para el informe; no puedes ejecutar Unity)

Lista para el supervisor (con `server.json` a 300 s; para probar rápido, bajar `pvpModeChangeSeconds` y
`pvpForcedSeconds` en un `server.json` local): al entrar en la ciudad sale «PvE» y el botón con cuenta atrás; al
salir de la ciudad desaparece el botón y P no hace nada; tras la cuenta, P o el botón cambian a «PvP»; en la arena
sale «PvP forzado»; al salir, «PvP forzado m:ss» que baja también dentro de la ciudad y vuelve al modo elegido; los
jugadores PvP llevan la marca; clic en uno con PvP efectivo propio fuera de la ciudad le hace daño (número flotante);
clic en un jugador sin marca no lo selecciona; morir por un jugador no quita experiencia; reconectar conserva modo y
forzado; sin red no cambia nada.

## 7. Verificación

`& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build Avelora.slnx -v q --nologo` y
`... test Avelora.slnx | Select-String 'Superado|Con error'` (deben seguir igual que tras I-15p: tus cambios no
entran en la solución, pero confirma que nada se rompió). Luego la sincronización con Unity.
