# Avelora: reglas para los agentes

Avelora es un MMORPG 2D con servidor autoritativo en C# (.NET 10) y cliente en Unity 6.
**No leas `README.md` entero** (casi 1900 líneas): busca con `grep`/`rg` solo la sección que nombra la tarea.

## Ahorro de tokens

- Lee solo los archivos que nombra la tarea y lo que encuentres buscando; no explores el repo entero.
- Para ubicar código, antes de buscar o leer usa el grafo (`graphify-out/`, se actualiza en cada commit):
  `graphify explain "NombreDeClase"` (archivo, línea y relaciones), `graphify affected "Nombre"` (qué
  depende de ello) o `graphify query "pregunta"`. Lee después solo las líneas que te indique.
- Mientras iteras, ejecuta solo las pruebas afectadas (`dotnet test Avelora.slnx --filter <Clase>`); la
  suite completa, una vez al final.
- Filtra las salidas largas: `dotnet test ... | Select-String 'Superado|Con error|error'`,
  `dotnet build ... -v q`, y de la sincronización con Unity anota solo cuántos archivos copió.
- La carga (`tools/carga/carga.ps1`) solo si la tarea la pide, y una sola vez.
- El informe es corto: lo que cambió, cifras de build/test/carga y riesgos. Sin repetir la tarea.

## Cómo se trabaja

- **Claude es el cerebro:** escribe cada tarea en `docs/agentes/tareas/<ID>.md` y revisa el resultado.
  Tú ejecutas **solo** lo que pide la tarea. Si algo no está claro o la tarea parece equivocada,
  no improvises: anótalo en el informe y detente.
- Al terminar, escribe el informe en `docs/agentes/informes/<ID>.md` con:
  1. Qué cambiaste (archivo por archivo, una línea cada uno).
  2. Salida resumida de `dotnet build Avelora.slnx -v q --nologo` y `dotnet test Avelora.slnx`
     (número de pruebas en verde/rojo). Si no pudiste ejecutarlas, dilo.
  3. Lo que no hiciste, dudas y riesgos.
- No hagas `git commit`, `git push` ni reescrituras de historial: Claude revisa y confirma.

## Reglas del código

- Respeta la arquitectura del plan (`docs/avelora-plan-servidor.md`): el servidor decide posición,
  daño y loot; el cliente solo envía intenciones.
- `shared/Avelora.Shared` es **netstandard2.1, C# 9, sin dependencias** (ni Unity ni NuGet).
  Nada de `using UnityEngine` ahí.
- Un solo hilo de juego modifica el mundo; la red solo encola. No añadas bloqueos en el tick.
- En el bucle de ticks no crees objetos por pasada: reutiliza buffers y listas, sin LINQ.
- Si cambias el formato binario de un mensaje, sube `ProtocolVersion` y actualiza sus pruebas.
- Toda lógica nueva lleva pruebas xUnit en `tests/Avelora.Tests`. La tarea no está hecha si
  `dotnet test` no queda en verde.
- Cambios mínimos: no refactorices ni añadas opciones que la tarea no pida.
- Código, comentarios e informes en español, igual que el código existente.

## Cliente de Unity

- La fuente de verdad es `client/` y `shared/` de este repositorio, no la carpeta de Unity.
- Proyecto de Unity: `E:\Unity\Nueva carpeta\My project (1)`. Para copiar cambios:
  `.\tools\sync-shared-to-unity.ps1 -ProyectoUnity 'E:\Unity\Nueva carpeta\My project (1)'`
- Nunca borres ni regeneres archivos `.meta`.
