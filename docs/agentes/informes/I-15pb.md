# Informe I-15pb

Estado: implementación y verificaciones completadas; sin commit.

## Cambios por archivo

- `client/Assets/Avelora/Scripts/Game/NetworkClient.cs`: recibe el estado/rechazo PvP, pide cambios fiables, permite seleccionar jugadores marcados con prioridad para PNJ y descarta objetivos que pierden la marca.
- `client/Assets/Avelora/Scripts/Game/NetworkClient.Gui.cs`: dibuja estado, cuenta atrás, botón y marcas PvP sobre jugadores remotos.
- `client/Assets/Avelora/Scripts/Game/RemotePlayerView.cs`: añade la marca PvP efectiva recibida en el snapshot.

## Build y pruebas

- `dotnet build Avelora.slnx -v q --nologo`: correcto, 0 advertencias y 0 errores.
- `dotnet test Avelora.slnx`: 803 superadas, 0 con error, 0 omitidas.
- Sincronización Unity ejecutada con el script requerido: 372 archivos copiados.
- Unity MCP Console: 0 entradas de error. No se compiló Unity; queda para el supervisor.

## Límites y riesgos

- No se ejecutó la comprobación manual en Unity. El supervisor debe comprobar los escenarios de ciudad/espera, cambio PvE↔PvP por P/botón, forzado y temporizador al salir de arena, marcas/selección/ataque y reconexión indicados en la tarea.
- El jugador local no tiene vista en `_remoteViews`; su estado lo indica el HUD PvP propio.
- No se modificó `shared/` ni ningún `.meta`. El modo sin red sigue separado: las peticiones PvP requieren transporte y estado recibido.
