# Informe I-12-cierre

## Cambios

- `README.md`: actualiza a 693/693 y corrige los resultados de carga I-12 con las cifras de la ejecución indicada.
- `docs/agentes/informes/I-12.md`: sustituye el borrador pendiente por el informe completo de los puntos 1–9, archivos, resultados y riesgos.
- `docs/agentes/informes/I-12-cierre.md`: registra este cierre, la verificación y lo que quedó fuera de alcance.

## Verificación

- Rama: `wip/i-12`; no se crearon commits.
- Estado final: `README.md` y `docs/agentes/informes/I-12.md` modificados; `docs/agentes/informes/I-12-cierre.md` nuevo; `docs/agentes/tareas/I-12-cierre.md` sigue sin seguimiento.
- `dotnet build Avelora.slnx -v q --nologo`: correcto, **0 errores y 0 advertencias**.
- `dotnet test Avelora.slnx`: **693 correctas, 0 fallidas, 0 omitidas**.
- Carga leída de `tools/carga/resultados/20261029-231725-fight-50-redund4-corr/`: 50/50 bots, 3201 ticks, p99 0,069 ms, 27 082 379 bytes enviados; los detalles están en README e informe I-12.

## No realizado, dudas y riesgos

- No se tocó código, `client/` ni Unity; no se lanzó otra carga.
- `tools/carga/carga-i12-temporal.ps1` no está presente, por lo que no fue necesario borrarlo.
- La carga registró 0 rechazos de clase/nivel/dos manos y 0 correcciones/saltos; los resultados de combate son de una ejecución aleatoria.
- Riesgos documentados para I-12: `equip_omitidos_req` puede contar repetidamente el mismo candidato y el bot no reintenta tras un rechazo del servidor.
