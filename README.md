# Avelora — núcleo del servidor autoritativo (C#)

Implementación por etapas del *plan del servidor* de Avelora: un MMORPG 2D con **servidor
autoritativo en C# (.NET)** que comparte con el cliente de Unity el código que ambos deben
calcular igual (movimiento y colisión).

> **Estado: Etapa F cerrada.** El servidor guarda **cuentas y personajes en SQLite** detrás de
> `IPlayerRepository`, y el login ya es real: **PBKDF2-HMAC-SHA256 con 600 000 iteraciones**,
> registro con **código de invitación** y límites de intentos por cuenta. El `Hello` lleva las
> credenciales y el personaje (**protocolo v2**); al entrar, el personaje aparece en la posición
> que tenía guardada si el mapa coincide.
>
> Si la misma cuenta entra dos veces, la sesión anterior recibe `Reject(LoggedInElsewhere)` y se
> cierra con un **retardo** para que el mensaje llegue. El guardado corre en su propio hilo —al
> desconectar, cada minuto y al apagar—, así que el tick nunca espera a SQLite.

| Comprobación | Resultado |
| --- | --- |
| `dotnet build Avelora.slnx` | **0 errores, 0 advertencias** |
| `dotnet test Avelora.slnx` | **216/216 en verde** |
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
| `Hello` | 1 | C → S | 41 B + cadenas | versión (u16), tick (u16), token (u32), huella del mapa (32 B), cuenta, contraseña, personaje e invitación (cada una: longitud u16 + UTF-8) |
| `MoveInput` | 2 | C → S | 7 B | secuencia (u32), dirX (i8), dirY (i8) |
| `Welcome` | 10 | S → C | 69 B | versión, tick, entidad (i32), tick servidor (u32), token, aparición (f32×2), velocidad (f32), medio (f32×2), huella (32 B) |
| `Reject` | 11 | S → C | 2 B | motivo (u8) |
| `Snapshot` | 12 | S → C | 11 B + 11 B/entidad | tick (u32), último input aplicado (u32), N (u16); por entidad: id (i32), x/y (i16), dirección (i8×2), tipo (u8) |

Canales:

- **Fiable y ordenado:** `Hello`, `Welcome`, `Reject`.
- **No fiable y secuenciado:** `MoveInput` y `Snapshot`. Si un paquete se pierde, el siguiente lo
  reemplaza; no se reintenta.

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
- Tope de mensaje: **1400 bytes**. Un snapshot con 96 entidades ocupa 1067 y cabe en un datagrama.
  Un mensaje mayor se rechaza sin decodificar.

### Límites y validación en la frontera

| Defensa | Valor por defecto |
| --- | --- |
| Conexiones simultáneas | 16 (10 jugadores + margen para rechazar con motivo) |
| Tamaño máximo de mensaje | 1400 B |
| Mensajes por conexión y segundo | 90; por encima se descarta, a 3× el tope se desconecta |
| Cola de entrada | 4096; se descarta si se llena |
| Cola de salida | 512; se tiran snapshots si se llena |
| Saludo sin completar | 5 s |
| Dirección de movimiento | solo −1, 0 o 1 (se valida en el códec, en la red y en la cola) |
| Secuencia de input | repetida o vieja: se descarta (comparación con vuelta de `uint`) |
| Punto de aparición | se comprueba al arrancar (`Program`) y al crear la entidad (`Zone`); si está en una pared, no se juega |
| Enumeraciones del cable | `EntityKind` y `RejectReason` desconocidos se rechazan |

Un paquete con tipo desconocido, truncado, de dirección inválida, con una enumeración inventada o por
encima del tope **no llega a la simulación**: el códec devuelve `false` y se cuenta como rechazado.

> Queda para las etapas siguientes: límite de conexiones por IP (Etapa H), cifrado del transporte y
> registro por evento de cada validación fallida (hoy se cuentan por informe). Los intentos de login
> por minuto ya existen desde la F (`maxFailedLoginsPerAccountPerMinute`). LiteNetLib puede añadir
> cifrado por conexión más adelante.

### Verificación de la Etapa C

| Prueba | Resultado |
| --- | --- |
| `dotnet test` (48 A + 67 B + 29 C + 16 de la revisión + 19 D + 15 E) | **216/216** (hoy; al cerrar la C eran 160) |
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
2. Se reaplican los pendientes desde la **posición autoritativa** del snapshot.
3. Se compara la posición predicha con la recién calculada:
   - **Diferencia por eje ≤ `MaxQuantizationError` (1/128 de tile) + 1e-5:** es ruido de
     cuantización; se conserva la predicción y **no se corrige** (si se corrigiera, el personaje
     vibraría con cada snapshot).
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
- El tope de **96 entidades por snapshot** sigue ahí: si una zona tuviera más, se recorta y se
  cuenta. El destinatario va primero, así que el recorte nunca lo elimina a él.
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
- La contraseña viaja **sin cifrar** por UDP en esta etapa; el cifrado del transporte es de la H.

### Registro con código de invitación

- Si la cuenta no existe, se crea solo cuando el `invitationCode` del saludo coincide **exacto** con
  `registrationInvitationCode`. Si ese ajuste está **vacío, el registro está cerrado**.
- En cualquier otro caso se responde `Reject(RegistrationClosed)`; una contraseña incorrecta de una
  cuenta existente es `Reject(InvalidCredentials)`.
- Nombres válidos: cuenta de 3 a 32 caracteres y personaje de 2 a 32, solo letras, dígitos, `_` y
  `-`; contraseña de 6 a 128. Si el personaje llega vacío se usa el nombre de la cuenta.

### Login en el hilo `avelora-login`

- El hilo de red solo valida lo barato (versión, tick, mapa, nombres, límites) y encola un
  `LoginRequest`. `LoginWorker` lo procesa en su propio hilo, **`avelora-login`**: ahí se hace
  PBKDF2, se lee SQLite y se carga o crea el personaje.
- El resultado vuelve por una cola que el hilo de red vacía en `PollOnce`. Un login lento no frena
  ni el tick ni el socket.
- **`maxPendingLogins` (16)** es el tope de logins *en vuelo*: los que esperan en la cola **y** el
  que se está calculando. Al llenarse, el saludo se rechaza con `RateLimited` sin gastar PBKDF2.
  El hueco solo se libera cuando la red recoge el resultado, así que **desconectar no lo libera**.
- **`maxFailedLoginsPerAccountPerMinute` (5)**: los `InvalidCredentials` se cuentan por cuenta y
  minuto. Agotado el tope, los siguientes saludos de esa cuenta se rechazan con `RateLimited` sin
  comprobar la contraseña. Al cambiar de minuto se tira la ventana anterior.

### Una sola sesión por cuenta

- La reserva es por **cuenta**, no por personaje. Si la misma cuenta entra desde otra conexión, la
  nueva pasa a ser la dueña y la anterior recibe `Reject(LoggedInElsewhere)`: se guarda su posición,
  se retira su entidad y se suelta su conexión.
- Si es el **mismo personaje**, la sesión nueva hereda la posición viva de la anterior, que es más
  reciente que la de la base de datos; así no reaparece donde se guardó por última vez.
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
  que la cola termine. Si quedara algo pendiente, se avisa por consola.
- El guardado corre en el hilo **`avelora-save`** con una cola de un solo escritor. Si el mismo
  personaje se encola dos veces antes de escribirse, la **última foto reemplaza** a la anterior.
- Antes de encolar se crea un `CharacterRecord` nuevo (`WithPosition`), una **copia inmutable**:
  el hilo de guardado nunca lee datos que la simulación está moviendo. Los fallos de escritura se
  cuentan y se imprimen; el resumen muestra guardados y fallos.

### Cliente de Unity

- `NetworkClient` manda cuenta, contraseña, personaje e invitación dentro del `Hello` y valida el
  `Welcome` como antes. Si el nombre del personaje va vacío, el servidor usa el de la cuenta.
- La escena `AveloraEtapaA` serializa las credenciales de desarrollo: `accountName: dev`,
  `password: avelora-dev`, `characterName` vacío e `invitationCode: avelora-dev`.
- Los rechazos nuevos tienen texto propio: `LoggedInElsewhere` se muestra como «tu cuenta se ha
  conectado desde otro sitio», `InvalidCredentials` como «cuenta o contraseña incorrectas» y
  `RegistrationClosed` como «cuenta inexistente o invitación no válida». La reconexión sigue siendo
  manual con **R**.

### Configuración

| Campo | Defecto | Significado |
| --- | --- | --- |
| `persistenceEnabled` | **true** | Activa cuentas, personajes y guardado en SQLite. |
| `databasePath` | **`data/avelora.db`** | Archivo SQLite; si es relativo, se crea junto a `server.json`. |
| `registrationInvitationCode` | **`avelora-dev`** | Código para crear cuentas. Vacío cierra el registro. |
| `saveIntervalSeconds` | **60** | Segundos entre guardados periódicos; debe ser positivo. |
| `maxPendingLogins` | **16** | Logins en vuelo (cola + cálculo) antes de responder `RateLimited`. |
| `maxFailedLoginsPerAccountPerMinute` | **5** | Fallos de contraseña por cuenta y minuto. |
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
| Protocolo | **v2**: el `Hello` lleva credenciales y personaje; `Welcome`, `Reject` y `Snapshot` no cambian |
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
| Empujar a la derecha contra la pared interior | se detiene en **x = −0.50** |
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
| **G** | Bots y pruebas de carga | ⏳ |
| **H** | Seguridad reforzada y (opcional) gateway | ⏳ |

---

## Próximo paso

La **Etapa F** ya está cerrada: las cuentas y los personajes viven en SQLite, el login es real y
corre en su propio hilo, y la posición sobrevive a la desconexión. La misma cuenta no puede jugar dos
veces: la sesión anterior recibe `LoggedInElsewhere` y se cierra con la pausa suficiente para que el
cliente lea el motivo. El protocolo subió a **v2** solo por el saludo; el tick, los snapshots y la
predicción siguen igual.

El siguiente bloque es la **Etapa G: bots y pruebas de carga** (plan §10). Hay que llevar los bots a
**30–50** conexiones simultáneas y medir el **peor caso** (todos en el mismo punto, entradas y salidas
masivas), la **resistencia** de varias horas sin que crezca la memoria, la **mala red** (latencia y
pérdida simuladas), el **abuso** (mensajes inválidos, inundación, movimiento acelerado) y las
**caídas** (matar el servidor en pleno juego, reiniciarlo y comprobar que se pierde como mucho el
último minuto y que la base queda consistente). Conviene vigilar también las **~9 correcciones de
predicción** que aparecieron al moverse en diagonal en el editor, probablemente por tirones de frames
mientras el editor está sin foco.
