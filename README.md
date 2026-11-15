# Avelora 3D

MMO de rol con servidor autoritativo en C# y cliente Unity **3D**. Nace de [Avelora](https://github.com/SnyderJGZ/Avelora) (2D), que sigue su propio camino.

Qué se trajo y por qué: `docs/diseno/reutilizar-3d.md`.

## Qué hay

- `server/`: servidor completo (tick fijo, combate, IA de PNJ, zonas, PvP, objetos, habilidades, niveles, SQLite, login con invitación). Protocolo v26.
- `shared/`: protocolo, códec, movimiento, colisión, predicción y transporte LiteNetLib, compartidos por cliente y servidor.
- `tests/`: 1111 pruebas.
- `maps/`: mapa Ciudad (`.avmap`): rejilla de colisión y zonas.
- `tools/`: simulador de equilibrio, bots, pruebas de carga, generador de iconos y flujo de agentes.
- `client/Assets/Avelora/`: código del cliente 2D como **punto de partida**: red, predicción, interfaz IMGUI y entrada. Falta la vista 3D; ya no están los sprites, los tilesets ni la escena.

El servidor simula en un plano: la posición `(x, y)` se dibuja como `Vector3(x, 0, y)`.

## Primeros pasos

```bash
dotnet build Avelora.slnx -c Release
dotnet test Avelora.slnx -c Release
dotnet run --project server/Avelora.Server -c Release
```

## Pendiente para el cliente 3D

1. Proyecto Unity 3D nuevo que use `client/Assets/Avelora` y `shared/` (con `tools/sync-shared-to-unity.ps1`).
2. Cargar la colisión desde el `.avmap` en vez de un Tilemap (`AveloraMap.cs`, `TilemapCollision.cs`).
3. Vista 3D de jugadores, PNJ y objetos (sustituye a los sprites de `RemotePlayerView.cs` y `NetworkClient.cs`).
4. Personaje modular con equipo visible y un mensaje de protocolo con el aspecto de cada jugador.
5. Escena del mapa Ciudad con modelos, colocados desde `Editor/MapaCiudadLayout.cs`.
