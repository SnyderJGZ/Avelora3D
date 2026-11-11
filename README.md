# Avelora — núcleo del servidor autoritativo (C#)

Implementación por etapas del *plan del servidor* de Avelora: un MMORPG 2D con **servidor
autoritativo en C# (.NET)** que comparte con el cliente de Unity el código que ambos deben
calcular igual (movimiento y colisión).

> **Estado: Etapas F, G e I-03c cerradas, Etapa H en curso; I-04 a I-30 de servidor implementadas (I-19 añade habilidades y protocolo v25; cliente I-30b pendiente).** El servidor guarda **cuentas y personajes en
> SQLite** detrás de `IPlayerRepository`, y el login ya es real: **PBKDF2-HMAC-SHA256 con 600 000
> iteraciones**, registro con **código de invitación** y límites de intentos por cuenta. Desde H-02
> (**protocolo v4**) la contraseña y la invitación viajan **cifradas con RSA** contra un reto de un
> solo uso; al entrar, el personaje aparece en la posición que tenía guardada si el mapa coincide.
> Desde I-01 el mundo tiene enemigos que el servidor mueve y todos ven; desde I-02 (**protocolo v5**)
> esos enemigos tienen vida, reciben los ataques de los jugadores, mueren y reaparecen. Desde I-03
> persiguen y golpean al jugador, y el jugador tiene vida, muerte y reaparición. Desde I-03c
> (**protocolo v6**) el snapshot lleva el tipo de cada PNJ (2001, 2002…) y el tope baja a 67 entidades.
> Desde I-04 (**protocolo v7**) hay botín en el suelo, recogida, inventarios persistentes y pociones;
> desde I-05 (**protocolo v8**) hay armas, equipo persistente y catálogo de objetos enviado al cliente.
> Desde I-06 (**protocolo v9**) las muertes de PNJ dan experiencia y suben niveles; I-08 añade clases,
> atributos, defensa y nivel máximo 25 con **esquema SQLite v4**, sin cambiar el protocolo. La vida y el
> daño dependen de la clase y de los puntos repartidos, no crecen automáticamente al subir nivel.
> Desde I-07 (**protocolo v10**) hay chat básico de zona con validación UTF-8 y límite de ritmo por jugador.
> Desde I-09 (**protocolo v11**) los jugadores reparten atributos y pueden reajustarlos con la piedra 3201.
> Desde I-14 (**protocolo v12**, esquema v5) hay 10 huecos de equipo, armaduras e inventario interactivo.
> Desde I-20 los PNJ pueden aparecer con rangos definidos por datos, con multiplicadores de estadísticas
> y tiradas adicionales de botín. I-21 fijó el protocolo en **v14**: el catálogo de objetos se
> reparte entre varios mensajes y tirar un objeto lo destruye. I-10 sube el protocolo a **v15** y el
> esquema SQLite a **v6**: el saludo autentica la cuenta y, después, cada conexión lista, crea, borra o
> selecciona uno de hasta 10 personajes con su clase. I-12 sube el protocolo a **v16** con restricciones
> de armas por clase/nivel y armas a dos manos; I-13 sube a **v17** por `CombatEvent` y añade ataques
> a distancia con línea de visión. I-25 sube a **v18** con botín y oro y migra SQLite al esquema **v7**;
> I-15 sube a **v19** con zonas normales, seguras y PvP anunciadas por mensajes propios.
> I-17 añade áreas y curación: **protocolo v22, esquema v8**. I-18 añade estados temporales:
> **protocolo v23**, sin cambiar el esquema.
> I-28 añade crítico, prisa, daño PvP y recarga global (protocolo v24); I-19 amplía `SkillCatalog` y sube a **v25**, sin cambiar el esquema.
> I-15p añade combate entre jugadores y modo PvE/PvP: **protocolo v20, esquema v8**.
> Ver [Etapa H](#qué-hace-la-etapa-h), [Etapa I](#qué-hace-la-etapa-i),
> [I-03c](#tipo-de-entidad-en-el-snapshot-i-03c-protocolo-v6), [I-04](#botín-e-inventario-i-04),
> [I-05](#armas-y-equipo-i-05-protocolo-v8), [I-06/I-08](#experiencia-niveles-y-clases-i-06-i-08),
> [I-07](#chat-i-07-protocolo-v10), [I-09](#reparto-y-reajuste-de-atributos-i-09-protocolo-v11),
> [I-14](#equipo-e-inventario-i-14-protocolo-v12-esquema-v5),
> [I-20](#mobs-elite-y-campeon-i-20-protocolo-v13), [I-21](#catalogo-troceado-y-fuera-dropitem-i-21-protocolo-v14),
> [I-10](#personajes-por-cuenta-i-10-protocolo-v15-esquema-v6), [I-11](#penalizacion-por-muerte-i-11),
> [I-12](#armas-por-clase-y-requisitos-i-12-protocolo-v16),
> [I-13](#ataque-a-distancia-y-combatevent-i-13-protocolo-v17),
> [I-15](#zonas-seguras-y-pvp-i-15-protocolo-v19) e
> [I-25](#botín-directo-dueño-del-botín-y-oro-i-25-protocolo-v18-esquema-v7).
>
> Si la misma cuenta entra dos veces, la sesión anterior recibe `Reject(LoggedInElsewhere)` y se
> cierra con un **retardo** para que el mensaje llegue. El guardado corre en su propio hilo —al
> desconectar, cada minuto y al apagar—, así que el tick nunca espera a SQLite.

| Comprobación | Resultado |
| --- | --- |
| `dotnet build Avelora.slnx -v q --nologo` | **0 errores, 0 advertencias** |
| `dotnet test Avelora.slnx` | **803/803 en verde** |
| Compilación en Unity 6 (cliente de F-02) | **0 errores, 0 avisos** |
| Escena `AveloraEtapaA` en Play con servidor | login con `dev`, posición guardada entre sesiones y expulsión `LoggedInElsewhere` |

![Etapa D en Unity: jugador local en el muro y dos remotos interpolados](docs/capturas/etapa-d.png)

> La captura es de la Etapa D. La Etapa E no cambia la vista por defecto (radio amplio); solo
> recorta los snapshots cuando el radio se reduce.

---

## Requisitos

- **.NET SDK 10** para `shared`, `server`, `tools` y las pruebas.
- **Unity 6** (`6000.6.5f1`) para el cliente. El proyecto de Unity activo es
  `E:\Unity\Nueva carpeta\My project (1)` (plantilla **2D / URP**, **Input System**, **Tilemap**).

No hace falta Unity para compilar lo compartido ni para ejecutar las pruebas.

---

## Estructura del repositorio

```
Avelora/
├─ shared/Avelora.Shared/           # Código compartido (netstandard2.1, C# 9, sin dependencias)
│   ├─ Avelora.Shared.asmdef        #   Ensamblado de Unity con «No Engine References»
│   ├─ SimulationConstants.cs       #   Tick rate y dt: el paso que comparten cliente y servidor
│   ├─ EntityKind.cs                #   Tipo de entidad (viaja en el snapshot)
│   ├─ Core/Vec2.cs                 #   Vector 2D (tipo de valor)
│   ├─ Core/MapCollisionGrid.cs     #   Matriz de colisión de tiles + AABB
│   ├─ Core/MovementSimulator.cs    #   Paso de movimiento (cliente y servidor)
│   ├─ Core/SequenceMath.cs         #   Comparación de secuencias con vuelta de uint
│   ├─ Maps/MapFile.cs              #   Formato binario .avmap y huella SHA-256
│   ├─ Prediction/                  #   Predicción y reconciliación del cliente
│   │   ├─ ClientPrediction.cs      #     Inputs pendientes, replay y corrección suave
│   │   └─ SnapshotTimeline.cs      #     Historial e interpolación de entidades remotas
│   └─ Protocol/                    #   Mensajes binarios: tipos, códec y cuantización
├─ shared/Avelora.Net/              # Transporte (netstandard2.1) sobre LiteNetLib 1.3.5
│   ├─ Avelora.Net.asmdef           #   Ensamblado de Unity (sin referencias a UnityEngine)
│   ├─ TransportContracts.cs        #   IServerTransport / IClientTransport (sin tipos de LiteNetLib)
│   ├─ LiteNetLibServerTransport.cs #   Servidor UDP
│   └─ LiteNetLibClientTransport.cs #   Cliente UDP (bots y cliente de Unity)
├─ server/Avelora.Server/           # Servidor headless (net10.0)
│   ├─ Program.cs                   #   Monta zona + red y arranca el bucle (Ctrl+C para parar)
│   ├─ server.json                  #   Configuración externa (tick, red, límites, rutas)
│   ├─ npcs.json                    #   Tipos (vida y reaparición) y puntos de aparición (I-01/I-02)
│   ├─ Simulation/                  #   Bucle, zona, movimiento, colas de entrada, área de interés
│   ├─ Net/                         #   Sesiones, handshake, entrada y snapshots filtrados
│   ├─ Entities/                    #   Entity, PlayerEntity, Id, almacén y grilla espacial
│   ├─ Metrics/                     #   Duración de tick: media, p99, sobrepasos
│   └─ Diagnostics/                 #   Informes por consola
├─ tools/Avelora.Bots/              # Clientes de consola por UDP (net10.0)
├─ maps/avelora-etapa-a.avmap       # Mapa exportado del Tilemap (contenido, se versiona)
├─ client/Assets/Avelora/           # Cliente Unity (fuente de verdad, se sincroniza)
│   ├─ Scenes/AveloraEtapaA.unity   #   Escena de prueba versionada (Player con NetworkClient)
│   ├─ Scripts/Game/                #   AveloraMap, LocalPlayerController, TilemapCollision,
│   │                               #   NetworkClient, RemotePlayerView, KeyboardDirection
│   ├─ Plugins/LiteNetLib.dll       #   Transporte UDP para Unity (netstandard2.1, 1.3.5)
│   ├─ Editor/                      #   Exportador del Tilemap a .avmap
│   └─ Art/, Tiles/                 #   Sprites y tiles de la escena (versionados)
├─ tests/Avelora.Tests/             # xUnit: geometría, mapa, servidor, protocolo, predicción y UDP
├─ tools/sync-shared-to-unity.ps1   # Copia shared + client al proyecto de Unity
└─ docs/capturas/                   # Capturas de la verificación (etapa-a.png, etapa-d.png)
```

`Avelora.Shared` es **netstandard2.1 y C# 9 a propósito**: Unity consume los mismos fuentes y el
servidor los referencia como ensamblado. **No tiene ninguna dependencia** (ni Unity ni NuGet), para
que copiarlo a Unity sea copiar archivos. Su `Avelora.Shared.asmdef` marca **«No Engine
References»**: si alguien escribe `using UnityEngine` ahí, Unity no compila. `Avelora.Net` sí
referencia LiteNetLib, pero la simulación no la conoce: solo ve las interfaces de transporte. La
lógica de predicción e interpolación también vive en `Avelora.Shared/Prediction` (sin Unity), para
poder probarla con xUnit; `NetworkClient` solo la conecta con el transporte y con el `Transform`.

---

## Puesta en marcha

### Backend y pruebas

```powershell
# Desde la raíz del repositorio
dotnet build Avelora.slnx -v q --nologo
dotnet test  Avelora.slnx
```

### Servidor

```powershell
# Arranca y se para con Ctrl+C. Escucha en UDP 7777.
dotnet run --project server/Avelora.Server

# Durante N segundos (útil para pruebas automáticas) u otra configuración
dotnet run --project server/Avelora.Server -- --seconds 15
dotnet run --project server/Avelora.Server -- mi-configuracion.json
```

La configuración (`server.json`) se copia junto al ejecutable; si no existe, el servidor escribe
una con los valores por defecto. La ruta del mapa (`maps/avelora-etapa-a.avmap`) se busca junto a
la configuración, en el directorio actual y junto al ejecutable.

### Bots (clientes por UDP)

Con el servidor en marcha, en otra consola:

```powershell
# Un bot durante 10 s
dotnet run --project tools/Avelora.Bots

# Tres bots, 15 s, contra otro puerto
dotnet run --project tools/Avelora.Bots -- --bots 3 --seconds 15
dotnet run --project tools/Avelora.Bots -- --host 127.0.0.1 --port 7777 --bots 10
```

Cada bot hace el handshake real, manda una intención cada 50 ms y lee snapshots. Al final imprime su
entidad, los snapshots recibidos y dónde acabó.

### Cliente Unity

1. Arranca el servidor (`dotnet run --project server/Avelora.Server`).
2. Abre el proyecto en Unity 6 y la escena **`Assets/Avelora/Scenes/AveloraEtapaA.unity`**.
3. Pulsa **Play**. El `NetworkClient` del `Player` hace el handshake contra
   **127.0.0.1:7777** (los mismos valores de `server.json`).
4. Muévete con **WASD** o las **flechas**. El overlay muestra estado, entidad, inputs pendientes,
   snapshots, correcciones y saltos; los demás jugadores aparecen como cuadrados con otro tinte.

**Controles:** movimiento en ocho direcciones. La diagonal está normalizada (no es más rápida).
Si el servidor no está levantado, el cliente muestra «sin respuesta»/«desconectado» y **R**
reintenta la conexión. Para jugar sin red (solo Etapa A), desactiva `connectOnStart` en el
inspector: el `LocalPlayerController` sigue funcionando como antes.

> ⚠️ **Editor sin foco:** Unity pausa el modo Play cuando el Editor pierde el foco si
> *Run In Background* está desactivado. Para pruebas automatizadas, actívalo
> (`Application.runInBackground = true` o *Edit → Project Settings → Player*). Si se pausa, el
> servidor puede cerrar la conexión por timeout; al volver, el cliente lo indica y **R** reconecta.

---

## Qué hace la Etapa A

### `Vec2`
Vector 2D `readonly struct`. No asigna en el heap (importante dentro del bucle de ticks), con
suma, resta, escala, longitud y `Normalized()`.

### `MapCollisionGrid`
Matriz de tiles con celdas libres/bloqueadas:

- **Fuera de la rejilla cuenta como bloqueado**, para que nadie salga del mapa por un borde.
- **`IsBoxWalkable`** comprueba **todos** los tiles que toca la caja del personaje, no solo su
  punto central (el error clásico que deja «colarse» por las esquinas).
- Las consultas usan **coordenadas de tile absolutas**: el origen de la rejilla se resta dentro,
  así un Tilemap con celdas negativas funciona sin que cliente ni servidor desplacen nada.
- `Overlaps` para contacto/alcance entre dos cajas (AABB).

### `MovementSimulator`
El paso de movimiento compartido:

- El **`dt` es constante** (1 / tick rate); el cliente nunca aporta tiempo.
- La **velocidad la impone el servidor**; el cliente nunca la envía.
- La **diagonal se normaliza aquí**, así que moverse en diagonal no es más rápido.
- Se mueve **cada eje por separado**: el personaje **se desliza** por las paredes en vez de
  quedarse pegado.
- `IsValidDirection` valida que la dirección sea −1, 0 o 1.

### Formato de mapa `.avmap`
El servidor nunca lee escenas de Unity: la colisión se **exporta desde el Tilemap** a este
archivo binario, y cliente y servidor cargan el mismo.

```
Offset  Tamaño  Campo
  0       4     Magic "AVMP"
  4       2     FormatVersion (ushort, LE) = 1
  6       2     Flags (reservado = 0)
  8       4     Width  (int32, LE)
 12       4     Height (int32, LE)
 16       4     OriginX (int32, LE)
 20       4     OriginY (int32, LE)
 24       8     reservado (= 0)
 32       N     Payload: 1 bit por tile, fila a fila, LSB-primero   (N = ceil(W·H / 8))
 32+N    32     SHA-256 de (cabecera + payload)
```

- **Portable:** enteros en little-endian escritos a mano (no `BitConverter`, que depende de la
  máquina).
- **Verificable:** el SHA-256 final detecta archivos truncados o corruptos al cargar.
- **Comparable:** `MapFile.ComputeContentHashBytes(grid)` da los 32 bytes que viajan en el saludo;
  `ComputeContentHash` da la misma huella en hexadecimal.
- Límite defensivo de 16.7 M de celdas (`MaxTiles`).

API: `MapFile.Save/Load` (disco) y `MapFile.Serialize/Deserialize` (memoria).

### Unity: simulación a paso fijo
`LocalPlayerController` no usa `FixedUpdate` ni la física de Unity: acumula `Time.deltaTime` y
ejecuta **pasos fijos de 1/20 s** con `MovementSimulator`, igual que el tick del servidor.
El dibujo interpola entre los dos últimos pasos (guía del núcleo §5.1).

`AveloraMap` construye la rejilla desde el Tilemap al arrancar (misma función que el exportador:
`TilemapCollision.Build`) y registra la huella del mapa en la consola.

---

## Qué hace la Etapa B

### El bucle: un hilo, paso fijo

`ServerCore` ejecuta un hilo dedicado (`avelora-sim`) con un reloj monótono (`Stopwatch`):

1. Mide cuánto ha pasado de verdad.
2. `FixedStepClock` decide **cuántos pasos de 50 ms** tocan (acumula los restos en `double`, no los
   pierde; en `float` acabarían comiéndose ticks).
3. Ejecuta esos pasos midiendo cada uno.
4. Si no toca ninguno, duerme 1 ms lejos del objetivo y **gira** los últimos 3 ms.

El paso **nunca depende del reloj**: a 20 Hz cada tick avanza exactamente 1/20 s, igual que la
predicción del cliente. El `dt` vive en `SimulationConstants` (librería compartida) y
`ServerConfig.Validate` **rechaza arrancar** con otro tick rate.

Tope anti-espiral: si el proceso se queda sin CPU 5 s, **no** recupera 100 ticks de golpe; descarta
el exceso y lo cuenta en `Metrics.DiscardedMs`.

### Entidades

- `Entity` (Id, tipo, mapa, posición, caja, celda espacial) y `PlayerEntity` (personaje, conexión,
  último input aplicado, cola de inputs).
- `EntityIdAllocator`: Ids crecientes que **nunca se reutilizan** (si un Id volviera a repartirse
  enseguida, un cliente podría confundir la entidad nueva con la vieja).
- `EntityStore`: diccionario Id → índice **y** lista densa. Borrar es O(1) (*swap-remove*) y
  recorrer es recorrer solo entidades vivas. El tick itera **por índice**, no con `foreach` sobre
  una interfaz, para no reservar memoria en cada pasada.

### De la red al tick, sin bloqueos

- **Solo el hilo de simulación toca el mundo**, así que dentro del tick no hay ningún `lock`.
- **Un input = un paso.** El cliente predice un paso por input, así que el servidor debe aplicar
  exactamente uno; si no, la reconciliación corregiría en cada snapshot. **Sin input, el jugador no
  se mueve.**
- Direcciones válidas: solo −1, 0 y 1, validadas en la frontera, antes de llegar a la simulación.
- Secuencias repetidas o viejas se descartan comparando con la última **aceptada**; el exceso por
  encima del margen también, y se cuenta. Un cliente no puede acelerar inundando de inputs.
- La cola de entrada tiene **tope**: si se llena se descarta, en vez de crecer sin límite.

### Métricas

`TickMetrics` guarda los últimos N ticks en un búfer circular y calcula **media, p99, peor tick y
cuántos se pasaron de 50 ms**; el informe añade recolecciones de GC y memoria. El p99 (que ordena)
se calcula al informar, no al simular.

Los informes leen `Zone.Players`, una **copia inmutable publicada al final de cada tick**: así el
hilo de consola no recorre la lista de entidades mientras la simulación la modifica.

### Verificación de la Etapa B

| Prueba | Resultado |
| --- | --- |
| `dotnet test` (48 de la Etapa A + 67 nuevas) | **115/115** |
| 12 s de simulación a 20 Hz | **240 ticks**; desviación **−0.26 %** |
| Duración de tick | media **0.010 ms**, p99 **0.053 ms**, peor **1.84 ms** |
| Ticks por encima de 50 ms | **0** (y **0 ms** descartados por el anti-espiral) |
| Recolecciones de basura durante 240 ticks | **0 / 0 / 0** |
| Mapa cargado por el servidor | 19×13, origen (−9,−6), huella `24ec4c86…` (la misma del cliente) |
| Cliente Unity, `SimulateSteps` contra el tabique | **x = −0.50**, el mismo punto que usa la prueba del servidor |

> El antiguo «jugador simulado» de la Etapa B (que inyectaba inputs por código) **se ha retirado**:
> ahora la única forma de entrar al mundo es la real, por UDP, con los bots.

---

## Qué hace la Etapa C

### Transporte

UDP con **LiteNetLib 1.3.5**, encerrada en `shared/Avelora.Net` detrás de dos interfaces:

| Interfaz | Quién la usa |
| --- | --- |
| `IServerTransport` | `NetworkServer` (y un transporte falso en las pruebas) |
| `IClientTransport` | `Avelora.Bots` hoy; el cliente de Unity en la Etapa D |

La simulación no conoce LiteNetLib ni UDP: solo llama a `InputQueue.TryEnqueue` y llena una cola de
salida. Cambiar de transporte es cambiar una clase en `Program.cs`.

- **LiteNetLib no es segura entre hilos.** Todo ocurre en un único hilo de red (`avelora-net`), que
  bombea `PollEvents` cada milisegundo y vacía la cola de salida. Los envíos también se hacen ahí.
- La conexión lleva una **clave** (`avelora-v1`); no es un secreto, solo evita que un escaneo de
  puertos abra sesiones. La autenticación real (cuenta y contraseña) está desde la Etapa F; el
  cifrado del transporte queda para la H.

### Protocolo binario

Un byte de tipo, después los campos. Enteros little-endian escritos a mano y `float` por sus bits
(ida y vuelta exacta). Todo vive en `Avelora.Shared/Protocol`, así que cliente y servidor **no
pueden discrepar** del formato.

> El plan (§4) sugería usar el escritor binario de la librería de red. Se escriben a mano por una
> razón de arquitectura: `Avelora.Shared` debe seguir **sin dependencias** para que Unity compile
> esos mismos fuentes sin añadir LiteNetLib. El formato es idéntico de cara al juego (little-endian,
> longitudes conocidas) y mover el códec a `NetDataWriter` más adelante no cambiaría los bytes.

| Tipo | Byte | Dirección | Tamaño | Contenido |
| --- | --- | --- | --- | --- |
| `Hello` | 1 | C → S | 299 B + cuenta | versión (u16), tick (u16), token (u32), huella del mapa (32 B), credenciales cifradas (256 B) y cuenta (longitud u16 + UTF-8); no incluye personaje |
| `MoveInput` | 2 | C → S | 7 B | secuencia (u32), dirX (i8), dirY (i8) |
| `Attack` | 3 | C → S | 5 B | objetivo (i32) |
| `Welcome` | 10 | S → C | 69 B | versión, tick, entidad (i32), tick servidor (u32), token, aparición (f32×2), velocidad (f32), medio (f32×2), huella (32 B) |
| `Reject` | 11 | S → C | 2 B | motivo (u8) |
| `Snapshot` | 12 | S → C | 11 B + 15 B/entidad | tick (u32), último input aplicado (u32), N (u16); por entidad: id (i32), x/y (i16), dirección (i8×2), kind (u8), vida (u8), acción/rango (u8; I-20), TypeId (u16) |
| `PickUp` | 4 | C → S | 5 B | id de entidad del objeto (i32) |
| `UseItem` | 5 | C → S | 2 B | índice del hueco (u8) |
| `Inventory` | 14 | S → C | 26 B + 4 B/hueco | número de huecos (u8), itemId y cantidad (u16 cada uno; 0/0 = vacío), 10 ids equipados (u16) y oro (u32) |
| `Equip` | 6 | C → S | 2 B | hueco que contiene el arma (u8) |
| `Unequip` | 7 | C → S | 3 B | hueco equipado y destino del inventario |
| `ItemCatalog` | 15 | S → C | 19 B + 37 B/objeto + tipo y nombre | reglas de puños y recogida; objeto: estadísticas, manos, nivel, permiso de clase, `weaponType` UTF-8 (≤16 B) y nombre (≤48 B) |
| `Progress` | 16 | S → C | **14 B** | nivel (u8), experiencia (u32), umbral siguiente (u32; 0 en el máximo), vida máxima (u32) |
| `ChatSend` | 8 | C → S | 3 B + texto | texto UTF-8 con longitud u16; máximo 120 bytes |
| `Chat` | 17 | S → C | 9 B + nombre + texto | emisor (i32), nombre UTF-8 (≤24 B) y texto UTF-8 (≤120 B), ambos con longitud u16 |
| `AllocatePoints` | 9 | C → S | **5 B** | puntos a añadir a FUE, DES, INT y VIT (u8 cada uno) |
| `Stats` | 18 | S → C | **41 B** | clase/nivel (u8), atributos efectivos y repartidos, puntos libres, vida, daño/tipo, defensa/resistencia, crítico y prisa (pb), recurso y reajuste gratis |
| `ResetPoints` | 19 | C → S | **1 B** | sin cuerpo; devuelve todos los puntos repartidos |
| `EntityKind.Item` | 2 | dentro de `Snapshot` | 1 B | objeto en el suelo; `TypeId` contiene `itemId` |
| `CreateCharacter` | 23 | C → S | 4 B + nombre | nombre UTF-8 (≤32 B) y `classId` (u8) |
| `DeleteCharacter` | 24 | C → S | 7 B + nombre | `characterId` (i32) y nombre UTF-8 de confirmación |
| `SelectCharacter` | 25 | C → S | 5 B | `characterId` (i32) |
| `CharacterList` | 26 | S → C | variable, ≤1400 B | máximo por cuenta, personajes (id, nombre, clase, nivel) y clases disponibles (id, nombre) |
| `CharacterRejected` | 27 | S → C | 2 B | motivo de rechazo de crear, borrar o seleccionar personaje (u8) |
| `CombatEvent` | 28 | S → C | 2 B + 11 B/evento | atacante (i32), objetivo (i32), daño mitigado (u16), banderas `Ranged`/`Magical`/`Killed`/`Heal`/`Absorbed`/`Critical` (u8); máximo 64 |
| `LootGained` | 29 | S → C | 10 B + 5 B/objeto | experiencia (u32), oro (u32), N (u8); itemId (u16), cantidad (u16), bandera `OnGround` (u8); máximo 32 |
| `ZoneList` | 30 | S → C | 2 B + 4 B/zona + 8 B/rectángulo | N (u8); id (u16), tipo (u8), R (u8); x/y/w/h (i16); máximo 32 zonas y 128 rectángulos (1154 B) |
| `ZoneChanged` | 31 | S → C | 4 B | id de zona (u16) y tipo (u8); id 0 solo con `Normal` |
| `SetPvpMode` | 32 | C → S | 2 B | modo elegido: PvE (0) o PvP (1) |
| `PvpStatus` | 33 | S → C | 8 B | modo, forzado, fase de cambio y segundos restantes |
| `PvpModeRejected` | 34 | S → C | 2 B | `NotInSafeZone` o `TooSoon` |
| `UseSkill` | 35 | C → S | 7 B | habilidad y objetivo |
| `SkillCatalog` | 36 | S → C | variable | habilidades de la clase y recurso |
| `SkillUsed` | 37 | S → C | 7 B | uso aceptado y objetivo |
| `SkillRejected` | 38 | S → C | 4 B | habilidad y motivo |
| `ResourceStatus` | 39 | S → C | 5 B | recurso actual y máximo |
| `StatusEffects` | 40 | S → C | 3 B + 7 B/estado | velocidad (%), cantidad (u8); tipo (u8), habilidad, ticks restantes y cantidad (u16); solo al dueño, máximo 4 |
| `StatusEvent` | 41 | S → C | 2 B + 5 B/entidad | cantidad (u8); entidad (i32) y máscara (u8); cambios de entidades visibles, máximo 64 |

Canales:

- **Fiable y ordenado:** `Hello`, `Welcome`, `Reject`, `Attack`, `PickUp`, `UseItem`, `Equip`,
  `Unequip`, `ChatSend`, `AllocatePoints`, `ResetPoints`, `ItemCatalog`, `Inventory`, `Progress`,
  `Stats`, `Chat`, `CreateCharacter`, `DeleteCharacter`, `SelectCharacter`, `CharacterList`,
  `CharacterRejected`, `LootGained`, `ZoneList`, `ZoneChanged`, `SetPvpMode`, `PvpStatus`, `PvpModeRejected`, `SkillCatalog`, `SkillUsed`, `SkillRejected`, `ResourceStatus`, `StatusEffects` y `StatusEvent`.
- **No fiable y secuenciado:** `MoveInput` y `Snapshot`. Si un paquete se pierde, el siguiente lo
  reemplaza; no se reintenta.
- **No fiable y sin secuencia compartida:** `CombatEvent`. Es cosmético y perderlo no altera el daño
  ni la vida del snapshot.

> El **protocolo v5** (I-02) añade `Attack` y dos bytes por entidad en el snapshot (`HealthPercent` y
> `EntityAction`). La versión sube siempre que cambie el formato, aunque el mensaje sea nuevo.
> I-03c añadió `TypeId` al snapshot y subió a v6; I-04 sube a **v7** por `EntityKind.Item` y los mensajes
> `PickUp`, `UseItem` e `Inventory`; I-05 sube a **v8** por `Equip`, `Unequip`, `ItemCatalog` y el campo
> `equippedItemId` al final de `Inventory`; I-06 sube a **v9** por `Progress`; I-07 sube a **v10** por
> `ChatSend` y `Chat`; I-09 sube a **v11** por `AllocatePoints`, `ResetPoints` y `Stats`; I-14 sube a **v12** por `MoveItem`,
> `DropItem`, `DestroyItem`, el destino en `Unequip`, los 10 huecos de `Inventory` y las armaduras de
> `ItemCatalog`; I-20 sube a **v13** al empaquetar el rango en el byte de acción del snapshot e I-21 a
> **v14** al retirar `DropItem` y repartir `ItemCatalog` en mensajes de hasta 1400 B. I-10 sube a
> **v15** al quitar el personaje del `Hello` e incorporar el flujo de selección y sus cinco mensajes; I-12 sube a **v16** al añadir manos, nivel, permiso por clase y `weaponType` a cada objeto de `ItemCatalog`; I-13 a **v17** por `CombatEvent`; I-25 a **v18** al añadir oro al inventario y `LootGained`; I-15 sube a **v19** por `ZoneList` y `ZoneChanged`.
> I-17 sube a **v22** por los campos de área, curación y objetivo; I-18 sube a **v23** por estados temporales y sus mensajes. El esquema sigue en v8.
> I-28 sube a **v24** por `Stats` (crítico y prisa) y `CombatEventFlags.Critical`; I-19 sube a **v25** por la recarga global y los campos `hits`, `areaCenter` y `critBonus` de `SkillCatalog`.
> I-15p sube a **v20** por los tres mensajes PvP y el bit 6 del snapshot (PvP efectivo, solo jugadores); el bit 7 sigue reservado.
> El tamaño de `EntityState` se mantiene en 15 B.

### Handshake

```
cliente                                   servidor
   │  conexión UDP (clave "avelora-v1")      │
   │ ──────────────────────────────────────► │  ¿aforo? ¿clave?
   │  Hello(versión, tick, huella, token)    │
   │ ──────────────────────────────────────► │  valida versión, tick y mapa
   │  Welcome(entidad, aparición, reglas)    │
   │ ◄────────────────────────────────────── │  crea la entidad en el tick
   │  MoveInput(seq, dx, dy) a 20 Hz         │
   │ ──────────────────────────────────────► │  InputQueue → tick
   │  Snapshot(tick, último input, entidades)│
   │ ◄────────────────────────────────────── │  20 por segundo
```

- El cliente **no manda posición ni tiempo**: solo la dirección que quiere tomar.
- Si la versión, el tick o la huella no coinciden, recibe `Reject` con el motivo y se cierra la
  conexión. Un cliente con otro mapa no llega a jugar.
- Si no saluda en `handshakeTimeoutSeconds` (5 s por defecto), se le cierra.
- El servidor impone **velocidad y caja de colisión** en el `Welcome`: el cliente predecirá con los
  mismos números (Etapa D).
- Al llenarse la zona, el siguiente recibe `Reject(ServerFull)`; por eso el transporte admite más
  conexiones que jugadores.

> Desde el **protocolo v4** el servidor manda primero un `Challenge` y cifra las credenciales; desde
> I-10 (`v15`) el `Hello` ya no lleva personaje. Tras autenticarse, el servidor envía `CharacterList`
> y solo crea una entidad de juego después de `SelectCharacter`.

### Los dos hilos

```
hilo de red (avelora-net)                        hilo de simulación (avelora-sim)
  Poll() ─ Hello ─────► valida ─┐
  Poll() ─ MoveInput ─► InputQueue ──────────────► BeforeTick: altas y bajas
                                                  Zone.Tick: 1 input = 1 paso
  envía ◄── cola de salida ◄────────────────────── AfterTick: un snapshot por sesión
```

- El hilo de red **nunca toca entidades**; el de simulación **nunca toca el socket**.
- Conexión → entidad se guarda en un diccionario concurrente que escribe la simulación y lee la red.
  Una lectura obsoleta solo encola un input para una entidad ya borrada: la zona lo descarta y lo
  cuenta (`ajena` en el informe).
- La cola de salida tiene tope. Si se llena, se tiran snapshots (el siguiente los reemplaza), nunca
  mensajes fiables.

### Snapshots y cuantización

- Un snapshot por tick y por cliente (20/s), con las entidades del **área de interés** de ese
  cliente. El jugador local entra siempre; los demás, si están dentro del radio. La grilla
  espacial y la histéresis de la Etapa E son las que recortan la lista.
- Las posiciones viajan como `short` en unidades de **1/64 de tile** (≈ 1,5 cm): la mitad que un
  `float` y de sobra para dibujar. La posición autoritativa del servidor sigue siendo `float`. La
  cuantización introduce como mucho `MaxQuantizationError` (1/128 de tile), el margen mínimo que
  deberá tolerar la reconciliación de la Etapa D para no vibrar.
- Ese `short` cubre **±512 tiles**. El mapa admite hasta 4096×4096, pero una entidad fuera de ese
  radio se saturaría en el borde; con un área de interés razonable (el defecto es 12 tiles) no se
  llega a ese extremo. En el mapa actual (19×13) no aplica.
- Cada cliente recibe `lastProcessedInputSequence`, la base de la reconciliación de la Etapa D. Para
  comparar secuencias de 32 bits se usa `SequenceMath.IsNewer`, que resta y mira el signo: así la
  vuelta de `uint` (cada 6,8 años a 20 Hz) no bloquea los inputs para siempre.
- Tope de mensaje: **1400 bytes**. Un snapshot con 77 entidades (el máximo) ocupa 1012 y cabe en el
  paquete máximo del canal secuenciado de LiteNetLib (1020 B, sin fragmentos); con 78 serían 1025 y
  se rechazaría. Un mensaje mayor se rechaza sin decodificar.

### Límites y validación en la frontera

| Defensa | Valor por defecto |
| --- | --- |
| Conexiones simultáneas | 16 (10 jugadores + margen para rechazar con motivo) |
| Tamaño máximo de mensaje | 1400 B |
| Mensajes por conexión y segundo | 90; por encima se descarta, a 3× el tope se desconecta; 5 s por encima en el mismo minuto también desconecta (H-02) |
| Cola de entrada | 4096; se descarta si se llena |
| Cola de salida | 512; se tiran snapshots si se llena |
| Saludo sin completar | 5 s |
| Dirección de movimiento | solo −1, 0 o 1 (se valida en el códec, en la red y en la cola) |
| Secuencia de input | repetida o vieja: se descarta (comparación con vuelta de `uint`) |
| Punto de aparición | se comprueba al arrancar (`Program`) y al crear la entidad (`Zone`); si está en una pared, no se juega |
| Enumeraciones del cable | `EntityKind`, `EntityAction` y `RejectReason` desconocidos se rechazan |

Un paquete con tipo desconocido, truncado, de dirección inválida, con una enumeración inventada o por
encima del tope **no llega a la simulación**: el códec devuelve `false` y se cuenta como rechazado.

> Queda para las etapas siguientes: límite de conexiones por IP (Etapa H), cifrado del transporte y
> registro por evento de cada validación fallida (hoy se cuentan por informe). Los intentos de login
> por minuto ya existen desde la F (`maxFailedLoginsPerAccountPerMinute`). LiteNetLib puede añadir
> cifrado por conexión más adelante.

### Verificación de la Etapa C

| Prueba | Resultado |
| --- | --- |
| `dotnet test` (48 A + 67 B + 29 C + 16 de la revisión + 19 D + 15 E + 22 F) | **216/216** (hoy; al cerrar la C eran 160) |
| Prueba de punta a punta por UDP (socket efímero) | handshake, inputs y snapshots, con la posición moviéndose a la derecha |
| Servidor 16 s + 3 bots 9 s | **321 ticks** (desviación **−0.01 %**) |
| Duración de tick con red | media **0.041 ms**, p99 **0.809 ms**, peor **5.454 ms** |
| Ticks por encima de 50 ms / descartados | **0 / 0 ms** |
| Recolecciones de basura | **0 / 0 / 0** |
| Bots | **3/3** conectados; **178** snapshots y **178** inputs cada uno |
| Mensajes rechazados / saludos rechazados / limitados / cortes / cola | **0 / 0 / 0 / 0 / 0** |
| Altas y bajas | **3 y 3** (al desconectar, la entidad desaparece del mundo) |

Las pruebas de red cubren además: versión distinta, mapa distinto, saludo mal formado, conexión sin
saludo, zona llena, dirección inválida, límite de mensajes (descartar y cortar por abuso) y snapshot
con posición cuantizada. La revisión añade pruebas de ida y vuelta del códec con direcciones y
enumeraciones fuera de rango, un barrido de **todos** los valores definidos de `EntityKind` y
`RejectReason` (para que añadir uno sin tocar el códec falle), comparación de secuencias con vuelta de
`uint`, formato de `Vec2` independiente de la cultura, y validación del punto de aparición en la
configuración **y** en `Zone.SpawnPlayer` contra el mapa real.

---

## Qué hace la Etapa D

### Predicción del jugador local

El cliente no espera al servidor para moverse. Cada **paso fijo de 1/20 s** llama a
`ClientPrediction.AddInput(dx, dy)`, que aplica **el mismo** `MovementSimulator` con la velocidad,
la caja y el mapa que llegaron en el `Welcome`:

- **Un input = un paso**, igual que en el servidor. Se envía también `(0, 0)`: así la secuencia
  avanza y el servidor sabe que el jugador sigue vivo aunque esté quieto.
- La secuencia es un `uint` que empieza en 1; el 0 se reserva para «ninguna». La comparación usa
  `SequenceMath.IsNewer`, que soporta la vuelta del contador.
- **El cliente nunca manda posición, velocidad ni tiempo.** Solo `(secuencia, dirX, dirY)`; el
  servidor decide dónde está el personaje.
- El dibujo interpola entre los dos últimos pasos fijos (`PreviousPosition → Position`), como hacía
  la Etapa A: la simulación es discreta a 20 Hz, pero la imagen no da tirones.

### Reconciliación

Cada snapshot trae `lastProcessedInputSequence`, el último input que el servidor aplicó **a este
cliente**. Al recibirlo:

1. Se descartan los inputs pendientes con secuencia **≤** la confirmada.
2. Se reaplican los pendientes desde la **posición autoritativa** del snapshot. Si coincide con la
   que el cliente predijo para ese input (dentro de la tolerancia), se reaplica desde la **predicha
   exacta**: en una esquina, el redondeo puede sacar la caja de la pared y el reaplicado avanzaría
   donde el servidor chocó (I-03c).
3. Se compara la posición predicha con la recién calculada:
   - **Diferencia por eje ≤ 2 × `MaxQuantizationError` (1/64 de tile) + 1e-5:** es ruido de
     cuantización; se conserva la predicción y **no se corrige**. Son dos medios pasos: el del
     snapshot y el de una base cuantizada tras una corrección anterior. Si se corrigiera, el
     personaje vibraría.
   - **Diferencia pequeña (≤ 0.5 tile):** se acepta la posición del servidor y se guarda un
     **desplazamiento visual** que se desvanece a 10 tiles/s. El personaje llega a la verdad
     autoritativa en unas décimas de segundo sin que se note el tirón.
   - **Diferencia grande (> 0.5 tile):** se salta a la posición del servidor sin suavizar. Un salto
     grande suele ser una pared, un teletransporte o una trampa; disimularlo taparía el problema.

| Parámetro | Valor |
| --- | --- |
| Paso fijo | `SimulationConstants.FixedDt` = 0.05 s |
| Tolerancia de cuantización | 1/128 tile + 1e-5 |
| Velocidad de corrección visual | 10 tiles/s |
| Distancia de salto | 0.5 tile |
| Inputs pendientes máximos | 128 (6.4 s a 20 Hz) |
| Tope de frame del cliente | 0.25 s (5 pasos) |

> El servidor limita a **4 inputs por jugador y tick**. Si un parón del Editor hace que el cliente
> produzca 5 pasos en un frame, el quinto se descarta en el servidor (`tope_jugador` en el informe).
> La reconciliación lo absorbe como una corrección pequeña; en la prueba contra el muro no cambió
> la posición y las correcciones siguieron en 0.

### Interpolación de los demás

`SnapshotTimeline` guarda **1 s** de snapshots con la hora local a la que llegaron (no la del
servidor: el reloj del cliente no está sincronizado). Para dibujar a un remoto:

- Se usa `renderTime = ahora − 0.1 s` (dos snapshots a 20 Hz): siempre se interpola entre dos
  estados **conocidos**, nunca se enseña el último salto de red.
- Se buscan las dos muestras que rodean `renderTime`, se interpolan las posiciones y se toma la
  dirección y el tipo de la muestra más nueva.
- Los snapshots viejos o repetidos se descartan comparando `ServerTick` con `SequenceMath.IsNewer`.
- Si la entidad falta en una de las dos muestras (acaba de aparecer o de irse), se dibuja en la que
  sí existe; si ya no está en el último snapshot, se retira la vista.
- Si el render se adelanta al último snapshot, se **extrapola como máximo 0.1 s** y después se
  congela: mejor un personaje quieto un instante que uno que se teletransporta.

El jugador local **no** se dibuja desde el timeline: se dibuja desde su predicción reconciliada. Las
vistas remotas se crean en runtime con el mismo sprite, material y orden de dibujo del jugador
local, y se tiñen según `EntityKind` (jugador remoto o PNJ).

### Handshake y bucle del cliente

`NetworkClient` vive en el `Player`, junto a `AveloraMap` y `SpriteRenderer`:

1. `Start` desactiva el `LocalPlayerController` de la Etapa A y abre el transporte.
2. Al conectarse, manda `Hello(versión, tick, huella del mapa, token aleatorio)`.
3. Valida el `Welcome`: misma versión, mismo tick, **mismo token** y misma huella. De ahí saca su
   entidad, el punto de aparición, la velocidad y la caja.
4. En cada `Update`: `Poll()` del transporte, bucle de pasos fijos con input, envío de `MoveInput`,
   reconciliación de los snapshots recibidos y render.
5. Si recibe `Reject`, muestra el motivo en español y cierra; **R** reintenta.

LiteNetLib **no es segura entre hilos**: `Poll` y `Send` se hacen en el hilo principal de Unity,
dentro de `Update`. No hay hilos nuevos en el cliente.

El mapa de Unity expone `AveloraMap.ContentHashBytes` (los 32 bytes de
`MapFile.ComputeContentHashBytes`), que es exactamente lo que viaja en el `Hello` y lo que el
servidor compara con su `.avmap`.

### Qué no cambia

El **layout binario es el mismo de la Etapa C**: no se sube la versión del protocolo. Los mensajes,
la cuantización y los límites ya estaban preparados; la Etapa D solo añade la lógica del cliente.
El servidor sigue siendo la única autoridad: el cliente puede pedir direcciones, pero no imponer
posiciones, velocidad, tiempo ni saltos.

### Verificación de la Etapa D

| Prueba | Resultado |
| --- | --- |
| `dotnet test` Debug y Release | **179/179** al cerrar la D (hoy **216/216** con las 15 de la E y las 22 de la F; 19 nuevas de D: 11 de predicción/reconciliación y 8 de timeline) |
| Compilación en Unity 6 | **0 errores, 0 avisos** |
| Handshake real desde Unity | entidad asignada, huella del mapa correcta, **20 inputs/s** |
| Reconciliación con 4 186 inputs consecutivos (2 862 en la consulta) | **0 correcciones, 0 saltos**; predicción y autoridad a ≤ 1/128 tile |
| Muro interior | el jugador local y el servidor se detienen en **x = −0.5** (predicción y autoridad) |
| Remotos | **2 vistas** con sprite `Player_0`, tinte distinto, posiciones interpoladas (no múltiplos de 1/64) |
| Desconexión de bots | vistas retiradas: `RemoteViewCount = 0` |
| Servidor 240 s + cliente + 2 bots | **4 800 ticks** (−0.00 %), p99 **0.051 ms**, peor 5.883 ms, **0** sobre 50 ms, **0** GC |
| Red del humo de 240 s | 5 altas / 4 bajas, 9 092 snapshots, **0** rechazos, **0** limitados, **0** descartes de cola |
| Servidor 120 s + captura con remotos | **2 400 ticks** (−0.01 %), p99 **0.056 ms**, 5 altas / 2 bajas, **0** rechazos, **0** GC |

En el humo de 240 s aparecieron además **10 inputs descartados por el tope de 4/tick** y **1 mensaje
de una conexión ya cerrada** (`ajena`). Los inputs extra vienen de parones del Editor (el cliente
genera 4–5 pasos de golpe); el servidor los descarta y la reconciliación los absorbe. Ninguno
produjo una corrección en la prueba porque el jugador estaba contra el muro.

> **Reproducir la prueba:** servidor en una consola, Play en Unity con *Run In Background* activo y
> `dotnet run --project tools/Avelora.Bots -- --bots 2 --seconds 60` en otra. El overlay del cliente
> muestra la entidad, los pendientes, los snapshots, las correcciones y los saltos en tiempo real.

---

## Qué hace la Etapa E

### Grilla espacial

`EntitySpatialGrid` (`server/Avelora.Server/Entities/EntitySpatialGrid.cs`) indexa cada entidad en
una celda cuadrada y permite preguntar «qué hay a menos de X tiles» sin recorrer el mundo entero:

- El lado de la celda es configurable (`interestCellSize`, **8 tiles** por defecto).
- Las celdas se crean **bajo demanda** en un `Dictionary<long, List<Entity>>`; no se reserva una
  matriz para todo el mapa. Un mapa de 4096×4096 con diez jugadores solo ocupa las celdas que se
  usan.
- Cada entidad guarda su celda en `Entity.CellX` / `Entity.CellY`; `Entity.NoCell` significa «sin
  indexar». Mover una entidad es **O(1)**: se retira de la lista vieja y se añade a la nueva.
- `Query(x, y, radio, lista)` recorre solo las celdas que toca el cuadrado que envuelve al círculo y
  filtra por distancia al centro. La lista se **reutiliza** entre snapshots: no hay reservas en el
  camino caliente.
- En `Zone.Tick`, después del movimiento, se reindexa cada entidad que haya cambiado de celda. Es
  **O(n)** con n = entidades vivas; el tick no cambia de forma.

### Área de interés e histéresis

`InterestSystem` (`server/Avelora.Server/Simulation/InterestSystem.cs`) decide qué ve cada jugador:

1. El **jugador local siempre entra**, aunque su celda no exista o el radio sea mínimo.
2. Una entidad **nueva** entra si su centro está a `interestRadius` o menos.
3. Una entidad que **ya estaba visible** se conserva mientras no pase de
   `interestRadius + interestHysteresis`.
4. Al volver a entrar, tiene que hacerlo por el radio de entrada, no por el de salida.

El conjunto de visibles vive en `PlayerEntity.VisibleEntityIds` (estado por jugador, solo hilo de
simulación). Se reemplaza cada snapshot sin reservar otro `HashSet`. La histéresis evita el parpadeo
clásico de una entidad parada justo en el borde.

`Zone.CollectVisibleEntities(viewer, lista)` es la costura que usa la red: vacía la lista, pone al
jugador primero y añade los candidatos visibles. `NetworkServer.BuildSnapshot` la llama por sesión y
codifica solo esos estados.

### Snapshots, métricas y límites

- El snapshot ya era variable; **el layout binario no cambia** y la versión del protocolo sigue en
  **v1**. No hay un mensaje de «desaparece»: la entidad simplemente deja de venir y el cliente
  retira su vista.
- `NetworkStats` añade `EntitiesSent` (suma de entidades incluidas) y `SnapshotBytes` (bytes de
  snapshot generados). El informe muestra `ents=` y `bytes=` y el resumen los totaliza.
- El tope de **entidades por snapshot** sigue ahí: si una zona tuviera más, se recorta y se cuenta.
  El destinatario va primero, así que el recorte nunca lo elimina a él. Al recortar, los objetos del
  suelo pasan al final: se cae botín antes que un jugador o un enemigo. Hoy son **77** (I-02): es el
  máximo que cabe en los 1020 B de un paquete secuenciado de LiteNetLib.
- El cliente **no necesitó cambios**: `NetworkClient` retira una vista remota cuando su entidad deja
  de aparecer en el último snapshot y la vuelve a crear si regresa; `SnapshotTimeline` ya usa la
  muestra que existe cuando una entidad falta en una de las dos.

### Configuración

| Campo | Defecto | Significado |
| --- | --- | --- |
| `interestRadius` | **12 tiles** | Radio de entrada. Cubre de sobra la vista del mapa actual (19×13) y su media diagonal (≈ 11.5 tiles). |
| `interestCellSize` | **8 tiles** | Lado de celda de la grilla. |
| `interestHysteresis` | **1 tile** | Margen extra para conservar visible lo que ya lo estaba. |

Con el radio por defecto, el mapa actual cabe entero en la vista y la AOI casi no recorta. Para
verla en acción, baja el radio (por ejemplo, a **5** o **1**). Puedes copiar estos campos en
`server.json` o usar un archivo de configuración aparte con solo los cambios:

```json
{
  "interestRadius": 5,
  "interestCellSize": 2,
  "interestHysteresis": 1
}
```

### Verificación de la Etapa E

| Prueba | Resultado |
| --- | --- |
| `dotnet test` Debug y Release | **194/194** (15 nuevas: 11 de grilla/área de interés, 3 de snapshots filtrados y 1 de configuración) |
| Grilla | celdas positivas y negativas, mover/retirar, radio exacto, lista reutilizada y consulta sin duplicados |
| Área de interés | jugador local siempre incluido, entrada/salida, histéresis y retirada de entidades |
| Snapshots filtrados | dos jugadores lejos reciben solo su entidad; al acercarse aparecen ambos; a 2.4 tiles sigue visible por la histéresis y a 2.6 desaparece |
| Métricas | `EntitiesSent` y `SnapshotBytes` cuentan lo generado; sin truncamientos con el aforo actual |
| Servidor 30 s + 5 bots, radio 1 | **600 ticks** (−0.07 %), p99 **0.094 ms**, peor 8.523 ms, **0** sobre 50 ms, **0** GC |
| Red del humo de 30 s | **1 995** snapshots, **2 413** entidades (**1.21/snapshot**), 5 altas / 5 bajas, **0** rechazos, **0** limitados, **0** descartes de cola |
| Protocolo | **v1**, sin cambios en el layout binario |

En ese humo, los 5 bots empiezan juntos y se separan por la sala: con radio 1 la media de entidades
por snapshot baja de **1.79** a los 5 s a **1.21** al final, que es exactamente el efecto buscado.
Los **4** mensajes `ajena` del informe son inputs de una conexión ya cerrada; la zona los descarta y
no afectan al tick ni a los snapshots.

---

## Qué hace la Etapa F

### SQLite detrás de `IPlayerRepository`

`IPlayerRepository` (`server/Avelora.Server/Persistence/IPlayerRepository.cs`) es la única costura
que conoce la base de datos: `Initialize`, `Login`, `GetOrCreateCharacter` y `SaveCharacter`.
`SqlitePlayerRepository` la implementa sobre **un solo archivo** (`data/avelora.db` por defecto):

- Cada operación abre y cierra su propia conexión; SQLite serializa las escrituras y solo hay un
  hilo escritor, así que no compiten dos.
- Al abrir se activan `foreign_keys` y `busy_timeout = 5000`; el archivo se pone en modo **WAL**,
  para que un login no espere a que termine un guardado.
- El esquema se crea la primera vez y su versión vive en **`PRAGMA user_version`**
  (`SchemaVersion = 1`). Si el archivo trae otra versión, el servidor **no arranca** en vez de
  tocar tablas que no conoce.
- Con `persistenceEnabled: false` no se abre nada: el servidor vuelve al modo de las etapas
  anteriores, con identidad local y `firstCharacterId`.

### Cuentas y personajes

- Tabla `accounts`: id, nombre (único y sin distinguir mayúsculas), hash, sal, iteraciones y fecha
  de creación.
- Tabla `characters`: id, cuenta, nombre, nivel, experiencia, vida, maná, zona, `x`/`y`, huella del
  mapa y fechas. Hoy el nivel, la experiencia, la vida y el maná nacen en **1 / 0 / 100 / 100** y no
  cambian: todavía no hay niveles ni combate.
- Al entrar se reutiliza la posición guardada **solo** si es de la misma zona y del mismo mapa
  (`map_hash`) y la caja del jugador cabe ahí; si el mapa cambió o la posición quedó en una pared,
  se aparece en `spawnX`/`spawnY`.
- Las contraseñas se guardan con **PBKDF2-HMAC-SHA256**, sal aleatoria de 16 B y hash de 32 B, con
  **600 000 iteraciones** por defecto. Las iteraciones se guardan por cuenta (tope 1 000 000): si
  una cuenta antigua entró con menos de las configuradas, **se recalcula el hash al entrar** sin
  pedirle nada al jugador. La comparación es en tiempo constante.
- En la Etapa F la contraseña viajaba **sin cifrar**; desde H-02 va cifrada (ver Etapa H).

### Registro con código de invitación

- Si la cuenta no existe, se crea solo cuando el `invitationCode` del saludo coincide **exacto** con
  `registrationInvitationCode`. Si ese ajuste está **vacío, el registro está cerrado**.
- En cualquier otro caso se responde `Reject(InvalidCredentials)`, lo mismo que una contraseña
  incorrecta: así no se puede averiguar qué cuentas existen (H-01). `RegistrationClosed` sigue en
  el enum, pero el servidor ya no lo envía, y este fallo no gasta el cupo de intentos de ese nombre.
- Cada cuenta tiene como máximo **`maxCharactersPerAccount`** personajes (defecto 3, rango 1–10); los
  borrados no cuentan. Los límites de cuenta/contraseña siguen siendo 3–32 y 6–128 caracteres.
- La cuenta se autentica primero; el flujo posterior de lista, creación, borrado lógico y selección
  está documentado en [I-10](#personajes-por-cuenta-i-10-protocolo-v15-esquema-v6).

### Login en el hilo `avelora-login`

- El hilo de red solo valida lo barato (versión, tick, mapa, cuenta, límites) y encola un
  `LoginRequest`. `LoginWorker` lo procesa en su propio hilo, **`avelora-login`**: ahí se hace
  PBKDF2, se lee SQLite y se lista, crea, borra o carga el personaje solicitado.
- El resultado vuelve por una cola que el hilo de red vacía en `PollOnce`. Un login lento no frena
  ni el tick ni el socket.
- **`maxPendingLogins` (16)** es el tope de logins *en vuelo*: los que esperan en la cola **y** el
  que se está calculando. Al llenarse, el saludo se rechaza con `RateLimited` sin gastar PBKDF2.
  El hueco solo se libera cuando la red recoge el resultado, así que **desconectar no lo libera**.
- **`maxPendingLoginsPerIp` (2)** limita los logins *en vuelo* desde una misma IP. Al superar el
  tope, el saludo se rechaza con `RateLimited` sin gastar PBKDF2; el hueco se libera al recoger el
  resultado y desconectar no lo libera.
- **`maxFailedLoginsPerAccountPerMinute` (5)**: los `InvalidCredentials` se cuentan por cuenta y
  minuto. Agotado el tope, los siguientes saludos de esa cuenta se rechazan con `RateLimited` sin
  comprobar la contraseña. Al cambiar de minuto se tira la ventana anterior. Desde H-01 el nombre se
  compara **sin distinguir mayúsculas** y los saludos **en vuelo** también cuentan: seis saludos
  seguidos de la misma cuenta no se cuelan antes de que vuelva el primer resultado.

### Una sola sesión por cuenta

- La reserva es por **cuenta**, desde la selección y no solo desde el juego. Si la misma cuenta entra
  desde otra conexión, la nueva pasa a ser la dueña y la anterior recibe `Reject(LoggedInElsewhere)`;
  si estaba jugando, se guarda su estado y se retira su entidad.
- Si es el **mismo personaje**, la sesión nueva parte de la **última foto encolada** en
  `CharacterSaveService` (`TryGetLatest`): posición, vida e inventario vivos. Así no hereda lo que el
  login leyó de la base, que puede ir hasta un intervalo de guardado atrasado. Vale para la sesión
  que se acaba de echar y para salir y volver a entrar antes de que se escriba el guardado. Antes
  solo se conservaba la posición, y el siguiente guardado pisaba la vida y el inventario buenos.
- El `Reject` fiable no siempre llega si se corta enseguida: LiteNetLib manda el paquete de
  desconexión antes que el mensaje en cola. Por eso el servidor apunta la conexión y espera
  **`rejectDisconnectDelayMilliseconds` (500 ms)** antes de cerrarla, descartando mientras tanto lo
  que llegue de ella. El cliente de Unity ya muestra el motivo en vez de «el servidor cerró la
  conexión».

### Cuándo se guarda

- **Al desconectar**: se toma una foto del personaje y se encola; después la entidad sale del mundo.
- **Cada `saveIntervalSeconds` (60 s)**: `BeforeTick` encola una foto de todos los conectados, sin
  tocar el tick.
- **Al apagar**: tras parar la simulación, `SaveAll` vuelve a encolar y `Flush` espera hasta 5 s a
  que la cola termine. Si quedara algo pendiente, se avisa por consola. Desde H-01 también se apaga
  en orden al **cerrar la ventana** de la consola (`SIGHUP`) o con el apagado del sistema
  (`SIGTERM`); en ese caso `Flush` espera 3 s, porque Windows solo concede unos 5 s en total.
- **Si una escritura falla** (H-01), la foto vuelve a la cola y se reintenta al cabo de 1 s; si
  mientras tanto llega una foto más nueva del mismo personaje, gana la nueva.
- El guardado corre en el hilo **`avelora-save`** con una cola de un solo escritor. Si el mismo
  personaje se encola dos veces antes de escribirse, la **última foto reemplaza** a la anterior.
- Antes de encolar se crea un `CharacterRecord` nuevo (`WithPosition`), una **copia inmutable**:
  el hilo de guardado nunca lee datos que la simulación está moviendo. Los fallos de escritura se
  cuentan y se imprimen; el resumen muestra guardados y fallos.

### Cliente de Unity

- El cliente backend de Unity requiere I-10b: enviará cuenta, contraseña e invitación en el `Hello`,
  recibirá `CharacterList` y permitirá crear, borrar o seleccionar antes de recibir `Welcome`.
- La escena `AveloraEtapaA` conserva las credenciales de desarrollo (`accountName: dev`,
  `password: avelora-dev`, `invitationCode: avelora-dev`); su interfaz aún no implementa la selección.
- Los rechazos nuevos tienen texto propio: `LoggedInElsewhere` se muestra como «tu cuenta se ha
  conectado desde otro sitio», `InvalidCredentials` como «cuenta, contraseña o invitación
  incorrectas» (desde H-01) y `CharacterLimit` como «la cuenta ya tiene el máximo de personajes».
  La reconexión sigue siendo manual con **R**.

### Configuración

| Campo | Defecto | Significado |
| --- | --- | --- |
| `persistenceEnabled` | **true** | Activa cuentas, personajes y guardado en SQLite. |
| `databasePath` | **`data/avelora.db`** | Archivo SQLite; si es relativo, se crea junto a `server.json`. |
| `registrationInvitationCode` | **vacío** (H-01) | Código para crear cuentas. Vacío cierra el registro. `server.json` y `server-carga.json` lo fijan a `avelora-dev`. |
| `saveIntervalSeconds` | **60** | Segundos entre guardados periódicos; debe ser positivo. |
| `maxPendingLogins` | **16** | Logins en vuelo (cola + cálculo) antes de responder `RateLimited`. |
| `maxPendingLoginsPerIp` | **2** (H-03) | Logins en vuelo desde una misma IP; entre 1 y `maxPendingLogins`. `server-carga.json` lo sube a 16. |
| `maxFailedLoginsPerAccountPerMinute` | **5** | Fallos de contraseña por cuenta y minuto. |
| `maxCharactersPerAccount` | **3** | Personajes vivos por cuenta, entre 1 y 10. |
| `characterSelectTimeoutSeconds` | **300** | Tiempo máximo en la pantalla de selección; al vencer se cierra con `Timeout`. |
| `rejectDisconnectDelayMilliseconds` | **500** | Espera antes de cortar una conexión ya rechazada; `0` corta en la vuelta siguiente. |

### Verificación de la Etapa F

```powershell
dotnet build Avelora.slnx -v q --nologo
dotnet test  Avelora.slnx
```

| Prueba | Resultado |
| --- | --- |
| `dotnet build` | **0 errores, 0 advertencias** |
| `dotnet test` | **216/216 en verde** (22 nuevas desde la E) |
| Protocolo | **v2** al cerrar F; la versión actual es **v25** y el saludo solo autentica la cuenta |
| Prueba real con Unity + un bot | login con `dev`, posición guardada entre sesiones y expulsión `LoggedInElsewhere` visible en el cliente |

Las pruebas cubren, además de lo anterior, la ida y vuelta del repositorio SQLite, el rehash al
entrar, el registro abierto y cerrado, el tope de logins en vuelo aunque el cliente se desconecte,
el límite de fallos por cuenta, la expulsión de la sesión anterior (también si es la conexión 0) y
el `Reject` que llega **antes** del corte en una prueba real por UDP.

### Pospuesto

Quedan fuera de la etapa y se retoman cuando existan esos sistemas:

- **Inventario** (personaje, ranura, ítem y cantidad): todavía no hay ítems en el juego.
- **Copia diaria de la base de datos**: mantenimiento, no hace falta con un solo archivo de
  desarrollo.
- **Guardado tras eventos** (subir de nivel, obtener un ítem raro): no hay niveles ni ítems; hoy se
  guarda por tiempo, al desconectar y al apagar.

---

## Qué hace la Etapa H

### Saludo cifrado (H-02, protocolo v4)

```
cliente                                   servidor
   │  conexión UDP                           │  ¿aforo? ¿límite por IP? ¿clave?
   │ ──────────────────────────────────────► │
   │  Challenge(nonce 16 B, módulo, exp.)    │  nonce aleatorio, de un solo uso
   │ ◄────────────────────────────────────── │
   │  Hello(…, cuenta,                        │
   │        RSA-OAEP(nonce ‖ clave ‖ invit.))│  se descifra en avelora-login
   │ ──────────────────────────────────────► │
   │  CharacterList                          │  cuenta autenticada; sin entidad en el mundo
   │ ◄────────────────────────────────────── │
   │  Create/Delete/SelectCharacter          │  operaciones permitidas en selección
   │ ──────────────────────────────────────► │
   │  Welcome                                 │  solo tras seleccionar un personaje
   │ ◄────────────────────────────────────── │
```

- El servidor tiene una clave **RSA de 2048 bits** en `serverKeyPath` (`data/server-key.pem`, fuera
  del repositorio). La genera el primer arranque; sin persistencia es efímera. Al arrancar imprime
  su **huella** (SHA-256 de módulo ‖ exponente).
- El cliente cifra `nonce ‖ contraseña ‖ invitación` con **RSA-OAEP-SHA1** (`HelloSecrets`, en
  `Avelora.Shared`). Se eligió RSA con `RSAParameters` porque funciona igual en .NET 10 y en el Mono
  de Unity.
- El nonce se borra al llegar el saludo: un `Hello` copiado no sirve en otra conexión. Si no se
  descifra o el nonce no coincide, se responde `InvalidCredentials`, se anota en el log y **no**
  gasta el cupo de la cuenta.
- **Huella fijada:** `serverKeyFingerprint` en `NetworkClient` y `--server-key <hex>` en los bots.
  Si no coincide, el cliente corta la conexión. Si está vacía, acepta la clave y avisa con la
  huella recibida (modo desarrollo).

### Límites de red (H-02)

- **`maxConnectionsPerIp` (4)**: conexiones simultáneas desde una misma IP. La configuración de carga
  lo sube a 100 porque todos los bots salen de 127.0.0.1.
- **`maxSecondsOverMessageLimit` (5)**: segundos, dentro del mismo minuto, que una conexión puede
  pasar por encima de `maxMessagesPerSecondPerConnection` antes de desconectarse.

### Arreglos de la auditoría (H-01)

Límite de login sin distinguir mayúsculas y contando los saludos en vuelo; registro cerrado por
defecto; respuesta única para no revelar qué cuentas existen; máximo configurable de 3 personajes por
cuenta (rango 1–10);
reintento de guardados fallidos; guardado al cerrar la ventana o al apagar; y la sincronización con
Unity ya no pisa archivos más nuevos. Detalle en `docs/agentes/informes/H-01.md`.

---

## Qué hace la Etapa I

### Enemigos (I-01)

Los enemigos son entidades del servidor (`EntityKind.Npc`, valor 1 del protocolo, que ya viajaba en
el snapshot). El servidor los mueve con su IA y todos los clientes los ven. Desde I-02 también tienen
vida, reciben ataques, mueren y reaparecen; desde I-03 persiguen y golpean al jugador. I-04 añade el
botín e inventario autoritativos.

`server/Avelora.Server/npcs.json` se copia junto al ejecutable, como `server.json`:

```json
{
  "types": [
    { "id": 2001, "name": "Orco", "speed": 2.0, "halfWidth": 0.35, "halfHeight": 0.35,
      "maxHealth": 30, "experience": 20, "respawnSeconds": 10, "aggroRadius": 4.0, "leashRadius": 8.0,
      "attackDamage": 8, "attackRange": 1.0, "attackCooldownSeconds": 1.5 }
  ],
  "spawners": [
    { "typeId": 2001, "x": 3.5, "y": 1.5, "count": 3, "wanderRadius": 3.0 },
    { "typeId": 2001, "x": -5.5, "y": 2.5, "count": 2, "wanderRadius": 2.5 }
  ]
}
```

Al arrancar se valida contra el mapa real y **si algo falla el servidor no arranca**, con el motivo:
`id` único y mayor que 0; `speed` en (0, 10]; `halfWidth` y `halfHeight` entre 0,05 y 2; `maxHealth`
entre 1 y 100000; `respawnSeconds` entre 0,5 y 3600; `aggroRadius` entre 0 y 20 (0 = pasivo);
`leashRadius` ≤ 40 y ≥ el `wanderRadius` de cada spawner del tipo; `attackDamage` entre 1 y 100000;
`attackRange` en (0, 5]; `attackCooldownSeconds` ≥ 0,2; cada `typeId` existe; `count` entre 1 y 50;
`wanderRadius` mayor que 0; la caja del tipo cabe en el punto (`IsBoxWalkable`) y el total no pasa de
500 enemigos. El arranque imprime una línea que resume tipos, spawners y enemigos.

**IA de deambular** (`Simulation/NpcSystem`): cada 16–50 ticks elige una de las 8 direcciones o
quedarse quieto (1/3), y se mueve con el mismo `MovementSimulator` que los jugadores, así que solo
choca con el mapa: no choca con jugadores ni con otros enemigos. Si toca una pared, vuelve a decidir
al tick siguiente. Si se aleja más de `wanderRadius` de su punto de aparición (`Home`), la correa lo
devuelve hacia él. Un único `System.Random` sembrado con `npcSeed` hace que dos zonas con la misma
semilla y los mismos datos produzcan las mismas posiciones.

Los enemigos **no se guardan**: `CharacterSaveService` solo recibe `CharacterRecord` y
`NetworkServer.SaveAllPlayers` exige `PlayerEntity`; `PublishPlayers`, el cupo y los informes también
filtran por `PlayerEntity`.

### Combate (I-02)

El cliente solo **pide** atacar: manda `Attack(targetId)` por el canal fiable y ordenado. El servidor
lo encola en una `AttackQueue` con tope `maxQueuedAttacks` (si se llena, se descarta y se cuenta), la
vacía en `Zone.Tick` **después** de mover a los jugadores y **antes** de `NpcSystem`, y decide con
estas reglas:

1. el atacante es un `PlayerEntity` de la zona;
2. el objetivo existe, es un `NpcEntity` y está vivo (`Action != Dead`);
3. la distancia entre centros es ≤ alcance + `attackRangeTolerance` (sin arma, el guerrero tiene
   1,2 + 0,5 = **1,7 tiles**; la tolerancia compensa la interpolación del cliente);
4. han pasado al menos `ceil(recarga × TickRate)` ticks (guerrero sin arma: **16 ticks**, 0,8 s) desde
   el último ataque **válido** de ese jugador. Se mide en ticks de la zona, nunca con el reloj del
   cliente.

Desde I-08 el daño, el alcance y la recarga sin arma son los de `unarmed` en `classes.json` (ver
«Clases y atributos (I-08)»). Un ataque válido quita ese daño de vida, con mínimo 0, deja al atacante en
`Action = Attacking` durante **6 ticks** (0,3 s) y suma `ataques_ok`. Si no es válido no hace nada y
suma al contador del motivo: `ataque_rech_objetivo`, `ataque_rech_distancia` o
`ataque_rech_cooldown`.

**Muerte y reaparición.** Al llegar a 0, el orco pasa a `Action = Dead`: deja de moverse y deja de
poder ser objetivo. A los **30 ticks** (1,5 s, lo que dura la animación) sale de la zona y del área de
interés. Su spawner programa la reaparición para `max(respawnSeconds, 1,5 s)` después de la muerte:
crea un orco **nuevo** con ID nuevo, en su `Home`, con la vida llena y la IA reiniciada. El spawner
nunca tiene más vivos + muertos + pendientes que su `count`. Todo va por ticks y con el mismo `Random`
sembrado, así que la misma semilla y los mismos ataques dan el mismo estado.

En el snapshot, `HealthPercent = ceil(100 × Health / MaxHealth)`; los jugadores mandan **100** hasta
I-03. `EntityAction` viaja como byte (`None`, `Attacking`, `Dead`) y un valor desconocido se rechaza
al decodificar.

El informe periódico y el resumen muestran `ataques_ok`, los tres rechazos, `ataques_cola_llena`,
`npc_muertes` y `npc_reapariciones`.

> El cliente de Unity de este repositorio todavía habla **v4**; el servidor v5 lo rechaza en el
> saludo. La vista de la vida, las acciones y las animaciones llega en **I-02b**. Los bots ya usan v5.

### Configuración

| Campo | Defecto | Significado |
| --- | --- | --- |
| `npcsPath` | **`npcs.json`** | Archivo de enemigos; si es relativo se resuelve junto a `server.json`, como `databasePath`. **Vacío = zona sin enemigos.** |
| `npcSeed` | **12345** | Semilla de la IA. Misma semilla y mismos datos = mismas posiciones. |
| `attackRangeTolerance` | **0.5** | Margen sumado al alcance para compensar la interpolación; entre 0 y 2. |
| `maxQueuedAttacks` | **1024** | Tope de ataques encolados por la red; si se llena, se descartan y se cuentan; ≥ 1. |

### Verificación de I-02

| Prueba | Resultado |
| --- | --- |
| `dotnet build` | **0 errores, 0 advertencias** |
| `dotnet test` | **304/304 en verde** (14 nuevas: 4 de protocolo, 1 de snapshot y 9 de combate, muerte, reaparición y determinismo) |
| Carga `fight`, 50 bots, 120 s (servidor 160 s) | tick p99 **0.050 ms**, peor **5.147 ms**, **0** ticks > 50 ms; **1 155** ataques válidos, **2 358** rechazos por objetivo, **0** por distancia, **0** por recarga, **0** por cola llena; **385** muertes y **385** reapariciones |
| Carga `random`, 50 bots, 60 s (servidor 100 s) | **0.180** correcciones por bot y minuto (igual que en I-01) |
| Bytes enviados | **111.3 MB** en `fight` y **57.1 MB** en `random` (13,16 B/entidad frente a 11,14 B de I-01; el snapshot creció 2 B) |

---

## Sincronizar con Unity

El repositorio es la fuente de verdad. Tras tocar `shared/` o `client/`:

```powershell
.\tools\sync-shared-to-unity.ps1 -ProyectoUnity 'E:\Unity\Nueva carpeta\My project (1)'
```

Copia `shared/Avelora.Shared/**` (incluido su `.asmdef`) a `Assets/Avelora/Scripts/Shared/`,
`shared/Avelora.Net/**` a `Assets/Avelora/Scripts/Net/` y `client/Assets/Avelora/**` a
`Assets/Avelora/` (archivos `.cs`, `.asmdef`, `.unity`, `.png`, `.asset` y `.dll`, con su `.meta`).
**Nunca borra `.meta`**, así no se rompen las referencias de la escena ni se regeneran los GUID.
Después, en Unity: *Assets → Refresh*.

Desde H-01, **no pisa** un archivo que sea más nuevo en Unity que en el repositorio (por ejemplo,
una escena guardada en el editor): lo salta con un aviso y al final dice cuántos saltó. Con
`-Forzar` lo sobrescribe todo, como antes.

> La escena viaja en el repositorio **con su `.meta`**, y los scripts también: la escena referencia
> cada `MonoBehaviour` por el GUID del `.meta`. Sin ellos, Unity generaría GUID nuevos al importar y
> la escena aparecería con «missing script».

> **LiteNetLib en Unity:** `client/Assets/Avelora/Plugins/LiteNetLib.dll` es la versión
> **1.3.5 netstandard2.1** del paquete NuGet, versionada con su `.meta`. `Avelora.Net` la referencia
> desde su `.asmdef` y no toca `UnityEngine`. El cliente de Unity usa la misma librería que los bots
> y el servidor, así que el protocolo no puede divergir por el transporte.

**Exportar el mapa:** selecciona el GameObject del Tilemap y usa
**Avelora → Exportar colisión del Tilemap (.avmap)…**. También se puede volcar desde el propio
juego con `AveloraMap.Export()` (o marcando *Export On Awake*).

---

## Verificación de la Etapa A en Unity

Escena `AveloraEtapaA`: una sala de 19×13 tiles con borde y una pared interior en la columna
`x = 0` (de `y = -6` a `y = 0`).

| Prueba | Resultado |
| --- | --- |
| Rejilla construida desde el Tilemap | 19×13, origen (−9,−6), huella `24ec4c86…` |
| Empujar a la derecha contra la pared interior | se detiene en **x = −0.50** (desde G-04, `Step` avanza hasta tocar la pared y se detiene en **x = −0.40**) |
| Subir, bajar y recorrer los bordes | se detiene en todos; **la caja sigue siendo válida** y el siguiente paso está bloqueado |
| Diagonal | recorre **0.200000** unidades, igual que en recto (normalizada) |
| Bucle en tiempo real (`Update`, 20 Hz) | el jugador avanza con `Time.deltaTime` y frena en las paredes |

Las cuatro esquinas se comprobaron con el invariante `IsBoxWalkable(posición) == true` y
`IsBoxWalkable(posición + un paso) == false`, que es la definición exacta de «pegado a la pared».

---

## Hoja de ruta (plan del servidor)

| Etapa | Contenido | Estado |
| --- | --- | --- |
| **A** | `Avelora.Shared`: `Vec2`, `MapCollisionGrid`, `MovementSimulator` + exportación del mapa | ✅ cerrada |
| **B** | Servidor headless: bucle de paso fijo, entidades, métricas de tick | ✅ cerrada |
| **C** | Red real: transporte UDP, handshake, sesión, `MoveInput` y snapshots | ✅ cerrada |
| **D** | Predicción, reconciliación e interpolación en el cliente de Unity | ✅ cerrada |
| **E** | Grilla espacial + área de interés con histéresis y snapshots filtrados | ✅ cerrada |
| **F** | Persistencia mínima (cuenta, personaje, mapa, posición) | ✅ cerrada |
| **G** | Bots y pruebas de carga | ✅ cerrada (pendiente: resistencia de varias horas) |
| **H** | Seguridad reforzada y (opcional) gateway | ⏳ H-01 y H-02 hechas |
| **I** | Enemigos, combate, botín, equipo, experiencia, niveles, clases, atributos, chat, selección de personaje y zonas | ⏳ I-01 a I-26 de servidor hechas; I-15b e I-15pb hechos; pendientes los clientes anteriores de la hoja de ruta |
| **I-03c** | Tipo de entidad en el snapshot (v6), segundo enemigo | ✅ cerrada |
| **I-04** | Botín, objetos en el suelo, inventario y pociones (protocolo v7) | ✅ servidor; I-04b implementa el cliente |
| **I-05** | Armas, equipo y catálogo (protocolo v8; esquema SQLite v3) | ✅ servidor; I-05b implementa el cliente |
| **I-06** | Experiencia y niveles (protocolo v9; esquema SQLite v3) | ✅ servidor; I-06b implementa el cliente |
| **I-07** | Chat básico de zona (protocolo v10) | ✅ servidor y bots; I-07b implementa el cliente |
| **I-08** | Clases, atributos y nivel 25 (sin protocolo; esquema SQLite v4) | ✅ servidor; reparto I-09 y selección de clase I-10 |
| **I-09** | Reparto y reajuste de puntos (protocolo v11; esquema SQLite v4) | ✅ servidor y bots; interfaz I-09b pendiente |
| **I-14** | Equipo de 10 huecos, armaduras e inventario interactivo (protocolo v12; esquema SQLite v5) | ✅ servidor, bots y cliente I-14b |
| **I-20** | Rangos de PNJ configurables y multiplicadores (protocolo v13; esquema SQLite v5) | ✅ servidor y bots; cliente I-20b pendiente |
| **I-21** | Catálogo troceado y tirar objetos = destruir (protocolo v14; esquema SQLite v5) | ✅ servidor y bots; cliente I-21b pendiente |
| **I-10** | Personajes por cuenta y elección de clase (protocolo v15; esquema SQLite v6) | ✅ servidor y bots; cliente I-10b pendiente |
| **I-11** | Penalización de experiencia al morir por un PNJ (sin protocolo ni migración) | ✅ servidor |
| **I-12** | Armas por clase, requisitos de nivel y dos manos (protocolo v16; esquema v6) | ✅ servidor y bots; cliente I-12b pendiente |
| **I-13** | Ataques a distancia con línea de visión y `CombatEvent` (protocolo v17; esquema v6) | ✅ servidor y bots; cliente I-13b pendiente |
| **I-15** | Zonas seguras y PvP con `ZoneList`/`ZoneChanged` (protocolo v19; esquema v7) | ✅ servidor, bots y cliente I-15b |
| **I-15p** | Combate entre jugadores, modo PvE/PvP y forzado (`SetPvpMode`, `PvpStatus`, `PvpModeRejected`; protocolo v20, esquema v8) | ✅ servidor, bots y cliente I-15pb |
| **I-16** | Habilidades, regeneración y bots (protocolo v21, esquema v8) | ✅ servidor; cliente I-16b/I-16c implementado, Play pendiente |
| **I-17** | Áreas y curación (protocolo v22, esquema v8) | ✅ servidor; cliente I-17b pendiente |
| **I-18** | Estados temporales (protocolo v23, esquema v8) | ✅ servidor y bots; cliente I-18b pendiente |
| **I-15q** | Esperas PvE/PvP y rótulos «PvP activo» (sin cambio de protocolo ni esquema) | ✅ servidor, cliente y pruebas |
| **I-15r** | Cancelación del PvP forzado al entrar en zona segura (sin cambio de protocolo ni esquema) | ✅ servidor y pruebas |
| **I-25** | Botín directo, dueño del botín y oro (`LootGained`, protocolo v18; esquema v7) | ✅ servidor, bots y cliente |
| **I-26** | Fórmulas compartidas en `CombatMath` y simulador `tools/Avelora.Balance` (sin cambio de protocolo ni esquema) | ✅ servidor, herramienta y pruebas |
| **I-27** | Modelo de daño, vida y defensa (§2.2–§2.3 del plan); datos de franjas 1 y 5 y orcos (sin cambio de protocolo ni esquema) | ✅ servidor, datos y pruebas |
| **I-28** | Crítico, velocidad de ataque, PvP y recarga global (§2.4–§2.5, §2.8 del plan); protocolo v24 | ✅ servidor, datos y pruebas; cliente I-28b pendiente |
| **I-29** | Velocidad de movimiento (§2.6 del plan): moveSpeed de botas, moveCap 115, PNJ a 3,0-3,4; protocolo v24 sin cambios | ✅ servidor, datos y pruebas; cliente sin cambios |
| **I-19** | Cuatro habilidades por clase (A-4 del plan): hits, critBonus, área propia, cura en área, rotación de bots; protocolo v25 | ✅ servidor, datos y pruebas; cliente I-19b pendiente |
| **I-30** | Armas y armaduras de las franjas 10, 15 y 20, tres PNJ nuevos y objetivos 1 y 3 afirmados | ✅ datos, servidor y pruebas; cliente I-30b pendiente |

### Enemigos y vida del jugador (I-03)

El protocolo sigue en **v5**: no hay mensajes nuevos, solo valores nuevos en los mismos bytes. El
`HealthPercent` del jugador ya es real, los orcos usan `Action = Attacking` y un jugador muerto llega
con `Action = Dead`.

**IA por estados** (`NpcAiState` en cada `NpcEntity`, resuelta en `NpcSystem`):

| Estado | Qué hace | Sale a |
| --- | --- | --- |
| `Wander` | Deambula como en I-01 y cada tick busca el jugador vivo más cercano dentro de `aggroRadius`. | `Chase` si lo encuentra |
| `Chase` | Camina en línea recta hacia el objetivo con `MovementSimulator` y desliza en las paredes. | `Attack` a ≤ `attackRange`; `Return` si el objetivo muere o se va, o si el PNJ pasa de `leashRadius` desde `Home` |
| `Attack` | Quieto y mirando al objetivo; golpea cuando la recarga lo permite. | `Chase` si el objetivo se aleja; `Return` en los mismos casos que `Chase` |
| `Return` | Vuelve a `Home` e ignora a los jugadores. | `Wander` al llegar (≤ 0,25), **con la vida llena** |

- **Aggro al recibir daño:** un PNJ en `Wander` al que pegan persigue al atacante, aunque esté fuera
  de `aggroRadius`. Con `aggroRadius = 0` el PNJ es pasivo: solo responde si le pegan.
- **Objetivo determinista:** el jugador vivo más cercano y, si empatan, el de ID menor. Se busca en
  `EntitySpatialGrid`.
- **Golpe:** el primero sale el tick siguiente a entrar en `Attack`. Después va uno cada
  `ceil(attackCooldownSeconds × 20)` ticks (30 en el orco). Cada golpe quita `attackDamage` con
  mínimo 0 y deja al PNJ en `Attacking` durante 6 ticks.
- **Dirección:** un eje con menos de 0,1 de diferencia no se mueve, para que no avance en zigzag.
- **Atajo:** la línea recta se atasca detrás de una pared en U. La correa lo devuelve a casa, así
  que nunca se queda atascado para siempre. Hará falta A* cuando haya interiores.

**Vida del jugador:**
- `PlayerEntity` tiene `Health` y `MaxHealth` (desde I-08, calculada con la clase en `classes.json`).
- **Al entrar,** la vida sale de `CharacterRecord.Health`, limitada a `[1, máximo]`. Un valor ≤ 0
  entra con la vida llena.
- **Al guardar,** se guarda la vida. Un jugador muerto se guarda **vivo, lleno y en `spawn`**, para
  que nadie entre ya muerto.
- **Regeneración:** recupera `playerRegenPerSecond` cuando lleva `playerRegenDelaySeconds` sin
  recibir daño. Usa un acumulador por ticks.

**Muerte y reaparición** (`PlayerVitalsSystem`):
- **Al llegar a 0,** el jugador pasa a `Action = Dead`. Desde ese momento:
  - el servidor sigue consumiendo sus inputs y avanzando `LastProcessedSequence`, pero no lo mueve;
  - su ataque se rechaza con `ataque_rech_muerto`;
  - ningún PNJ lo elige como objetivo.
- **A los `playerRespawnSeconds`,** reaparece en `spawn` con la vida llena.
- **Orden de `Zone.Tick`:**
  1. jugadores;
  2. combate;
  3. IA de los PNJ;
  4. vida de los jugadores.

**Métricas nuevas:** `npc_golpes`, `jugador_muertes`, `jugador_reapariciones` y
`ataque_rech_muerto`.

**Bots:**
- Con su jugador muerto, mandan movimiento cero y no atacan.
- Al reaparecer, reinician la predicción (`ClientPrediction.Reset`) para que el salto no cuente como
  corrección.
- Cuentan `muertes_propias` y `reapariciones`.

| Campo de `server.json` | Defecto | Significado |
| --- | --- | --- |
| `playerRespawnSeconds` | **5** | Tiempo muerto antes de reaparecer; entre 0,5 y 600. |
| `playerRegenPerSecond` | **2** | Vida recuperada por segundo; ≥ 0 (0 = sin regeneración). |
| `playerRegenDelaySeconds` | **5** | Segundos sin daño antes de regenerar; ≥ 0. |

### Verificación de I-03

| Prueba | Resultado |
| --- | --- |
| `dotnet build` | **0 errores, 0 advertencias** |
| `dotnet test` | **343/343 en verde** (38 nuevas) |
| Carga `fight`, 50 bots, 120 s | tick p99 **0.077 ms**, peor **5.186 ms**; **806** ataques válidos, **0** rechazos por distancia o recarga; **259** golpes de PNJ; **262** muertes y **262** reapariciones de orcos; **10** muertes y **9** reapariciones de jugadores; **0.05** correcciones por bot y minuto |
| Carga `random`, 50 bots, 60 s | tick p99 **0.075 ms**; los orcos persiguen a bots que no se defienden: **632** golpes, **42** muertes y **35** reapariciones (las demás caen al final de la prueba); **0.80** correcciones por bot y minuto, frente a 0.18 en I-02. Es una por muerte: los inputs en vuelo al morir. |

La reaparición usa `ClientPrediction.Teleport`, que mueve la predicción **sin** reiniciar la
secuencia de inputs. Con `Reset`, la secuencia volvía a 1 y el servidor descartaba como viejos todos
los inputs posteriores. Por eso la primera carga dio 163 correcciones por bot y minuto.

---

### Tipo de entidad en el snapshot (I-03c, protocolo v6)

El snapshot ahora lleva el **tipo de PNJ** para que el cliente distinga un enemigo de otro.

- **Protocolo**: sube de v5 a **v6** porque `EntityState` gana `TypeId` (`ushort`, 2 bytes) al final, tras
  `Action`. `EntityStateSize` pasa de 13 a **15 B**.
- **Jugadores** mandan `TypeId = 0`. **PNJs** mandan su `id` de `npcs.json` (2001, 2002…).
- **Tope de entidades por snapshot**: con 15 B/entidad + 11 B de cabecera bajo 1020 B, caben **67**
  (antes 77). `MaxEntitiesPerSnapshot` se actualiza a 67.
- El códec **no valida** el tipo: cualquier `ushort` es válido; el cliente dibuja un tipo desconocido
  con el aspecto por defecto.
- **Validación en `npcs.json`**: `id` debe estar entre **1 y 65535** (cabe en `ushort`).
- **`SnapshotTimeline.EntitySample`** gana `TypeId` y lo copia del snapshot más nuevo (igual que
  `HealthPercent` y `Action`).

**`npcs.json` con dos tipos (ejemplo):**

```json
{
  "types": [
    { "id": 2001, "name": "Orco", "speed": 2.0, "halfWidth": 0.35, "halfHeight": 0.35,
      "maxHealth": 30, "experience": 20, "respawnSeconds": 10, "aggroRadius": 4.0, "leashRadius": 8.0,
      "attackDamage": 8, "attackRange": 1.0, "attackCooldownSeconds": 1.5 },
    { "id": 2002, "name": "Orco guerrero", "speed": 1.6, "halfWidth": 0.35, "halfHeight": 0.35,
      "maxHealth": 60, "experience": 35, "respawnSeconds": 20, "aggroRadius": 5.0, "leashRadius": 8.0,
      "attackDamage": 14, "attackRange": 1.0, "attackCooldownSeconds": 2.0 }
  ],
  "spawners": [
    { "typeId": 2001, "x": 3.5, "y": 1.5, "count": 3, "wanderRadius": 3.0 },
    { "typeId": 2001, "x": -5.5, "y": 2.5, "count": 2, "wanderRadius": 2.5 },
    { "typeId": 2002, "x": 5.5, "y": -4.5, "count": 1, "wanderRadius": 2.0 }
  ]
}
```

El `id` de cada tipo debe estar entre **1 y 65535**.

| Prueba | Resultado |
| --- | --- |
| `dotnet build` | **0 errores, 0 advertencias** |
| `dotnet test` | **348/348 en verde** (5 nuevas: `TypeId` ida y vuelta, rango del `id`, snapshot del servidor con jugador 0 y orco 2002, tolerancia de reconciliación y esquina con base exacta) |
| Carga `fight`, 50 bots, 120 s | tick p99 **0.054 ms**, peor **5.024 ms**; **0 correcciones** (antes del arreglo: entre 0.03 y 3.61 por bot y minuto según la pasada); 10 muertes y 10 reapariciones de jugadores |
| Carga `random`, 50 bots, 120 s | tick p99 **0.066 ms**; **0.67** correcciones por bot y minuto con **94** muertes: son los inputs en vuelo al morir |
| Bytes enviados | **119–120 MB** en `fight` frente a 115 MB en I-03 (**+4 %**). Cada entidad pesa un 15 % más, pero las cabeceras no cambian |

**Correcciones de predicción encontradas en I-03c.** En `fight` variaban mucho entre pasadas porque
dependían de que algún bot acabase pegado a una esquina. El registro `-LogCorrections` mostró dos
causas, las dos en `ClientPrediction.Reconcile`:

1. **Reaplicar desde la base cuantizada en una esquina.** Con la caja rozando a la vez dos bordes de
   tile, el redondeo a 1/64 la saca 1/128 de la pared. El reaplicado avanzaba en diagonal donde el
   servidor, con la posición exacta, chocaba. Se corregía a una posición equivocada, y el snapshot
   siguiente lo devolvía: siempre en pares.
   - **Arreglo:** cada input pendiente guarda la posición predicha tras aplicarlo. Si el servidor
     confirma esa posición dentro del redondeo, se reaplica desde la predicha exacta.
2. **Cascada por tolerancia justa.** Tras una corrección, la predicción parte de una base cuantizada,
   así que queda hasta medio paso (1/128) fuera del camino real. El snapshot suma otro medio paso.
   - **Arreglo:** la tolerancia pasa a **dos medios pasos** (1/64 de tile, medio píxel).

### Botín e inventario (I-04)

El cliente solo envía `PickUp(entityId)` y `UseItem(slot)`: el servidor valida la vida, la distancia,
el tipo de objeto, el espacio y la recarga. El protocolo es **v8** (I-05); `EntityKind.Item = 2` identifica
los objetos del suelo y su `TypeId` es el `itemId`. Sus snapshots llevan vida 100, acción `None` y
dirección `(0,0)`. `PickUp` y `UseItem` son de cliente a servidor; `Inventory` devuelve todos los
huecos después del catálogo, tras el `Welcome` y después de cada cambio (84 B en el cable con 20 huecos,
incluido `equippedItemId`).

`server/Avelora.Server/items.json` declara los objetos. `kind` admite `material`, `potion` y, desde
I-05, `weapon`;
`id` es único (1–65535), `maxStack` va de 1 a 999; `heal` (1–100000) es obligatorio en pociones y
está prohibido en materiales. El arranque valida estos datos y las referencias de botín:

```json
{
  "items": [
    { "id": 3001, "name": "Colmillo de orco", "kind": "material", "maxStack": 99 },
    { "id": 3002, "name": "Poción de vida", "kind": "potion", "maxStack": 10, "heal": 30 }
  ]
}
```

Cada tipo de `npcs.json` puede declarar `loot: [{ itemId, chance, min, max }]`. El `itemId` debe
existir y `1 ≤ min ≤ max ≤ maxStack`; `chance` está en (0, 1]. Al morir un PNJ, cada entrada tira
su probabilidad y cantidad con el mismo `Random` determinista de `NpcSystem`. Cada entrada que sale
crea una entidad en su posición con desplazamiento de ±0,4 tiles por eje y caduca tras
`groundItemSeconds` (60 s por defecto). El límite por zona es `maxGroundItems`; lo que lo supera se
cuenta como `objetos_descartados`.

**Recoger:** se acepta solo si el jugador está vivo, el id es un `GroundItemEntity` y la distancia
entre centros no supera `pickupRange + attackRangeTolerance` (1,5 + 0,5 = 2 tiles por defecto). El
servidor calcula antes si cabe el stack entero; primero llena pilas del mismo objeto hasta `maxStack`
y luego huecos vacíos en orden. Si no cabe todo, no cambia nada. En la cola de un mismo tick gana la
primera petición válida.

**Pociones:** `UseItem` requiere jugador vivo, ranura existente con tipo `potion`, vida por debajo del
máximo y recarga cumplida. Cura hasta el máximo, consume una unidad y vacía la ranura al gastar la
última. La recarga se redondea a `ceil(potionCooldownSeconds × TickRate)` ticks. Las acciones de
objetos se resuelven después del combate y antes de `NpcSystem`; la simulación conserva un único hilo
propietario del mundo, mientras la red solo encola.

**Persistencia:** `inventory_items` guarda `(character_id, slot, item_id, count)` con borrado en
cascada. I-04 introdujo la migración SQLite v1→v2; I-05 lleva el esquema a v3 para guardar el arma
equipada. Guardar el personaje reescribe también sus filas de
inventario en la misma transacción. Al cargar se descartan y registran filas de objetos eliminados,
ranuras fuera de rango o cantidades no válidas, sin impedir el login.

El informe periódico y el resumen final muestran `objetos_soltados`, `objetos_recogidos`,
`objetos_caducados`, `objetos_descartados`, `objetos_en_suelo`, los cuatro rechazos de recogida y de
uso, `pociones_usadas` y `acciones_objeto_cola_llena`. En modo `fight`, los bots recogen objetos
cercanos y usan la poción 3002 cuando su vida baja del 40 %; el resumen incluye `recogidas` y
`pociones`.

| Campo de `server.json` | Defecto | Rango / significado |
| --- | --- | --- |
| `itemsPath` | `items.json` | Ruta junto a la configuración; vacío desactiva objetos. |
| `inventorySlots` | 20 | Número de huecos, 1–60. |
| `groundItemSeconds` | 60 | Permanencia en el suelo, 5–3600 s. |
| `maxGroundItems` | 500 | Máximo de entidades objeto por zona, 1–5000. |
| `pickupRange` | 1.5 | Tiles entre centros, > 0 y ≤ 5; se suma la tolerancia de ataque. |
| `potionCooldownSeconds` | 1.0 | Recarga, ≥ 0,1 s. |
| `maxQueuedItemActions` | 1024 | Tope de acciones pendientes de la red, ≥ 1. |

**Verificación y carga I-04:** reproducir con
`.\tools\carga\carga.ps1 -Bots 50 -Seconds 120 -Behavior fight -LogCorrections -NuevaBase`.
En esta ejecución (servidor 160 s, 50/50 bots): tick p99 **0,069 ms**, peor **5,703 ms**, **0** ticks
por encima de 50 ms; **0 correcciones** (**0/bot/min**, igual que J-01). Se soltaron **166** objetos,
se recogieron **165**, caducó **0** y quedó **1** en el suelo; hubo **258** rechazos de recoger por
objeto inexistente/ya recogido y ninguno por jugador muerto, distancia o inventario lleno. Se usaron
**27** pociones y hubo **12** muertes / **10** reapariciones de jugador; hubo 4 rechazos de uso por
objeto y 1 por recarga. Los bots enviaron 423 peticiones de recoger y 32 de poción. El servidor envió
**35 242 538 B (35,2 MB)**, **5,5 MB más (+18,7 %)** que J-01 (29,7 MB); no apareció ninguna línea
`[corrección]` que requiriera análisis individual. El p99 subió **0,022 ms** respecto a J-01 (0,047 ms).

### Armas y equipo (I-05, protocolo v8)

`items.json` admite ahora `kind: "weapon"`. Las armas tienen un nombre no vacío de hasta **48 bytes
UTF-8**, `maxStack: 1`, `damage` de 1 a 100000, `range` mayor que 0 y como máximo 5 tiles, y
`cooldownSeconds` de al menos 0,1 s. Los tres campos de combate son obligatorios y están prohibidos
en materiales y pociones. El catálogo incluye la espada corta (3101: daño 14, alcance 1,2, recarga
0,8 s) y el hacha de orco (3102: daño 22, alcance 1,3, recarga 1,2 s). El orco 2001 suelta la espada
con probabilidad 0,05; el 2002 suelta el hacha con probabilidad 0,15; ambos montos son una unidad.

`kind: "weapon"` admite `damageType` (`physical` por defecto o `magical`, I-23); el bastón 3104 lo declara mágico y lo mitiga `magicResist`.

El jugador tiene un único hueco de equipo, `EquippedWeaponId` (0 = puños). `Equip(slot)` solo se
acepta si está vivo y el hueco contiene un arma; equipa la nueva y devuelve la anterior a ese mismo
hueco, sin requerir espacio libre. `Unequip` guarda el arma en el primer hueco vacío; si el inventario
está lleno o no había arma se rechaza. Ambas intenciones usan la cola acotada de acciones de objeto y
el servidor devuelve el `Inventory` completo tras un cambio válido.

El combate usa daño, alcance y recarga del arma equipada. El servidor suma `attackRangeTolerance` al
alcance igual que con los puños, y convierte la recarga en `ceil(cooldownSeconds × TickRate)` ticks:
16 para la espada y 24 para el hacha. Sin arma se aplica el `unarmed` de la clase
(`classes.json`, I-08), que es también lo que el `ItemCatalog` le manda al cliente. La autoridad de daño, alcance, recarga y equipo
permanece en el servidor.

Tras `Welcome`, el servidor entrega uno o más mensajes `ItemCatalog` y luego `Inventory`, por el canal
fiable y ordenado. El catálogo incluye `pickupRange`, daño/alcance/recarga de los puños y todos los
objetos: id, tipo, pila máxima, curación o estadísticas de arma y nombre. `Inventory` añade al final
`equippedItemId` (u16). `Equip` (byte de ranura) y `Unequip` (sin carga útil) usan los bytes 6 y 7 de
cliente a servidor; `ItemCatalog` usa el 15 de servidor a cliente. Cada mensaje lleva una cabecera de
19 B y objetos de 32 B más el nombre; el servidor divide el catálogo en el orden original en trozos
de hasta 1400 bytes.

En I-05 la base subió a **esquema v3** (el esquema actual es v4 desde I-08). V2→v3 añade
`characters.equipped_item_id INTEGER NOT NULL DEFAULT 0` en
una transacción; v1→v3 aplica primero la migración de inventario v1→v2 y después la v2→v3, cada una
transaccionalmente. El arma se guarda junto a inventario/vida y también viaja en la foto de
`TryGetLatest`, para que una reconexión inmediata conserve el equipo. Si el id guardado no existe o
no es un arma, se registra el aviso y se carga como 0.

El informe periódico y el resumen final muestran `equipados`, `desequipados`,
`equipar_rech_muerto`, `equipar_rech_objeto`, `equipar_rech_lleno`, `equipar_rech_vacio` y
`ataques_con_arma`. Los bots `fight` leen el catálogo, equipan el arma disponible de mayor daño y
ajustan alcance y recarga al arma equipada (o a los puños); el resumen de bots también muestra
`equipados` confirmados por el servidor.

**Verificación y carga I-05:**
`dotnet build Avelora.slnx -v q --nologo` terminó con **0 errores y 0 advertencias**;
`dotnet test Avelora.slnx` pasó **421/421**. En la pasada de carga de 50 bots durante 120 s
(`-LogCorrections`, servidor 160 s), se conectaron **50/50**; el tick p99 fue **0,055 ms** (I-04:
0,069 ms), el peor tick **6,920 ms** y hubo **0** ticks sobre 50 ms. Hubo **0 correcciones** y
ninguna línea `[corrección]` que analizar. Se confirmaron **13 equipados**, se hicieron **156 ataques
con arma** y no hubo rechazos de equipar; los PNJ murieron/reaparecieron **252/252** veces y los
jugadores murieron/reaparecieron **9/8** veces. El servidor envió **27 633 309 B (27,6 MB)**,
**7 609 229 B (21,6 %) menos** que los 35 242 538 B (35,2 MB) de I-04.

### Experiencia, niveles y clases (I-06 / I-08)

I-06 añadió el mensaje `Progress` (protocolo v9, `MessageType = 16`, 14 bytes, fiable y ordenado): nivel
(u8), experiencia (u32), umbral siguiente (u32; 0 en el máximo) y vida máxima (u32). Tras `Welcome`
llega después de `ItemCatalog` e `Inventory`; tras las muertes se publica el progreso después del
combate, como máximo una vez por jugador y tick. I-08 no cambia ningún mensaje; I-09 añade `Stats` y
sube la versión actual a **v11**.

`levels.json` contiene `pointsPerLevel` (0–100; defecto 3) y los **24 umbrales** hasta el nivel máximo
25: `100, 150, 225, 340, 500, 750, 1100, 1650, 2500, 3500, 4700, 6200, 8000, 10000, 12500, 15500,
19000, 23000, 27500, 32500, 38000, 44000, 51000, 59000`. El jugador que da el golpe mortal recibe experiencia;
I-08 la escala en décimas enteras según el nivel del PNJ y del jugador, con multiplicador entre 0,1 y
1,3 y mínimo de 1. Al subir de nivel se rellena la vida; I-27 añade crecimiento por nivel a vida máxima,
defensa y resistencia mágica. El daño no aumenta automáticamente con el nivel porque los atributos no
crecen solos. En nivel 25 la experiencia queda en 0.

La información detallada de atributos, clases, mitigación y el esquema actual está en [I-08](#clases-y-atributos-i-08).
La verificación histórica de la carga de I-06 fue p99 **0,054 ms**, **5845** de experiencia y **32**
subidas en 50 bots/120 s; I-08 vuelve a medir la misma carga con las reglas nuevas.

El mensaje `Progress` sigue sin cambios en el protocolo actual v11. Los contadores `experiencia_otorgada` y
`subidas_nivel` aparecen en los informes; `fight` procesa el progreso y muestra subidas y nivel máximo.

**Verificación y carga I-06:**
`dotnet build Avelora.slnx -v q --nologo`: **0 errores, 0 advertencias**; `dotnet test Avelora.slnx`:
**449/449**. En la carga prescrita `.\tools\carga\carga.ps1 -Bots 50 -Seconds 120 -Behavior fight -LogCorrections -NuevaBase`
(servidor 160 s), se conectaron **50/50**. El tick p99 fue **0,054 ms** (I-05: 0,055 ms), el peor
**7,732 ms** y hubo **0** ticks sobre 50 ms. Se registraron **0 correcciones** y ninguna línea
`[corrección]`; los bots sumaron **5845** de experiencia, **32** subidas y nivel máximo **3**. Murieron
y reaparecieron **287/287 PNJ** y **7/6 jugadores**. Hubo **134 ataques con arma**. El servidor envió
**23 890 303 B (23,9 MB)**, **3 743 006 B (13,5 %) menos** que los 27 633 309 B de I-05.

### Clases y atributos (I-08)

`classes.json` es la fuente de verdad para las clases. Incluye guerrero (1), mago (2), ranger (3) y
sanador (4); el código no selecciona fórmulas por id, así que agregar otra clase es agregar una entrada
con sus atributos, defensa, recurso y ataque sin arma. Todo personaje nuevo nace como clase 1; la
selección de clase queda para I-10 y el reparto se describe en I-09.

Los atributos efectivos son `base + alloc_*` (el equipo suma 0 hasta I-14). No hay columna de puntos
libres: se derivan como `pointsPerLevel × nivel − (alloc_str + alloc_dex + alloc_int + alloc_vit)`;
desde I-09 el jugador puede repartirlos y reajustarlos.
Fórmulas enteras (I-27):

- vida máxima = `baseHealth + nivel × healthPerLevel + VIT × healthPerVit`;
- golpe básico = `floor((daño de arma o unarmed.damage + bonus) × (100 + primario × powerPerPoint) / 100)`;
- habilidad = `floor((skill.damage + floor(daño de arma o unarmed.damage × weaponPercent / 100) + bonus) × (100 + primario × powerPerPoint) / 100)`; `powerPerPoint` es 2 y el bonus no se aplica a curas ni escudos;
- recurso máximo = `resource.base + INT × resource.perInt` (el valor actual se persiste en `mana`);
- defensa/resistencia = valor de clase + `nivel × defensePerLevel`/`nivel × magicResistPerLevel` + equipo + bonus;
- daño mitigado = `max(1, floor(daño bruto × 100 / (100 + defensa/resistencia)))`.
- probabilidad crítica (pb) = `min(critCap, critBase + DES efectiva × critPerDex)`; daño crítico = `floor(golpe × critMultiplier / 100)`;
- prisa (pb) = `min(hasteCap, DES efectiva × hastePerDex)`; recarga básica = `max(minAttackTicks, ceil(ticksArma × 10000 / (10000 + prisa)))`;
- contra otro jugador, tras mitigar: `max(1, floor(daño × pvpDamagePercent / 100))`; no reduce curas.

Las armas declaran su tipo de daño y los puños usan el `damageType` de la clase. Los PNJ declaran
`damageType` (`physical` por defecto o `magical`): sus ataques se mitigan con defensa o resistencia
mágica del jugador, respectivamente. Contra PNJ, el daño del jugador usa la defensa o resistencia
declarada para ese tipo. Con defensa 0 se conserva el daño bruto (con mínimo 1).

Valores originales de I-08, anteriores al reequilibrio I-27: vida máxima **100/80/90/90**
(guerrero/mago/ranger/sanador), daño sin arma **12/14/13/13**, espada corta del guerrero **16** y
recurso máximo **100/92/100/88**. Los personajes existentes se migran a guerrero con todos los puntos
sin gastar; al subir de nivel ganan 3 puntos repartibles.

SQLite está en **esquema v4**. La migración v3→v4, transaccional, agrega `class_id` (defecto 1) y
`alloc_str`, `alloc_dex`, `alloc_int`, `alloc_vit` (defecto 0); una base nueva se crea directamente en
v4. Al cargar se normaliza una clase desconocida a 1, se ponen a cero las cuatro asignaciones si alguna
es negativa o su suma excede los puntos disponibles, se recorta la vida al máximo de la clase y `mana`
al rango `[0, recurso máximo]`. Los guardados y `TryGetLatest` conservan clase, asignaciones y recurso.

`npcs-carga.json`, `levels-carga.json` y `classes-carga.json` mantienen las reglas de carga alineadas
con los datos del servidor. El servidor sigue autoritativo: el cliente no determina atributos, daño,
mitigación ni experiencia.

**Verificación y carga I-08:** `dotnet build Avelora.slnx -v q --nologo`: **0 errores y 0 advertencias**;
`dotnet test Avelora.slnx`: **522/522**. En la pasada prescrita de 50 bots/120 s con `fight`,
`-LogCorrections` y `-NuevaBase` (160 s de servidor), se conectaron **50/50** y se completaron **3200
ticks**; el p99 fue **0,073 ms** (I-07: 0,090 ms), el peor tick **9,330 ms** y hubo **0** ticks sobre
50 ms. Se observaron **0 correcciones** y ninguna línea `[corrección]`; murieron/reaparecieron
**203/203 PNJ** y murieron/reaparecieron **6/6 jugadores**. El servidor envió **31 256 232 B (31,3 MB)**
(I-07: 26 894 373 B); los snapshots incluyeron 1 995 696 entidades frente a 1 704 901 en I-07, por
lo que el tráfico depende de la distribución de los bots dentro del área de interés. La muestra hizo
**636 ataques válidos** con 12 de daño sin arma del guerrero (I-07 usó 10); el menor número de bajas
en esta pasada no permite aislar el efecto del daño frente a la variación de rutas/objetivos. Detalles,
incluida una segunda pasada, en el [informe I-08](docs/agentes/informes/I-08.md).

### Reparto y reajuste de atributos (I-09, protocolo v11)

`levels.json` asigna `pointsPerLevel` puntos por nivel (3 por defecto, incluido el nivel 1). Los puntos
libres siguen siendo derivados: `pointsPerLevel × nivel − suma(alloc_*)`; la base SQLite permanece en
**esquema v4**, pues I-08 ya guarda los cuatro contadores repartidos. `freeResetMaxLevel` (1–255, defecto
10) determina hasta qué nivel el reajuste es gratis; `resetItemId` (1–65535, defecto **3201**) identifica
el objeto que debe existir en `items.json` al arrancar.

`AllocatePoints` (cliente→servidor, tipo 9, 5 bytes) lleva los incrementos FUE/DES/INT/VIT como cuatro
u8. La cola acotada de acciones los aplica solo en el hilo de simulación. El servidor exige jugador vivo,
suma mayor que cero y no superior a los puntos libres; el reparto es atómico. Al añadir VIT, la vida
máxima aumenta según `healthPerVit` y la vida actual aumenta exactamente lo mismo. También se actualiza
el recurso máximo derivado de INT sin reponer el recurso actual. No se implementa el tope de atributo de
`10 + 4 × nivel`: con los 3 puntos por nivel de los datos actuales nunca se alcanza.

`ResetPoints` (tipo 19, un byte) devuelve los cuatro contadores a cero. Sin puntos repartidos o con el
personaje muerto se rechaza. Por encima del nivel gratuito consume una unidad del objeto 3201; si no la
tiene, no cambia nada. El reajuste recalcula máximos y recorta vida/recurso actuales a esos máximos, sin
curar ni regenerar recurso. La **Piedra de reajuste** es `material`, `maxStack: 10`, y el Orco guerrero
(2002) la suelta con probabilidad **0,03**; `items-carga.json`, `levels-carga.json` y `npcs-carga.json`
replican los datos necesarios para la carga.

`Stats` (servidor→cliente, tipo 18, 41 bytes, fiable y ordenado) contiene classId/nivel, atributos
efectivos, puntos asignados/libres, vida máxima, daño bruto actual y su tipo (0 físico, 1 mágico),
defensa/resistencia, crítico y prisa en pb, recurso actual/máximo y si el reajuste siguiente es gratis. Se envía después de
`Welcome`, en el orden `Welcome`, `ItemCatalog`, `Inventory`, `Progress`, `Stats`; también tras un reparto
o reajuste válido, al subir de nivel y al equipar/desequipar. No se emite cada tick. El bot `fight` lee
`Stats` y reparte los puntos de forma que la mitad (redondeando hacia su atributo principal) vaya al
mayor atributo base de su primera muestra sin puntos, y el resto a VIT; no contiene nombres de clase en
el código.

Los informes incluyen `stats_repartos`, `stats_puntos`, `stats_rech_muerto`, `stats_rech_vacio`,
`stats_rech_exceso`, `reajustes` y `reajuste_rech_muerto`, `reajuste_rech_vacio`, `reajuste_rech_objeto`.
El protocolo sube a **v11**; el esquema de persistencia no cambia. Los puntos ya viajan en la foto de
guardado y `TryGetLatest`, incluida la reconexión inmediata.

**Verificación y carga I-09:** `dotnet build -c Release` acabó con **0 errores y 0 advertencias**. En la
pasada de 50 bots/120 s con `fight`, `-LogCorrections` y `-NuevaBase` (servidor 160 s) se conectaron
**50/50**; hubo **3201 ticks**, p99 **0,061 ms**, peor **11,341 ms** y **0** ticks por encima de 50 ms.
No hubo correcciones ni líneas `[corrección]`. El servidor contó **74 repartos / 222 puntos**, 0 rechazos,
**239** muertes/reapariciones de PNJ y **6** muertes/reapariciones de jugador. Los bots confirmaron
**222 puntos** y recibieron **157 `Stats`** en total. El servidor envió **38 027 637 B (38,0 MB)**,
frente a 31 256 232 B de I-08; los snapshots incluyeron 2 447 123 entidades frente a 1 995 696. Hubo
36 muertes de PNJ más que en la pasada histórica de I-08 (203), con el mismo número de muertes de
jugador; el aumento es compatible con el daño extra de FUE, pero una sola pasada no aísla el efecto de
la variación de rutas y objetivos. Los bots llegaron como máximo al nivel 2, así que no necesitaron
reajustar ni consumir piedras.

### Equipo e inventario (I-14, protocolo v12, esquema v5)

**Diez huecos de equipo** con ids fijos (`EquipSlot` en `Avelora.Shared`; se guardan y viajan así):
0 `weapon`, 1 `offhand`, 2 `head`, 3 `body`, 4 `legs`, 5 `hands`, 6 `feet`, 7 `ring`, 8 `neck`,
9 `accessory`. Un objeto por hueco. En `items.json`, `kind: "armor"` exige `slot` (cualquiera salvo
`weapon`) y `maxStack: 1`; admite `defense`, `magicResist` y bonus `str`/`dex`/`int`/`vit` (0–255), que
también pueden llevar las armas. Las armas van siempre al hueco 0. Objetos de prueba 3301–3310 (casco de
cuero y de hierro, peto, pantalones, guantes, botas, escudo, anillo, collar, amuleto); los orcos los
sueltan con probabilidad 0,06 cada uno.

`CharacterStats` suma los bonus de todo lo equipado a los atributos (antes de derivar vida y recurso) y
la defensa/resistencia de los objetos a las de la clase. Equipar o quitar no cura: la vida actual se
recorta a la nueva máxima si baja. La vida máxima con equipo se respeta también al subir de nivel, al
cargar de la base y al entrar en la zona.

Mensajes (todos fiables y ordenados, resueltos en el hilo de simulación y atómicos; nada se duplica):

| Mensaje | Tipo | Cuerpo | Regla |
|---|---|---|---|
| `Equip` | 6 | hueco de inventario (u8) | El servidor deduce el hueco de equipo; si está ocupado, **intercambia** (el viejo ocupa el hueco del nuevo), así que funciona con el inventario lleno. |
| `Unequip` | 7 | hueco de equipo (u8), destino (u8, 255 = primero libre) | Sin hueco libre se rechaza. Destino vacío: va ahí; con un objeto del mismo hueco de equipo: intercambio; con otra cosa: rechazo. |
| `MoveItem` | 20 | origen, destino (u8), cantidad (u16, 0 = todo) | A vacío mueve (dividir si es parte); al mismo objeto junta hasta `maxStack` y el resto se queda; a otro objeto intercambia solo con la pila entera. |
| `DestroyItem` | 22 | hueco (u8), cantidad (u16) | Lo borra. |

`Inventory` (14) sustituye el arma equipada por los **10** ids de equipo (u16). `ItemCatalog` (15) añade
`ItemKind.Armor = 3` y por objeto `equipSlot` (u8, 255 = no se equipa), `defense`, `magicResist` (u16) y
los cuatro bonus (u8). Todo se rechaza con el jugador muerto.

**Esquema v5:** tabla `character_equipment(character_id, slot, item_id)`; la migración copia
`equipped_item_id` al hueco 0 y la columna deja de usarse. Equipo e inventario se guardan en la misma
transacción; al cargar, un objeto inexistente o que no va en ese hueco se descarta con aviso.

Métricas: `intercambios_equipo`, `movidos`, `divididos`, `juntados`, `intercambiados`,
`destruidos` y los rechazos `equipo_rech_{muerto,vacio,hueco,lleno,destino}`, `equip_rech_{objeto,vacio}`,
`mover_rech_{muerto,hueco,mismo,vacio,cantidad,objeto,lleno}` y
`destruir_rech_{muerto,hueco,vacio,cantidad,objeto}`. Bots: `fight` equipa armaduras si el hueco está
vacío (`armaduras_equipadas`); el comportamiento nuevo **`inventory`** (9) recoge botín y cada 0,5 s manda
una acción de inventario al azar.

Cliente (I-14b, actualizado en I-16c): arrastrar entre huecos del inventario mueve, intercambia o junta
(con Shift, media pila); arrastrar al panel de personaje (C) equipa y del panel al inventario quita;
doble clic usa o quita; clic derecho equipa o quita. Desde I-21 tirar destruye el objeto: los objetos
del suelo quedan reservados al botín de PNJ, porque permitir soltar unidades llenaba el tope global
de 500 y bloqueaba el botín de todos durante 60 s. El tipo 21 queda retirado y no se reutiliza.

**Verificación y carga histórica I-14, anterior a I-21:** 0 advertencias, **565** pruebas en verde. 50 bots `fight` (120 s): p99
**0,082 ms**, 0 correcciones, 56 armaduras equipadas. 30 bots `inventory` (120 s): p99 **0,078 ms**,
0 correcciones; 58 equipados, 40 desequipados, 56 movidos, 7 divididos, 9 intercambiados, 71 tirados y
54 destruidos; balance exacto (267 soltados por PNJ + 71 tirados = 338 recogidos) y ninguna pila fuera
de `1…maxStack` en la base. Detalle en `docs/agentes/informes/I-14.md`.

### Mobs élite y campeón (I-20, protocolo v13)

El campo opcional de primer nivel `ranks` en `npcs.json` define ids 1–3, nombre para consola, probabilidad
de aparición, multiplicadores `health`, `damage` y `experience` (1–20), y `lootRolls` (1–5). La suma de
`chance` no puede superar 1; el resto de probabilidad corresponde al rango 0, normal e implícito. Al no
declarar `ranks`, todos los enemigos permanecen normales como antes. Cada tipo puede declarar
`"ranks": false` para excluirse de esos sorteos; el valor por defecto es `true`.

```json
"ranks": [
  { "id": 1, "name": "Élite", "chance": 0.08, "health": 2.5, "damage": 1.5, "experience": 3.0, "lootRolls": 2 },
  { "id": 2, "name": "Campeón", "chance": 0.02, "health": 5.0, "damage": 2.0, "experience": 8.0, "lootRolls": 3 }
]
```

La zona sortea el rango en cada aparición y reaparición con su `Random` determinista, en orden ascendente
de id. Solo multiplica la vida máxima, el daño y la experiencia del PNJ; redondea al entero más cercano
(empates alejándose de cero) y garantiza un mínimo de 1. No modifica velocidad, defensa, alcance, recarga,
radios ni nivel. `lootRolls` repite la tabla de botín completa y vuelve a tirar cada probabilidad en cada
pasada; el límite existente de objetos en el suelo sigue aplicándose.

En el snapshot, `EntityState` conserva sus 15 bytes: los bits 0–3 del byte de acción contienen `None`,
`Attacking` o `Dead`; los bits 4–5 contienen el rango; los bits 6–7 deben ser cero. Jugadores y objetos
del suelo llevan rango 0. El protocolo sube a **v13** y la base de datos no cambia (esquema v5).
El arranque lista los rangos cargados; los informes periódico y final incluyen `elites_aparecidos`,
`elites_muertos` y, por cada id configurado, `rango{id}_aparecidos` y `rango{id}_muertos`. Los bots
`fight` cuentan `elites_vistos` por id distinto de entidad.

**Verificación y carga I-20:** `dotnet build Avelora.slnx -v q --nologo`: **0 errores y 0 advertencias**;
`dotnet test Avelora.slnx`: **582/582**. La pasada indicada de 50 bots `fight` durante 120 s con
`-LogCorrections -NuevaBase` conectó **50/50** y completó **3200 ticks**; p99 **0,062 ms**, peor tick
**10,901 ms** y **0** ticks por encima de 50 ms. No hubo correcciones ni líneas `[corrección]`. El
servidor envió **28 659 208 B (28,66 MB)**; registró **265** muertes de PNJ y **15** de jugador. Hubo
**29/23** apariciones/muertes de rangos distintos de 0: rango 1, **25/21**; rango 2, **4/2**. Los bots
sumaron **352 `elites_vistos`** (conteo por cliente de ids distintos, no ids globales únicos). Frente a
la pasada histórica I-14 (11 muertes de jugador), esta ejecución tuvo 4 más; una pasada con rutas y
combates variables no basta para atribuir esa diferencia causalmente a los rangos. Salidas guardadas en
`tools/carga/resultados/20261028-221016-fight-50-redund4-corr/`.

### Catálogo troceado y fuera `DropItem` (I-21, protocolo v14)

El catálogo puede superar el tamaño de un datagrama. El servidor conserva la cabecera y el formato de
cada entrada y lo reparte, en orden, en mensajes `ItemCatalog` de hasta 1400 B. Todos repiten las
reglas de recogida y puños. Los manda por el canal fiable entre `Welcome` e `Inventory`, y al arrancar
valida la codificación con las reglas de la clase 1. El cliente vacía el catálogo al recibir `Welcome`,
añade las entradas de cada parte y actualiza con cada una el alcance de recogida y las reglas de puños.
Un catálogo vacío también produce una parte válida con cero objetos.

Los jugadores no pueden dejar objetos en el suelo: tirar destruye la cantidad seleccionada, igual que
`DestroyItem` (22). El suelo queda para el botín de PNJ; permitir soltar unidades podía llenar en
segundos el tope de 500 objetos y bloquear durante 60 s el botín de todos. El mensaje tipo 21 está
retirado y no se reutiliza. No cambia el esquema SQLite v5.

**Verificación y carga I-21:** `dotnet build Avelora.slnx -v q --nologo` pasó sin advertencias y
`dotnet test Avelora.slnx` pasó **586/586**. La carga prescrita de 30 bots `inventory` durante 120 s con
`-LogCorrections -NuevaBase` conectó **30/30**; el servidor completó **3120 ticks** en 156 s, tick p99
**0,112 ms**, peor **9,039 ms**, 0 sobre 50 ms. Hubo **0 correcciones**, **0 saltos**, **0** rechazos de
red y ninguna línea `[corrección]`. Acciones válidas: 80 equipados, 53 desequipados, 60 movidos y 80
destruidos; las divisiones (10) e intercambios (3) son subconjuntos de los movimientos. Hubo 6805
rechazos semánticos de acciones, sin saturación de cola. El botín generó/recogió **281/281** objetos y
`objetos_descartados=0`; terminó con el suelo vacío. Logs: `tools/carga/resultados/20261028-224633-inventory-30-redund4-corr/`.

### Personajes por cuenta (I-10, protocolo v15, esquema v6)

La autenticación ya no elige un personaje. El flujo es `Challenge` → `Hello` (cuenta y credenciales
cifradas, sin nombre de personaje) → login en `avelora-login` → `CharacterList`. La cuenta queda
reservada desde ese momento, también mientras selecciona. En selección solo se permiten
`CreateCharacter`, `DeleteCharacter` y `SelectCharacter`; los mensajes de juego se descartan y cuentan
como rechazados. Crear, borrar y cargar para seleccionar se ejecutan en el hilo de login. Al seleccionar,
el servidor entra al mundo y envía `Welcome`, catálogo, inventario, progreso y estadísticas como antes.
`characterSelectTimeoutSeconds` (300 s) cierra con `Timeout` si no se completa la selección.

| Mensaje | Tipo | Contenido |
| --- | ---: | --- |
| `CreateCharacter` | 23 | nombre UTF-8 (≤32 B) y `classId` |
| `DeleteCharacter` | 24 | id y confirmación exacta del nombre guardado |
| `SelectCharacter` | 25 | id del personaje de la cuenta autenticada |
| `CharacterList` | 26 | máximo configurado, personajes y clases de `classes.json` |
| `CharacterRejected` | 27 | `NameTaken`, `NameInvalid`, `CharacterLimit`, `CharacterNotFound`, `ClassUnknown` o `ConfirmMismatch` |

`maxCharactersPerAccount` vale **3** por defecto y admite 1–10; el códec permite listas de hasta 10.
Los borrados no cuentan para el límite. Los nombres de personaje se recortan en los extremos y deben
tener 3–16 caracteres: letras ASCII o `áéíóúüñ` (mayúsculas incluidas), dígitos y espacios simples
entre palabras; deben empezar por letra y no pueden ser reservados (`admin`, `administrador`, `gm`,
`moderador`, `mod`, `avelora`, `sistema`, `servidor`, `server`, `system`). Se truncan al límite de
24 bytes UTF-8 de `MaxChatNameBytes`, sin partir una secuencia UTF-8. Los nombres son únicos en todo el
servidor, sin distinguir mayúsculas ni tildes en su caja (`ToLowerInvariant`). Al cargar
`classes.json` se comprueba que una lista con 10 nombres de personaje de 32 bytes y las clases
anunciadas cabe en el límite de 1400 bytes; una configuración que lo supere impide arrancar.

El borrado es lógico: `deleted_utc` excluye el personaje de la lista y de la selección, pero conserva
sus filas de inventario y equipo; el nombre queda libre. El esquema **v6** agrega `name_key` y
`deleted_utc`, y crea un índice único parcial para personajes vivos. La migración v5→v6 rellena las
claves; si había colisiones globales, renombra los posteriores con sufijo numérico y avisa por consola.
Los nombres legacy que no satisfacen las reglas se conservan y también se notifican.

Con `persistenceEnabled: false` se mantiene el mismo protocolo y flujo usando un repositorio en memoria;
cuentas y personajes duran hasta apagar el servidor. El snapshot ahora lleva el `classId` de cada jugador
en `TypeId`. Los bots seleccionan el primer personaje existente, o crean `bot<N>` (añadiendo `x` si el
nombre está ocupado) con una clase anunciada por el servidor; informan `personajes_creados` y
`rechazos_personaje`.

**Verificación y carga I-10:** protocolo v15; esquema SQLite v6; pruebas de selección, validación de
nombres, clases, migraciones, borrado, concurrencia y recuperación persistente. Dos pasadas prescritas,
ambas con `-LogCorrections`:

| Pasada | Conectados | `Welcome` p50 / máximo | Tick p99 con 50 jugadores | Correcciones | Personajes creados / rechazados |
| --- | ---: | ---: | ---: | ---: | ---: |
| `-NuevaBase` | 50/50 | 375 / 485 ms | 0,246 ms | 0 (sin líneas `[corrección]`) | 50 / 0 |
| Misma base | 50/50 | 297 / 437 ms | 0,245 ms | 0 (sin líneas `[corrección]`) | 0 / 0 |

En ambas ejecuciones el tick tuvo **0** sobrepasos de 50 ms; el peor tick fue 8,748 ms en la primera y
10,312 ms en la segunda. El primer `Welcome` p50 sube respecto de los ~250 ms previos por el paso nuevo
de lista/creación/selección; la pasada repetida selecciona el personaje persistido y queda en 297 ms.
Logs: `tools/carga/resultados/20261029-212059-fight-50-redund4-corr/` y
`tools/carga/resultados/20261029-212404-fight-50-redund4-corr/`.

### Penalización por muerte (I-11)

`deathPenaltyPercent` en `levels.json` admite de 0 a 100 (defecto 10; 0 desactiva la penalización).
Solo una muerte causada por un PNJ resta experiencia: el servidor calcula el piso de
`umbral del nivel actual × porcentaje / 100`, limitado a la experiencia que tenga el jugador. No se
reduce el nivel y el nivel máximo no pierde experiencia. `Progress` notifica el cambio en el tick
siguiente, y los informes muestran `muertes_penalizadas` y `xp_perdida_muerte`. No cambia el protocolo
sin introducir su propio cambio de protocolo ni esquema; en aquella entrega el protocolo era v17/esquema v6; ahora son v18/v7.

**Verificación y carga I-11:** build sin advertencias; `dotnet test Avelora.slnx` **666/666**. La carga
prescrita de 50 bots `fight` durante 120 s (`-LogCorrections -NuevaBase`) conectó 50/50 y completó
3201 ticks; p99 **0,068 ms**, peor **8,442 ms**, 0 ticks sobre 50 ms. Hubo 14 muertes de jugador, 13
penalizadas (145 XP perdidas), 266 muertes de PNJ, **0 correcciones y 0 saltos**; la pérdida no puede
dejar XP negativa ni reducir el nivel. La salida de bots no expone XP actual, así que se confirma con
las pruebas del servidor. Logs: `tools/carga/resultados/20261029-215231-fight-50-redund4-corr/`.

### Armas por clase y requisitos (I-12, protocolo v16)

`items.json` declara `weaponType` (texto de 1–16 bytes UTF-8, sin espacios en los extremos) y
`handedness` (`one` por defecto o `two`) en cada arma. `requiredLevel` (1–255, defecto 1) está
permitido en armas y armaduras. Los materiales y pociones no pueden declarar esos campos. Al arrancar,
el servidor valida `weaponType` y `requiredLevel` con el contexto del objeto, valida en `classes.json`
la lista obligatoria `weaponTypes` (0–8 tipos únicos por clase) y comprueba que cada tipo configurado
exista en algún arma. La comprobación cruzada se omite únicamente si `itemsPath` está vacío.

`EquipRules` es la regla común para `Equip`, el intercambio de `Unequip` y la carga: primero valida que
la clase admita el tipo de arma (sin distinguir mayúsculas), después compara el nivel con
`requiredLevel`. Un arma de dos manos ocupa `Weapon` y vacía `Offhand`, pasando el objeto de ese hueco
al inventario en la misma operación. Si no hay hueco libre se rechaza todo el cambio; el hueco que
deja la propia arma nueva cuenta cuando no había arma previa. No se puede equipar en `Offhand` mientras
haya un arma de dos manos. Los intercambios por `Unequip` obedecen las mismas reglas.

Al cargar un personaje persistido, el servidor recorre el equipo en orden de hueco y mueve al primer
hueco libre del inventario lo que ya no cumple clase/nivel o el conflicto de manos. Si el inventario
está lleno, conserva el objeto equipado y escribe un aviso con nombre de personaje e id de objeto; no
se pierde nada. La corrección ocurre antes de recalcular estadísticas y enviar `Inventory`, y no se
cuenta como equipar/desequipar.

En v16 cada entrada de `ItemCatalog` incluye, tras los atributos, `handedness` (u8 0/1),
`requiredLevel` (u8), `usableByClass` (u8 0/1) y `weaponType` (cadena UTF-8 ≤16 B), seguidos del nombre
(≤48 B). Los no-armas llevan `weaponType` vacío; armaduras y materiales son utilizables por clase.
El servidor genera el bit `usableByClass` por jugador: para armas refleja `weaponTypes` de su clase,
sin enviar ni repetir la lista de tipos. Se envía al seleccionar y entrar con el personaje; si las
clases cambiasen en caliente, habría que reenviar el catálogo.

El catálogo mantiene una cabecera de 19 B y cada entrada ocupa 37 B fijos más los bytes de tipo y
nombre; los trozos siguen limitados a 1400 B. El catálogo de ejemplo tiene 18 objetos y se codifica
en **1 trozo de 983 B**, con **417 B** de margen. La fórmula de tamaño especificada (`32 + 3 + 2 + tipo + nombre`)
da 101 B como máximo por objeto, no los 103 B de la estimación textual; con ambas cifras caben 13 objetos
del peor caso en un paquete de 1400 B. Los JSON de carga replican las armas, clases y drops
del servidor: el orco puede soltar el objeto 3103 (5 %), y el orco guerrero los objetos 3104 (8 %) y
3105 (6 %). Los bots `fight` descartan candidatos por clase, nivel o manos y resumen
`equip_omitidos_req`; los bots `inventory` conservan sus acciones aleatorias.

Los contadores nuevos del servidor son `equip_rech_clase`, `equip_rech_nivel`,
`equip_rech_dosmanos`, `equipo_corregido_carga` y `equipo_aviso_carga`, visibles tanto en los informes
periódicos como en el resumen final. No hay mensaje de rechazo nuevo: un equipado rechazado no provoca
un `Inventory` nuevo.

**Verificación y carga I-12:** `dotnet build Avelora.slnx -v q --nologo` sin advertencias;
`dotnet test Avelora.slnx` **693/693**. En la pasada prescrita de 50 bots `fight` durante 120 s
(`-LogCorrections -NuevaBase`) conectaron 50/50; el servidor completó 3201 ticks en 160 s, tick p99
**0,069 ms**, peor **8,370 ms**, 0 ticks sobre 50 ms y **27 082 379 B** enviados. No hubo rechazos de
clase/nivel/dos manos ni correcciones/saltos; `bots.log` no contiene líneas `[corrección]`. Los bots
omitieron **144** candidatos por requisitos; se equiparon **3 armas y 55 armaduras** (58 en el contador
del servidor), hubo **806** ataques válidos (**30** con arma), **247** muertes de PNJ y **339** drops
(todos recogidos). Hubo 15 muertes de jugador frente a las 14 de I-11; una ejecución aleatoria no permite
atribuir la diferencia a las armas de dos manos. `equipo_corregido_carga=0` y `equipo_aviso_carga=0`
con base nueva. Logs: `tools/carga/resultados/20261029-231725-fight-50-redund4-corr/`. El protocolo es
v16 y el esquema sigue v6.

### Ataque a distancia y `CombatEvent` (I-13, protocolo v17)

El alcance es numérico, no depende de nombres de clase, arma u objeto. El Arco corto (3103) pasa de
3 a **6 tiles** y el Bastón de roble (3104) de 2,5 a **5 tiles**; daño y recarga no cambian. El arco
supera el `aggroRadius` de los PNJ actuales (4 y 5), de modo que puede abrir el combate antes de ser
detectado. El bastón iguala el aggro del guerrero, porque el daño mágico a distancia fuerte llegará con
habilidades. Incluso 6 + `attackRangeTolerance` (0,5) = 6,5 queda por debajo de
`interestRadius - interestHysteresis` (11), por lo que el objetivo sigue en el snapshot. El alcance sin
arma de las clases se mantiene en 5.

`ItemCatalog.MaxWeaponRange` fija el máximo de datos en **8**; `items.json` rechaza 8,01 con un mensaje
que indica el límite. `ServerConfig.ValidateAttackRanges(items, classes)` comprueba al arrancar que
`interestRadius` sea estrictamente mayor que el mayor alcance (armas y ataques sin arma) más
`attackRangeTolerance`; se ejecuta también cuando `itemsPath` está vacío. El códec del catálogo no
tenía un límite de alcance de 5, por lo que no hubo que modificarlo. La base SQLite sigue en esquema
v6.

**Línea de visión.** `MapCollisionGrid.HasLineOfSight` recorre con Amanatides–Woo las celdas absolutas
que cruza el segmento entre los centros. Fuera del mapa cuenta como bloqueado; las celdas inicial y
final también se comprueban. Si el segmento pasa exactamente por una esquina, se rechaza si cualquiera
de las celdas laterales está bloqueada: esta regla conservadora evita disparar por la rendija diagonal
entre dos paredes. No reserva memoria y la misma implementación está disponible para los bots.

**Combate.** Tras validar atacante, objetivo, vida y distancia, el servidor valida línea de visión
para **todos** los ataques. Si una pared bloquea, no hay daño, evento ni consumo de recarga; sin mapa,
`CombatSystem` conserva el comportamiento anterior. Para el melé no cambia el resultado normal: con
cajas de 0,35 tiles los centros no pueden quedar a alcance cuerpo a cuerpo atravesando una pared. Un
PNJ en `Return` es inmune a ataques (también al melé), para que un tirador no lo mate por la espalda al
volver a casa; cuenta `ataque_rech_regreso`. Otros contadores nuevos: `ataque_rech_vision` y
`ataques_distancia` (alcance base > 2, sin tolerancia).

**IA de PNJ.** `StuckTicks` cuenta los ticks consecutivos en que el PNJ quería moverse pero no avanzó.
Tras **20 ticks (1 s)** atascado en `Chase`, abandona la persecución y vuelve a casa
(`pnj_persecucion_abandonada`). Tras **40 ticks (2 s)** atascado en `Return`, se teletransporta a
`Home`; en el tick siguiente recupera toda la vida y pasa a `Wander` (`pnj_regreso_forzado`). Deslizar
por una pared mientras avanza no cuenta como atasco. A* sigue pendiente para mapas con interiores.

**Eventos visuales.** El mensaje nuevo de servidor a cliente `CombatEvent` (tipo 28) lleva `N` y hasta
64 entradas de 11 B: atacante y objetivo (i32), daño mitigado (u16) y banderas `Ranged`, `Magical` y
`Killed`. Es cosmético: la vida ya viaja en el snapshot. Se manda un mensaje **no fiable** por
destinatario y snapshot; acumula los eventos entre snapshots, envía los de los que el cliente es
atacante/objetivo o tiene visibles, y luego vacía el buffer. El buffer de zona tiene capacidad 256;
sin sesiones se vacía igualmente. Por destinatario se envían como máximo 64 entradas, y los excesos
se cuentan en `combate_recortados`; el buffer saturado suma `combate_descartados`. El mensaje no se
mezcla con `bytes enviados` de snapshot.

Métricas de red: `combate_msgs`, `combate_entradas`, `combate_bytes` y `combate_recortados` (junto a
`combate_descartados`). Los bots `fight` usan la misma línea de visión: si están a alcance pero tras una
pared continúan caminando y omiten el ataque (`ataques_omitidos_vision`); cuentan los eventos recibidos
(`combate_recibidos`) y los ataques enviados con alcance base > 2
(`ataques_distancia_enviados`). `BotBehavior` conserva sus valores y numeración.

**Coste de red estimado:** con las cifras de I-12, unos 1 500 eventos × 10 destinatarios × 13 B
equivalen a ~200 KB frente a 27 082 379 B de snapshots (menos del 1 %). La carga del 30/10
(`tools/carga/resultados/20261030-214716-fight-50-redund4-corr/`) midió `combate_bytes` de
**192 524 B**, el **0,68 %** de **28 430 301 B** de snapshots, en línea con la estimación (~200 KB).
Hubo **0 recortados, 0 descartados, 0 correcciones** y tick p99 de **0,063 ms**.
Sin embargo, dio **`ataques_distancia = 0`** por un defecto previo del bot: comparaba el daño base
del arma con `FistDamage`, que incluye el bono de atributo. Ya está corregido: sin arma equipada,
el umbral es 0; el resultado a distancia queda pendiente de repetir la carga.
El cambio de datos de carga tampoco es una repetición idéntica de I-12: allí solo 0,05
del Orco / 0,08 del Orco guerrero daban los objetos 3103/3104; la copia de carga de I-13 sube ambas
probabilidades a **0,3** para generar suficientes ataques a distancia. Los JSON del servidor no
cambian esas probabilidades.

**Verificación I-13:** `dotnet build Avelora.slnx -v q --nologo` y `dotnet test Avelora.slnx` se
documentan en `docs/agentes/informes/I-13.md`; la suite tiene **709 pruebas**. El protocolo es v17,
sin migración SQLite. La hoja de ruta mantiene pendiente el cliente I-13b.

### Botín directo, dueño del botín y oro (I-25, protocolo v18, esquema v7)

El botín directo, recoger con E y desequipar automáticamente usan todos los huecos del inventario desde el 0. Se completan primero las pilas existentes. Ningún hueco está reservado para la barra rápida. Sin espacio, el botín directo cae reservado para su dueño y recoger con E se rechaza.

**Dueño.** El primer jugador que acierta marca al PNJ durante 10 s; sus siguientes golpes renuevan
la marca. Si no vuelve a acertar en ese plazo, otro golpe aceptado puede cambiarla. Si el dueño ya no
está en la zona, la marca pasa al último atacante presente; si ninguno está, no se entrega botín,
oro ni experiencia. El dueño puede estar muerto mientras siga en la zona. La marca guarda un id de
entidad: al desconectarse y volver se asigna otro, así que pierde el dueño anterior.

**Reparto.** Los objetos que caben enteros entran directamente al inventario. Si una pila completa
no cabe, cae entera al suelo reservada al dueño durante 30 s; después cualquiera puede recogerla con
E. E sigue siendo necesaria para esos objetos caídos (y para objetos ajenos cuya reserva ya venció).
No se divide una pila entre inventario y suelo. Los objetos caducan según `groundItemSeconds` y el
límite de entidades del suelo sigue aplicándose.

**Oro.** `npcs.json` declara `goldMin`/`goldMax` (0..10 000) por tirada, igual que los tipos de carga;
cada tirada de un élite suma oro por separado (máximo actual por muerte: 5 × 10 000). El personaje
no supera **1 000 000 000** de oro. El oro viaja en `Inventory` como `u32`, no en `Progress`: el botín
cambia huecos y oro en la misma operación y el mensaje fiable mantiene ambos coherentes; `Progress`
sigue siendo nivel, experiencia y vida. SQLite v7 añade `characters.gold` con valor inicial 0 y migra
las bases anteriores.

`LootGained` (tipo 29) lleva experiencia, oro y hasta 32 objetos con la marca `OnGround`. Es fiable y
ordenado, se envía solo al dueño después del `Inventory` que ya refleja el cambio. Si no hubo ninguna
recompensa efectiva, no se envía. Las métricas de simulación son `cambios_dueno`, `pnj_sin_dueno`,
`oro_otorgado`, `botin_directo`, `botin_suelo_lleno`, `botin_lineas_recortadas` y
`recogida_rech_reservada`; las de red `botin_msgs`/`botin_bytes`. Los bots contabilizan
`botin_recibidos`, `botin_exp`, `botin_oro`, `botin_objetos`, `botin_al_suelo` y `oro_final`; equipan
armas y armaduras al recibir el inventario, también cuando el objeto no necesitó recogida.

La carga `fight` compara esos contadores con experiencia/oro de los bots, equipamiento, bytes y
correcciones. I-25: build sin advertencias ni errores, **734/734** pruebas; carga de 50 bots/120 s,
50/50 conectados, **0 correcciones**, tick p99 **0,039 ms**, peor tick **9,179 ms**, **25 638 577 B**
enviados, 311 muertes/311 `botin_msgs`, 0 PNJ sin dueño, 577 objetos directos y 13 armas/77 armaduras
equipadas. El servidor registró 1355 de oro y los bots 1353; también difieren en 18 XP y un mensaje
(311 en servidor, 310 recibidos por bots). La última recompensa coincidió con el cierre escalonado de
los bots (el servidor siguió hasta 160 s); la carga es consistente salvo ese mensaje final no observado
por un bot. Sin `[corrección]` en los logs. Informe: `docs/agentes/informes/I-25.md`.

**Trabajo futuro:** grupos con reparto estilo WoW (dueño = grupo, turnos o tiradas) y mostrar al
cliente quién es el dueño de un PNJ. El cliente Unity incorpora I-25b para protocolo v18; I-15b incorporó v19; el cliente I-15pb para v20 sigue pendiente.

### Zonas seguras y PvP (I-15, protocolo v19)

`zones.json` declara zonas por `id`, nombre descriptivo, `kind` (`safe` o `pvp`) y uno o más
rectángulos de casillas (`x`, `y`, `w`, `h`):

```json
{
  "zones": [
    { "id": 1, "name": "Ciudad", "kind": "safe", "rects": [ { "x": -30, "y": -22, "w": 60, "h": 44 } ] },
    { "id": 2, "name": "Arena del Este", "kind": "pvp", "rects": [ { "x": 44, "y": 28, "w": 14, "h": 10 } ] }
  ]
}
```

`zonesPath` resuelve el archivo junto a `server.json`; vacío significa que
todo el mapa es normal y conserva el arranque anterior. Se admiten hasta 32 zonas, 16 rectángulos por
zona y 128 en total; cada rectángulo debe caber dentro del mapa y usar coordenadas i16. Los bordes son
inferior/izquierdo inclusivos y superior/derecho exclusivos. En solapes, `safe` prevalece sobre `pvp`,
y entre zonas del mismo tipo gana la primera declarada. Los nombres solo están en JSON para lectura y
diagnóstico: la red transmite id, tipo y rectángulos.

El servidor envía `ZoneList` fiable y ordenado inmediatamente después de `Welcome`, solo si hay zonas.
Es un mensaje separado para mantener el `Welcome` de tamaño fijo (69 B); con cero zonas no se añade
ningún mensaje. `ZoneChanged` se envía al jugador afectado, solo al cambiar de zona (incluye volver a
id 0 / `Normal`); también detecta la zona del punto de aparición y la reaparición en el tick.

En `safe` no se puede atacar desde dentro ni hacia dentro: el rechazo ocurre antes de distancia,
visión y recarga, por lo que no consume el cooldown. Los PNJ ignoran jugadores seguros, cancelan la
persecución y vuelven a casa; una segunda rejilla los detiene en el borde de la zona. Al iniciar,
`ValidateSpawners` rechaza cualquier radio de aparición/deambulación que toque una casilla segura.
El botín sigue perteneciendo al jugador marcado aunque entre en una zona segura. `pvp` solo identifica
el área: el combate entre jugadores se describe en [I-15p](#combate-entre-jugadores-i-15p-protocolo-v20-esquema-v8).
No cambia el esquema SQLite, que sigue en v7.

Las métricas del servidor incluyen `ataque_rech_zona_segura`, `aggro_cancelado_zona`, `cambios_zona`,
`entradas_zona_segura`, `zona_msgs` y `zona_bytes`. Los bots informan `zona_listas`, `zona_cambios`,
`zona_segura_entradas` y `zona_pvp_entradas`. La configuración de carga usa `zones-carga.json`, copia
idéntica a `zones.json`; las dos pasadas de 50 bots (fight 120 s y random 60 s) dejaron estos resultados: en fight (120 s) tick p99 **0.061 ms** (I-25: 0.039), 0 correcciones, 265 muertes de PNJ y 265 `botin_msgs` (I-25: 311; los bots salen de la ciudad antes de luchar), `zona_msgs=243` = `zona_cambios` de los bots, `zona_listas=50/50`, `entradas_zona_segura=115`; en random (60 s) p99 **0.069 ms**, **0.24** correcciones por bot y minuto (14 muertes, inputs en vuelo), `zona_msgs=297` = `zona_cambios`, `aggro_cancelado_zona=8`. Logs en `tools/carga/resultados/20261031-222140-fight-50-redund4-corr/` y `20261031-222426-random-50-redund4-corr/`.
La suite de I-15 añade pruebas de protocolo, límites/bordes/solapes, mapa real, IA, combate, red y bots. Build sin advertencias; **760/760** pruebas.

**Fuera de alcance de I-15:** el combate PvP se añadió en [I-15p](#combate-entre-jugadores-i-15p-protocolo-v20-esquema-v8); editor visual y servicios de ciudad siguen fuera. Los clientes I-15b e I-15pb están hechos; I-15q ajusta los tiempos y rótulos.

### Combate entre jugadores (I-15p, protocolo v20, esquema v8)

Cada jugador elige **PvE** (por defecto) o **PvP**. El modo solo cambia tras **30 segundos seguidos
en zona segura**, o **60 segundos** si se entra con PvP efectivo; esa espera se fija al entrar y no
disminuye si el forzado termina dentro. Salir reinicia la espera. Fuera de ella no se puede cambiar. En zona PvP todos
tienen **PvP forzado** y al salir conservan el forzado otros **5 minutos**, pero se cancela al entrar en una zona
segura. El PvP efectivo es el modo elegido PvP, estar en zona PvP o tener tiempo forzado restante.

Dos jugadores pueden dañarse en zona normal o PvP si ambos tienen PvP efectivo, están vivos y ninguno
está en zona segura. Alcance, línea de visión, defensa y recarga se resuelven en el servidor como
contra PNJ. Una muerte PvP no da experiencia al atacante ni penaliza la de la víctima; se mantiene
la reaparición de I-03. El forzado sigue corriendo estando muerto fuera de la ciudad; dentro de una zona segura se cancela al entrar.

SQLite migra automáticamente de v7 a **v8**: `pvp_mode` conserva el modo elegido y
`pvp_forced_seconds` el tiempo restante, redondeado hacia arriba. El forzado solo corre conectado:
desconectarse no lo elimina. Dentro de la arena se recarga cada tick; desconectarse allí conserva
los 300 segundos y la posición. La espera en segura no se guarda: conectar o reaparecer empieza en 0.
La espera de 60 segundos se decide según el PvP efectivo del tick de entrada a la zona segura.
Se permite cambiar el modo elegido durante el forzado; el efectivo sigue PvP hasta agotarlo.

`SetPvpMode(mode)` (2 B) pasa por la cola fiable de acciones. `PvpModeRejected(reason)` (2 B) distingue
`NotInSafeZone` y `TooSoon`. Pedir el modo actual no envía nada y cuenta en `pvp_modo_igual`.
`PvpStatus` (8 B) se envía solo al propio jugador, en el primer tick y cuando cambia el modo, tipo
de forzado o fase de cambio. Incluye segundos para que el cliente descuente localmente, sin envíos
por segundo. Sin zonas configuradas no se envía este estado; las reglas y temporizadores siguen
funcionando. Los demás jugadores reciben el PvP efectivo en el **bit 6** del byte acción/rango del
snapshot, solo para `Player`, sin aumentar los 15 B por entidad.

`server.json`: `pvpModeChangeSeconds=30`, `pvpModeChangeFromPvpSeconds=60` y `pvpForcedSeconds=300`,
los tres entre 1 y 3600. La espera de 60 segundos se decide con el PvP efectivo previo a la cancelación.
El HUD muestra «PvP activo»; solo cuenta el forzado tras la arena cuando el modo elegido es PvE. El botón de
la ciudad conserva la cuenta atrás para cambiar de modo. La carga
usa **5/10/30 segundos**. Bots: `--pvp-bots N` (o `-PvpBots N` en el guion) selecciona bots
que piden PvP al estar listos (esperan quietos en la zona segura hasta entonces) y pueden perseguir jugadores PvP visibles; `BotBehavior` no cambia.

Métricas: `pvp_ataques_ok`, `pvp_muertes`, `ataque_rech_pvp_segura`, `ataque_rech_pvp_atacante_pve`,
`ataque_rech_pvp_objetivo_pve`, `ataque_rech_pvp_muerto`, `ataque_rech_pvp_propio`, `pvp_cambios_modo`,
`pvp_modo_igual`, `pvp_rech_fuera_segura`, `pvp_rech_pronto`, `pvp_estado_msgs`, `pvp_estado_bytes`,
`pvp_rech_msgs`. Los bots cuentan `pvp_estados`, `pvp_rechazos` y `pvp_modo_pedidos`.

Verificación I-15p/I-15q/I-15r: **809/809** pruebas, build con 0 advertencias. Carga de 50 bots: `fight` 120 s con
`-PvpBots 10 -LogCorrections -NuevaBase` y `random` 60 s con `-LogCorrections -NuevaBase`;
Fight tras el arreglo: `pvp_ataques_ok`=0, 10 peticiones y 10 cambios de modo, 1224 rechazos por zona segura y 0 correcciones; no se repitió la carga ni se ajustó más el comportamiento.
Las cifras y comparación con I-15 están en [el informe I-15p](docs/agentes/informes/I-15p.md).
El cliente Unity I-15pb usa protocolo v20; I-15q también ajusta en él los rótulos y la cuenta atrás; I-15r
cancela el forzado al entrar en zona segura.

### Habilidades (I-16, protocolo v21)

El servidor carga `skills.json`: una habilidad de daño de objetivo único por clase, con id, nombre,
clase, nivel mínimo, coste, recarga, alcance, daño y tipo físico o mágico. El daño usa la fórmula del
ataque básico sustituyendo el arma por la habilidad, sin sumar el arma equipada; aplica la misma
mitigación, alcance con tolerancia, visión, zonas seguras y reglas PvP. Marca aggro, dueño del botín,
asesino, experiencia y `CombatEvent` como un ataque. El servidor decide todos los resultados.

`resource.regenPerSecond` en `classes.json` es opcional (0 por defecto, 0..1000): regenera enteros
una vez por segundo, solo vivo y hasta el máximo. Furia 4, Maná 3 y Energía 6; reaparecer no rellena.
El recurso se guarda en `mana` (esquema v8); las recargas por jugador y habilidad no se guardan y
son independientes de `LastAttackTick`. Un rechazo no consume recurso, recarga ni vida del objetivo.

El primer fallo determina el rechazo: habilidad desconocida, clase, nivel, lanzador muerto,
objetivo inválido, zona segura, PvP no permitido, distancia, visión, recarga o recurso insuficiente.
Ataques y habilidades comparten `AttackQueue`, orden de llegada y límites.

`SkillCatalog` se envía una vez al entrar, tras `ItemCatalog`, filtrado por clase y con el nombre
del recurso. `UseSkill` pide usarla; `SkillUsed` confirma al dueño, que cuenta la recarga con el
catálogo; `SkillRejected` devuelve el motivo. `ResourceStatus` solo va al dueño, al primer tick
y cuando cambia el recurso actual o máximo; regenerar no reenvía `Stats` ni añade datos al snapshot.
`skillsPath` por defecto es `skills.json`, relativo al archivo de configuración. Vacío desactiva
catálogo, regeneración y `ResourceStatus`; las peticiones se rechazan como `UnknownSkill`.

Métricas: `hab_usadas`, `hab_rechazadas`, `hab_catalogo_bytes`, `recurso_msgs`, `recurso_bytes`;
el resumen desglosa `hab_rech_desconocida`, `hab_rech_clase`, `hab_rech_nivel`, `hab_rech_muerto`,
`hab_rech_objetivo`, `hab_rech_segura`, `hab_rech_pvp`, `hab_rech_distancia`, `hab_rech_vision`,
`hab_rech_recarga` y `hab_rech_recurso`. Bots: `--skill-bots N` (0 por defecto), primeras N
instancias; esperan recurso y recarga con margen. Resumen: `hab_enviadas`, `hab_ok`,
`hab_rechazadas`, `recurso_msgs`.

Carga: `.\tools\carga\carga.ps1 -Bots 50 -Seconds 120 -Behavior fight -SkillBots 25 -PvpBots 10 -LogCorrections -NuevaBase`.
Verificación histórica del servidor I-16: **849/849** pruebas, build con 0 advertencias. Cifras: [informe I-16](docs/agentes/informes/I-16.md).

Cliente I-16b/I-16c (v21): K abre las habilidades y la barra rápida tiene diez accesos propios,
independientes del inventario (I muestra todos sus huecos). Arrastrar un objeto desde I crea un
acceso a su tipo sin moverlo ni enviar MoveItem; desde K crea un acceso de habilidad. Dentro de
la barra los accesos se mueven o intercambian. Clic derecho o arrastrar fuera de la barra retira
solo el acceso, sin tirar ni destruir objetos; soltar sobre un panel que tapa el destino cancela.
El acceso de objeto muestra la cantidad total en el inventario; al agotarse queda apagado con 0.
Las teclas 1–0 usan el primer hueco con ese tipo de objeto (sin existencias: «No te quedan») o la
habilidad sobre el objetivo, con recarga, coste y bloqueo por nivel. Los accesos se guardan en
PlayerPrefs por personaje, solo en este ordenador; las antiguas claves de habilidades se migran.
Verificación y comprobaciones pendientes en Play: [informe I-16c](docs/agentes/informes/I-16c.md).


#### Áreas y curación (I-17, protocolo v22)

El catálogo contiene seis habilidades. `effect` vale `damage` (por defecto) o `heal`;
`radius` vale 0 para un objetivo o 0.5..8 para área; `maxTargets` vale 2..16 (sin radio debe ser 0).
I-19 añade curación con radio centrada en el lanzador; el área ofensiva de I-17 sigue centrada en el objetivo.

El área se centra en el objetivo principal, validado con las reglas de I-16, alcance y visión
desde el lanzador. Incluye el borde del radio sin tolerancia. Los secundarios pasan por
`CheckTarget` (vivos, distintos del lanzador, PNJ fuera de Return, zonas seguras y PvP efectivo)
y se omiten en silencio si fallan. La visión se comprueba desde el centro a cada secundario.
El tope elige los más cercanos; empates conservan el orden de `EntityStore`. El bruto se calcula
una vez y cada objetivo lo mitiga; botín, aggro, muerte y eventos se atribuyen por objetivo.
Recurso y recarga se cobran una vez por lanzamiento.

Se cura a uno mismo o a cualquier jugador vivo: PNJ, muertos e inexistentes son `InvalidTarget`.
Un lanzador sin PvP efectivo no puede curar a quien lo tiene (`PvpNotAllowed`); las zonas seguras
permiten curar. Alcance y visión se comprueban salvo sobre uno mismo. Vida llena produce
`FullHealth = 12`, sin gasto ni recarga. Se usa la fórmula de habilidad sin mitigación y se
recorta al déficit de vida; no cambia daño reciente, regeneración, acción, botín ni amenaza.

`CombatEvent` mantiene entradas de 11 B: `Heal = 8`, atacante sanador, objetivo curado y cantidad
real en `Damage`, sin otras banderas. `SkillCatalogEntry` añade `effect u8` (0 daño, 1 cura) y
`radius f32`; `maxTargets` no viaja. `SkillUsed` mide 7 B e incluye `targetId i32`: solo el
lanzador recibe la confirmación para dibujar el área; observadores reciben eventos de combate.

Métricas nuevas: `hab_area_objetivos`, `hab_area_tope`, `hab_curas`, `hab_cura_total` y, en el
resumen, `hab_rech_vida_llena`. Los bots priorizan área y se curan bajo el 70 % de vida;
publican `hab_curas_enviadas`. Suite: **898/898** pruebas (39 nuevas). Validación y carga: [informe I-17](docs/agentes/informes/I-17.md).

#### Estados temporales (I-18, protocolo v23)

`skills.json` añade `status` (`bonus`, `shield`, `slow` o `taunt`), `statusSeconds` y
`statusAmount`. `effect` admite `damage`, `heal` o `none`; este último requiere daño 0 y un estado.
En `SkillCatalogEntry`, `effect` vale 0/1/2 y se añaden `statusKind u8`, `statusTicks u16` y
`statusAmount u16` (5 B por habilidad). I-19 añade `hits u8`, `areaCenter u8` y `critBonus u16`;
la cabecera de `SkillCatalog` lleva `globalCooldownTicks u16` (protocolo v25).
El servidor valida duración de 0,5..30 s, convierte a ticks con redondeo hacia arriba y rechaza
estados en área salvo `shield` aplicado por una cura en área. Hay cuatro tipos y como máximo uno de cada tipo por entidad; una aplicación más
débil se ignora —en el escudo se compara la cantidad restante— y una igual o mayor reemplaza
cantidad, lanzador y caducidad. Caducan al principio
del tick límite; la muerte limpia todos los estados y no se guardan en SQLite.

`Slow` y `Taunt` son dañinos y comparten las reglas de objetivo del ataque: PvP efectivo, zonas
seguras y PNJ en `Return`; provocar solo acepta PNJ. `Bonus` y `Shield` son beneficiosos: uno mismo
o un jugador vivo, con el límite PvP de curación; la zona segura no los impide. El bonus suma al
daño bruto y a defensa/resistencia, sin alterar máximos de vida o recurso; al cambiar se publica
`Stats`. El escudo absorbe después de mitigar: `CombatEventFlags.Absorbed = 16` informa lo absorbido
en una entrada aparte y el evento normal solo se envía si queda daño. La ralentización (10..80 %)
afecta a jugadores y PNJ con la misma velocidad efectiva; el dueño recibe `StatusEffects` al cambiar
la lista para que predicción y reconciliación usen la velocidad nueva. Provocar fija al PNJ en `Chase`
contra el lanzador; si vuelve o cambia de objetivo, el estado se rompe, y al caducar no restaura un
objetivo anterior.

El guerrero recibe Provocar; el mago, Rayo helado y Escudo arcano; el sanador, Bendición; el ranger,
Tiro a las piernas. `StatusEffects` (40) lleva velocidad y tipos, habilidades, ticks restantes
y cantidades solo al dueño; `StatusEvent` (41) anuncia máscaras a los observadores visibles cuando
cambian, sin duración. Ninguno modifica el snapshot. Métricas: `est_bonus`, `est_escudo`, `est_lento`,
`est_provocar`, `est_ignorados`, `est_expirados`, `est_muerte`, `est_provocar_roto`, `est_absorbido`,
`est_msgs`, `est_bytes`, `est_evt_msgs` y `est_evt_bytes`; los bots informan `est_enviadas`, `est_msgs`
y `est_lento_msgs`. Suite: **933/933** pruebas. El cliente compatible queda pendiente de I-18b.
No hay estados en área fuera del escudo de una cura en área, estados periódicos, aturdimiento ni desplazamiento forzado.

#### Simulador de equilibrio (I-26)

El simulador reutiliza los catálogos del servidor y presenta DPS de objetivo único, TTK y vida perdida contra cada tipo de PNJ:

```powershell
dotnet run --project tools/Avelora.Balance -- --niveles 1,5,10,25 --build puro --equipo ninguno
```

Opciones: `--niveles`, `--build puro|equilibrado`, `--equipo ninguno|franja`, `--sin-habilidades`, `--datos <carpeta>` y `--salida <archivo>`. La suite suma **977/977** pruebas; la tabla de referencia sin habilidades coincide con §1 del [plan de combate](docs/diseno/plan-combate-equilibrio.md). Informe: [I-26](docs/agentes/informes/I-26.md).

#### Modelo de daño, vida y defensa (I-27)

La vida y las defensas derivadas de clase crecen por nivel; el daño usa un multiplicador entero de `powerPerPoint` y las habilidades suman su porcentaje del arma (`weaponPercent`). El bonus se añade a la base del daño antes del multiplicador. Los ataques de PNJ seleccionan defensa o resistencia mágica mediante `damageType`. Protocolo v23 y esquema v8 no cambian; **996/996** pruebas.

#### Crítico, prisa y PvP (I-28)

El ataque básico esperado suma el crítico (`critBase + DES × critPerDex`, limitado por `critCap`) y reduce su recarga con la prisa (`DES × hastePerDex`); ambos usan la DES efectiva con equipo. En PvP el daño se mitiga primero y luego se multiplica por `pvpDamagePercent` (60 %); las curas no se reducen. `Stats` (formato introducido en v24) comunica crítico y prisa en puntos básicos; el servidor usa una semilla de combate configurable y las habilidades comparten una recarga global de 1 s.

| Nivel | Build | Guerrero | Mago | Ranger | Sanador | Sanador / media |
| ---: | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | puro | 14,28 | 15,16 | 14,63 | 11,16 | 76,0 % |
| 1 | equilibrado | 14,28 | 15,16 | 14,62 | 10,16 | 69,2 % |
| 5 | puro | 21,67 | 20,84 | 21,02 | 16,26 | 76,8 % |
| 5 | equilibrado | 19,94 | 19,85 | 18,87 | 15,23 | 77,9 % |
| 10 | puro | 33,83 | 34,10 | 34,48 | 24,40 | 71,5 % |
| 10 | equilibrado | 29,52 | 30,31 | 28,39 | 22,36 | 76,0 % |
| 15 | puro | 53,84 | 51,15 | 53,86 | 39,63 | 74,8 % |
| 15 | equilibrado | 45,99 | 43,57 | 45,62 | 33,53 | 74,4 % |
| 20 | puro | 73,77 | 68,20 | 73,37 | 52,86 | 73,6 % |
| 20 | equilibrado | 61,62 | 56,84 | 60,45 | 43,69 | 73,3 % |

I-30 afirma los objetivos 1, 3, 7 y 8 con equipo de franja. Todas las parejas cumplen 8–15 s sin sanador y 8–28 s con sanador. Rotación incluye nivel 20 y mantiene la excepción 8–35 % de Lluvia de flechas. D1–D4 aprobadas por el usuario.

#### Habilidades por clase (I-19, protocolo v25)

`skills.json` define cuatro habilidades por clase (cinco para el sanador), desbloqueadas en niveles 1, 5, 10 y 20; se retira el id 4002. `hits` aplica 1..5 golpes con un crítico independiente por golpe, `critBonus` suma al multiplicador crítico, `areaCenter: self` centra el área en el lanzador y las curas en área siguen las reglas PvP y de línea de vista de la curación individual. El bot selecciona por catálogo, nivel, recurso, alcances y recargas. `SkillCatalog` v25 incluye recarga global en la cabecera y `hits`, `areaCenter` y `critBonus` por entrada.

| Clase | Id | Habilidad | Nivel | Coste | Recarga (s) | Da?o | Arma (%) |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: |
| Guerrero | 4001 | Golpe poderoso | 1 | 25 | 6 | 5 | 130 |
| Guerrero | 4007 | Provocar | 5 | 20 | 10 | 0 | 0 |
| Guerrero | 4011 | Torbellino | 10 | 15 | 10 | 12 | 150 |
| Guerrero | 4012 | Furia imparable | 20 | 40 | 60 | 0 | 0 |
| Mago | 4008 | Rayo helado | 1 | 25 | 6 | 9 | 160 |
| Mago | 4009 | Escudo arcano | 5 | 30 | 15 | 0 | 0 |
| Mago | 4005 | Bola de fuego | 10 | 20 | 8 | 9 | 120 |
| Mago | 4013 | Lluvia de meteoros | 20 | 10 | 40 | 30 | 400 |
| Ranger | 4003 | Disparo certero | 1 | 20 | 6 | 6 | 120 |
| Ranger | 4014 | Tiro a las piernas | 5 | 10 | 8 | 12 | 100 |
| Ranger | 4015 | Ráfaga | 10 | 20 | 10 | 4 | 50 |
| Ranger | 4016 | Lluvia de flechas | 20 | 10 | 40 | 35 | 400 |
| Sanador | 4006 | Curación | 1 | 25 | 3 | 20 | 0 |
| Sanador | 4004 | Castigo | 1 | 20 | 5 | 6 | 100 |
| Sanador | 4010 | Bendición | 5 | 30 | 20 | 0 | 0 |
| Sanador | 4017 | Curación en área | 10 | 45 | 12 | 16 | 0 |
| Sanador | 4018 | Renovación divina | 20 | 70 | 60 | 30 | 0 |

Objetivo 8, rotación equilibrada con equipo de franja (porcentaje del daño total):

| Nivel | Clase | Habilidad | Lanzamientos | Daño (%) |
| ---: | --- | --- | ---: | ---: |
| 1 | Guerrero | Golpe poderoso | 30 | 19,57 |
| 1 | Mago | Rayo helado | 25 | 24,99 |
| 1 | Ranger | Disparo certero | 30 | 22,21 |
| 1 | Sanador | Castigo | 32 | 24,24 |
| 5 | Guerrero | Golpe poderoso | 30 | 23,87 |
| 5 | Mago | Rayo helado | 27 | 26,54 |
| 5 | Ranger | Disparo certero | 30 | 18,45 |
| 5 | Ranger | Tiro a las piernas | 23 | 15,85 |
| 5 | Sanador | Castigo | 33 | 21,95 |
| 10 | Guerrero | Golpe poderoso | 21 | 14,44 |
| 10 | Guerrero | Torbellino | 18 | 16,75 |
| 10 | Mago | Bola de fuego | 15 | 11,04 |
| 10 | Mago | Rayo helado | 16 | 14,71 |
| 10 | Ranger | Disparo certero | 27 | 14,27 |
| 10 | Ranger | Ráfaga | 18 | 13,25 |
| 10 | Ranger | Tiro a las piernas | 23 | 12,98 |
| 10 | Sanador | Castigo | 35 | 21,50 |
| 15 | Guerrero | Golpe poderoso | 21 | 14,25 |
| 15 | Guerrero | Torbellino | 18 | 15,71 |
| 15 | Mago | Bola de fuego | 15 | 10,54 |
| 15 | Mago | Rayo helado | 18 | 15,81 |
| 15 | Ranger | Disparo certero | 27 | 13,67 |
| 15 | Ranger | Ráfaga | 18 | 11,83 |
| 15 | Ranger | Tiro a las piernas | 23 | 11,59 |
| 15 | Sanador | Castigo | 36 | 21,06 |
| 20 | Guerrero | Golpe poderoso | 21 | 14,07 |
| 20 | Guerrero | Torbellino | 18 | 15,29 |
| 20 | Mago | Bola de fuego | 17 | 10,41 |
| 20 | Mago | Lluvia de meteoros | 5 | 10,21 |
| 20 | Mago | Rayo helado | 16 | 12,41 |
| 20 | Ranger | Disparo certero | 26 | 11,94 |
| 20 | Ranger | Ráfaga | 18 | 10,61 |
| 20 | Ranger | Lluvia de flechas | 5 | 8,23 |
| 20 | Ranger | Tiro a las piernas | 23 | 10,23 |
| 20 | Sanador | Castigo | 36 | 20,37 |

La supervisión aplica D1–D4: rotación en banda en niveles 1/5/10/15/20, con Lluvia de flechas en 8–35 %. Todas las duraciones de duelos con habilidades quedan afirmadas; victorias informativas en el [informe I-30](docs/agentes/informes/I-30.md). Cliente I-30b pendiente.

#### Contenido de referencia (I-30)

Armas añadidas (nivel 20 ajustado por supervisión; recarga, alcance, manos y tipo de daño se heredan de la franja 5):

| Id | Nombre | Nivel | Tipo | Daño |
| ---: | --- | ---: | --- | ---: |
| 3109 | Hacha de acero | 10 | Hacha | 23 |
| 3110 | Bastón de ébano | 10 | Bastón | 20 |
| 3111 | Arco de tejo | 10 | Arco | 18 |
| 3112 | Maza de bronce | 10 | Maza | 14 |
| 3113 | Hacha de batalla | 15 | Hacha | 31 |
| 3114 | Bastón de cristal | 15 | Bastón | 26 |
| 3115 | Arco compuesto | 15 | Arco | 24 |
| 3116 | Maza de acero | 15 | Maza | 19 |
| 3117 | Hacha de runas | 20 | Hacha | 37 |
| 3118 | Bastón de runas | 20 | Bastón | 30 |
| 3119 | Arco de runas | 20 | Arco | 28 |
| 3120 | Maza de runas | 20 | Maza | 22 |

Armaduras añadidas (orden: cabeza, cuerpo, piernas, manos, pies, mano secundaria; resistencia mágica 0 si no se indica):

| Id | Nombre | Nivel | Hueco | Defensa | Res. mágica | Bonus |
| ---: | --- | ---: | --- | ---: | ---: | --- |
| 3313 | Casco de acero | 10 | head | 3 | 1 | — |
| 3314 | Cota de placas | 10 | body | 7 | 2 | — |
| 3315 | Grebas de acero | 10 | legs | 4 | 1 | — |
| 3316 | Guantes de acero | 10 | hands | 2 | 0 | DES +1 |
| 3317 | Botas de acero | 10 | feet | 2 | 0 | moveSpeed +5 |
| 3318 | Escudo de acero | 10 | offhand | 4 | 1 | — |
| 3319 | Yelmo de placas | 15 | head | 9 | 5 | — |
| 3320 | Coraza de placas | 15 | body | 17 | 5 | — |
| 3321 | Grebas de placas | 15 | legs | 9 | 5 | — |
| 3322 | Guanteletes | 15 | hands | 3 | 0 | DES +1 |
| 3323 | Botas de placas | 15 | feet | 3 | 0 | moveSpeed +5 |
| 3324 | Escudo de torre | 15 | offhand | 3 | 1 | — |
| 3325 | Yelmo de runas | 20 | head | 22 | 25 | — |
| 3326 | Coraza de runas | 20 | body | 35 | 25 | — |
| 3327 | Grebas de runas | 20 | legs | 21 | 25 | — |
| 3328 | Guanteletes de runas | 20 | hands | 8 | 0 | DES +1 |
| 3329 | Botas de runas | 20 | feet | 8 | 0 | moveSpeed +5 |
| 3330 | Escudo de runas | 20 | offhand | 0 | 25 | — |

PNJ nuevos (todos: caja 0,35; alcance 1,0; recarga 1,5 s; reaparición 20 s; aggro 5, correa 8):

| Id | Nombre | Nivel | PV | Daño | Defensa | Res. mágica | Tipo | Vel. | XP | Oro |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: |
| 2003 | Orco veterano | 10 | 147 | 34 | 10 | 0 | physical | 3,2 | 350 | 20–40 |
| 2004 | Chamán orco | 15 | 206 | 49 | 15 | 15 | magical | 3,0 | 1250 | 40–80 |
| 2005 | Orco acorazado | 20 | 289 | 84 | 20 | 0 | physical | 3,0 | 3250 | 80–150 |

Spawners (`npcs.json`: count 3/3/3/3/2/2; carga: 4/4/4/4/2/2; radio 3,0):

| Tipo | X | Y |
| ---: | ---: | ---: |
| 2003 | -61,5 | -15,5 |
| 2003 | -34,5 | 52,5 |
| 2004 | 60,5 | -28,5 |
| 2004 | 48,5 | -52,5 |
| 2005 | 72,5 | -40,5 |
| 2005 | 72,5 | 40,5 |

DPS esperado con ataque básico (tabla recalculada de §2.8):

| Nivel | Build | Guerrero | Mago | Ranger | Sanador | Sanador / media |
| ---: | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | puro | 14,28 | 15,16 | 14,63 | 11,16 | 76,0 % |
| 1 | equilibrado | 14,28 | 15,16 | 14,62 | 10,16 | 69,2 % |
| 5 | puro | 21,67 | 20,84 | 21,02 | 16,26 | 76,8 % |
| 5 | equilibrado | 19,94 | 19,85 | 18,87 | 15,23 | 77,9 % |
| 10 | puro | 33,83 | 34,10 | 34,48 | 24,40 | 71,5 % |
| 10 | equilibrado | 29,52 | 30,31 | 28,39 | 22,36 | 76,0 % |
| 15 | puro | 53,84 | 51,15 | 53,86 | 39,63 | 74,8 % |
| 15 | equilibrado | 45,99 | 43,57 | 45,62 | 33,53 | 74,4 % |
| 20 | puro | 73,77 | 68,20 | 73,37 | 52,86 | 73,6 % |
| 20 | equilibrado | 61,62 | 56,84 | 60,45 | 43,69 | 73,3 % |


### Chat (I-07, protocolo v10)

Hay un único canal de chat por zona. Un `ChatSend` válido se devuelve como `Chat` a **todos los
jugadores de la zona**, incluido el emisor, aunque estén lejos según el área de interés o el emisor
esté muerto. No hay canales privados, comandos, filtro de palabras, silencios ni guardado del chat.

El servidor valida el contenido, no confía en la interfaz del cliente: decodifica UTF-8 estricto,
recorta los espacios de los extremos y exige de 1 byte al límite configurado. Rechaza caracteres
Unicode de control (`Cc`, incluidos saltos de línea y tabuladores) y de formato (`Cf`, como U+200B y
U+202E). El protocolo v10 impone además un máximo de 120 bytes aunque `chatMaxBytes` se configure por
encima de ese valor.

Cada conexión tiene un cubo de fichas con una capacidad inicial de 3; se repone a 1 ficha por segundo
según los ticks de la zona. Al no tener ficha, el mensaje se rechaza sin notificación al cliente.
Los contadores `chat_recibidos`, `chat_entregados` y `chat_rech_invalido`, `chat_rech_largo`,
`chat_rech_rapido` aparecen en los informes periódicos y en el resumen final. El servidor valida y
difunde el chat en el hilo de red, usando el mapa concurrente de conexiones de la zona y la cola de
salida fiable: no toca las entidades ni bloquea el tick.

| Campo de `server.json` | Defecto | Rango / significado |
| --- | --- | --- |
| `chatMaxBytes` | 120 | Bytes UTF-8 después de recortar, entre 1 y 255; el tope del protocolo sigue siendo 120. |
| `chatBurst` | 3 | Fichas disponibles al inicio, entre 1 y 20. |
| `chatPerSecond` | 1 | Fichas repuestas por segundo de ticks, entre 0,1 y 10. |

El bot `chat` se desplaza como `random` y manda un texto breve cada 5 s ± 1 s; cuenta
`chat_enviados` y `chat_recibidos`. `fight` conserva su comportamiento.

**Verificación y carga I-07:** pasadas con 50 bots durante 120 s y `-LogCorrections` (servidor 160 s).

| Comportamiento | Tick p99 | Correcciones | Chat recibido / copias / rechazos | Bytes enviados |
| --- | --- | --- | --- | --- |
| `chat` | **0,062 ms** | 8 (0,08/bot/min), 0 saltos | 1168 / 57 351 / 0 | **30,5 MB** |
| `fight` | **0,090 ms** | 0, 0 saltos | 0 / 0 / 0 | **26,9 MB** |

En `fight`, frente a I-06 (0,054 ms y 23,9 MB), el p99 subió 0,036 ms y los bytes aumentaron
3 004 070 B (**12,6 %**); hubo 0 ticks por encima de 50 ms. En `chat` se conectaron 50/50 bots y el
servidor no registró rechazos de chat. Las 8 líneas `[corrección]` fueron correcciones pequeñas
(≈0,14–0,21 tiles) en cinco bots, con `baseOk=sí` y distancias a pared en los diagnósticos; tres
parejas consecutivas, sin saltos. No se modificó predicción, fuera del alcance de I-07.

---

### Mapa «Ciudad» (J-04)

Pasada visual J-04b: se actualizó el layout determinista; huella 8bd5c8c768ad7abb41f62ac65a458af8a14cb422ac428c15140caa68b4529bc2; 15.301 casillas libres y alcanzables.

El servidor y los bots cargan **`maps/avelora-ciudad.avmap`**, mapa de 160×120 (x −80..79, y −60..59), con huella `8bd5c8c768ad7abb41f62ac65a458af8a14cb422ac428c15140caa68b4529bc2`. La aparición está en (0,5; −4,5); la zona segura «Ciudad» ocupa −30,−22,60×44 y «Arena del Este» es PvP en 44,28,14×10. Hay 10 spawners (6 orcos 2001 y 4 orcos guerreros 2002) fuera de la ciudad. El layout determinista es `Editor/MapaCiudadLayout.cs`, pintado/exportado por `Editor/MapaCiudadGenerator.cs` desde «Avelora › Generar mapa «Ciudad»». El layout comprueba mediante BFS que puertas, claros y arena sean alcanzables desde la aparición; en la generación comprobada hubo 15.301 casillas libres, todas alcanzables.

### Mapa «Valle» (J-02)

Sustituido por la «Ciudad» (J-04); archivo y generador conservados.

El mapa histórico **`maps/avelora-valle.avmap`** mide 80×60 (x de −40 a 39 e y de −30 a 29).
Lo generaba el menú **Avelora › Generar mapa «Valle»** (`Editor/ValleyMapGenerator.cs`, semilla fija:
volver a generarlo da la misma huella) con el arte de Tiny Swords recortado a 64 px en `Art/Map`:
- **isla** con costa irregular, playa y espuma; el mar rodea todo el borde;
- **río** de norte a sur con **dos puentes** (y = 0 e y = 16) que lleva a las tierras altas del este;
- **aldea** en el centro, una plaza de arena con camino a cada zona; el spawn está en (−6,5; 0,5);
- **bosque** espeso al oeste, con tres claros y un estanque; arboledas al norte;
- **mesetas de roca** con acantilado al este del río.

Capas: `Water`, `Foam`, `Sand`, `Floor` (hierba), `Deco`, `Rock`, `Bridge` y los árboles en `Props`.
Solo el Tilemap `Collision` cuenta para la colisión: lleva un tile invisible (`Tiles/Map/Block`) en
el agua, las mesetas, los acantilados, los troncos y las rocas del mar. El generador exporta el
`.avmap` junto a la carpeta `Assets` y comprueba que todos los claros se alcanzan desde el spawn
(2290 casillas alcanzables). `npcs.json` pone 25 enemigos en los 10 claros: orcos (2001) al oeste del
río y orcos guerreros (2002) en las tierras altas.

### Mapa de prueba de 80×60 (J-01)

Sustituido por el «Valle»; `maps/avelora-prueba.avmap` se conserva. Era un mapa de 80×60 casillas
(x de −40 a 39, y de −30 a 29) generado por código en el Tilemap de `AveloraEtapaA.unity`:
- **12 salas** de unas 19×19, separadas por muros en x = −20, 0 y 20 y en y = −10 y 10;
- una puerta de 3 casillas entre cada par de salas vecinas;
- 2 columnas de 2×2 por sala.

Se generó con una semilla fija y se comprobó que las 4155 casillas de suelo se alcanzan desde la
sala de inicio. El spawn está en (−9,5; 0,5), en una sala sin enemigos. `npcs.json` reparte 26
enemigos en las otras 11 salas: los orcos guerreros (2002) en las del este.

- **Mapa viejo:** `maps/avelora-etapa-a.avmap` (19×13) se conserva solo para las pruebas. Varias
  usan sus paredes, y sigue siendo el valor por defecto de `ServerConfig.MapPath`.
- **Cámara:** sigue al jugador local (`cameraFollowsPlayer` en `NetworkClient`).
- **Bot `fight`:** pasea como `random` mientras no ve ningún enemigo.

| Carga (50 bots, 120 s) | Resultado |
| --- | --- |
| `fight` | tick p99 **0.047 ms**; **731** ataques válidos; **216** orcos muertos y reaparecidos; **20** muertes de jugador; **0 correcciones**; **29.7 MB** enviados |
| `random` | tick p99 **0.053 ms**; 286 golpes de orco y 6 muertes; **0.06** correcciones por bot y minuto; **30.4 MB** enviados |

Se envía la mitad que en el mapa pequeño: los bots se reparten y cada uno ve menos entidades.

---

## Próximo paso

El servidor ya implementa **I-04 a I-30**, con persistencia SQLite en esquema v8 y protocolo v25, incluidos I-15p/I-15q, I-16, I-17, I-18, I-19 e I-30.
El cliente usa v21 (I-19b pendiente; incompatible con el servidor v25) e incorpora habilidades, recurso y barra de accesos (I-16b/I-16c); queda pendiente la comprobación en Play indicada en sus informes.
Quedan pendientes las tareas de cliente anteriores indicadas en la hoja de ruta,
además de los trabajos de seguridad H aún abiertos.
