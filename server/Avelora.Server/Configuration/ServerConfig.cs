#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Avelora.Server.Items;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Configuration
{
    /// <summary>
    /// Ajustes del servidor, leídos de <c>server.json</c> (plan del servidor §3: «configuración
    /// externa: tick rate, límites y rutas en un archivo, no en el código»).
    /// </summary>
    /// <remarks>
    /// Todos los valores tienen un defecto razonable, así que el servidor arranca aunque el
    /// archivo no exista: en ese caso se escribe uno para poder editarlo.
    /// </remarks>
    public sealed class ServerConfig
    {
        /// <summary>Nombre del archivo de configuración por defecto.</summary>
        public const string DefaultFileName = "server.json";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
        };

        /// <summary>
        /// Ticks por segundo. Debe coincidir con <see cref="SimulationConstants.TickRate"/>:
        /// el cliente lleva compilado ese paso, así que un valor distinto rompería predicción y
        /// reconciliación (guía del núcleo §5.1).
        /// </summary>
        public int TickRate { get; set; } = SimulationConstants.TickRate;

        /// <summary>Ruta del mapa <c>.avmap</c>. Si es relativa se busca junto a la configuración, en el directorio actual y junto al ejecutable.</summary>
        public string MapPath { get; set; } = "maps/avelora-etapa-a.avmap";

        /// <summary>Identificador de la zona (en el MVP hay una sola).</summary>
        public int ZoneId { get; set; } = 1;

        /// <summary>Posición X de aparición del jugador.</summary>
        public float SpawnX { get; set; } = -1.5f;

        /// <summary>Posición Y de aparición del jugador.</summary>
        public float SpawnY { get; set; } = -2.5f;

        /// <summary>Identificador del primer personaje cuando la persistencia está desactivada.</summary>
        public int FirstCharacterId { get; set; } = 1;

        /// <summary>Máximo de personajes vivos por cuenta (I-10), entre 1 y 10.</summary>
        public int MaxCharactersPerAccount { get; set; } = 3;

        /// <summary>Segundos máximos que una cuenta puede permanecer eligiendo personaje (I-10).</summary>
        public double CharacterSelectTimeoutSeconds { get; set; } = 300d;

        /// <summary>Activa cuentas, personajes y guardado en SQLite (Etapa F).</summary>
        public bool PersistenceEnabled { get; set; } = true;

        /// <summary>
        /// Ruta del archivo SQLite. Si es relativa se resuelve junto a la configuración, que a su
        /// vez viaja junto al ejecutable.
        /// </summary>
        public string DatabasePath { get; set; } = "data/avelora.db";

        /// <summary>
        /// Ruta de la clave RSA-2048 con la que se cifra el saludo (H-02). Si es relativa se
        /// resuelve junto a la configuración. Sin persistencia la clave es efímera y no se guarda.
        /// </summary>
        public string ServerKeyPath { get; set; } = "data/server-key.pem";

        /// <summary>
        /// Ruta del archivo de enemigos (<c>npcs.json</c>, I-01). Si es relativa se resuelve junto
        /// a la configuración, como <see cref="DatabasePath"/>. Vacío = zona sin enemigos.
        /// </summary>
        public string NpcsPath { get; set; } = "npcs.json";

        /// <summary>Ruta de zonas del mapa (I-15). Vacía mantiene todo el mapa como zona normal.</summary>
        public string ZonesPath { get; set; } = "";

        /// <summary>
        /// Ruta del archivo de objetos (<c>items.json</c>, I-04). Si es relativa se resuelve junto
        /// a la configuración, como <see cref="DatabasePath"/>. Vacío = sin objetos.
        /// </summary>
        public string ItemsPath { get; set; } = "items.json";

        /// <summary>Archivo de umbrales y bonus de nivel (I-06).</summary>
        public string LevelsPath { get; set; } = "levels.json";

        /// <summary>Archivo de clases y fórmulas de atributos (I-08).</summary>
        public string ClassesPath { get; set; } = "classes.json";

        /// <summary>Archivo de habilidades (I-16). Vacío = sin habilidades.</summary>
        public string SkillsPath { get; set; } = "skills.json";

        /// <summary>Número de huecos del inventario (I-04).</summary>
        public int InventorySlots { get; set; } = 20;

        /// <summary>Segundos que un objeto permanece en el suelo antes de caducar (I-04).</summary>
        public double GroundItemSeconds { get; set; } = 60d;

        /// <summary>Segundos durante los que el botín caído queda reservado a su dueño (I-25).</summary>
        public double LootReserveSeconds { get; set; } = 30d;

        /// <summary>Segundos seguidos en zona segura para cambiar el modo PvP (I-15p).</summary>
        public double PvpModeChangeSeconds { get; set; } = 30d;

        /// <summary>Espera en zona segura si se entró con PvP efectivo (I-15q).</summary>
        public double PvpModeChangeFromPvpSeconds { get; set; } = 60d;

        /// <summary>Segundos de PvP forzado al salir de una zona PvP (I-15p).</summary>
        public double PvpForcedSeconds { get; set; } = 300d;

        /// <summary>Segundos sin golpear para que cambie el dueño del botín (I-25).</summary>
        public double LootTagSeconds { get; set; } = 10d;

        /// <summary>Máximo de objetos en el suelo por zona (I-04).</summary>
        public int MaxGroundItems { get; set; } = 500;

        /// <summary>Distancia máxima para recoger, en tiles, medida entre centros (I-04).</summary>
        public float PickupRange { get; set; } = 1.5f;

        /// <summary>Segundos de recarga entre dos pociones (I-04).</summary>
        public double PotionCooldownSeconds { get; set; } = 1.0d;

        /// <summary>Tope de acciones de objeto encoladas por la red (I-04).</summary>
        public int MaxQueuedItemActions { get; set; } = 1024;

        /// <summary>Máximo de bytes UTF-8 de un mensaje de chat (I-07).</summary>
        public int ChatMaxBytes { get; set; } = ProtocolConstants.MaxChatBytes;

        /// <summary>Fichas iniciales del chat por jugador (I-07).</summary>
        public int ChatBurst { get; set; } = 3;

        /// <summary>Fichas de chat que se reponen por segundo, medidas en ticks de zona (I-07).</summary>
        public double ChatPerSecond { get; set; } = 1d;

        /// <summary>
        /// Semilla del azar de la IA de los enemigos. Con la misma semilla y los mismos datos, dos
        /// zonas producen exactamente las mismas posiciones.
        /// </summary>
        public int NpcSeed { get; set; } = 12345;

        /// <summary>Semilla del azar del crítico.</summary>
        public int CombatSeed { get; set; } = 54321;

        /// <summary>
        /// Código necesario para crear una cuenta nueva. Vacío cierra el registro.
        /// </summary>
        // El defecto cierra el registro: si el código de desarrollo no se escribe a propósito en
        // la configuración, un servidor expuesto por descuido no deja crear cuentas.
        public string RegistrationInvitationCode { get; set; } = string.Empty;

        /// <summary>Cada cuántos segundos se guardan los personajes conectados.</summary>
        public double SaveIntervalSeconds { get; set; } = 60d;

        /// <summary>
        /// Logins que pueden estar autenticándose a la vez. Con la cola llena se rechaza el saludo
        /// con <c>RateLimited</c> sin gastar PBKDF2.
        /// </summary>
        public int MaxPendingLogins { get; set; } = 16;

        /// <summary>
        /// Intentos fallidos que admite una misma cuenta por minuto. Superado el tope, el siguiente
        /// saludo se rechaza con <c>RateLimited</c> sin comprobar la contraseña.
        /// </summary>
        public int MaxFailedLoginsPerAccountPerMinute { get; set; } = 5;

        /// <summary>Velocidad del jugador, en unidades por segundo. La impone el servidor, nunca el cliente.</summary>
        public float PlayerSpeed { get; set; } = 4f;

        /// <summary>Semiancho de la caja de colisión del jugador.</summary>
        public float PlayerHalfWidth { get; set; } = 0.4f;

        /// <summary>Semialto de la caja de colisión del jugador.</summary>
        public float PlayerHalfHeight { get; set; } = 0.4f;

        /// <summary>Segundos que un jugador permanece muerto antes de reaparecer (I-03).</summary>
        public double PlayerRespawnSeconds { get; set; } = 5d;

        /// <summary>Vida que recupera un jugador por segundo cuando toca regenerar (I-03).</summary>
        public double PlayerRegenPerSecond { get; set; } = 2d;

        /// <summary>Segundos desde el último daño antes de empezar a regenerar (I-03).</summary>
        public double PlayerRegenDelaySeconds { get; set; } = 5d;

        /// <summary>
        /// Margen de distancia que se suma al alcance. Compensa que el cliente ve a los demás unos
        /// 100 ms en el pasado por la interpolación (I-02).
        /// </summary>
        public float AttackRangeTolerance { get; set; } = 0.5f;

        /// <summary>Tope de ataques encolados por la red: si se llena, se descartan (I-02).</summary>
        public int MaxQueuedAttacks { get; set; } = 1024;

        /// <summary>Tope de intenciones que el tick saca de la cola de entrada por vuelta (protege la duración del tick).</summary>
        public int MaxIntentsPerTick { get; set; } = 128;

        /// <summary>Tope de intenciones que se aceptan por jugador y tick (margen para el jitter).</summary>
        public int MaxIntentsPerPlayerPerTick { get; set; } = 4;

        /// <summary>Capacidad de la cola de inputs de cada jugador (una entrada se consume por tick).</summary>
        public int InputBufferCapacity { get; set; } = 8;

        /// <summary>Tope de la cola concurrente de entrada: si se llena, se descarta (guía del núcleo §2.3).</summary>
        public int MaxQueuedIntents { get; set; } = 4096;

        /// <summary>Número de ticks que guarda la ventana de métricas (600 = 30 s a 20 Hz).</summary>
        public int MetricsWindowTicks { get; set; } = 600;

        /// <summary>Cada cuántos segundos se imprime el informe de métricas.</summary>
        public double ReportIntervalSeconds { get; set; } = 5.0;

        /// <summary>Puerto UDP de escucha. <c>0</c> deja que el sistema elija uno (útil en pruebas).</summary>
        public int Port { get; set; } = ProtocolConstants.DefaultPort;

        /// <summary>Jugadores que caben en la zona.</summary>
        public int MaxPlayers { get; set; } = 10;

        /// <summary>
        /// Conexiones que admite el transporte. Es mayor que <see cref="MaxPlayers"/> a propósito:
        /// así al que sobra se le puede responder «lleno» en vez de cerrarle el socket sin explicación.
        /// </summary>
        public int MaxConnections { get; set; } = 16;

        /// <summary>
        /// Conexiones que admite cada IP. Por defecto 4: varias personas pueden compartir una casa
        /// y un router sin que el límite las confunda con un abuso (H-02).
        /// </summary>
        public int MaxConnectionsPerIp { get; set; } = 4;

        /// <summary>Tope de mensajes por conexión y segundo (los inputs van a 20/s: deja margen al jitter).</summary>
        public int MaxMessagesPerSecondPerConnection { get; set; } = 90;

        /// <summary>
        /// Segundos, dentro del mismo minuto, en los que una conexión puede superar el límite de
        /// mensajes sin ser cortada. Al llegar al tope se desconecta (H-02).
        /// </summary>
        public int MaxSecondsOverMessageLimit { get; set; } = 5;

        /// <summary>Tope de mensajes pendientes de enviar. Si se llena, se tiran los snapshots viejos.</summary>
        public int MaxQueuedOutboundMessages { get; set; } = 512;

        /// <summary>Cada cuántos ticks se envía un snapshot (1 = 20 por segundo).</summary>
        public int SnapshotEveryTicks { get; set; } = 1;

        /// <summary>
        /// Radio del área de interés, en tiles. Una entidad nueva se envía a un jugador si está a
        /// esta distancia o menos. Por defecto cubre de sobra la vista del mapa actual.
        /// </summary>
        public float InterestRadius { get; set; } = InterestSettings.DefaultRadius;

        /// <summary>Lado de cada celda de la grilla espacial del área de interés, en tiles.</summary>
        public float InterestCellSize { get; set; } = InterestSettings.DefaultCellSize;

        /// <summary>
        /// Margen extra, en tiles, que conserva visible a una entidad que ya lo estaba. Evita que
        /// una entidad parada en el borde aparezca y desaparezca en ticks alternos.
        /// </summary>
        public float InterestHysteresis { get; set; } = InterestSettings.DefaultHysteresis;

        /// <summary>Segundos que se espera el saludo de una conexión recién abierta.</summary>
        public double HandshakeTimeoutSeconds { get; set; } = 5.0;

        /// <summary>
        /// Milisegundos que se espera antes de cerrar una conexión a la que ya se le envió un
        /// <c>Reject</c>. Sin esta pausa, el corte puede adelantar al mensaje fiable y el cliente
        /// se queda sin saber el motivo (F-01d). <c>0</c> corta en la siguiente vuelta de la red.
        /// </summary>
        public int RejectDisconnectDelayMilliseconds { get; set; } = 500;

        /// <summary>Duración de la ejecución en segundos. <c>0</c> = hasta que se pulse Ctrl+C.</summary>
        public double RunSeconds { get; set; }

        /// <summary>Posición de aparición del jugador.</summary>
        public Vec2 Spawn => new Vec2(SpawnX, SpawnY);

        /// <summary>Caja de colisión del jugador.</summary>
        public Vec2 PlayerHalfSize => new Vec2(PlayerHalfWidth, PlayerHalfHeight);

        /// <summary>
        /// Reglas de vida del jugador (I-03) ya convertidas a ticks y con el punto de reaparición.
        /// La zona las recibe al construirse; la simulación no lee <see cref="ServerConfig"/>.
        /// </summary>
        public PlayerVitalsSettings PlayerVitals => new PlayerVitalsSettings(
            Spawn,
            PlayerRespawnSeconds,
            PlayerRegenPerSecond,
            PlayerRegenDelaySeconds);

        /// <summary>
        /// Busca el archivo de configuración: primero el que se pase por argumento, luego el
        /// directorio actual y por último el del ejecutable.
        /// </summary>
        public static string ResolveConfigPath(string? requested)
        {
            if (!string.IsNullOrWhiteSpace(requested))
            {
                string full = Path.GetFullPath(requested);
                if (!File.Exists(full))
                {
                    throw new FileNotFoundException("No existe el archivo de configuración indicado.", full);
                }

                return full;
            }

            string inCurrent = Path.Combine(Environment.CurrentDirectory, DefaultFileName);
            if (File.Exists(inCurrent))
            {
                return inCurrent;
            }

            string besideExecutable = Path.Combine(AppContext.BaseDirectory, DefaultFileName);
            return File.Exists(besideExecutable) ? besideExecutable : inCurrent;
        }

        /// <summary>Carga la configuración; si el archivo no existe escribe uno con los valores por defecto.</summary>
        public static ServerConfig Load(string configPath)
        {
            ServerConfig config;

            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                config = JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions) ?? new ServerConfig();
            }
            else
            {
                config = new ServerConfig();
                Save(configPath, config);
            }

            config.Validate();
            return config;
        }

        /// <summary>Escribe la configuración en disco.</summary>
        public static void Save(string configPath, ServerConfig config)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOptions));
        }

        /// <summary>
        /// Comprueba que los valores son utilizables. Se hace al arrancar y no en mitad del tick:
        /// un ajuste imposible debe impedir arrancar (plan del servidor §7, «validar en la frontera»).
        /// </summary>
        public void Validate()
        {
            if (TickRate != SimulationConstants.TickRate)
            {
                throw new InvalidOperationException(
                    "tickRate vale " + TickRate + " pero la simulación compartida está compilada a " +
                    SimulationConstants.TickRate + " Hz (SimulationConstants.TickRate). " +
                    "Cliente y servidor deben avanzar con el mismo paso: cambia la constante compartida " +
                    "(y el cliente) antes que este archivo.");
            }

            if (string.IsNullOrWhiteSpace(MapPath))
            {
                throw new InvalidOperationException("mapPath está vacío: hace falta un mapa .avmap.");
            }

            if (PlayerSpeed <= 0f || float.IsNaN(PlayerSpeed) || float.IsInfinity(PlayerSpeed))
            {
                throw new InvalidOperationException("playerSpeed debe ser un número positivo.");
            }

            if (PlayerHalfWidth <= 0f || PlayerHalfHeight <= 0f)
            {
                throw new InvalidOperationException("La caja de colisión del jugador debe tener tamaño positivo.");
            }

            if (MaxIntentsPerTick < 1 || MaxIntentsPerPlayerPerTick < 1)
            {
                throw new InvalidOperationException("Los topes de intenciones por tick deben ser >= 1.");
            }

            if (InputBufferCapacity < 1)
            {
                throw new InvalidOperationException("inputBufferCapacity debe ser >= 1.");
            }

            if (MaxQueuedIntents < 1)
            {
                throw new InvalidOperationException("maxQueuedIntents debe ser >= 1.");
            }

            if (MetricsWindowTicks < 1)
            {
                throw new InvalidOperationException("metricsWindowTicks debe ser >= 1.");
            }

            if (ReportIntervalSeconds <= 0d)
            {
                throw new InvalidOperationException("reportIntervalSeconds debe ser positivo.");
            }

            if (RunSeconds < 0d)
            {
                throw new InvalidOperationException("runSeconds no puede ser negativo.");
            }

            ValidateNetwork();
            ValidateInterest();
            ValidateCombat();
            ValidateVitals();
            ValidatePersistence();
            ValidateItems();
            ValidateChat();
            if (string.IsNullOrWhiteSpace(LevelsPath))
            {
                throw new InvalidOperationException("levelsPath está vacío.");
            }

            if (string.IsNullOrWhiteSpace(ClassesPath))
            {
                throw new InvalidOperationException("classesPath está vacío.");
            }
        }

        /// <summary>Comprueba que el área de interés contenga el alcance máximo de ataque.</summary>
        public void ValidateAttackRanges(ItemCatalog? items, ClassCatalog classes)
        {
            if (classes is null)
            {
                throw new ArgumentNullException(nameof(classes));
            }

            float maxRange = 0f;
            if (items?.Items != null)
            {
                for (int i = 0; i < items.Items.Count; i++)
                {
                    ItemType? item = items.Items[i];
                    if (item != null && item.IsWeapon && item.Range > maxRange)
                    {
                        maxRange = item.Range;
                    }
                }
            }

            if (classes.Classes != null)
            {
                for (int i = 0; i < classes.Classes.Count; i++)
                {
                    float range = classes.Classes[i]?.Unarmed?.Range ?? 0f;
                    if (range > maxRange)
                    {
                        maxRange = range;
                    }
                }
            }

            if (maxRange + AttackRangeTolerance >= InterestRadius)
            {
                throw new InvalidOperationException(
                    "interestRadius (" + InterestRadius + ") debe ser mayor que el alcance máximo de ataque (" +
                    maxRange + ") + attackRangeTolerance (" + AttackRangeTolerance + ").");
            }
        }

        /// <summary>
        /// Valida la vida del jugador (I-03). Los rangos son los mismos que documenta el README:
        /// un valor imposible debe impedir arrancar, no aparecer como una muerte instantánea.
        /// </summary>
        private void ValidateVitals()
        {
            if (!(PlayerRespawnSeconds >= 0.5d) ||
                PlayerRespawnSeconds > 600d ||
                double.IsNaN(PlayerRespawnSeconds) ||
                double.IsInfinity(PlayerRespawnSeconds))
            {
                throw new InvalidOperationException(
                    "playerRespawnSeconds debe estar entre 0,5 y 600.");
            }

            if (PlayerRegenPerSecond < 0d ||
                double.IsNaN(PlayerRegenPerSecond) ||
                double.IsInfinity(PlayerRegenPerSecond))
            {
                throw new InvalidOperationException("playerRegenPerSecond no puede ser negativo.");
            }

            if (PlayerRegenDelaySeconds < 0d ||
                double.IsNaN(PlayerRegenDelaySeconds) ||
                double.IsInfinity(PlayerRegenDelaySeconds))
            {
                throw new InvalidOperationException("playerRegenDelaySeconds no puede ser negativo.");
            }
        }

        /// <summary>
        /// Valida el combate (I-02). Un alcance o un daño imposibles impedirían jugar con reglas
        /// claras, así que se para al arrancar y no en mitad del tick.
        /// </summary>
        private void ValidateCombat()
        {
            if (AttackRangeTolerance < 0f ||
                AttackRangeTolerance > 2f ||
                float.IsNaN(AttackRangeTolerance) ||
                float.IsInfinity(AttackRangeTolerance))
            {
                throw new InvalidOperationException(
                    "attackRangeTolerance debe estar entre 0 y 2.");
            }

            if (MaxQueuedAttacks < 1)
            {
                throw new InvalidOperationException("maxQueuedAttacks debe ser >= 1.");
            }
        }

        /// <summary>
        /// Valida la persistencia. Con la base de datos desactivada no se exige ruta ni intervalo;
        /// con ella activa, ambos tienen que permitir guardar de forma segura.
        /// </summary>
        private void ValidatePersistence()
        {
            if (!PersistenceEnabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(DatabasePath))
            {
                throw new InvalidOperationException("databasePath está vacío y la persistencia está activada.");
            }

            if (SaveIntervalSeconds <= 0d ||
                double.IsNaN(SaveIntervalSeconds) ||
                double.IsInfinity(SaveIntervalSeconds))
            {
                throw new InvalidOperationException("saveIntervalSeconds debe ser un número positivo.");
            }

            if (string.IsNullOrWhiteSpace(ServerKeyPath))
            {
                throw new InvalidOperationException("serverKeyPath está vacío y la persistencia está activada.");
            }

            if (RegistrationInvitationCode is null)
            {
                throw new InvalidOperationException("registrationInvitationCode no puede ser null.");
            }

            if (RegistrationInvitationCode.Length > ProtocolConstants.MaxInvitationCodeBytes)
            {
                throw new InvalidOperationException(
                    "registrationInvitationCode no puede superar los " +
                    ProtocolConstants.MaxInvitationCodeBytes + " caracteres.");
            }
        }

        /// <summary>
        /// Valida los objetos e inventario (I-04).
        /// </summary>
        private void ValidateItems()
        {
            if (InventorySlots < 1 || InventorySlots > 60)
            {
                throw new InvalidOperationException("inventorySlots debe estar entre 1 y 60.");
            }

            if (!(GroundItemSeconds >= 5d) ||
                GroundItemSeconds > 3600d ||
                double.IsNaN(GroundItemSeconds) ||
                double.IsInfinity(GroundItemSeconds))
            {
                throw new InvalidOperationException(
                    "groundItemSeconds debe estar entre 5 y 3600.");
            }

            if (!(LootTagSeconds >= 1d) || LootTagSeconds > 120d ||
                double.IsNaN(LootTagSeconds) || double.IsInfinity(LootTagSeconds))
            {
                throw new InvalidOperationException("lootTagSeconds debe estar entre 1 y 120.");
            }

            if (!(LootReserveSeconds >= 0d) || LootReserveSeconds > GroundItemSeconds ||
                double.IsNaN(LootReserveSeconds) || double.IsInfinity(LootReserveSeconds))
            {
                throw new InvalidOperationException(
                    "lootReserveSeconds debe estar entre 0 y groundItemSeconds.");
            }

            if (!(PvpModeChangeSeconds >= 1d) || PvpModeChangeSeconds > 3600d ||
                double.IsNaN(PvpModeChangeSeconds) || double.IsInfinity(PvpModeChangeSeconds))
            {
                throw new InvalidOperationException("pvpModeChangeSeconds debe estar entre 1 y 3600.");
            }

            if (!(PvpModeChangeFromPvpSeconds >= 1d) || PvpModeChangeFromPvpSeconds > 3600d ||
                double.IsNaN(PvpModeChangeFromPvpSeconds) || double.IsInfinity(PvpModeChangeFromPvpSeconds))
            {
                throw new InvalidOperationException("pvpModeChangeFromPvpSeconds debe estar entre 1 y 3600.");
            }

            if (!(PvpForcedSeconds >= 1d) || PvpForcedSeconds > 3600d ||
                double.IsNaN(PvpForcedSeconds) || double.IsInfinity(PvpForcedSeconds))
            {
                throw new InvalidOperationException("pvpForcedSeconds debe estar entre 1 y 3600.");
            }

            if (MaxGroundItems < 1 || MaxGroundItems > 5000)
            {
                throw new InvalidOperationException("maxGroundItems debe estar entre 1 y 5000.");
            }

            if (!(PickupRange > 0f) ||
                PickupRange > 5f ||
                float.IsNaN(PickupRange) ||
                float.IsInfinity(PickupRange))
            {
                throw new InvalidOperationException(
                    "pickupRange debe ser mayor que 0 y como mucho 5.");
            }

            if (!(PotionCooldownSeconds >= 0.1d) ||
                double.IsNaN(PotionCooldownSeconds) ||
                double.IsInfinity(PotionCooldownSeconds))
            {
                throw new InvalidOperationException("potionCooldownSeconds debe ser >= 0,1.");
            }

            if (MaxQueuedItemActions < 1)
            {
                throw new InvalidOperationException("maxQueuedItemActions debe ser >= 1.");
            }
        }

        /// <summary>Valida los límites configurables del chat de zona (I-07).</summary>
        private void ValidateChat()
        {
            if (ChatMaxBytes < 1 || ChatMaxBytes > 255)
            {
                throw new InvalidOperationException("chatMaxBytes debe estar entre 1 y 255.");
            }

            if (ChatBurst < 1 || ChatBurst > 20)
            {
                throw new InvalidOperationException("chatBurst debe estar entre 1 y 20.");
            }

            if (ChatPerSecond < 0.1d || ChatPerSecond > 10d ||
                double.IsNaN(ChatPerSecond) || double.IsInfinity(ChatPerSecond))
            {
                throw new InvalidOperationException("chatPerSecond debe estar entre 0,1 y 10.");
            }
        }

        /// <summary>
        /// Resuelve el archivo SQLite. A diferencia del mapa, si es relativo se crea junto a la
        /// configuración (no se busca uno existente en otros directorios): la base de datos es
        /// del servidor, no un contenido compartido.
        /// </summary>
        public string ResolveDatabasePath(string configPath)
        {
            if (string.IsNullOrWhiteSpace(DatabasePath))
            {
                throw new InvalidOperationException("databasePath está vacío.");
            }

            if (DatabasePath == ":memory:" || Path.IsPathRooted(DatabasePath))
            {
                return DatabasePath;
            }

            string? configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            return Path.GetFullPath(Path.Combine(configDirectory ?? ".", DatabasePath));
        }

        /// <summary>
        /// Resuelve el archivo de enemigos. Como la base de datos, si la ruta es relativa se
        /// resuelve junto a la configuración. Una ruta vacía significa «sin enemigos».
        /// </summary>
        public string ResolveNpcsPath(string configPath)
        {
            if (string.IsNullOrWhiteSpace(NpcsPath))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(NpcsPath))
            {
                return NpcsPath;
            }

            string? configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            return Path.GetFullPath(Path.Combine(configDirectory ?? ".", NpcsPath));
        }

        /// <summary>Resuelve zones.json junto a server.json; una ruta vacía desactiva las zonas.</summary>
        public string ResolveZonesPath(string configPath)
        {
            if (string.IsNullOrWhiteSpace(ZonesPath))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(ZonesPath))
            {
                return ZonesPath;
            }

            string? configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            return Path.GetFullPath(Path.Combine(configDirectory ?? ".", ZonesPath));
        }

        /// <summary>
        /// Resuelve skills.json junto a server.json; una ruta vacía desactiva las habilidades
        /// (I-16).
        /// </summary>
        public string ResolveSkillsPath(string configPath)
        {
            if (string.IsNullOrWhiteSpace(SkillsPath))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(SkillsPath))
            {
                return SkillsPath;
            }

            string? configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            return Path.GetFullPath(Path.Combine(configDirectory ?? ".", SkillsPath));
        }

        /// <summary>
        /// Resuelve el archivo de la clave del servidor. Como la base de datos, si la ruta es
        /// relativa se crea junto a la configuración: la clave es del servidor, no un contenido
        /// que se busque en varios directorios.
        /// </summary>
        public string ResolveServerKeyPath(string configPath)
        {
            if (string.IsNullOrWhiteSpace(ServerKeyPath))
            {
                throw new InvalidOperationException("serverKeyPath está vacío.");
            }

            if (Path.IsPathRooted(ServerKeyPath))
            {
                return ServerKeyPath;
            }

            string? configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            return Path.GetFullPath(Path.Combine(configDirectory ?? ".", ServerKeyPath));
        }

        /// <summary>
        /// Resuelve el archivo de objetos. Como la base de datos, si la ruta es relativa se
        /// resuelve junto a la configuración. Una ruta vacía significa «sin objetos».
        /// </summary>
        public string ResolveItemsPath(string configPath)
        {
            if (string.IsNullOrWhiteSpace(ItemsPath))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(ItemsPath))
            {
                return ItemsPath;
            }

            string? configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            return Path.GetFullPath(Path.Combine(configDirectory ?? ".", ItemsPath));
        }

        /// <summary>Resuelve el archivo de niveles junto a la configuración.</summary>
        public string ResolveLevelsPath(string configPath)
        {
            if (string.IsNullOrWhiteSpace(LevelsPath))
            {
                throw new InvalidOperationException("levelsPath está vacío.");
            }

            if (Path.IsPathRooted(LevelsPath))
            {
                return LevelsPath;
            }

            string? configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            return Path.GetFullPath(Path.Combine(configDirectory ?? ".", LevelsPath));
        }

        /// <summary>Resuelve el archivo de clases junto a la configuración.</summary>
        public string ResolveClassesPath(string configPath)
        {
            if (string.IsNullOrWhiteSpace(ClassesPath))
            {
                throw new InvalidOperationException("classesPath está vacío.");
            }

            if (Path.IsPathRooted(ClassesPath))
            {
                return ClassesPath;
            }

            string? configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            return Path.GetFullPath(Path.Combine(configDirectory ?? ".", ClassesPath));
        }

        /// <summary>
        /// Valida el área de interés. Los tres valores tienen que ser finitos, y radio y celda
        /// positivos; la histéresis puede ser cero (sin margen).
        /// </summary>
        private void ValidateInterest()
        {
            if (InterestRadius <= 0f || float.IsNaN(InterestRadius) || float.IsInfinity(InterestRadius))
            {
                throw new InvalidOperationException("interestRadius debe ser un número positivo.");
            }

            if (InterestCellSize <= 0f || float.IsNaN(InterestCellSize) || float.IsInfinity(InterestCellSize))
            {
                throw new InvalidOperationException("interestCellSize debe ser un número positivo.");
            }

            if (InterestHysteresis < 0f || float.IsNaN(InterestHysteresis) || float.IsInfinity(InterestHysteresis))
            {
                throw new InvalidOperationException("interestHysteresis no puede ser negativo.");
            }

            if (float.IsInfinity(InterestRadius + InterestHysteresis))
            {
                throw new InvalidOperationException("interestRadius + interestHysteresis no es representable.");
            }
        }

        /// <summary>
        /// Valida la configuración de red: puerto, aforo, colas y tiempos. Todo se comprueba antes
        /// de abrir el socket, para no arrancar con una combinación imposible.
        /// </summary>
        private void ValidateNetwork()
        {
            if (Port < 0 || Port > 65535)
            {
                throw new InvalidOperationException("port debe estar entre 0 y 65535.");
            }

            if (MaxPlayers < 1)
            {
                throw new InvalidOperationException("maxPlayers debe ser >= 1.");
            }

            if (MaxConnections < MaxPlayers)
            {
                throw new InvalidOperationException(
                    "maxConnections (" + MaxConnections + ") no puede ser menor que maxPlayers (" + MaxPlayers + "): " +
                    "los jugadores que sobran deben poder recibir un rechazo.");
            }

            if (MaxConnectionsPerIp < 1)
            {
                throw new InvalidOperationException("maxConnectionsPerIp debe ser >= 1.");
            }

            if (MaxMessagesPerSecondPerConnection < SimulationConstants.TickRate)
            {
                throw new InvalidOperationException(
                    "maxMessagesPerSecondPerConnection debe cubrir al menos los inputs de un tick (" +
                    SimulationConstants.TickRate + "/s).");
            }

            if (MaxSecondsOverMessageLimit < 1)
            {
                throw new InvalidOperationException("maxSecondsOverMessageLimit debe ser >= 1.");
            }

            if (MaxQueuedOutboundMessages < 1)
            {
                throw new InvalidOperationException("maxQueuedOutboundMessages debe ser >= 1.");
            }

            if (SnapshotEveryTicks < 1)
            {
                throw new InvalidOperationException("snapshotEveryTicks debe ser >= 1.");
            }

            if (HandshakeTimeoutSeconds <= 0d)
            {
                throw new InvalidOperationException("handshakeTimeoutSeconds debe ser positivo.");
            }

            if (RejectDisconnectDelayMilliseconds < 0)
            {
                throw new InvalidOperationException("rejectDisconnectDelayMilliseconds no puede ser negativo.");
            }

            // Límites del login: son frenos de la red delante de PBKDF2 y SQLite (Etapa F).
            if (MaxPendingLogins < 1)
            {
                throw new InvalidOperationException("maxPendingLogins debe ser >= 1.");
            }

            if (MaxFailedLoginsPerAccountPerMinute < 1)
            {
                throw new InvalidOperationException("maxFailedLoginsPerAccountPerMinute debe ser >= 1.");
            }

            if (MaxCharactersPerAccount < 1 || MaxCharactersPerAccount > ProtocolConstants.MaxCharactersPerAccount)
            {
                throw new InvalidOperationException(
                    "maxCharactersPerAccount debe estar entre 1 y " + ProtocolConstants.MaxCharactersPerAccount + ".");
            }

            if (CharacterSelectTimeoutSeconds <= 0d ||
                double.IsNaN(CharacterSelectTimeoutSeconds) ||
                double.IsInfinity(CharacterSelectTimeoutSeconds))
            {
                throw new InvalidOperationException("characterSelectTimeoutSeconds debe ser positivo y finito.");
            }
        }

        /// <summary>
        /// Comprueba que la caja del jugador cabe en el punto de aparición. Si aparece solapando
        /// una pared queda atrapado para siempre: <see cref="MovementSimulator.Step"/> no acepta
        /// ningún movimiento hacia una posición no caminable, ni siquiera para salir.
        /// </summary>
        public void ValidateSpawn(MapCollisionGrid map)
        {
            if (map is null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            if (!map.IsBoxWalkable(Spawn, PlayerHalfSize))
            {
                throw new InvalidOperationException(
                    "El punto de aparición (" + SpawnX + "; " + SpawnY + ") no está libre: la caja del " +
                    "jugador se solapa con una pared y quedaría atrapada. Corrige spawnX/spawnY.");
            }
        }

        /// <summary>
        /// Resuelve la ruta del mapa probando junto a la configuración, en el directorio actual y
        /// junto al ejecutable. Así funciona igual con <c>dotnet run</c>, con el ejecutable
        /// publicado o con la configuración en otro sitio.
        /// </summary>
        public string ResolveMapPath(string configPath)
        {
            if (Path.IsPathRooted(MapPath))
            {
                if (!File.Exists(MapPath))
                {
                    throw new FileNotFoundException("No se encuentra el mapa indicado en mapPath.", MapPath);
                }

                return MapPath;
            }

            string? configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(configDirectory ?? ".", MapPath)),
                Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, MapPath)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, MapPath)),
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException(
                "No se encuentra el mapa '" + MapPath + "'. Se buscó en:" + Environment.NewLine +
                "  " + string.Join(Environment.NewLine + "  ", candidates));
        }
    }
}
