# Avelora: plan del servidor

*Plan de planificación del servidor multijugador · 16 de octubre de 2026*

## Alcance y principios

- **MVP para 10 personas** conectadas a la vez, en un solo mapa.
- **Simple en alcance, pero con las bases puestas** para poder ampliar las capacidades después sin rehacer lo anterior.
- **Servidor autoritativo en C# (.NET)** con una librería UDP, compartiendo código con el cliente de Unity.
- Se mantiene la filosofía de trabajo: "primero que funcione, luego pulir", con revisión de código entre etapas.

### Orden de planificación acordado

1. Modelo de autoridad
2. Lenguaje, motor y librería de red
3. Arquitectura del servidor (bucle de ticks, hilos)
4. Transporte y protocolo
5. Movimiento y sincronización
6. Mundo
7. Seguridad y validación de acciones
8. Persistencia y cuentas
9. Optimización con datos reales
10. Test de estrés

---

## 1. Modelo de autoridad

**Decisión: servidor autoritativo.** El cliente solo envía intenciones ("quiero moverme hacia aquí", "usar la habilidad 2 sobre este objetivo") y el servidor decide qué pasa de verdad. Esto evita la mayoría de los trucos desde el diseño.

**El servidor calcula y decide:**

- Posición y colisiones.
- Daño, cooldowns y estados alterados.
- Loot, inventario y experiencia.

**El cliente puede hacer por su cuenta:**

- Predecir su propio movimiento para que se sienta fluido.
- Mostrar animaciones, efectos y números flotantes.
- Todo lo visual y de interfaz.

**Regla de oro: el cliente nunca decide posición, daño ni loot.**

---

## 2. Lenguaje, motor y red

**Decisión: servidor en C# (.NET) que no dependa de Unity**, con una librería de UDP con canales fiables (la propuesta inicial es LiteNetLib).

**Por qué C# sin Unity:**

- **Comparte código con el cliente:** `MovementSimulator` y `MapCollisionGrid` corren idénticos en ambos lados, lo que permite predecir y validar el movimiento.
- **No carga el motor de Unity en el servidor:** un servidor headless de Unity consume muchos más recursos por instancia.
- **Un solo lenguaje:** menos cambio de contexto y menos errores de traducción entre cliente y servidor.

**Antes de fijar la librería de red**, revisar que siga mantenida y que sea compatible con Unity 6.

**Alternativas descartadas por ahora:**

- Netcode, Mirror o Fish-Net: están pensados para servidores hechos con Unity y atarían el proyecto a ese modelo.
- Go o Rust para el servidor: más rendimiento posible, pero se perdería el código compartido y se duplicaría la lógica.

---

## 3. Arquitectura del servidor

### Estructura de proyectos

- `Avelora.Shared`: la librería compartida con el cliente, compilada como .NET Standard 2.1 para que Unity pueda usarla.
- `Avelora.Server`: aplicación de consola que referencia a `Avelora.Shared`.
- El cliente de Unity usa la misma librería, así que `MovementSimulator` y `MapCollisionGrid` son el mismo código en ambos lados.

### Bucle de ticks

- Paso de tiempo fijo, no atado a los FPS. Se empieza en 20 ticks por segundo, configurable.
- Cada tick hace lo mismo y en este orden: leer los inputs recibidos, simular el mundo (movimiento, combate, IA, estados) y enviar el estado a los clientes.
- El hilo de red solo recibe paquetes y los deja en una cola. El hilo del juego es el único que modifica el mundo, así que no hacen falta bloqueos ni hay condiciones de carrera.
- El guardado en base de datos va por una cola aparte, en segundo plano, para que nunca congele un tick.

### Costuras para crecer

Son baratas hoy y caras de añadir después:

- **`Zone` como contenedor del mundo:** tiene sus entidades, su mapa y su propio tick. El MVP tiene una sola zona. Más adelante, varias zonas o instancias son más objetos de este tipo, e incluso cada una podría correr en su propio hilo.
- **Sistemas separados:** movimiento, combate, loot y estados como módulos independientes que actúan sobre las entidades, en lugar de una clase gigante.
- **Identificadores de entidad desde el inicio:** cada jugador, enemigo e ítem tiene un ID único que cliente y servidor comparten.
- **Persistencia detrás de una interfaz** (`IPlayerRepository`): se empieza con SQLite y, si hace falta, se cambia la implementación sin tocar el resto.
- **Mensajes por tipo, con un manejador por cada uno:** añadir una función nueva es añadir un mensaje y su manejador, sin tocar el núcleo.
- **Configuración externa:** tick rate, límites y rutas en un archivo, no en el código.

### Lo que NO se hace todavía

Es sobreingeniería para 10 jugadores: ECS completo, microservicios, sistemas de mensajería externos o partición espacial del mapa.

---

## 4. Transporte y protocolo

### Transporte

UDP con canales fiables mediante la librería de red. Cada mensaje elige cómo viaja:

- **Fiable y ordenado:** login, uso de ítems, movimiento de inventario, recoger loot, chat futuro.
- **No fiable:** los inputs de movimiento y los snapshots de posición. Si un paquete se pierde, el siguiente lo reemplaza.
- **No fiable pero secuenciado:** descarta los paquetes que llegan tarde, útil para el estado del mundo.

### Serialización

Binaria y compacta, escrita con el escritor binario de la propia librería de red. Las definiciones de mensajes viven en `Avelora.Shared`, para que cliente y servidor usen exactamente las mismas. Si más adelante se quiere algo más automático, MessagePack es la alternativa natural, y migrar es viable porque los mensajes están centralizados.

### Formato de cada mensaje

Un byte con el tipo de mensaje seguido de sus datos. Al conectar, un saludo inicial intercambia la **versión del protocolo**, para rechazar clientes desactualizados.

### Mensajes del MVP

**Cliente → servidor:**

- `Login`
- `Input` (con número de secuencia y dirección de movimiento)
- `SelectTarget`
- `UseSkill`
- `PickupItem`
- `UseItem` / `MoveItem`

**Servidor → cliente:**

- `LoginResult`
- `WorldSnapshot` (posición y vida de cada entidad)
- `EntitySpawn` / `EntityDespawn`
- `DamageEvent` y `StatusEffectEvent`
- `InventoryUpdate`
- `LootDrop`

### Detalles que importan

- **Número de secuencia en cada input:** el servidor responde con el "último input procesado", lo que permite al cliente reconciliar su predicción.
- **Snapshots completos al inicio:** con 10 jugadores y pocos enemigos pesan muy poco. Enviar solo los cambios (deltas) queda como mejora futura, y la estructura del mensaje ya lo permite.
- **Validación al deserializar:** longitudes máximas, rangos de valores y rechazo de mensajes malformados. Es la primera línea de defensa.

---

## 5. Movimiento y sincronización

**Decisión: movimiento solo por teclas (dirección)** para empezar, por ser la opción más factible. El movimiento por clic queda para después, como otro tipo de mensaje.

El objetivo es que el personaje responda al instante, que el servidor tenga siempre la última palabra y que los demás jugadores se vean fluidos. Se combinan tres técnicas.

### 1. Predicción en el cliente (el propio personaje)

- Al pulsar una tecla, el cliente mueve al personaje de inmediato usando `MovementSimulator`, sin esperar al servidor.
- Cada input lleva un número de secuencia y se guarda en una lista de "pendientes de confirmar".
- El cliente simula con el mismo paso de tiempo fijo que el servidor, y la imagen en pantalla se suaviza entre pasos para que no se vea a saltos.

### 2. Reconciliación (corregir lo predicho)

- Cada snapshot del servidor incluye el último input procesado y la posición real en ese momento.
- El cliente descarta los inputs ya confirmados, coloca al personaje en la posición del servidor y vuelve a aplicar los inputs pendientes.
- Si la diferencia es pequeña, se corrige con un deslizamiento suave. Si es grande (un choque con una pared que el cliente no vio, o un intento de trampa), se salta a la posición correcta.
- Como el código de movimiento es el mismo en ambos lados, en condiciones normales casi nunca hay correcciones visibles.

### 3. Interpolación (los demás jugadores y los enemigos)

- No se predicen. Se dibujan ligeramente en el pasado, unos 100 ms (dos snapshots a 20 Hz), interpolando entre los dos últimos recibidos.
- Da un movimiento fluido a cambio de verlos un poco atrasados, algo que casi no se nota en un RPG 2D con combate por habilidades.
- Si faltan snapshots (pérdida de paquetes), se extrapola un instante muy corto y luego se detiene.

### Reglas en el servidor

- **Un input por tick por jugador:** si llegan más, se descartan. Impide acelerar el personaje enviando inputs de más.
- **El servidor aplica `MapCollisionGrid`:** el cliente nunca decide si puede atravesar algo.
- **Los snapshots incluyen solo lo necesario:** posición, dirección y vida. Todo lo demás viaja como evento aparte.

### Mensaje `Input`

- Número de secuencia.
- Dirección en un solo byte (ocho direcciones más "quieto").
- Banderas opcionales para acciones futuras.
- La velocidad en diagonal se normaliza dentro de `MovementSimulator`, no en el cliente, así ambos lados calculan lo mismo.

### Detalle técnico a vigilar

Los decimales (`float`) pueden dar resultados mínimamente distintos entre cliente y servidor. La reconciliación absorbe esas diferencias, pero conviene que `MovementSimulator` use operaciones simples y deterministas. Si algún día aparece una desincronización molesta, se puede pasar a aritmética de punto fijo.

---

## 6. El mundo

**Decisión: un solo mapa dentro de una sola `Zone`** que contiene todo lo que existe en el servidor.

### Qué vive en la `Zone`

- La cuadrícula de colisiones exportada desde el Tilemap (la exportación de la Etapa A).
- El registro de entidades con sus IDs: jugadores, enemigos e ítems en el suelo.
- Los puntos de aparición de jugadores y las definiciones de los spawners de enemigos.
- El `EnemySpawner` con sus temporizadores de respawn, que pasa a correr en el servidor y no en el cliente.
- Los ítems caídos (el equivalente de `WorldItem`) como entidades del servidor, que el cliente solo dibuja.

### Datos estáticos

Los `ItemData`, `LootTable` y estadísticas de enemigos son ScriptableObjects, y el servidor no puede leerlos porque no usa Unity. Hace falta una **exportación de datos desde el editor**, igual que la del mapa, a archivos que el servidor cargue al arrancar (JSON es suficiente para el MVP). Así existe una sola fuente de verdad, con los IDs que ya se usan (por ejemplo, el 1001).

### Costuras para crecer

- `Zone` tiene un `ZoneId` y carga su mapa y sus datos por ese ID. Añadir otro mapa es crear otra `Zone`, y los portales entre mapas serían un mensaje nuevo.
- El generador de snapshots recibe "qué entidades ve este jugador". Hoy devuelve todas, y más adelante se sustituye por un filtro de área sin tocar el resto.

### Consecuencia para el código actual

La lógica que hoy está dentro de MonoBehaviours (daño, cooldowns, estados alterados, IA) tendrá que correr en el servidor. Conviene ir separando las partes puras en clases de C# normales que puedan vivir en `Avelora.Shared`, dejando en Unity solo la parte visual. No hace falta hacerlo ahora, pero hay que tenerlo en cuenta al llegar al combate.

---

## 7. Seguridad y validación de acciones

**Principio: el servidor no se fía de nada que venga del cliente.** Todo mensaje se trata como una petición que puede ser falsa, y el servidor decide si se cumple.

### Capa de red

- **Versión de protocolo y clave de conexión** en el saludo inicial, para rechazar clientes desactualizados o ajenos.
- **Tamaño máximo de mensaje** y rechazo de cualquier mensaje malformado.
- **Límite de frecuencia por jugador y por tipo de mensaje:** por ejemplo, un `UseSkill` por tick como máximo y un tope de mensajes por segundo en total. Quien lo supere de forma repetida se desconecta.
- **Límite de conexiones por IP** e intentos de login por minuto, para frenar abusos básicos.
- **Cifrado:** hasta donde se sabe, las librerías UDP de este tipo no cifran el tráfico por sí solas. Para 10 jugadores es aceptable en el MVP, pero hay que tenerlo presente al diseñar el login.

### Validación por acción (todo en el servidor)

- **Movimiento:** un input por tick, colisiones con `MapCollisionGrid` y velocidad máxima fija.
- **Usar habilidad:** objetivo existente y vivo, dentro del alcance, cooldown cumplido según el servidor, maná suficiente y sin estar bajo Stun o Silence.
- **Recoger ítem:** el ítem sigue existiendo, el jugador está a distancia válida y hay espacio en el inventario. Como el servidor procesa esto en su único hilo de juego, dos jugadores no pueden recoger el mismo ítem a la vez.
- **Usar o mover ítems:** el ítem existe en el inventario *del servidor*, con esa cantidad. El cliente solo pide, no afirma qué tiene.
- **Selección de objetivo:** el ID existe y está dentro de un rango razonable.

### Reglas generales

- **Verificar la propiedad:** el cliente nunca puede actuar sobre una entidad que no sea la suya.
- **Anti-duplicación:** las operaciones de inventario se aplican de forma atómica y los guardados en base de datos van dentro de transacciones.
- **No enviar de más:** el servidor manda solo lo que el cliente necesita ver, sin estadísticas ocultas ni datos de otros jugadores que no hagan falta.
- **Registro de sospechas:** cada validación fallida se anota con jugador y motivo. Con 10 personas basta con un archivo de log.

### Contraseñas

Si hay cuentas, las contraseñas se guardan siempre con un hash lento y con sal, nunca en texto plano.

---

## 8. Persistencia y cuentas

### Base de datos: SQLite para el MVP

- Es un solo archivo, sin servidor aparte que instalar ni mantener, y para 10 jugadores sobra.
- Solo permite un escritor a la vez, pero eso encaja con el diseño: una única cola de guardado en segundo plano.
- Va detrás de la interfaz `IPlayerRepository`. Si Avelora crece, pasar a PostgreSQL consiste en escribir otra implementación, sin tocar el resto del servidor.

### Qué se guarda

- **Cuentas:** ID, nombre de usuario, hash de la contraseña y fecha de creación.
- **Personajes:** ID, cuenta, nombre, nivel, experiencia, vida y maná, posición y zona.
- **Inventario:** personaje, ranura, ID de ítem (los de los datos exportados, como el 1001) y cantidad.

**No se guarda en el MVP:** enemigos, ítems caídos en el suelo, estados alterados activos y cooldowns. Al reiniciar el servidor, el mundo vuelve a empezar limpio.

### Cuándo se guarda

- Al desconectarse un jugador.
- Cada cierto tiempo (por ejemplo, cada minuto), para limitar lo que se pierde si el servidor se cae.
- Tras eventos importantes, como subir de nivel u obtener un ítem raro.
- Al apagar el servidor de forma ordenada.

Antes de encolar un guardado, el servidor hace una **copia de los datos** del personaje, para que el hilo de guardado nunca lea datos que el hilo del juego esté modificando.

### Flujo de login

1. El cliente envía `Login` y el servidor compara el hash de la contraseña, calculado con una librería mantenida de bcrypt o Argon2.
2. Si la misma cuenta ya está conectada, se desconecta la sesión anterior, para evitar dos copias del mismo personaje.
3. El servidor carga el personaje, lo coloca en la `Zone` y envía `LoginResult` con el primer snapshot.

### Creación de cuentas

Con 10 jugadores, se pide un **código de invitación** para registrarse, así nadie ajeno crea cuentas en el servidor.

### Mantenimiento

- Una tabla con el número de versión del esquema, para poder cambiar las tablas sin perder datos.
- Una copia diaria del archivo de la base de datos.

---

## 9. Optimización con datos reales

Con 10 jugadores no hay nada que optimizar hoy, pero conviene **medir desde el primer día**, para que cuando algo se degrade se vea en números y no por intuición.

### Qué medir

- **Duración de cada tick** frente a su presupuesto. A 20 ticks por segundo son 50 ms, y si un tick lo supera, el servidor lo anota en el log.
- **Bytes enviados y recibidos por segundo**, en total y por cliente.
- **Tamaño de las colas:** la de mensajes entrantes y la de guardado en base de datos.
- **Memoria y pausas del recolector de basura.**

Un contador sencillo que escriba un resumen al log cada pocos segundos es suficiente. Para investigar más a fondo existen herramientas oficiales de .NET como `dotnet-counters` y `dotnet-trace`.

### Regla desde el inicio

Dentro del bucle de ticks, evitar crear objetos nuevos en cada pasada: reutilizar buffers y listas, y evitar LINQ. Es lo que más evita tirones por el recolector de basura.

### Mejoras para cuando los números lo pidan, no antes

- Snapshots con solo los cambios en lugar del estado completo.
- Posiciones comprimidas a menos bytes.
- Enviar con menos frecuencia lo que está lejos del jugador, usando la costura de "qué ve cada jugador".

---

## 10. Test de estrés

### Bots de prueba

Una aplicación de consola que usa `Avelora.Shared` y la misma librería de red, y que se conecta con cuentas de prueba enviando inputs, habilidades y recogidas de ítems al azar. Es una herramienta pequeña y muy valiosa, porque se usará en todas las etapas.

### Qué probar

- **Carga:** de 30 a 50 bots, para tener un margen de 3 a 5 veces sobre los 10 jugadores reales. Éxito significa ticks por debajo de la mitad del presupuesto y sin desbordes.
- **Peor caso:** todos en el mismo punto atacando al mismo enemigo, y entradas y salidas masivas a la vez.
- **Resistencia:** varias horas seguidas, vigilando que la memoria no crezca sin parar.
- **Mala red:** latencia y pérdida de paquetes simuladas. La librería de red suele ofrecer simulación propia y, si no, existen herramientas del sistema. Conviene hacerlo desde las primeras pruebas con red, no solo al final.
- **Abuso:** bots que envían mensajes inválidos, inundan el servidor, aceleran el movimiento o intentan recoger el mismo ítem dos veces. Así se comprueba que la validación del punto 7 funciona.
- **Caídas:** matar el servidor en pleno juego, reiniciarlo y verificar que se pierde como mucho el último minuto y que la base de datos queda consistente. También probar la reconexión de clientes.

---

## Fuera del alcance del MVP

Chat, grupos, comercio entre jugadores, misiones, balance fino y movimiento por clic. Se pueden añadir después sin rehacer lo anterior.

## Siguiente paso

Empezar a construir por la **Etapa A**: la librería `Avelora.Shared` (Vec2, MapCollisionGrid, MovementSimulator) y la exportación del mapa desde el Tilemap.
