# Diseño: clases, estadísticas, progresión y profesiones (BORRADOR)

> **Estado:** borrador para decidir con el usuario. Solo documento: no hay código ni tareas
> escritas todavía. Cada decisión abierta lista 2–3 opciones y marca una como **Recomendada**.
> Los números son un punto de partida para equilibrar, no un compromiso.
> **Actualizado con las respuestas del usuario: ver §0, que manda sobre §2–§6.** §7 y §8 están reescritos.

Principios que se respetan en todo el documento:

- El servidor es la autoridad. El cliente pide (`Attack`, `Equip`…) y el servidor decide.
- Los datos de juego viven en JSON del servidor (`items.json`, `npcs.json`, `levels.json` y los
  nuevos) y el servidor manda catálogos al cliente. El cliente solo pone visuales (ScriptableObjects).
- No rehacer lo que ya funciona: la curva del guerrero propuesta **reproduce la actual** (100 de vida
  en nivel 1, +15 por nivel, 235 en nivel 10).
- Cada tarea cambia una cosa, sube la versión de protocolo/esquema solo si hace falta y se verifica
  con pruebas + carga, como hasta ahora.

---
## 0. Decisiones del usuario (mandan sobre el resto del documento)

Respuestas dadas tras el primer borrador. Donde una sección posterior diga otra cosa, **vale esta**.
Las secciones 2–6 conservan el análisis original y llevan una nota si algo quedó superado.

| # | Tema | Decisión |
| --- | --- | --- |
| 1 | Referencias | Todas las mencionadas (Tibia, Ragnarok, WoW, Albion): combate por selección de objetivo, clases con rol claro y equipo por huecos. |
| 2 | Clases | **Cuatro**: guerrero, mago, ranger **y sanador**; el sistema no asume una lista cerrada (ids de datos). |
| 3 | Personajes | **3 por cuenta**. |
| 4 | Puntos | **Repartidos por el jugador.** **No se puede cambiar de clase.** |
| 5 | PvP | **Sí.** |
| 6 | Nivel y muerte | **Nivel máximo 25 en el MVP.** La muerte causada por un mob **resta experiencia**. |
| 7 | Habilidades | MVP: **3 principales + 1 final** por clase. Más habilidades después del MVP. |
| 8 | Equipo | Casco, armadura, botas, guantes, pantalones, anillo, collar, escudo/mano secundaria y accesorio, más el arma: **10 huecos**. |
| 9 | Comercio | **Sin comercio entre jugadores** en el MVP. |
| 10 | Oficios a nivel 5 | **Los oficios (profesiones) se desbloquean a nivel 5.** Las clases futuras (bárbaro, paladín…) serán **clases base nuevas**, no avanzadas (§0.11). |
| 11 | Arte | Cuadrados de color: guerrero **rojo**, mago **azul**, ranger **verde**, sanador **blanco**. |

### 0.1 Sanador (clase nueva)

| | Sanador |
| --- | --- |
| Papel | Apoyo: cura, protege y aporta daño bajo. |
| Atributo principal | INT (potencia de curación y de su daño) |
| Base FUE/DES/INT/VIT | 1 / 2 / 7 / 8 → vida 90 |
| Armas | maza/cetro (cuerpo a cuerpo corto) y bastón de sanación |
| Recurso | Maná (como el mago) |
| Color de prueba | Blanco |

Requiere dos cosas que hoy no existen: apuntar a **otro jugador** y un efecto «curar» (el combate de
hoy es jugador → PNJ). Entra con I-15 (jugadores como objetivo) e I-17 (curación).

### 0.2 Puntos repartidos por el jugador (sustituye a §4.1 y al crecimiento fijo de §2.2/§3.2)

Atributo = base de clase + puntos que el jugador reparte + bonus de equipo.

- **Puntos por nivel: 3 (provisional, §0.11).** Con nivel 25 son 75 puntos (el nivel 1 también da 3). Es un valor de `levels.json` (`pointsPerLevel`) que se revisa al cerrar la fórmula de stats, los stats de objetos y las defensas/resistencias.
- El servidor guarda **solo lo repartido** por atributo (`alloc_str/dex/int/vit`); los **puntos libres
  se calculan** (`pointsPerLevel × nivel − repartidos`). Así subir de nivel no necesita escribir en la base de datos
  ni se puede desincronizar.
- La clase se distingue por **base de atributos, armas, habilidades, recurso y `vidaPorVIT`** (p. ej.
  guerrero 5, mago 4), no por crecimiento fijo.
- `AllocatePoints` lo valida el servidor: suma ≤ puntos libres, valores no negativos, rate-limit.
- **Riesgo nuevo:** un jugador puede dejarse sin vida o sin daño. Se mitiga con un tope por atributo
  ligado al nivel (propuesta: `10 + 4 × nivel`) y con el reajuste de §0.4.
- Los personajes actuales (guerreros) conservan 100 de vida en nivel 1 y reciben todos sus puntos
  **sin gastar**, no se les quita nada.

### 0.3 Tres personajes por cuenta (sustituye a §3.5)

De las tres opciones de §3.5, ahora **es la B**: hay que separar la autenticación de la entrada en el
mundo. Flujo: `Hello` (cuenta/contraseña/invitación, sin nombre de personaje) → el servidor responde
`CharacterList` (hasta 3: id, nombre, clase, nivel) → el cliente envía `CreateCharacter(nombre, clase)`,
`DeleteCharacter(id)` o `SelectCharacter(id)` → `Welcome`. Implicaciones:
la conexión pasa por un estado «autenticado sin personaje» (en el hilo de login); la sesión única por
cuenta se mantiene; el índice de nombre pasa a ser **único en todo el servidor** (necesario para chat y
PvP; ver pregunta 8) y hay que comprobar duplicados antes de migrar; el borrado es lógico y con
confirmación. Es la tarea con más riesgo (toca el handshake cifrado), por eso va pronto (I-10).

### 0.4 Reajuste de puntos (sustituye a §4.3)

Como el jugador reparte a mano, necesita poder corregir. Sin comercio ni moneda en el MVP:

- **A — Primer reajuste gratis hasta el nivel 5; después con un objeto raro («piedra de reajuste») que
  suelta un mob. — Recomendada.** Pros: no hay economía que lo sostenga, premia jugar y evita builds
  rotas al empezar. Contras: hay que añadir un objeto y su efecto.
- **B — Siempre gratis.** Pros: sin objetos. Contras: no hay coste a las decisiones.
- **C — Sin reajuste.** Contras: un error es definitivo; frustra con decenas de puntos en juego.

### 0.5 Equipo: 10 huecos (amplía §6.2)

`weapon`, `offhand` (escudo o mano secundaria), `head`, `body`, `legs`, `hands`, `feet`, `ring`,
`neck` y `accessory`. Un solo anillo salvo que se indique lo contrario. Tabla
`character_equipment(character_id, slot, item_id)` (ya prevista), con ids de hueco fijos en
`Avelora.Shared`. Un arma de dos manos (`twoHanded`) bloquea `offhand`. Cada objeto puede dar defensa y
bonus de atributo; **ligadas al nivel y a la clase** (`requiredLevel`, `weaponType`). Sin comercio, el
botín y las futuras profesiones son la única fuente de equipo.

### 0.6 PvP

Hoy `Attack` solo admite PNJ. Opciones:

- **A — PvP solo en zonas marcadas del mapa (rectángulos en datos). — Recomendada.** Pros: control
  del griefing, se puede probar y medir, no obliga a decidir facciones; el sanador y los aliados
  funcionan fuera. Contras: hay que definir las zonas y comprobar la posición en cada ataque.
- **B — PvP en todo el mapa.** Pros: simple. Contras: griefing, cualquier jugador nuevo muere a manos de
  otros; el MVP de 80×60 es pequeño.
- **C — Opt-in con marca personal.** Pros: flexible. Contras: más estado y reglas (tiempo de marca,
  salir/entrar) que probar.

En el MVP: sin XP ni penalización de XP por muerte entre jugadores, sin pérdida de objetos; un PNJ
mata → penaliza; un jugador mata → no. «Aliado» para curar se decide en la pregunta 4.

### 0.7 Nivel 25, experiencia y muerte

- `levels.json` pasa a **24 umbrales**. Propuesta de curva (a equilibrar): `100, 150, 225, 340, 500,
  750, 1100, 1650, 2500, 3500, 4700, 6200, 8000, 10000, 12500, 15500, 19000, 23000, 27500, 32500,
  38000, 44000, 51000, 59000`. Para llegar a 25 hacen falta muchos más tipos de enemigo (hoy 2): es
  contenido, no sistema.
- **Muerte por mob:** pierde experiencia (propuesta: 10 % del umbral del nivel actual, nunca baja de
  nivel y nunca por debajo de 0). La muerte entre jugadores no penaliza.

### 0.8 Habilidades del MVP: 3 principales + 1 final por clase

16 habilidades en total, desbloqueo propuesto en los niveles 1, 5, 10 y 20. Restricción técnica
importante: **sin desplazamientos forzados** (saltos, empujones) en el MVP, porque chocan con la
predicción del movimiento; sí entran daño, áreas, curas y estados temporales.

| Clase | Principales (ejemplo) | Final (ejemplo) |
| --- | --- | --- |
| Guerrero | Golpe poderoso (daño alto), Provocar (aggro), Torbellino (área corta) | Furia imparable (bonus de daño/defensa temporal) |
| Mago | Bola de fuego (área), Rayo helado (ralentiza), Escudo arcano (absorbe) | Lluvia de meteoros (área grande) |
| Ranger | Disparo certero, Ráfaga (varios disparos), Tiro debilitante (reduce defensa) | Lluvia de flechas (área) |
| Sanador | Curación, Curación en área, Bendición (bonus temporal) | Renovación divina (curación masiva) |

Eso exige en el servidor: `UseSkill`, `skills.json`, coste de recurso, recarga, selección de objetivo
(PNJ, jugador, suelo), área por radio y estados temporales (§7: I-16 a I-19).

### 0.9 Cliente: cuadrados de color

Hasta tener sprites, cada clase se dibuja con un cuadrado: guerrero rojo, mago azul, ranger verde,
sanador blanco. Se resuelve en el cliente con un ScriptableObject por `classId` (visual); el servidor
solo manda el id (`TypeId` de jugador = `classId`).

### 0.10 Segunda ronda de decisiones (sustituye lo que contradiga arriba)

1. **Oficios y clases a nivel 5.** Los oficios (§5) se desbloquean a nivel 5. Interpretación para las
   clases: en el MVP solo existen las **4 bases** (guerrero, mago, ranger, sanador), disponibles al
   crear el personaje. **Corrección (§0.11):** no habrá clases avanzadas; bárbaro, paladín, etc. serán
   **clases base nuevas** que se añaden con el tiempo, así que `parentClassId`/`unlockLevel` ya no
   hacen falta. Solo se evita cerrar la puerta (el `class_id` guardado puede cambiar de una base a su
   hija sin migración, y las armas/habilidades se filtran por `weaponTypes`/`classId` de datos, no por
   código).
2. **Reajuste de puntos (sustituye a §0.4):** gratis **hasta el nivel 10**; después, con un objeto
   («piedra de reajuste») que suelta un mob. El objeto se añade a `items.json`; no hay comercio.
3. **PvP (sustituye a §0.6): zonas marcadas + marca personal.** Un jugador solo puede dañar a otro
   cuando se cumplen **las tres** cosas: ambos están en una zona PvP, ambos tienen la marca puesta y
   ninguno está muerto. Fuera de zona la marca no hace nada. Detalles propuestos: la marca se activa con
   `SetPvpMark(on)`; activarla surte efecto al instante, **corrección (§0.11):** la marca solo se quita en zonas seguras; al entrar en una zona PvP la marca no se activa sola. La curación de
   aliados afecta a cualquier jugador **no hostil** (el sanador cura a cualquiera salvo a quien le esté
   atacando en ese momento por PvP; la regla exacta se afina en I-17).
4. **Armas de dos manos y base para clases futuras.** Cada arma declara `handedness`
   (`one` | `two`); una `two` bloquea `offhand`. Los tipos de arma y la lista que cada clase puede usar
   son **datos** (`weaponTypes` en `classes.json`), así bárbaro (hacha a dos manos), paladín (maza y
   escudo) u otras clases se añaden sin código nuevo en `Equip`. Por ahora arco y bastón del ranger y
   el mago son de dos manos; la espada, el hacha y la maza, de una.
5. **Muerte por mob: 10 % del umbral del nivel** (decidido). **Pérdida de objetos en PvP:** queda
   explícitamente para después del MVP, para explorarla (no entra en ninguna tarea actual). Se deja
   anotado que exigirá reglas de «objeto ligado»/seguro y registro de quién mató.
6. **Nombres:** únicos en todo el servidor (sin distinguir mayúsculas), asignados por el jugador al
   crear el personaje, **inmutables**. Validación sugerida: 3–16 caracteres, letras (incluidas tildes) y
   dígitos, sin espacios múltiples, lista de nombres reservados («admin», «gm»…). Índice único en
   SQLite y comprobación en la transacción de creación (dos jugadores eligiendo el mismo nombre a la
   vez: gana el primero y el segundo recibe un rechazo de nombre ocupado).

### 0.11 Tercera ronda de decisiones

1. **Puntos por nivel: 3, provisional** mientras se diseña la fórmula de stats, los stats de objetos,
   la defensa y las resistencias. Es solo `pointsPerLevel` en `levels.json`; cambiarlo no toca código
   ni base de datos (los puntos libres se derivan, §0.2). Con 3 puntos por nivel, a nivel 25 hay 75 puntos
   (3 × 25), la mitad de lo planteado antes: la base de clase y el equipo pesan más.
2. **Habilidad final: nivel 20** (las demás en 1, 5 y 10).
3. **Marca PvP:** se puede **activar en cualquier sitio, pero solo se quita dentro de una zona segura**;
   fuera no hay forma de quitarla. Además de zonas PvP, el mapa necesita **zonas seguras** (donde el
   daño entre jugadores está desactivado y se puede quitar la marca). Una zona puede ser PvP, segura o
   normal (sin PvP aunque haya marca). Consecuencia a vigilar: con la marca puesta fuera de una zona
   PvP no se puede dañar a nadie, así que «estar marcado» solo importa dentro de zonas PvP; el riesgo es
   que un jugador marcado por error tenga que volver a una zona segura para quitarla (es la intención).
4. **Clases futuras = clases base nuevas, fuera del MVP.** En el MVP solo existen guerrero, mago,
   ranger y sanador; lo único que se hace por las futuras es **dejar la base**: nada de lo que se
   construya puede depender de un `classId` concreto en el código. No hay clases avanzadas. `classes.json` es una lista abierta
   (añadir bárbaro o paladín es añadir una entrada con sus armas, atributos base, habilidades y color
   provisional) y se retiran `parentClassId`/`unlockLevel`. Un cambio de clase sigue sin existir.
5. **Resistencias.** Para no rehacer la defensa cuando lleguen los elementos, el modelo de §2.3 se amplía
   de forma compatible: `defensa` (física) más **resistencias por tipo de daño** (`fisico`, `magico` para
   empezar; luego fuego, hielo…). Cada ataque y habilidad declara su tipo; el daño recibido usa la
   defensa para el físico y la resistencia para el mágico, con la misma forma `100/(100+valor)`.
   Objetos, clases y estados temporales pueden sumar a ambas. I-08 solo implementa físico y mágico.
6. **Curva de experiencia y dificultad (mi decisión razonada para la mejor experiencia).**
   - **Objetivo de ritmo:** unas 5 muertes de mob por nivel al principio, subiendo a ~30 hacia nivel 25.
     Tiempo orientativo hasta el máximo: 20–30 horas de juego.
   - **Umbrales:** se mantiene la tabla actual (100…2500 hasta nivel 10) y sigue con crecimiento
     decreciente: `3500, 4700, 6200, 8000, 10000, 12500, 15500, 19000, 23000, 27500, 32500, 38000,
     44000, 51000, 59000` (24 en total). Cada nivel cuesta entre un 16 % y un 40 % más que el anterior:
     las primeras subidas son rápidas (engancha) y las últimas dejan de ser exponenciales.
   - **Nivel del mob:** `npcs.json` gana `level`. La experiencia se escala por diferencia de nivel:
     `multiplicador = clamp(1 + 0,1 × (nivelMob − nivelJugador), 0,1, 1,3)`. Así matar mobs muy por debajo
     no sirve para subir y los mobs un poco por encima dan un premio.
   - **Dificultad por franjas de mapa:** cada ~5 niveles, una franja con mobs nuevos (HP, daño y
     recompensa mayores). Objetivos de combate, sin armadura ni habilidades: un mob normal del mismo
     nivel muere en ~5 s y te quita ~40 % de la vida si no usas pociones; un «élite» tarda ~15 s y
     exige curarse; dos mobs a la vez ya son peligrosos. Las pociones y las habilidades se equilibran a
     partir de ahí.
   - **Muerte:** 10 % del umbral del nivel actual (decidido); con la curva de arriba, a nivel 24 son
     5 900 puntos (~3 mobs), a nivel 5 son 50. Nunca baja de nivel.
   - **Contenido que hace falta:** al menos 5 franjas de mobs (hoy solo hay 2 tipos de orco); es trabajo
     de datos tras los sistemas, no parte de este plan.

### 0.12 Zonas seguras y cómo delimitarlas

Decidido: las zonas seguras son **las ciudades y ciertas zonas concretas del mapa**, y debe existir una
forma de delimitarlas donde se necesiten. Esto convierte las zonas en un sistema de datos propio, no en
un detalle del PvP; por eso va en su tarea previa (I-15, abajo).

**Tipos de zona:** `safe` (ciudad/refugio), `pvp` (PvP habilitado) y, por defecto, `normal` (el resto).
Si dos zonas se solapan manda la más restrictiva: `safe` > `pvp` > `normal`.

**Reglas en una zona `safe`:**
- Ningún jugador puede dañar a otro; ni habilidades de área alcanzan a los de dentro.
- La marca PvP se puede quitar (y no se puede activar el daño por ella).
- Los mobs **no aparecen** ni **entran** en una zona segura (los spawners no se colocan ahí y la IA
  trata el borde como pared). **Decidido:** cuando el jugador entra en una zona segura, el aggro de los
  mobs sobre él **se cancela** (pasan a `Return`, reutilizando la IA); no pueden dañarlo dentro, y si
  sale otra vez no lo recuerdan hasta que vuelvan a detectarlo. Un jugador dentro tampoco puede atacar a un mob (evita «tirar desde la ciudad»).
- Curar y usar pociones siguen permitidos. La regeneración de vida puede ser más rápida (opcional).
- Se pueden poner cosas que solo existen en ciudades (PNJ de servicio, tiendas, reajuste de puntos) más adelante.

**Cómo se delimitan (opciones):**

- **A — Rectángulos (y uniones de rectángulos) en un `zones.json` del servidor. — Recomendada para empezar.**
  Cada zona: `id`, `name`, `kind` (`safe`/`pvp`), lista de rectángulos `{x, y, w, h}` en coordenadas del
  mapa y, opcional, una lista de «exclusiones». Pros: lo escribe el agente y lo revisa cualquiera,
  tests triviales, un solo origen de verdad en el servidor (como `npcs.json`), una ciudad en L o con un
  hueco se hace con 2–3 rectángulos. Contras: no es visual; las formas muy irregulares dan trabajo.
- **B — Capa pintada en el Tilemap de Unity y exportada con el mapa (`.avmap`).** Una capa «Zonas» con un
  tile por tipo; el exportador (como el de colisiones) la mete en el archivo. Pros: se dibuja a ojo,
  ideal para ciudades irregulares; la huella del mapa ya cubre cambios. Contras: cambia el formato
  `.avmap` (versión del formato, servidor y cliente a la vez), requiere trabajo en el editor de Unity y
  el diseñador depende de Unity para tocar una zona.
- **C — Círculos/polígonos en JSON.** Pros: más formas. Contras: más geometría que probar para poco
  beneficio frente a rectángulos.

**Recomendada: A ahora, con una interfaz única (`ZoneMap.KindAt(x, y)`)**, de modo que B pueda
reemplazar la fuente de datos más adelante sin tocar el combate ni el PvP. Una cuadrícula precalculada
al cargar (`kind` por casilla) hace la consulta O(1) en el tick. Si el diseñador quiere editar
ciudades visualmente, B se hace después como tarea aparte.

**Qué necesita saber el cliente:** el tipo de zona donde está el jugador (para mostrar «Zona segura» /
«Zona PvP», habilitar el botón de la marca y pintar las zonas en el minimapa). El servidor manda un
mensaje `ZoneChanged(zoneId, kind)` al entrar/salir y, en el `Welcome`, la lista de zonas
(rectángulos incluidos, para dibujarlas). No se confía en el cliente: la regla de daño la aplica
siempre el servidor.

**Casos límite a probar:** jugador en el borde exacto de la zona; teletransporte o reaparición dentro de
una zona; cambiar de zona mientras la recarga de ataque corre; el mismo punto en dos zonas; un mob muerto
a la vez que el jugador entra en zona segura (no pierde la experiencia ganada); mapas sin `zones.json`
(todo es `normal`, el servidor sigue arrancando).

---


## 1. Estado actual (breve)

| Área | Hoy |
| --- | --- |
| Simulación | Servidor .NET 10, tick 20 Hz, UDP (LiteNetLib), SQLite. Un hilo de simulación por zona. |
| Combate | `Attack(targetId)` del cliente. `CombatSystem` valida: atacante vivo, objetivo PNJ vivo, distancia ≤ alcance + `attackRangeTolerance` (0,5), recarga en ticks. Solo jugador → PNJ. Daño = daño del arma (o puños: 10) `+ (nivel−1) × damagePerLevel`. **Sin defensa**: el daño no se mitiga. Alcance máximo validado: 5 tiles (todo es cuerpo a cuerpo en la práctica: 1,2–1,3). |
| Vida | `PlayerMaxHealth` (100, `ServerConfig`) `+ (nivel−1) × 15`. Regeneración 2/s tras 5 s sin daño. Muerte y reaparición (I-03). Pociones: curan 30 fijos. |
| Niveles | `levels.json`: `healthPerLevel` 15, `damagePerLevel` 1, `experienceToNext` (9 umbrales → nivel máx. 10). XP solo para quien da el golpe mortal. Nivel y XP persisten. |
| Armas / equipo | Un único hueco (`EquippedWeaponId`, 0 = puños). Armas: espada corta (14 daño, 1,2 alcance, 0,8 s) y hacha de orco (22, 1,3, 1,2 s). Sin armadura, sin requisitos, sin tipos de arma. |
| NPC | Orco (30 PV, 8 daño/1,5 s) y orco guerrero (60 PV, 14 daño/2,0 s). Daño plano, sin defensa. |
| Persistencia | Esquema **v3**. `characters` ya tiene `level`, `experience`, `health`, **`mana`** (sin uso), `equipped_item_id`. `inventory_items(character_id, slot, item_id, count)`. Migraciones por `PRAGMA user_version`, cada una en su transacción (v1→v2→v3). |
| Personaje | Se crea solo en el primer login (el `Hello` lleva cuenta, contraseña, nombre de personaje e invitación). No hay clase. `UNIQUE(account_id, name)` ya permitiría varios personajes por cuenta. |
| Protocolo | **v10**. Mensajes relevantes: `Attack`(3), `Equip`(6), `Unequip`(7), `ItemCatalog`(15), `Inventory`(14, lleva `equippedItemId`), `Progress`(16: nivel, XP, umbral, vida máxima), `Chat`(17). Snapshot: 15 B/entidad con `kind`, vida %, acción y `TypeId`. |
| Cliente | Predicción de movimiento, interpolación, UI de inventario. Todo el arte es provisional. |

Detalles a comprobar al implementar (no se han verificado aquí): qué valor lleva `TypeId` para
jugadores (probablemente 0), y si `ItemCatalog` ya se parte en trozos (con 23 B/objeto y el tope de
1400 B por mensaje, entran unos 40 objetos; con armaduras y recetas se quedará corto).

---

## 2. Estadísticas

### 2.1 Qué atributos

- **Opción A — Sin atributos (modelo plano).** Cada clase tiene su tabla de vida/daño por nivel (lo
  que hoy es `levels.json`, pero por clase).
  - Pros: mínimo trabajo, cero cambios de protocolo.
  - Contras: el equipo no puede dar «+fuerza», no hay nada que repartir ni mostrar, y las
    profesiones y habilidades futuras no tienen dónde engancharse. Habría que rehacerlo luego.
- **Opción B — Cuatro atributos: Fuerza (FUE), Destreza (DES), Inteligencia (INT), Vitalidad (VIT). — Recomendada.**
  - Pros: es lo que el usuario nombra; cada clase tiene un atributo principal claro; el equipo y las
    pociones/comida de profesiones pueden dar bonus; permite puntos libres más adelante sin cambiar
    fórmulas. Es compacto: 4 números.
  - Contras: más datos que equilibrar; los atributos no principales de cada clase hacen poco en v1.
- **Opción C — Tres atributos (FUE/DES/INT) y la vida sale solo de la clase.**
  - Pros: menos números.
  - Contras: la vida deja de poder mejorar con equipo/atributos, y VIT es la palanca natural para
    «tanque vs. cañón de cristal».

**Recomendada: B.** Es el modelo mínimo que no obliga a rehacer nada cuando lleguen equipo con bonus,
puntos libres, habilidades y profesiones.

### 2.2 De dónde salen

```
atributo = base de clase + (nivel − 1) × crecimiento de clase      (siempre, automático)
         + bonus de equipo                                         (suma de los objetos equipados)
         + puntos libres repartidos                                (0 en v1; ver §4)
         + efectos temporales (comida, pociones)                   (profesiones, mucho después)
```

El servidor **deriva** los atributos cuando hace falta; en la base de datos solo se guardan clase,
nivel y (más adelante) puntos libres. Así no hay datos redundantes que corregir al cargar.

### 2.3 Fórmulas (todas en una única clase del servidor, como hoy `LevelTable`)

```
vidaMáx      = vidaBase + VIT × vidaPorVIT                  vidaBase 50, vidaPorVIT 5
daño bruto   = dañoArma + ⌊atributoDeEscalado × dañoPorPunto⌋   dañoPorPunto 0,5
                (atributoDeEscalado lo dice el arma: FUE, DES o INT)
defensa      = defensaDeEquipo + defensaBaseDeClase
dañoRecibido = máx(1, ⌊daño bruto × 100 / (100 + defensa)⌋)
recursoMáx   = recursoBase(clase) + INT × recursoPorINT(clase)
recarga      = la del arma (sin cambios), en ticks
```

- Mitigación `100/(100+def)`: nunca llega a inmunidad, no necesita tope y es fácil de leer (20 de
  defensa ≈ −17 %, 100 ≈ −50 %). Mínimo 1 para que nada sea inmune.
- Enteros y suelo (`⌊ ⌋`) en el servidor: determinista y fácil de probar.
- El daño recibido por el jugador (orcos) también pasa por la mitigación; los PNJ ganan un campo
  opcional `defense` (0 por defecto) para el daño que reciben de los jugadores.
- Sin críticos, esquiva ni tipos de daño en v1 (no son deterministas ni baratos de probar; se añaden
  cuando haya habilidades). Un único tipo de «defensa» en v1.

**Recurso (maná/energía).** Opciones:

- **A — Maná para todos**, escalado por INT. Pros: un solo recurso. Contras: el guerrero con INT
  baja tendría casi nada.
- **B — Un recurso genérico por clase (nombre, color, máximo y regeneración definidos en JSON). — Recomendada.**
  Maná (mago), energía (ranger), furia/aguante (guerrero). Pros: el protocolo, la base de datos
  (la columna `mana` ya existe, se reutiliza) y la UI manejan un solo número «recurso»; cada clase
  se siente distinta cambiando solo datos. Contras: hay que decidir 3 curvas.
- **C — Solo mago y ranger tienen recurso.** Pros: el guerrero es simple. Contras: casos especiales
  en código y UI.

El ataque básico **no cuesta recurso** (así nadie se queda sin poder atacar). El recurso solo se
gasta en habilidades (§3.4); hasta entonces solo se muestra y se regenera.

### 2.4 Encaje con `levels.json`

`levels.json` hoy mezcla dos cosas: la curva de XP (común a todos) y el crecimiento de vida/daño
(que pasa a depender de la clase).

- **Se queda** `experienceToNext` (y el nivel máximo, que sigue siendo `longitud + 1`).
- **Se sustituyen** `healthPerLevel` y `damagePerLevel`: dejan de existir y su efecto sale de
  `growth` de cada clase en `classes.json` (§3.2). La validación y las pruebas de I-06 se adaptan.
- **Compatibilidad con jugadores existentes:** todos pasan a *guerrero* y su vida máxima no cambia
  (50 + 5 × VIT, con VIT de guerrero = 10 + 3 por nivel → 100 + 15 × (nivel−1)). El daño **sí
  cambia** (hoy +1 por nivel; con FUE de guerrero 4 + 2 por nivel, +2 en nivel 1 y +11 en nivel 10).
  Es un reequilibrio explícito, no un accidente: hay que repasar los PV de los orcos (§3.1 muestra la
  tabla).
- `ServerConfig.PlayerMaxHealth`, `PlayerAttackDamage/Range/CooldownSeconds` dejan de ser la verdad:
  quedan como valores de reserva (clase desconocida, pruebas antiguas) y el ataque sin arma pasa a
  definirse por clase (§3.3).

### 2.5 Datos nuevos (esbozo de `classes.json`)

```json
{
  "classes": [
    {
      "id": 1, "name": "Guerrero",
      "base":   { "str": 4, "dex": 3, "int": 1, "vit": 10 },
      "growth": { "str": 2, "dex": 1, "int": 0, "vit": 3 },
      "baseDefense": 0,
      "resource": { "name": "Furia", "base": 100, "perInt": 0, "regenPerSecond": 2 },
      "weaponTypes": ["sword", "axe"],
      "unarmed": { "damage": 10, "range": 1.2, "cooldownSeconds": 0.8, "scaling": "str" }
    }
  ]
}
```

Reglas de validación: ids únicos y > 0, atributos 0–1000, `weaponTypes` conocidos, rangos como en los
demás JSON del servidor (mismo estilo de errores que `levels.json`).

---

## 3. Clases

### 3.1 Qué las distingue

Nivel 1 → nivel 10 (con las fórmulas de §2; DPS = daño por golpe / recarga con el arma inicial
propuesta):

| | Guerrero | Ranger | Mago |
| --- | --- | --- | --- |
| Papel | Cuerpo a cuerpo, resistente | A distancia física, móvil | A distancia mágico, frágil |
| Atributo principal | FUE | DES | INT |
| Base FUE/DES/INT/VIT | 4 / 3 / 1 / 10 | 2 / 6 / 2 / 8 | 1 / 3 / 8 / 6 |
| Crecimiento por nivel (suma 6) | +2 / +1 / +0 / +3 | +0 / +3 / +1 / +2 | +0 / +1 / +3 / +2 |
| Vida nivel 1 → 10 | 100 → 235 | 90 → 180 | 80 → 170 |
| Armas permitidas | espada, hacha | arco | bastón |
| Alcance de ataque | ~1,2 (cuerpo a cuerpo) | ~7 tiles | ~6 tiles |
| Arma inicial (ejemplo) | espada: 14 daño, 0,8 s | arco: 12 daño, 1,0 s | bastón: 12 daño, 1,2 s |
| Daño nivel 1 → 10 | 16 → 25 (DPS 20 → 31) | 15 → 28 (DPS 15 → 28) | 16 → 29 (DPS 13 → 24) |
| Recurso | Furia (100) | Energía (100) | Maná (60 + 4 × INT) |

Orientación de equilibrio: el guerrero hace más DPS y aguanta más pero tiene que estar encima del
orco; el ranger y el mago hacen menos DPS a cambio de alcance. Lo que haga justo al mago (daño de
área, control) llegará con las habilidades. Con estos números un guerrero de nivel 1 mata un orco
(30 PV) en 2 golpes (hoy 3); si no se quiere, se suben los PV de los orcos, que es solo JSON.

### 3.2 Crecimiento

Crecimiento **fijo por clase** (columna «+X por nivel»), no aleatorio. Se define en `classes.json`
(`growth`). Es lo que sustituye a `healthPerLevel/damagePerLevel`.

### 3.3 Armas permitidas y ataque a distancia

**Armas por clase.** `items.json` gana en las armas `weaponType` (`sword`, `axe`, `bow`, `staff`…) y
`scaling` (`str`/`dex`/`int`), y opcionalmente `requiredLevel`. `Equip` rechaza un arma cuyo tipo no
esté en `weaponTypes` de la clase (motivo nuevo en `RejectReason`/respuesta de equipar). El botín no
se filtra por clase: cualquiera puede recoger cualquier arma (y venderla/intercambiarla más adelante).

**Ataque sin arma.** Cada clase define `unarmed` (daño, alcance, recarga, escalado), así el mago y el
ranger nunca están indefensos ni tienen que «golpear con los puños» a distancia. Es la forma de que
hoy un mago nuevo tenga un ataque válido antes de existir cualquier bastón.

**Cómo es un ataque a distancia en el servidor** (hoy no existe; `Attack(targetId)` ya sirve):

- **Opción A — Impacto instantáneo (*hitscan*) con comprobación de línea de visión y evento
  cosmético. — Recomendada.**
  `Attack(targetId)` no cambia. `CombatSystem` ya valida distancia y recarga; solo hay que subir el
  máximo de alcance (hoy 5) y, si el alcance > cuerpo a cuerpo, comprobar que no hay pared entre
  ambos con `MapCollisionGrid`. Para que el cliente dibuje la flecha/bola sin saber nada de
  física, un mensaje nuevo `CombatEvent` (atacante, objetivo, daño, tipo de ataque) se manda a los
  jugadores que ven a atacante u objetivo; sirve también para números de daño flotantes.
  Pros: no hay entidades nuevas, ninguna carrera de red, determinista y barato (los bots cuestan lo
  mismo que hoy). Contras: un proyectil no se puede esquivar ni bloquear; sin daño de área real
  hasta tener habilidades; el proyectil visual puede llegar «tarde» respecto al golpe.
- **Opción B — Proyectiles reales** (entidad `Projectile` en el snapshot que se mueve y colisiona).
  Pros: se pueden esquivar, base para áreas y habilidades con trayectoria. Contras: aparecen entidades
  efímeras en snapshot (más bytes), colisión en el tick, interpolación en el cliente, mucho más que
  probar; es un cambio grande para tres clases sin habilidades todavía.
- **Opción C — Hitscan ahora, proyectiles reales solo para las habilidades que los necesiten luego.**
  Es la evolución natural de A: A no cierra la puerta a B.

**Recomendada: A (con C como ruta futura).** Es lo mínimo para que el ranger y el mago existan sin
tocar el snapshot ni la IA.

Efectos colaterales a repasar en esa tarea: la IA de los orcos hoy persigue al agresor (aggro por
daño) y vuelve a casa por `leashRadius` (8): un atacante a 7 tiles puede dejarlos en bucle de
persecución/regreso; probablemente baste con comprobar `leashRadius` frente al alcance máximo. El
radio de interés (`InterestRadius`) debe ser mayor que el alcance máximo para que el objetivo exista
para el cliente.

### 3.4 Habilidades: ¿desde el inicio o más adelante?

- **A — Sin habilidades al principio; solo ataque básico + estadísticas + armas. — Recomendada.**
  Pros: las clases ya se sienten distintas por alcance/atributos; el equilibrio es manejable; el
  protocolo de habilidades (barra, `UseSkill`, cooldowns, costes) se diseña después con datos reales
  de uso. Contras: durante unas tareas el mago y el ranger solo «pegan de lejos».
- **B — 1 habilidad por clase desde el nivel 1.** Pros: el recurso sirve para algo ya. Contras:
  obliga a hacer a la vez UI de barra, mensaje nuevo y efectos (daño de área, curación, estado) para
  3 clases.
- **C — Habilidades completas (3–5 por clase, desbloqueo por nivel).** Contras: es un proyecto
  entero; riesgo alto de rehacer combate.

**Recomendada: A**, con el hueco ya previsto: recurso y regeneración existen desde la tarea de
clases, y `skills.json` (id, clase, nivel requerido, coste, recarga, alcance, efecto) llega como
bloque propio después de armas, distancia y armadura.

### 3.5 Cómo se elige la clase

Hoy el personaje se crea en el primer login con el nombre del `Hello`.

- **A — Añadir `classId` al `Hello` (protocolo v11); solo se usa al crear el personaje. — Recomendada.**
  Si el personaje no existe y no llega clase válida, el servidor responde `Reject(ClassRequired)`
  y el cliente muestra la pantalla de elección y vuelve a conectar con la clase. Si el personaje ya
  existe, `classId` se ignora (no se puede cambiar de clase). Pros: cambio pequeño, mismo handshake
  cifrado, reutiliza la validación y el flujo de rechazo. Contras: el cliente necesita saber las
  clases *antes* de conectar (ver nota abajo); una cuenta = un personaje por nombre, sin gestión de
  varios.
- **B — Mensaje `CreateCharacter` tras autenticar y `Welcome` solo cuando existe personaje.**
  Pros: permite varios personajes por cuenta y pantalla de selección real. Contras: hay que partir el
  handshake en dos fases (estado «autenticado sin personaje»), el hilo de login y la sesión única por
  cuenta se complican; es más trabajo del que la clase necesita.
- **C — Todos nacen guerreros y se cambia de clase después.** Pros: cero trabajo ahora. Contras:
  rehace persistencia y balance cuando llegue el cambio; el usuario quiere elegir al crear.

**Recomendada: A.** B queda como evolución si algún día hay varios personajes por cuenta.

Nota sobre el catálogo: la pantalla de elección aparece antes del `Welcome`, o sea, antes de recibir
ningún catálogo. Solución propuesta: los ids de clase son una enumeración fija en `Avelora.Shared`
(1 guerrero, 2 ranger, 3 mago) y el cliente asocia nombre/icono/descripción por ScriptableObject
(visual). Los números (atributos, vida, daño) los manda el servidor después con un mensaje `Stats`.
Si no se quiere ese acoplamiento, la alternativa es que `Reject(ClassRequired)` vaya seguido de un
`ClassCatalog` (más protocolo; opción menos recomendada).

**Otros jugadores:** el snapshot ya tiene `TypeId` por entidad; para `EntityKind.Player` puede llevar
el `classId` y el cliente dibuja el sprite correcto sin ampliar el snapshot (comprobar que hoy no se
usa).

---

## 4. Progresión

### 4.1 Puntos: automáticos o repartidos

- **A — Automáticos por clase (los valores de `growth`). — Recomendada para v1.**
  Pros: nada que equilibrar ni explicar al jugador, cero UI de reparto, cero riesgo de personajes
  «rotos»; los atributos son derivables (no hace falta persistirlos). Contras: poca personalización:
  dos guerreros del mismo nivel son iguales salvo por equipo.
- **B — Totalmente manuales** (p. ej. 5 puntos por nivel). Pros: personalización máxima. Contras:
  obliga a UI, mensaje `AllocatePoints`, validación, reespecialización y mucho equilibrio desde el día 1.
- **C — Híbrido: crecimiento automático + 1–2 puntos libres por nivel.** Pros: personalización sin
  perder el eje de la clase. Contras: todo lo de B en pequeño.

**Recomendada: A ahora, C después si se quiere.** Se deja preparado sin coste: `levels.json` puede
tener `freePointsPerLevel: 0` desde la primera tarea y la columna `characters.free_points` + los
repartidos (`alloc_str`…`alloc_vit`) se añaden en la migración de esa futura tarea; la fórmula de §2.2
ya los suma.

### 4.2 Nivel máximo

- **A — Dejar 10** (lo que hay). Pros: sin trabajo; el contenido actual (2 enemigos, un mapa) no
  da para más. Contras: llegaremos a él enseguida con tres clases.
- **B — Subir a 20.** Pros: margen para armas/armaduras con `requiredLevel`. Contras: hay que añadir
  tabla de XP y contenido para llenarlo.
- **C — 30–50.** Contras: sin contenido que lo justifique ahora.

**Recomendada: A (10) mientras no haya contenido; subirlo es solo editar JSON** (`experienceToNext` y
el crecimiento por clase siguen siendo lineales, sirven para cualquier nivel hasta el límite de 254
de la tabla).

### 4.3 Reespecialización y cambio de clase

- Con puntos automáticos **no hay nada que reespecializar** (recomendado hoy).
- Cambio de clase: no se permite en v1 (la clase se fija al crear; borrar y crear otro personaje es la
  vía).
- Si llega el híbrido (C de §4.1): reespecialización por objeto de un solo uso o gratis hasta cierto
  nivel; se decide entonces.
- La muerte sigue sin quitar experiencia (como en I-06). Se decidirá cuando haya economía.

---

## 5. Profesiones (esbozo; se implementarán mucho después)

Solo para no cerrar puertas; nada de esto entra en el plan de tareas de §7.

**Cuáles (propuesta inicial):**

- *Recolección:* minería, herboristería, tala (y pesca más adelante).
- *Producción:* herrería (armas y armaduras de metal), carpintería/arquería (arcos, bastones),
  alquimia (pociones), cocina (comida con bonus temporal).
- *Posible:* encantamiento (bonus de atributos a objetos).

**Qué producen y cómo tocan lo ya existente:**

- Los **materiales** ya existen (`kind: "material"`, p. ej. colmillo de orco 3001); se añaden menas,
  hierbas, madera… como más objetos de `items.json`. Las **pociones** ya existen: alquimia las fabrica.
- Las **armas y armaduras** fabricadas son objetos normales de `items.json`, con `weaponType`,
  `defense`, bonus de atributo y `requiredLevel`. Las profesiones no necesitan un sistema de objetos
  propio.
- Datos nuevos de servidor: `professions.json` (nombre, niveles), `recipes.json` (ingredientes →
  producto, profesión y nivel requeridos, estación necesaria) y puntos de recolección en `npcs.json` o
  un archivo de mapa (un tipo de entidad «nodo de recurso» que se agota y reaparece, como el
  respawn de PNJ).
- Persistencia: tabla `character_professions(character_id, profession_id, level, experience)`; el
  inventario y su límite de huecos no cambian.
- Protocolo: mensajes `Gather(targetId)` y `Craft(recipeId)`, y un catálogo de recetas. Ambos pasan por
  el mismo patrón «pedir / el servidor decide» de `PickUp` y `UseItem`.

**Cómo se aprenden (decisión futura):**

- **A — Libres: el jugador sube cualquiera, con un máximo de 2 principales + secundarias. — Recomendada.**
  Pros: no depende de la clase; da identidad al jugador. Contras: hay que fijar el límite.
- **B — Atadas a la clase.** Contras: el arte y la economía se acoplan a las tres clases.
- **C — Todas a la vez sin límite.** Contras: nadie tiene que comerciar.

Subida por *uso* (cada recolección o fabricación da experiencia de profesión), separada de la
experiencia de combate.

**Economía:** hoy no hay moneda ni intercambio. Las profesiones solo cobran sentido con (a) un
sumidero/valor de objetos (vender a PNJ o entre jugadores) y (b) objetos con requisitos de nivel/clase.
Conviene decidir moneda y comercio antes de empezar profesiones, no antes de clases.

---

## 6. Impacto técnico

### 6.1 Protocolo (hoy v10)

Cada subida solo si cambia el formato (regla vigente). Cambios previstos, uno por tarea:

| Versión | Cambio | Tarea |
| --- | --- | --- |
| v11 | `Hello` gana `classId` (u8); `Reject(ClassRequired)` y rechazo de clase inválida; mensaje `Stats` (S→C: clase, FUE/DES/INT/VIT efectivos, defensa, daño de ataque, recurso actual y máximo); `TypeId` de jugador = `classId` (sin cambio de formato del snapshot). | I-09 |
| v12 | `ItemCatalog` gana `weaponType`, `scaling`, `requiredLevel` por arma; rechazo de `Equip` por clase/nivel. | I-10 |
| v13 | Mensaje `CombatEvent` (S→C) para ataques a distancia y daño flotante. El `Attack` no cambia. | I-11 |
| v14 | Equipo por huecos: `Inventory` pasa de un `equippedItemId` a una lista por hueco; `Equip` indica hueco de equipo; `ItemCatalog` gana tipo `armor` con defensa y bonus. | I-12 |

(Si se prefiere menos saltos, I-10 y I-11 pueden compartir versión; lo que no conviene es mezclar
cambios de protocolo con la fórmula de combate en la misma tarea.)

### 6.2 SQLite (hoy v3)

| Versión | Cambio | Tarea |
| --- | --- | --- |
| v4 | `ALTER TABLE characters ADD COLUMN class_id INTEGER NOT NULL DEFAULT 1` (los personajes existentes son guerreros). La columna `mana` se **reutiliza** como «recurso» (si se prefiere un nombre honesto, renombrarla en la misma migración: `ALTER TABLE … RENAME COLUMN`, soportado por SQLite ≥ 3.25). | I-08 |
| v5 | Tabla `character_equipment(character_id, slot, item_id)`; se copia `equipped_item_id` a `slot = weapon` y se deja de usar la columna (SQLite no hace borrado de columnas barato; se ignora o se recrea la tabla). | I-12 |
| futuras | `free_points` y repartidos (si se hace C de §4.1); `character_professions`. | después |

Mismo patrón que ahora: cada migración en su transacción, cadena v1→v2→v3→v4…, corrección y aviso al
cargar valores inválidos (clase que ya no existe → guerrero; recurso por encima del máximo → recorte).
Hay que añadir una prueba de migración con una base v3 con personajes reales.

### 6.3 Datos JSON del servidor

- **Nuevo `classes.json`**: atributos base/crecimiento, defensa base, recurso, tipos de arma, ataque
  sin arma (§2.5). Se carga y valida como `levels.json`; ruta en `server.json` (`classesPath`).
- **`levels.json`**: se queda con `experienceToNext` (y `freePointsPerLevel` opcional); se retiran
  `healthPerLevel`/`damagePerLevel`.
- **`items.json`**: armas ganan `weaponType`, `scaling`, `requiredLevel` (opcional); aparece
  `kind: "armor"` con `slot`, `defense`, bonus de atributo (I-12). El límite de alcance (≤ 5) sube
  (~10) para armas a distancia.
- **`npcs.json`**: `defense` opcional (0 por defecto).
- **Más adelante:** `skills.json`, `professions.json`, `recipes.json`.
- El servidor seguirá siendo la autoridad y mandando lo que el cliente necesita (`ItemCatalog`,
  `Stats`). Las copias que usa la carga (`tools/carga/*.json`) se mantienen sincronizadas como hoy.

### 6.4 Cliente (lo hace Claude, tareas «b»)

Selección de clase en el primer login, panel de atributos, barra de recurso, sprites por clase
(placeholder hasta que existan), proyectil cosmético con `CombatEvent`, números de daño, panel de
equipo con huecos. El cliente no calcula nada de lo anterior.

### 6.5 Bots y pruebas

- **Bots:** `Hello` con clase (parámetro `-Class` o reparto entre las tres); `fight` respeta
  `range`/recarga de la clase y, para las de distancia, se queda a ~70 % del alcance; métricas por
  clase (golpes, muertes, nivel máximo).
- **Pruebas xUnit:**
  - Datos: cada campo inválido de `classes.json`; clase desconocida; armas con `weaponType` no
    declarado.
  - Fórmulas: tabla fija nivel 1/5/10 por clase para vida, daño, recurso; mitigación con defensa 0,
    20, 100; mínimo 1; sin desbordes en nivel y atributos máximos.
  - Combate: rechazo de arma por clase y por nivel; alcance a distancia; pared entre atacante y
    objetivo; recarga con arma de distancia; PNJ con `defense`.
  - Persistencia: migración v3→v4 con datos existentes; clase fuera de rango al cargar; reconexión
    inmediata (`TryGetLatest`) conserva clase y recurso.
  - Protocolo: ida y vuelta de `Hello` con clase, `Stats`, `CombatEvent`; rechazo de longitudes.
  - Red: sin clase → `Reject(ClassRequired)`; con clase → `Welcome` y luego `ItemCatalog`, `Inventory`,
    `Progress`, `Stats` en orden.
- **Carga:** `.\tools\carga\carga.ps1 -Bots 50 -Seconds 120 -Behavior fight -LogCorrections -NuevaBase`,
  comparando tick p99 (I-07 `fight`: 0,090 ms) y bytes (26,9 MB). `CombatEvent` añade tráfico: medirlo.

---

## 7. Plan por tareas (revisado con las decisiones de §0)

Formato y reglas heredados de `docs/agentes/tareas/` (`I-xx.md`): base, protocolo, «no toques Unity»,
informe en `docs/agentes/informes/I-xx.md` desde el principio, `dotnet build` con 0 advertencias,
`dotnet test` en verde, carga con `-LogCorrections`. Los agentes no hacen commits. Cada tarea tiene su
versión «b» de cliente (la escribe Claude). Las versiones de protocolo/esquema son orientativas: se
asignan por orden real; regla vigente, una subida por tarea que cambie el formato.

Las dos primeras son server-only y no cambian el protocolo; así el riesgo de datos se retira pronto.

### I-08 — Clases, atributos y nivel 25 en el servidor (esquema v4, sin protocolo)

- **Alcance:** `classes.json` con las **cuatro** clases (guerrero, mago, ranger, sanador); atributos
  derivados (`base de clase + puntos repartidos + equipo`); fórmulas únicas de §2.3; `class_id` y
  columnas de puntos repartidos (`alloc_str/dex/int/vit`) en `characters`; puntos libres **derivados**
  (`puntosPorNivel × nivel − repartidos`, sin columna propia); `levels.json` pasa a 24 umbrales (nivel 25) y
  gana `pointsPerLevel`, pierde `healthPerLevel/damagePerLevel`; recurso persistido en `mana`;
  `npcs.json` gana `defense`; mitigación activa (defensa 0 = igual que hoy); los personajes existentes
  pasan a guerrero con todos sus puntos sin gastar; `classes.json` es una lista abierta de clases base
  (añadir una es solo datos, §0.11); puntos por nivel: 3 (provisional); defensa y resistencias
  (físico/mágico) según §0.11; `npcs.json` gana `level` y la experiencia se escala por diferencia de
  nivel; curva de 24 umbrales de §0.11.
- **Fuera:** repartir puntos (cliente), elegir clase, armas por clase, distancia, equipo, habilidades.
- **Verificación:** tablas de fórmulas por clase; migración v3→v4 con datos reales; carga sin
  correcciones y p99 comparable a I-07.

### I-09 — Repartir puntos y mensaje `Stats` (protocolo v11)

- **Alcance:** `AllocatePoints(str, dex, int, vit)` validado en el servidor (suma ≤ puntos libres, vivo
  o no, atómico); `Stats` (clase, atributos efectivos, puntos libres, defensa, daño, recurso); reajuste
  `ResetPoints` (gratis hasta nivel 10; después consume la «piedra de reajuste», §0.10); bots que
  reparten según su clase.
- **Fuera:** UI, selección de clase.
- **Verificación:** rechazos (exceso de puntos, valores negativos, spam), persistencia y reconexión.
- **I-09b (Claude):** panel de atributos con botones + y confirmar.

### I-10 — Tres personajes por cuenta (protocolo v12, esquema v5)

- **Alcance:** el login pasa a dos fases (ver §0.3): `Hello` sin personaje → `CharacterList` →
  `CreateCharacter(nombre, clase)` / `DeleteCharacter(id)` / `SelectCharacter(id)` → `Welcome`; máximo 3
  por cuenta; nombres únicos globalmente, sin distinguir mayúsculas, elegidos por el jugador y con las
  reglas de §0.10 (índice nuevo; las bases existentes se revisan por duplicados antes de migrar; un
  duplicado se resuelve renombrando con sufijo y se avisa); borrado con confirmación y borrado lógico; la sesión única por cuenta se mantiene;
  `TypeId` de jugador = `classId`; bots que crean y eligen personaje.
- **Fuera:** UI, cambiar de clase, transferir objetos entre personajes.
- **Verificación:** pruebas de máquina de estados (elegir sin crear, crear el cuarto, borrar el
  seleccionado, reconectar a mitad), migración y carga.
- **I-10b (Claude):** pantalla de selección/creación con **cuadrados de color**: guerrero rojo, mago
  azul, ranger verde, sanador blanco (ScriptableObject visual por `classId`).

### I-11 — Penalización de experiencia por muerte a manos de un mob (sin protocolo nuevo)

- **Alcance:** si el jugador muere por un PNJ, pierde un porcentaje de la experiencia del nivel actual
  (propuesta: 10 % del umbral al siguiente, mínimo 0, **nunca baja de nivel**); se avisa con `Progress`;
  muerte por jugador o sin causa no penaliza; configurable en `levels.json`.
- **Fuera:** pérdida de objetos, durabilidad, reaparición diferente.
- **Verificación:** pruebas de mínimo en 0, nivel máximo, muerte por PNJ frente a PvP.

### I-12 — Armas por clase y requisitos (protocolo v13)

- **Alcance:** `weaponType`, `scaling`, `requiredLevel` en `items.json`/`ItemCatalog`; `Equip` rechaza
  por clase y nivel; armas de ejemplo para las cuatro clases (espada/hacha, arco, bastón y maza/cetro
  del sanador); `handedness` (`one`/`two`) y bloqueo de `offhand` (§0.10); los tipos de arma y los
  permitidos por clase son solo datos, para añadir bárbaro o paladín sin tocar `Equip`.
- **Verificación:** pruebas de `Equip` y catálogo; carga.

### I-13 — Ataque a distancia con línea de visión y `CombatEvent` (protocolo v14)

- **Alcance:** igual que el borrador (§3.3): alcance máximo de armas sube, línea de visión con
  `MapCollisionGrid`, `CombatEvent`, revisión de la IA de PNJ con atacantes lejanos, bots a distancia.
- **Verificación:** pruebas de pared/alcance/recarga; carga sin PNJ atrapados.
- **I-13b (Claude):** proyectil cosmético y números de daño.

### I-14 — Equipo de 10 huecos y armadura (protocolo v15, esquema v6)

- **Alcance:** tabla `character_equipment(character_id, slot, item_id)`; huecos de §0.5; `kind:
  "armor"` con `slot`, `defense` y bonus de atributo; `Inventory` con lista de huecos; migración de
  `equipped_item_id`; `ItemCatalog` troceado si hace falta.
- **Fuera:** durabilidad, conjuntos, encantamientos, comercio.
- **Verificación:** migración con arma equipada; equipar/desequipar por hueco; mitigación; reconexión.
- **I-14b (Claude):** panel de equipo.

### I-15 - Zonas del mapa: seguras y PvP (protocolo v19)

- **Alcance:** `zones.json` (A de §0.12) con validación, `ZoneMap.KindAt(x, y)` precalculada, reglas de
  zona `safe` (sin daño entre jugadores, sin mobs ni ataques a mobs dentro, IA que se detiene en el
  borde), mensaje `ZoneChanged` y lista de zonas en el `Welcome`; el mapa de prueba gana una ciudad
  segura y una zona PvP de ejemplo; métricas de ataques rechazados por zona.
- **Fuera:** PvP (I-15p), editor visual de zonas (opción B), tiendas/PNJ de ciudad.
- **Verificación:** pruebas de solape, bordes, reaparición dentro, mapa sin zonas; carga con bots que
  entran y salen de la ciudad.
- **I-15b (Claude):** rótulo «Zona segura / Zona PvP» y zonas en el minimapa.

### I-15p - Combate entre jugadores (PvP) (protocolo v20)

- **Alcance:** sobre las zonas de I-15 y la marca personal (§0.10): `SetPvpMark(on)`;
  `Attack` admite jugadores como objetivo solo si ambos están en zona PvP y marcados; la marca solo se quita en zona segura (§0.11); sin XP ni penalización de XP por muerte entre jugadores; la reaparición de
  I-03 se mantiene; el estado de marca viaja en el snapshot o en un mensaje propio (decidir en la
  tarea, mirando el coste en bytes).
- **Fuera:** facciones, grupos, gremios, reputación, **pérdida de objetos** (explorar tras el MVP).
- **Verificación:** pruebas de reglas, bots de dos bandos.

### I-16 — Habilidades: base y daño de objetivo único (protocolo v17)

- **Alcance:** `skills.json`; `SkillCatalog`; `UseSkill(skillId, targetId)`; coste de recurso, recarga
  en ticks, requisito de nivel y de clase; **una** habilidad de daño de objetivo único por clase para
  validar el sistema.
- **Fuera:** áreas, curas, estados, habilidades finales.

### I-17 — Áreas y curación (protocolo v18)

- **Alcance:** efecto de área centrado en el objetivo con radio; curación de aliado (ver §0.6: quién es
  aliado); sanador funcional; mago con área.
- **Verificación:** pruebas de radio, filtrado de objetivos, límites por tick.

### I-18 — Estados temporales (escudo, ralentización, provocar, bonus)

- **Alcance:** efectos con duración medida en ticks (modificador de atributo, absorción, ralentización,
  provocar a PNJ), aplicados y expirados en el servidor; `Stats` los refleja. **Sin desplazamientos
  forzados ni saltos** en el MVP: chocan con la predicción y reconciliación del movimiento.
- **Verificación:** expiración, acumulación, muerte limpia estados.

### I-19 — Cuatro habilidades por clase (3 principales + 1 final) y reequilibrio

- **Alcance:** 16 habilidades en `skills.json` con los efectos disponibles; la final se desbloquea
  hacia el nivel 20 (propuesta: 1, 5, 10 y 20); pasada de equilibrio y bots por clase.
- **b:** barra de habilidades, recargas, iconos provisionales de color.

Fuera de este plan (contenido, no sistemas): enemigos para los niveles 1–25 (hoy hay solo dos tipos),
más mapas, botín de armaduras y armas, y las **profesiones** (§5), que no entran antes de las
anteriores. Se proponen más habilidades solo después del MVP.

Orden: I-08 → I-09 → I-10 → I-11 → I-12 → I-13 → I-14 → I-15 → I-16…I-19. I-10 (tres personajes) es la de
mayor riesgo por tocar el handshake; conviene hacerla antes de acumular más datos por personaje.

---

## 8. Preguntas abiertas (tras la tercera ronda)

Lo importante está decidido (§0). Quedan valores por confirmar; el plan sigue sirviendo con estos
valores por defecto si no se contesta:

1. **Puntos por nivel:** 3, provisional hasta cerrar la fórmula de stats (§0.11): es tarea de diseño, no pregunta.
2. **Habilidad final:** nivel 20 (decidido).
3. **Marca PvP:** solo se quita en zona segura (decidido; §0.11).
4. **Clases futuras (bárbaro, paladín…):** **fuera del MVP** (decidido). Solo se dejan las bases: clases, armas, habilidades y equipo son datos y ninguna regla del código nombra una clase concreta. Sin preguntas pendientes.
5. **Curva de experiencia y dificultad:** propuesta razonada en §0.11; se ajusta con contenido real.
6. **Zonas:** decidido (§0.12): ciudades y zonas concretas, definidas con rectángulos en `zones.json`
   (mi decisión; el pintado en Unity queda para después). Los mobs no entran en zonas seguras y su aggro
   se cancela en cuanto el jugador entra en una (decidido). Sin preguntas pendientes sobre zonas.
