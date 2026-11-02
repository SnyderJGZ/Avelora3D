# Plan: combate y equilibrio (daño, defensa, crítico, velocidades, clases y habilidades)

**Estado:** propuesta para después de I-18/I-18b. Base: commit `19a44f8` (I-17b), protocolo v22 (v23 con I-18),
esquema v8, 898 pruebas. No implementa nada. Manda sobre §2.3 y §7 (I-19) de
`docs/diseno/clases-estadisticas-profesiones.md` donde los contradiga; §0 de ese documento sigue mandando.

Regla de siempre: todos los números viven en datos (`classes.json`, `items.json`, `skills.json`, `npcs.json`,
`levels.json`, `ServerConfig`); el código no nombra clases, objetos ni habilidades concretas.

---

## 1. Diagnóstico (estado actual, con cifras)

**Fórmulas que hay** (`server/Avelora.Server/Progression/CharacterStats.cs`):

| Qué | Fórmula hoy | Dónde |
| --- | --- | --- |
| Vida máx. | `50 + VIT × healthPerVit` (5 en las cuatro clases) | `CharacterStats.Calculate` |
| Daño bruto | `dañoArma (o unarmed) + ⌊primario × 0,5⌋` (suma plana) | `CharacterStats.GetAttackDamage` |
| Mitigación | `máx(1, ⌊daño × 100 / (100 + def)⌋)`; físico → `defense`, mágico → `magicResist` (I-14/I-23) | `CharacterStats.MitigateDamage`, `CombatSystem.ApplyDamage` |
| Daño de PNJ | `attackDamage` mitigado **solo por `defense`** (no hay tipo de daño en PNJ) | `NpcSystem` ~574 |
| Habilidad / cura / escudo (I-18) | `GetAttackDamage` con `skill.damage` como base: misma suma plana | `CombatSystem.ResolveSkill`, `ResolveHeal` |
| Velocidad de ataque | `cooldownSeconds` del arma, fija; ningún atributo ni objeto la cambia | `CombatSystem.Resolve` ~250 |
| Velocidad de movimiento | `PlayerSpeed` 4,0 casillas/s para todos; orcos 2,0 y 1,6; I-18 añade ralentización | `ServerConfig`, `npcs.json` |

**Qué falta:** crítico, velocidad de ataque variable, vida por nivel, defensa por clase/nivel, tipo de daño en
PNJ, escalado de habilidades con el equipo, modificador PvP, esquiva (se propone no hacerla, §6) y una
herramienta que mida todo esto.

**Qué está mal o desequilibrado** (build «todo al primario», arma propia de la clase):

| Nivel | Guerrero DPS / PV | Mago DPS / PV | Ranger DPS / PV | Sanador DPS / PV |
| --- | --- | --- | --- | --- |
| 1 | 21,2 / 100 | 15,0 / 80 | 16,0 / 90 | 15,0 / 90 (sin maza) |
| 5 | 28,8 / 100 | 21,0 / 80 | 22,0 / 90 | **27,0** / 90 (maza) |
| 10 | 38,8 / 100 | 29,0 / 80 | 30,0 / 90 | 34,0 / 90 |
| 25 | 66,2 / 100 | 51,0 / 80 | 52,0 / 90 | 57,0 / 90 |

1. **El guerrero hace un 30-40 % más de DPS que el mago** y además tiene más vida; el sanador supera al mago.
   El mago, que debería ser el que más daño hace, es el último.
2. **El arma deja de importar:** a nivel 25 el atributo aporta +39..41 y el arma 10..16. El bastón (10) hace
   **lo mismo que pegar sin arma** (10) y además exige nivel 3.
3. **Las habilidades se diluyen:** Golpe poderoso es 1,47× el golpe con espada a nivel 1 y solo 1,15× a nivel 25,
   porque base de habilidad y arma reciben el mismo `+⌊P × 0,5⌋`.
4. **Sin vida por nivel:** con 0 en VIT el guerrero tiene 100 PV a nivel 25. «Todo al primario» es un cañón
   de cristal sin coste (no hay tope por atributo).
5. **La defensa casi no existe:** el mejor equipo actual suma `defense` 15 (−13 %) y `magicResist` 5 (−5 %);
   PNJ con 0/0. Los PNJ pegan físico siempre, así que `magicResist` no sirve en PvE.
6. **Los mobs son triviales:** el orco (nivel 1, 30 PV, 8 de daño cada 1,5 s) muere en 2 golpes del guerrero
   (0,8 s) y le quita el 8 % de la vida; el objetivo de §0.11 es ~5 s y ~40 %. Los orcos (2,0 y 1,6 casillas/s)
   no alcanzan a nadie (4,0): el ranger (alcance 6) mata cualquier cosa sin recibir un golpe.
7. **PvP:** guerrero 25 contra mago 25 sin VIT: 2 golpes (≈1 s). Sin modificador PvP no hay duelo posible.
8. **DES no hace nada** salvo al ranger; FUE e INT nada fuera de su clase (aceptable), VIT solo vida.
9. **Mago y sanador ya tienen 4 y 3 habilidades** (4002, 4005, 4008, 4009 / 4004, 4006, 4010) y la tabla de §0.8
   les añade más: con I-19 tal cual tendrían 5.

---

## 2. Modelo objetivo

Todo entero o en puntos básicos (1 pb = 0,01 %) en el servidor, determinista con semilla. Las fórmulas se
centralizan en **una** clase estática, `CombatMath` (sale de `CharacterStats`), que usan `CombatSystem`,
`NpcSystem` y el simulador (§3.2): así el simulador no puede divergir del juego.

### 2.1 Atributos: qué hace cada uno

| Atributo | Efecto |
| --- | --- |
| Primario de la clase (`primary`: FUE, DES o INT) | Multiplica el daño, la cura y el escudo: `+powerPerPoint` % por punto |
| DES (todas las clases) | Crítico `+critPerDex` pb por punto y velocidad de ataque `+hastePerDex` pb por punto |
| INT | Recurso máx. (`resource.perInt`, ya existe) |
| VIT | Vida (`healthPerVit` por clase) |

### 2.2 Vida, defensa y resistencia

```
vidaMáx   = baseHealth + nivel × healthPerLevel(clase) + VIT × healthPerVit(clase)
defensa   = defense(clase) + nivel × defensePerLevel(clase) + Σ equipo + bonus (I-18)
resMágica = magicResist(clase) + nivel × magicResistPerLevel(clase) + Σ equipo + bonus (I-18)
recibido  = máx(1, ⌊bruto × 100 / (100 + defensa o resMágica según tipo)⌋)        (sin cambios)
```

| Clase | healthPerLevel | healthPerVit | defensePerLevel | magicResistPerLevel | critBase (pb) |
| --- | --- | --- | --- | --- | --- |
| Guerrero | 10 | 6 | 2 | 0 | 500 |
| Mago | 6 | 4 | 0 | 2 | 500 |
| Ranger | 8 | 5 | 1 | 1 | 800 |
| Sanador | 7 | 5 | 1 | 2 | 300 |

`baseHealth` sigue en 50. Los PNJ ganan `damageType` (`physical` por defecto) para que `magicResist` sirva
en PvE (orcos chamanes, etc.).

### 2.3 Daño

```
base      = dañoArma (o unarmed) + bonus(I-18)            ← el bonus entra ANTES del multiplicador
bruto     = ⌊base × (100 + primario × powerPerPoint) / 100⌋                    powerPerPoint = 2
habilidad = ⌊(skill.damage + dañoArma × skill.weaponPercent / 100) × (100 + primario × powerPerPoint) / 100⌋
crítico   = ⌊bruto × critMultiplier / 100⌋ si sale; después la mitigación, el escudo y la vida (orden de I-18)
PvP       = ⌊recibido × pvpDamagePercent / 100⌋ cuando atacante y víctima son jugadores    pvpDamagePercent = 60
```

- `damagePerPoint` (0,5) se sustituye por `powerPerPoint` (multiplicativo). Así arma, habilidad y atributo
  crecen juntos y una habilidad mantiene su relación con el golpe básico a todos los niveles.
- `weaponPercent` (nuevo en `skills.json`, 0 por defecto = comportamiento actual) hace que la habilidad crezca
  con el arma: Golpe poderoso `damage 6, weaponPercent 150`. Curas y escudos usan la misma fórmula.
- **Armas por franja** (DPS base del arma = `damage / cooldownSeconds`): franjas de `requiredLevel` 1, 5, 10, 15
  y 20 con DPS base **14, 18, 23, 29, 37**. Armas de dos manos +10 % (pierden el escudo), a distancia −5 %,
  bastón +5 %. Ejemplo: espada corta (0,8 s) `damage 11`; arco corto (1,0 s) `damage 13`; bastón de roble
  (1,0 s) `damage 15`, nivel 1. `unarmed` pasa a DPS base 8 (siempre peor que cualquier arma).

### 2.4 Crítico

```
probabilidad (pb) = mín(critCap, critBase(clase) + DES × critPerDex + Σ equipo crit)    critPerDex 20, critCap 4000
multiplicador      = critMultiplier (global 150) + skill.critBonus (0 por defecto)
```

- Lo tira un `System.Random` del `CombatSystem` sembrado con `ServerConfig.CombatSeed` (mismo patrón que
  `NpcSeed`); las pruebas lo inyectan. Las curas pueden ser críticas; los escudos y los PNJ no (en el MVP).
- Sin esquiva ni precisión en el MVP (§6, decisión 5): todo golpe que pasa las reglas de alcance acierta.

### 2.5 Velocidad de ataque

```
prisa (pb)     = mín(hasteCap, DES × hastePerDex + Σ equipo attackSpeed)          hastePerDex 15, hasteCap 3000
recargaTicks   = máx(minAttackTicks, ⌈ticksArma × 10000 / (10000 + prisa)⌉)         minAttackTicks 8 (0,4 s)
```

Solo el ataque básico. Las recargas de habilidades no cambian (son la palanca de equilibrio de cada
habilidad). A 20 Hz un tick es el 5-6 % de una recarga de 0,8 s: la prisa se nota a saltos de ~5 %; aceptado.
Hoy las habilidades no comparten recarga global; se añade `globalCooldownSeconds` 1,0 en `skills.json`
(raíz) para que no se lancen cuatro en el mismo tick.

### 2.6 Velocidad de movimiento

```
velocidad = PlayerSpeed × mín(moveCap, 100 + Σ equipo moveSpeed) / 100 × SpeedPercent(ralentización I-18) / 100
```

- Todas las clases a 100 %; botas pueden dar `moveSpeed` 5..10; `moveCap` 115. Se reutiliza el `SpeedPercent`
  del mensaje `StatusEffects` de I-18 como **porcentaje total** (equipo × ralentización), que el cliente ya
  aplica con `ClientPrediction.SetSpeed`; el formato no cambia.
- PNJ: velocidad de 3,0 a 3,4 (75-85 % del jugador). Un cuerpo a cuerpo alcanza a quien se queda quieto, el
  ranger aún puede alejarse andando y la ralentización pasa a ser la herramienta de control.

### 2.7 Curvas de referencia por nivel (contenido que deben cumplir los datos)

«Build equilibrado» = 2/3 de los puntos al primario y 1/3 a VIT; equipo de la franja. Con §2.2-2.5:

| Nivel | PV guerrero / mago | DPS ref. (melé) | Defensa equipo (set completo) | PNJ normal: PV / golpe (1,5 s) / def |
| --- | --- | --- | --- | --- |
| 1 | 126 / 84 | 16 | 6 | 80 / 13 / 1 |
| 5 | 190 / 124 | 23 | 12 | 105 / 22 / 5 |
| 10 | 270 / 174 | 32 | 20 | 147 / 34 / 10 |
| 15 | 350 / 224 | 44 | 30 | 206 / 49 / 15 |
| 20 | 430 / 274 | 60 | 40 | 289 / 66 / 20 |
| 25 | 510 / 324 | 63 | 40 | 330 / 75 / 25 |

Élite y campeón siguen con los multiplicadores de rango de I-20 (`npcs.json` → `ranks`). Los dos orcos actuales
se recolocan en esta tabla (orco nivel 1: 80 PV, 13 de daño; orco guerrero nivel 3: 95 PV, 18 de daño).

### 2.8 Estados de I-18 dentro del modelo

| Estado | Entra en |
| --- | --- |
| Bonus | `+A` a la **base** del daño (antes del multiplicador) y `+A` a defensa y resMágica. Cambio pequeño respecto a I-18 (allí se suma al bruto). |
| Escudo | Igual que I-18: después de mitigación y crítico; cantidad con la fórmula de habilidad. |
| Ralentización | Multiplica la velocidad de §2.6. No afecta a la velocidad de ataque. |
| Provocar | Sin cambios. |

---

## 3. Equilibrio: objetivos y herramienta

### 3.1 Objetivos medibles (todos sobre el build equilibrado con equipo de la franja)

1. **PvE, mob normal de su nivel, solo ataque básico:** muere en **5 s ± 20 %** (4-6 s); el jugador pierde
   **30-50 %** de la vida (quieto, sin pociones). Con rotación de habilidades: **3-4,5 s**.
2. **Élite de su nivel:** 12-18 s; sin poción ni cura muere el jugador en al menos 2 de las 4 clases.
3. **DPS de objetivo único** de guerrero, mago y ranger dentro de **±10 %** de su media en cada nivel 1, 5, 10,
   15, 20 y 25; sanador al **65-80 %** de esa media.
4. **Área:** Bola de fuego y Lluvia de flechas con 3 objetivos ≥ **1,8×** su DPS de objetivo único.
5. **Aguante:** vida efectiva (PV × (100+def)/100) del guerrero 1,3-1,6× la media; mago 0,7-0,85×.
6. **Cura:** el sanador mantiene a un guerrero contra un élite de su nivel durante 30 s sin quedarse sin maná.
7. **PvP, duelo 1 contra 1 del mismo nivel y equipo, 200 semillas:** ninguna pareja de clases gana más del **65 %**
   de los duelos; duración media **8-15 s**.
8. **Habilidades:** cada habilidad de daño aporta entre el 10 % y el 35 % del daño de la rotación; ninguna con
   menos del 5 % (inútil) ni más del 50 % (obligatoria).

### 3.2 Herramienta: simulador sin red

- **Proyecto nuevo** `tools/Avelora.Balance/` (consola .NET 10, en `Avelora.slnx`, referencia a
  `Avelora.Server`). Carga los JSON reales con `ClassCatalog`, `ItemCatalog`, `SkillCatalog` y `NpcCatalog`
  y usa **solo** `CombatMath` para daño, crítico, mitigación y recargas.
- **Motor** `CombatSimulator` (en el proyecto de la herramienta): combate por ticks a 20 Hz entre dos
  combatientes quietos y en alcance, sin mapa ni red. Jugador: rotación codiciosa (la habilidad de daño lista
  con más daño por coste; si no, ataque básico), recurso y regeneración reales. PNJ: golpe cada
  `attackCooldownSeconds`. Modo «esperanza» (crítico como media, sin azar) y modo «semilla» (N repeticiones).
- **Builds:** `puro` (todo al primario) y `equilibrado`. **Equipo:** por hueco, el objeto con mayor
  `requiredLevel ≤ nivel` que la clase pueda llevar (sin datos nuevos).
- **Salida:** tablas Markdown a la consola (DPS por clase y nivel, TTK y % de vida contra cada PNJ, matriz de
  duelos) y opción `--salida <archivo>`. Ejemplo:
  `dotnet run --project tools/Avelora.Balance -- --niveles 1,5,10,15,20,25 --duelos 200 --semilla 1`.
- **Pruebas** `tests/Avelora.Tests/Balance/` (referencian el proyecto de la herramienta):
  - Equivalencia: el mismo golpe resuelto por `CombatSystem` (zona de prueba) y por `CombatSimulator` da el
    mismo daño y la misma recarga en ticks.
  - Objetivos de §3.1 como `[Theory]` por clase y nivel. Entran **en modo informe** (I-26: solo escriben las
    cifras) y pasan a **afirmar** en I-30, cuando los datos de las franjas existan.

---

## 4. Habilidades (I-19): 4 por clase, niveles 1, 5, 10 y 20

Mecánicas: daño, área centrada en el objetivo, cura (I-16/I-17), bonus, escudo, ralentización, provocar
(I-18). Nuevas y pequeñas, todas en datos: `weaponPercent` (§2.3), `critBonus` (§2.4), `hits` (1-5 golpes en
el mismo tick, cada uno con su crítico), `areaCenter: "self"` (área alrededor del lanzador, sin objetivo) y
**cura en área** (`effect: "heal"` + `radius`: aliados válidos según la regla de cura de I-17).

| Clase | Nv 1 | Nv 5 | Nv 10 | Nv 20 (final) |
| --- | --- | --- | --- | --- |
| Guerrero | Golpe poderoso (4001): daño 150 % arma | Provocar (4007): taunt 4 s | Torbellino (4011): área `self` r 2, 100 % arma, 6 obj. | Furia imparable (4012): bonus propio 12 s, +daño +def |
| Mago | Rayo helado (4008): daño + slow 40 % 3 s | Escudo arcano (4009) | Bola de fuego (4005): área r 2,5 | Lluvia de meteoros (4013): área r 4, 10 obj., recarga 45 s |
| Ranger | Disparo certero (4003): daño, `critBonus` 50 | Tiro a las piernas (4014): daño + slow 50 % 3 s | Ráfaga (4015): `hits` 3 | Lluvia de flechas (4016): área r 3,5, 8 obj. |
| Sanador | Curación (4006) **y** Castigo (4004) | Bendición (4010): bonus | Curación en área (4017): r 4 | Renovación divina (4018): cura en área r 6 + escudo pequeño a cada uno |

- **Se retira** Proyectil arcano (4002): el mago se queda en 4 (decisión 1, §6). Su id no se reutiliza; el
  cliente ignora atajos a ids desconocidos.
- El sanador tiene **5** (dos a nivel 1, para poder subir solo); es la única excepción (decisión 1).
- «Tiro debilitante» (reducir defensa) necesitaría un quinto tipo de estado (`MaxStatusEffects` = 4, protocolo):
  se sustituye por ralentización. Un estado «debilitar» queda para después del MVP.
- Valores iniciales (coste, recarga, `damage`, `weaponPercent`) los fija I-19 con el simulador hasta cumplir el
  objetivo 8 de §3.1; las recargas de las finales, 45-90 s.

---

## 5. Tareas (I-20 a I-25 ya están usadas; las nuevas empiezan en I-26)

Orden: **I-18 → I-18b → I-26 → I-27 → I-28 → I-28b → I-29 → I-19 → I-19b → I-30 → I-30b.**
Versiones orientativas (una subida por tarea que cambie el formato).

| Tarea | Qué | Peso | Protocolo / esquema | Cliente | Verificación |
| --- | --- | --- | --- | --- | --- |
| **I-26** Fórmulas en `CombatMath` y simulador | Mover `GetAttackDamage`, `MitigateDamage` y el cálculo de recarga de `CharacterStats`/`CombatSystem` a `CombatMath` sin cambiar resultados; `tools/Avelora.Balance` y pruebas de equivalencia y de informe. | Media | No / No | — | Las 898 pruebas siguen verdes sin tocarlas; el simulador reproduce la tabla de §1 (guerrero 1: 21,2 DPS); carga sin cambios. |
| **I-27** Modelo de daño, vida y defensa | §2.2, §2.3 (sin crítico ni PvP), bonus antes del multiplicador, `damageType` de PNJ, `weaponPercent`; reescribir números de `classes.json`, `items.json` (armas por franja 1 y 5, armaduras), `npcs.json` (orcos a §2.7) y `skills.json`; ajustar pruebas que fijan cifras. | Pesada | No (cambian valores de `Stats`, no el formato) / No | — | Tablas de fórmulas por clase; simulador cumple objetivos 1 y 3 en niveles 1 y 5; carga con bots `fight` sin correcciones. |
| **I-28** Crítico, velocidad de ataque y PvP | §2.4, §2.5, `globalCooldownSeconds`, `pvpDamagePercent`, `CombatSeed`; `CombatEventFlags.Critical = 32`; `Stats` gana probabilidad de crítico y prisa. | Media | **v24** / No | I-28b (liviana): números de crítico más grandes; crítico y prisa en el panel de atributos | Pruebas con `Random` inyectado; recargas en ticks con topes; duelos del simulador (objetivo 7) en informe. |
| **I-29** Velocidad de movimiento | §2.6: `moveSpeed` en armaduras, `moveCap`, `SpeedPercent` total en `StatusEffects`; PNJ a 3,0-3,4; bots que respetan la velocidad. | Liviana | No / No (si el formato de `StatusEffects` no cambia) | Nada si I-18b aplica `SpeedPercent` siempre; si no, entra en I-28b | Pruebas de movimiento con botas y ralentización juntas; carga: correcciones por bot y minuto como I-18. |
| **I-19** Cuatro habilidades por clase | Tabla de §4: `hits`, `areaCenter`, `critBonus`, cura en área, `requiredLevel` 1/5/10/20, retirar 4002; bots con rotación por clase; valores ajustados con el simulador. | Pesada | **v25** (`SkillCatalogEntry` gana campos) / No | I-19b (media): nivel de desbloqueo y bloqueadas en gris en el panel, anillo de área propia, colores provisionales | Pruebas por mecánica nueva; objetivo 8 de §3.1 en informe; carga con bots de las 4 clases. |
| **I-30** Pasada de equilibrio y contenido de referencia | Armas y armaduras de las franjas 10, 15 y 20; un PNJ normal por franja (5 tipos, al menos uno mágico y uno con defensa alta) según §2.7; las pruebas de §3.1 pasan de informe a **afirmar**. | Media (datos) | No / No | I-30b (liviana): color/tamaño por `TypeId` de los mobs nuevos | `dotnet test` con los objetivos afirmados; tabla del simulador en el informe; carga. |

Ninguna toca el esquema SQLite: todo lo nuevo es derivado o de datos. Ninguna toca `client/` salvo las «b».
Riesgo conocido: I-27 cambia la vida y el daño de los personajes existentes (son derivados): es un reequilibrio
explícito, como en I-08.

---

## 6. Decisiones abiertas (con recomendación)

1. **Número de habilidades por clase.** Mago y sanador ya tienen habilidades de sobra. *Recomendación:* 4 por
   clase; retirar Proyectil arcano (4002); el sanador conserva 5 (Curación y Castigo a nivel 1) para poder
   subir solo.
2. **Daño en PvP.** *Recomendación:* `pvpDamagePercent` 60 global (duelos de 8-15 s); curas sin reducir.
3. **Velocidad de los mobs.** *Recomendación:* subirla a 3,0-3,4 (hoy 2,0/1,6, nunca alcanzan a nadie) y que la
   ralentización sea el control del ranger y del mago.
4. **Tipos de armadura por clase (tela/cuero/placas).** *Recomendación:* no en el MVP; la diferencia de aguante
   sale de `healthPerLevel`, `defensePerLevel` y `magicResistPerLevel` de la clase, y todos llevan el mismo equipo.
5. **Esquiva y precisión.** *Recomendación:* no en el MVP; solo crítico. Un fallo aleatorio frustra con
   recargas largas y complica el PvP; se reconsidera con más estados.
