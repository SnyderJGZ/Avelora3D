# I-12 (cierre): documentación

Rama `wip/i-12`: quédate en ella. El código de I-12 está terminado (693 pruebas, 0 advertencias); **no toques código**.
Solo documentación. Sin commits. Resultados de la última carga en `tools/carga/resultados/20261029-231725-fight-50-redund4-corr/` (léelos y usa sus cifras).
Tarea original: `docs/agentes/tareas/I-12.md`.

# Cierre de I-12 (fix 2, para Codex)

Repo `C:\Users\SnyderJGZ\Projects\Avelora`, cambios sin commit (no revertir, no commit/push, no tocar `client/`).
Código y pruebas ya están (build 0 advertencias, 693/693). Falta solo documentación:

1. `README.md` línea ~46: cambia `685/685` por `693/693`; y línea ~1723 (`dotnet test ... 685/685`) por `693/693`.
2. Carga: espera a que termine la que lanzó Codex (carpeta nueva en `tools/carga/resultados/` con fecha de hoy, p. ej. `20261029-231725-fight-50-redund4-corr`); no lances otra. Lee sus informes y copia las cifras.
3. Reescribe `docs/agentes/informes/I-12.md` entero (hoy tiene 11 «Pendiente»): una sección por punto de la tarea (1 a 9), «Archivos cambiados» (di que `PlayerEntity.CharacterName` es campo NUEVO, usado para el aviso de carga), «Dudas y riesgos». Hechos reales:
   - datos añadidos, protocolo v16, reglas, carga, bots, README (resume `git status`);
   - el catálogo se manda por jugador al entrar con personaje; 18 objetos, 1 trozo de 983 B, margen 417 B sobre 1400 B;
   - `ResolveEquip` con clase no encontrada cuenta `RejectedEquipClass` (decisión); `EnforceEquipment` con clase desconocida mueve el objeto al inventario (prueba `EnforceEquipment_Clase_Desconocida_...`);
   - pruebas: archivos tocados y total 693; build/test: cifras reales;
   - carga: bots 50/50, tick p99, bytes enviados, muertes PNJ y jugador, armas/armaduras equipadas, `equip_omitidos_req`, `equip_rech_clase/nivel/dosmanos`, `equipo_corregido_carga`, `equipo_aviso_carga`; si salen líneas `[corrección]` analízalas; si `equip_rech_clase/nivel` > 0 con `fight`, es fallo del bot;
   - riesgos: `equip_omitidos_req` cuenta el mismo candidato en cada llamada; el bot no reintenta tras un rechazo del servidor.
4. Borra `tools/carga/carga-i12-temporal.ps1` si ya no se usa (es un archivo temporal sin seguimiento) o justifica en el informe por qué queda.
