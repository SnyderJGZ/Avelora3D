# Qué sirve de Avelora para un proyecto en 3D

Fecha: 2026-11-15. Estado de partida: `main` en 2c6d362, protocolo v26, 1111 pruebas.

Avelora sigue en 2D. Esta nota separa lo que se puede llevar tal cual a un cliente 3D, lo que hay que adaptar y lo que no sirve.

## Idea clave

El servidor simula el mundo en un plano (x, y). Un cliente 3D solo tiene que dibujar ese plano como suelo: la posición del servidor (x, y) pasa a ser `Vector3(x, 0, y)`. Por eso todo el servidor y el código compartido valen sin cambios.

Límite: la altura sería solo visual. Puentes, pisos o saltos con efecto en el juego exigen cambiar la simulación del servidor.

**Recomendación:** no hacer un repo nuevo. Basta con añadir un segundo cliente Unity (`client3d/`) en este mismo repo. Usaría `shared/` y se conectaría al mismo servidor, así que el 2D y el 3D pueden convivir y probarse con las mismas cuentas.

## 1. Se lleva tal cual

| Parte | Ruta | Qué aporta |
|---|---|---|
| Servidor completo | `server/Avelora.Server/` | Tick fijo, combate, IA de PNJ, interés por zonas, objetos, habilidades, niveles, PvP, persistencia SQLite, login con invitación, límites por IP |
| Datos de juego | `server/Avelora.Server/*.json` | Clases, objetos, PNJ, habilidades, niveles, zonas y equilibrio ya ajustado |
| Protocolo y códec | `shared/Avelora.Shared/Protocol/` | Mensajes v26, `HelloSecrets`, `RejectReason` |
| Movimiento y colisión | `shared/Avelora.Shared/Core/` | `MovementSimulator`, `MapCollisionGrid`, `Vec2` (idénticos en cliente y servidor) |
| Predicción | `shared/Avelora.Shared/Prediction/` | `ClientPrediction`, `SnapshotTimeline` (predicción, reconciliación e interpolación) |
| Transporte | `shared/Avelora.Net/` | LiteNetLib en cliente y servidor |
| Formato de mapa | `shared/Avelora.Shared/Maps/MapFile.cs`, `maps/*.avmap` | Rejilla de colisión y zonas; el mapa Ciudad sirve como suelo lógico |
| Inventario | `shared/Avelora.Shared/Inventory/InventoryDrag.cs` | Arrastrar y soltar validado |
| Pruebas | `tests/Avelora.Tests/` | Las 1111 siguen valiendo porque no prueban el dibujado |
| Herramientas | `tools/Avelora.Balance`, `tools/Avelora.Bots`, `tools/carga/` | Simulador de equilibrio, bots y pruebas de carga |
| Flujo de agentes | `tools/agente.ps1`, `AGENTS.md`, `docs/agentes/` | Reparto de tareas, OpenCode y revisión con Codex |
| Diseño | `docs/diseno/plan-combate-equilibrio.md`, `clases-estadisticas-profesiones.md` | Fórmulas y objetivos de equilibrio |
| Iconos | `tools/arte/iconos.py`, `Resources/GUI/Items`, `GUI/Skills` | La interfaz 3D sigue usando iconos 2D |

## 2. Se lleva adaptándolo

| Parte | Ruta | Qué cambia |
|---|---|---|
| Red del cliente | `client/.../NetworkClient.cs` (2643 líneas) | La conexión, el login, la selección de personaje, los snapshots, la predicción, la entrada y el chat valen. Hay que sacar a una vista aparte lo que crea sprites (unas 18 referencias a `Sprite`/`SpriteRenderer`) y convertir `(x, y)` a `(x, 0, y)` |
| Interfaz | `client/.../NetworkClient.Gui.cs` | La interfaz IMGUI (acceso, inventario, habilidades, barra rápida, chat, ayudas, números de daño) vale casi entera. Los números de daño y las barras sobre la cabeza deben proyectar con `Camera.WorldToScreenPoint`, que ya es lo que hacen |
| Entrada | `LocalPlayerController.cs`, `KeyboardDirection.cs` | Las 8 direcciones valen. Con cámara girable, la dirección hay que rotarla según la cámara |
| Vista de entidades | `RemotePlayerView.cs` | La interpolación y la selección valen; el dibujo pasa de hojas de sprites a modelo + `Animator` |
| Aspecto por clase/PNJ | `ClassLook.cs`, `NpcLook.cs` | Misma idea (tipo → apariencia), con prefabs 3D en vez de sprites |
| Minimapa | `NetworkClient.Gui.cs` | Se dibuja desde la rejilla de colisión: vale sin cambios |
| Trazado del mapa | `Editor/MapaCiudadLayout.cs` | Ya dice dónde van murallas, casas, caminos y agua. Puede colocar modelos 3D en vez de tiles |

## 3. No sirve en 3D

- Sprites de clases, PNJ y objetos en el suelo (`Resources/Characters`, `Resources/Npcs`) y sus hojas animadas de PixelLab.
- Terreno de doble rejilla y tilesets: `Editor/MapaTerreno.cs`, `Editor/MapaV2Trocear.cs`, `tools/arte/tileset.py`, `Art/Terreno`, `Art/Mapa v2`.
- Pintado del mapa con tilemaps: la parte de `Editor/MapaCiudadGenerator.cs` que pinta (el trazado sí sirve, ver arriba).
- `TilemapCollision.cs` y `Editor/TilemapCollisionExporter.cs`: en 3D la colisión sale del `.avmap`.
- `tools/arte/pixellab_hojas.py` y `tools/arte/animar.py`.
- El panel de UI de PixelLab y `GUI/Dark/*` solo sirven si se quiere la misma estética.

## 4. Lo nuevo que pediría el 3D

1. Personaje modular con un esqueleto común: armas en el hueso de la mano y piezas de armadura intercambiables. Esto da el equipo visible tipo Mu Online.
2. Un mensaje de protocolo con el aspecto de los demás jugadores (arma, casco, cuerpo, guantes, botas), al aparecer y al cambiar el equipo. También le hace falta al 2D si se quiere equipo visible.
3. Cámara fija isométrica o en perspectiva, como en Mu Online.
4. Escena del mapa Ciudad con modelos (murallas, casas, árboles) colocados desde `MapaCiudadLayout`.

Arte posible: paquetes de personajes modulares con licencia libre (KayKit, Quaternius) y animaciones de Mixamo. Hay que comprobar las licencias antes de usarlos.
