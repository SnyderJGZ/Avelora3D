#nullable enable
using System;
using Avelora.Shared;

namespace Avelora.Shared.Protocol
{
    /// <summary>
    /// Desafío que el servidor manda nada más conectar (protocolo v4): el nonce de esta sesión y
    /// la clave pública RSA con la que el cliente debe cifrar el saludo.
    /// </summary>
    /// <remarks>
    /// El nonce es de un solo uso: el servidor lo borra al recibir el <see cref="HelloMessage"/> o
    /// al desconectar. Así un saludo reenviado en otra conexión no puede descifrarse.
    /// </remarks>
    public readonly struct ChallengeMessage
    {
        public ChallengeMessage(byte[] nonce, byte[] modulus, byte[] exponent)
        {
            Nonce = nonce ?? Array.Empty<byte>();
            Modulus = modulus ?? Array.Empty<byte>();
            Exponent = exponent ?? Array.Empty<byte>();
        }

        /// <summary>Nonce aleatorio de esta conexión (<see cref="HelloSecrets.NonceSize"/> bytes).</summary>
        public byte[] Nonce { get; }

        /// <summary>Módulo RSA de 2048 bits (<see cref="HelloSecrets.ModulusSize"/> bytes).</summary>
        public byte[] Modulus { get; }

        /// <summary>Exponente público (de 1 a 4 bytes).</summary>
        public byte[] Exponent { get; }
    }

    /// <summary>
    /// Saludo inicial del cliente: versión, tick, mapa y credenciales cifradas (plan §4 y §8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// El cliente no manda ni su posición ni su tiempo. Manda lo que el servidor necesita para
    /// decidir si puede jugar: versión del protocolo, tick al que está compilado, qué mapa tiene,
    /// quién dice ser y qué personaje quiere usar.
    /// </para>
    /// <para>
    /// Desde el protocolo v4 la contraseña y la invitación ya no viajan en el saludo: van cifradas
    /// con RSA-OAEP-SHA1 en <see cref="EncryptedSecrets"/>, usando el nonce y la clave pública del
    /// <see cref="ChallengeMessage"/>. El nombre de cuenta y el de personaje siguen en claro.
    /// </para>
    /// </remarks>
    public readonly struct HelloMessage
    {
        /// <summary>
        /// Construye un saludo sin credenciales. Se conserva para pruebas y para el modo sin
        /// persistencia; el servidor de verdad usa el constructor completo.
        /// </summary>
        public HelloMessage(ushort protocolVersion, ushort tickRate, byte[] mapHash, uint clientToken)
            : this(
                protocolVersion,
                tickRate,
                mapHash,
                clientToken,
                "dev",
                new byte[HelloSecrets.EncryptedSize],
                "dev")
        {
        }

        public HelloMessage(
            ushort protocolVersion,
            ushort tickRate,
            byte[] mapHash,
            uint clientToken,
            string accountName,
            byte[] encryptedSecrets,
            string characterName)
        {
            ProtocolVersion = protocolVersion;
            TickRate = tickRate;
            MapHash = mapHash;
            ClientToken = clientToken;
            AccountName = accountName ?? string.Empty;
            EncryptedSecrets = encryptedSecrets ?? Array.Empty<byte>();
            CharacterName = characterName ?? string.Empty;
        }

        /// <summary>Versión del protocolo que habla el cliente.</summary>
        public ushort ProtocolVersion { get; }

        /// <summary>Tick rate al que está compilado el cliente.</summary>
        public ushort TickRate { get; }

        /// <summary>Huella SHA-256 del mapa que tiene cargado.</summary>
        public byte[] MapHash { get; }

        /// <summary>Número arbitrario del cliente; se devuelve en el <see cref="WelcomeMessage"/> para emparejar.</summary>
        public uint ClientToken { get; }

        /// <summary>Nombre de la cuenta. El servidor lo compara sin distinguir mayúsculas.</summary>
        public string AccountName { get; }

        /// <summary>
        /// Bloque de <see cref="HelloSecrets.EncryptedSize"/> bytes con
        /// <c>nonce ‖ contraseña ‖ invitación</c> cifrado con RSA-OAEP-SHA1.
        /// </summary>
        public byte[] EncryptedSecrets { get; }

        /// <summary>Personaje que se quiere usar. Vacío significa «el mismo nombre que la cuenta».</summary>
        public string CharacterName { get; }
    }

    /// <summary>
    /// Intención de movimiento: la secuencia más nueva y las direcciones de este input y de los
    /// anteriores (guía §4.3 y G-03).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Sequence</c> crece de uno en uno por input. El servidor la usa para descartar repetidos
    /// y viejos y, en la Etapa D, el cliente para saber qué ha confirmado ya el servidor.
    /// </para>
    /// <para>
    /// Cada mensaje lleva hasta <see cref="ProtocolConstants.MaxInputsPerMessage"/> pares
    /// <c>dirX, dirY</c>, del más nuevo al más viejo: el par <c>i</c> corresponde a la secuencia
    /// <c>Sequence - i</c>. Así una pérdida de UDP no se convierte en un paso perdido: el mensaje
    /// siguiente rellena el hueco. Se guardan cuatro pares fijos para no reservar memoria por
    /// mensaje (el camino caliente no debe generar basura).
    /// </para>
    /// </remarks>
    public readonly struct MoveInputMessage
    {
        private readonly sbyte _dir0X;
        private readonly sbyte _dir0Y;
        private readonly sbyte _dir1X;
        private readonly sbyte _dir1Y;
        private readonly sbyte _dir2X;
        private readonly sbyte _dir2Y;
        private readonly sbyte _dir3X;
        private readonly sbyte _dir3Y;

        /// <summary>Un input sin redundancia: el formato de las etapas anteriores.</summary>
        public MoveInputMessage(uint sequence, sbyte directionX, sbyte directionY)
            : this(sequence, 1, directionX, directionY, 0, 0, 0, 0, 0, 0)
        {
        }

        /// <summary>
        /// Un mensaje con <paramref name="count"/> inputs, del más nuevo (<c>dir0</c>) al más viejo.
        /// Los pares que no se usan se ignoran.
        /// </summary>
        public MoveInputMessage(
            uint sequence,
            int count,
            sbyte dir0X,
            sbyte dir0Y,
            sbyte dir1X,
            sbyte dir1Y,
            sbyte dir2X,
            sbyte dir2Y,
            sbyte dir3X,
            sbyte dir3Y)
        {
            if (count < 1 || count > ProtocolConstants.MaxInputsPerMessage)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    count,
                    "El mensaje debe llevar entre 1 y " + ProtocolConstants.MaxInputsPerMessage + " inputs.");
            }

            Sequence = sequence;
            Count = count;
            _dir0X = dir0X;
            _dir0Y = dir0Y;
            _dir1X = dir1X;
            _dir1Y = dir1Y;
            _dir2X = dir2X;
            _dir2Y = dir2Y;
            _dir3X = dir3X;
            _dir3Y = dir3Y;
        }

        /// <summary>Secuencia del input más nuevo del mensaje.</summary>
        public uint Sequence { get; }

        /// <summary>Cuántos inputs lleva, entre 1 y <see cref="ProtocolConstants.MaxInputsPerMessage"/>.</summary>
        public int Count { get; }

        /// <summary>
        /// Dirección del input <paramref name="index"/>: 0 es el más nuevo y <c>Count - 1</c> el más
        /// viejo. Devuelve <c>false</c> si el índice no corresponde a un input del mensaje.
        /// </summary>
        public bool TryGetDirection(int index, out sbyte directionX, out sbyte directionY)
        {
            if (index < 0 || index >= Count)
            {
                directionX = 0;
                directionY = 0;
                return false;
            }

            switch (index)
            {
                case 0:
                    directionX = _dir0X;
                    directionY = _dir0Y;
                    return true;
                case 1:
                    directionX = _dir1X;
                    directionY = _dir1Y;
                    return true;
                case 2:
                    directionX = _dir2X;
                    directionY = _dir2Y;
                    return true;
                case 3:
                    directionX = _dir3X;
                    directionY = _dir3Y;
                    return true;
                default:
                    directionX = 0;
                    directionY = 0;
                    return false;
            }
        }
    }

    /// <summary>
    /// Intención de atacar a una entidad (I-02). Solo lleva el objetivo: el daño, el alcance y el
    /// tiempo de recarga los decide el servidor.
    /// </summary>
    public readonly struct AttackMessage
    {
        public AttackMessage(int targetId)
        {
            TargetId = targetId;
        }

        /// <summary>Identificador de la entidad a la que se quiere atacar.</summary>
        public int TargetId { get; }
    }

    /// <summary>Intención de recoger un objeto del suelo (I-04). Solo lleva el identificador de la
    /// entidad-objeto; el servidor valida distancia, espacio en el inventario y lo añade.</summary>
    public readonly struct PickUpMessage
    {
        public PickUpMessage(int entityId)
        {
            EntityId = entityId;
        }

        /// <summary>Identificador de la entidad <see cref="EntityKind.Item"/> a recoger.</summary>
        public int EntityId { get; }
    }

    /// <summary>Intención de usar un objeto del inventario (I-04). Solo lleva el índice del hueco;
    /// el servidor valida que sea una poción, que el jugador no tenga la vida llena y la recarga.</summary>
    public readonly struct UseItemMessage
    {
        public UseItemMessage(byte slot)
        {
            Slot = slot;
        }

        /// <summary>Índice del hueco del inventario (0..<see cref="ServerConfig.MaxInventorySlots"/>-1).</summary>
        public byte Slot { get; }
    }

    /// <summary>Intención de equipar el arma que ocupa un hueco (I-05).</summary>
    public readonly struct EquipMessage
    {
        public EquipMessage(byte slot) => Slot = slot;

        /// <summary>Hueco del inventario que contiene el arma.</summary>
        public byte Slot { get; }
    }

    /// <summary>Intención de desequipar un hueco en un destino del inventario (I-14).</summary>
    public readonly struct UnequipMessage
    {
        public UnequipMessage(byte equipSlot, byte targetSlot)
        {
            EquipSlot = equipSlot;
            TargetSlot = targetSlot;
        }

        public byte EquipSlot { get; }
        public byte TargetSlot { get; }
    }

    /// <summary>Intención de mover, dividir, juntar o intercambiar una pila (I-14).</summary>
    public readonly struct MoveItemMessage
    {
        public MoveItemMessage(byte fromSlot, byte toSlot, ushort count)
        {
            FromSlot = fromSlot;
            ToSlot = toSlot;
            Count = count;
        }

        public byte FromSlot { get; }
        public byte ToSlot { get; }
        public ushort Count { get; }
    }

    /// <summary>Intención de destruir objetos del inventario (I-14).</summary>
    public readonly struct DestroyItemMessage
    {
        public DestroyItemMessage(byte slot, ushort count)
        {
            Slot = slot;
            Count = count;
        }

        public byte Slot { get; }
        public ushort Count { get; }
    }

    /// <summary>Texto que el cliente propone publicar en el chat de la zona (I-07).</summary>
    public readonly struct ChatSendMessage
    {
        public ChatSendMessage(string text) => Text = text ?? string.Empty;

        public string Text { get; }
    }

    /// <summary>Petición de añadir puntos a los cuatro atributos (I-09).</summary>
    public readonly struct AllocatePointsMessage
    {
        public AllocatePointsMessage(byte str, byte dex, byte intelligence, byte vit)
        {
            Str = str;
            Dex = dex;
            Int = intelligence;
            Vit = vit;
        }

        public byte Str { get; }
        public byte Dex { get; }
        public byte Int { get; }
        public byte Vit { get; }
    }

    /// <summary>Petición de devolver todos los puntos repartidos (I-09).</summary>
    public readonly struct ResetPointsMessage
    {
    }

    /// <summary>Tipos de objeto anunciados por el servidor.</summary>
    public enum ItemKind : byte
    {
        Material = 0,
        Potion = 1,
        Weapon = 2,
        Armor = 3,
    }

    /// <summary>Datos de un tipo de objeto del catálogo del servidor.</summary>
    public readonly struct ItemCatalogEntry
    {
        public ItemCatalogEntry(
            ushort id,
            ItemKind kind,
            ushort maxStack,
            uint heal,
            uint damage,
            float range,
            float cooldownSeconds,
            string name,
            byte equipSlot = byte.MaxValue,
            ushort defense = 0,
            ushort magicResist = 0,
            byte str = 0,
            byte dex = 0,
            byte intelligence = 0,
            byte vit = 0)
        {
            Id = id;
            Kind = kind;
            MaxStack = maxStack;
            Heal = heal;
            Damage = damage;
            Range = range;
            CooldownSeconds = cooldownSeconds;
            Name = name ?? string.Empty;
            EquipSlot = equipSlot;
            Defense = defense;
            MagicResist = magicResist;
            Str = str;
            Dex = dex;
            Int = intelligence;
            Vit = vit;
        }

        public ushort Id { get; }
        public ItemKind Kind { get; }
        public ushort MaxStack { get; }
        public uint Heal { get; }
        public uint Damage { get; }
        public float Range { get; }
        public float CooldownSeconds { get; }
        public string Name { get; }
        public byte EquipSlot { get; }
        public ushort Defense { get; }
        public ushort MagicResist { get; }
        public byte Str { get; }
        public byte Dex { get; }
        public byte Int { get; }
        public byte Vit { get; }
    }

    /// <summary>
    /// Parte del catálogo de objetos y reglas de puños que envía el servidor (I-05/I-21). Los
    /// clientes vacían el catálogo al recibir <see cref="WelcomeMessage"/> y añaden los objetos de
    /// cada mensaje <c>ItemCatalog</c>; un mensaje puede contener solo una parte del catálogo total.
    /// </summary>
    public readonly struct ItemCatalogMessage
    {
        public ItemCatalogMessage(
            float pickupRange,
            uint fistDamage,
            float fistRange,
            float fistCooldownSeconds,
            ItemCatalogEntry[] items)
        {
            PickupRange = pickupRange;
            FistDamage = fistDamage;
            FistRange = fistRange;
            FistCooldownSeconds = fistCooldownSeconds;
            Items = items ?? Array.Empty<ItemCatalogEntry>();
        }

        public float PickupRange { get; }
        public uint FistDamage { get; }
        public float FistRange { get; }
        public float FistCooldownSeconds { get; }
        public ItemCatalogEntry[] Items { get; }
    }

    /// <summary>Respuesta afirmativa al saludo: identidad, punto de aparición y reglas del servidor.</summary>
    /// <remarks>
    /// Viajan la velocidad y el tamaño de la caja de colisión porque las impone el servidor: el
    /// cliente debe predecir con exactamente los mismos números o se desviará en las paredes.
    /// El punto de aparición va en <c>float</c> (no cuantizado) porque es el origen de la
    /// predicción y conviene que sea exacto.
    /// </remarks>
    public readonly struct WelcomeMessage
    {
        public WelcomeMessage(
            ushort protocolVersion,
            ushort tickRate,
            int entityId,
            uint serverTick,
            uint clientToken,
            float spawnX,
            float spawnY,
            float speed,
            float halfWidth,
            float halfHeight,
            byte[] mapHash)
        {
            ProtocolVersion = protocolVersion;
            TickRate = tickRate;
            EntityId = entityId;
            ServerTick = serverTick;
            ClientToken = clientToken;
            SpawnX = spawnX;
            SpawnY = spawnY;
            Speed = speed;
            HalfWidth = halfWidth;
            HalfHeight = halfHeight;
            MapHash = mapHash;
        }

        public ushort ProtocolVersion { get; }

        public ushort TickRate { get; }

        /// <summary>Identificador de la entidad del jugador. Lo asigna el servidor.</summary>
        public int EntityId { get; }

        /// <summary>Tick del servidor en el momento de aceptar.</summary>
        public uint ServerTick { get; }

        /// <summary>El mismo número que envió el cliente en el saludo.</summary>
        public uint ClientToken { get; }

        public float SpawnX { get; }

        public float SpawnY { get; }

        /// <summary>Velocidad en unidades por segundo.</summary>
        public float Speed { get; }

        /// <summary>Semiancho de la caja de colisión.</summary>
        public float HalfWidth { get; }

        /// <summary>Semialto de la caja de colisión.</summary>
        public float HalfHeight { get; }

        /// <summary>Huella del mapa que está usando el servidor.</summary>
        public byte[] MapHash { get; }
    }

    /// <summary>Motivo del rechazo. Viaja como un byte, no como texto: el cliente lo traduce.</summary>
    public enum RejectReason : byte
    {
        Unknown = 0,

        /// <summary>La versión del protocolo no coincide.</summary>
        ProtocolVersion = 1,

        /// <summary>El cliente está compilado a otro tick rate.</summary>
        TickRate = 2,

        /// <summary>El cliente tiene otro mapa (o una versión distinta del mismo mapa).</summary>
        MapMismatch = 3,

        /// <summary>La zona está llena.</summary>
        ServerFull = 4,

        /// <summary>El primer mensaje no es un saludo válido.</summary>
        MalformedMessage = 5,

        /// <summary>Se acabó el tiempo para saludar.</summary>
        Timeout = 6,

        /// <summary>Insistió por encima del límite de frecuencia después de que se le descartaran mensajes.</summary>
        RateLimited = 7,

        /// <summary>La cuenta o la contraseña no son válidas.</summary>
        InvalidCredentials = 8,

        /// <summary>La cuenta no existe y el registro está cerrado o la invitación no coincide.</summary>
        /// <remarks>
        /// El servidor ya no lo emite: para no revelar qué cuentas existen, ese caso responde
        /// <see cref="InvalidCredentials"/>. Se conserva el valor para no renumerar el resto.
        /// </remarks>
        RegistrationClosed = 9,

        /// <summary>Ese personaje ya está jugando en otra conexión.</summary>
        /// <remarks>
        /// El servidor ya no lo emite: desde la Etapa F, la misma cuenta conectada dos veces echa a
        /// la sesión anterior (<see cref="LoggedInElsewhere"/>). Se conserva el valor para no
        /// renumerar el resto.
        /// </remarks>
        CharacterInUse = 10,

        /// <summary>La misma cuenta se conectó desde otra conexión: esta sesión se cierra.</summary>
        LoggedInElsewhere = 11,

        /// <summary>La cuenta ya tiene el máximo de personajes permitido.</summary>
        CharacterLimit = 12,
    }

    /// <summary>Respuesta negativa al saludo.</summary>
    public readonly struct RejectMessage
    {
        public RejectMessage(RejectReason reason)
        {
            Reason = reason;
        }

        public RejectReason Reason { get; }
    }

    /// <summary>Estado de una entidad dentro de un snapshot.</summary>
    /// <remarks>
    /// Ocupa 15 bytes: Id (4), posición cuantizada (2+2), dirección y tipo (1+1+1), vida (1), acción
    /// y rango empaquetados (1, I-20) y tipo de PNJ (2, I-03c).
    /// </remarks>
    public readonly struct EntityState
    {
        public EntityState(
            int id,
            short x,
            short y,
            sbyte directionX,
            sbyte directionY,
            EntityKind kind,
            byte healthPercent,
            EntityAction action,
            ushort typeId = 0,
            byte rank = 0)
        {
            Id = id;
            X = x;
            Y = y;
            DirectionX = directionX;
            DirectionY = directionY;
            Kind = kind;
            HealthPercent = healthPercent;
            Action = action;
            TypeId = typeId;
            Rank = rank;
        }

        public int Id { get; }

        /// <summary>Posición X en unidades de 1/64 de tile.</summary>
        public short X { get; }

        /// <summary>Posición Y en unidades de 1/64 de tile.</summary>
        public short Y { get; }

        public sbyte DirectionX { get; }

        public sbyte DirectionY { get; }

        public EntityKind Kind { get; }

        /// <summary>
        /// Vida restante en porcentaje, de 0 a 100. Los jugadores mandan 100 hasta I-03; un PNJ
        /// herido nunca manda 0 si le queda algo de vida (se redondea hacia arriba).
        /// </summary>
        public byte HealthPercent { get; }

        /// <summary>Acción visible de la entidad (atacando o muerta).</summary>
        public EntityAction Action { get; }

        /// <summary>Tipo de PNJ de <c>npcs.json</c> (2001, 2002…); 0 para los jugadores (I-03c).</summary>
        public ushort TypeId { get; }

        /// <summary>Rango del PNJ (0 = normal); jugadores y objetos llevan 0.</summary>
        public byte Rank { get; }
    }

    /// <summary>
    /// Estado del mundo en un tick. No fiable y secuenciado: si se pierde, el siguiente lo
    /// reemplaza (plan del servidor §4).
    /// </summary>
    public readonly struct SnapshotMessage
    {
        public SnapshotMessage(uint serverTick, uint lastProcessedInputSequence, EntityState[] entities)
        {
            ServerTick = serverTick;
            LastProcessedInputSequence = lastProcessedInputSequence;
            Entities = entities;
        }

        public uint ServerTick { get; }

        /// <summary>
        /// Último input del destinatario que el servidor ha aplicado. Es la base de la
        /// reconciliación de la Etapa D.
        /// </summary>
        public uint LastProcessedInputSequence { get; }

        public EntityState[] Entities { get; }
    }

    /// <summary>Inventario completo del jugador (I-04/I-05). Se manda tras el <see cref="WelcomeMessage"/> y
    /// tras cada cambio (recoger o usar). Lleva el número de huecos y, por cada uno, el <c>itemId</c>
    /// (0 = vacío) y la cantidad.</summary>
    public readonly struct InventoryMessage
    {
        public InventoryMessage(byte slotCount, ushort[] itemIds, ushort[] counts)
            : this(slotCount, itemIds, counts, new ushort[ProtocolConstants.EquipSlotCount])
        {
        }

        public InventoryMessage(byte slotCount, ushort[] itemIds, ushort[] counts, ushort[] equipment)
        {
            SlotCount = slotCount;
            ItemIds = itemIds ?? Array.Empty<ushort>();
            Counts = counts ?? Array.Empty<ushort>();
            Equipment = equipment ?? Array.Empty<ushort>();
        }

        /// <summary>Número de huecos del inventario (≤ 60).</summary>
        public byte SlotCount { get; }

        /// <summary>Identificador de objeto por hueco. 0 = vacío.</summary>
        public ushort[] ItemIds { get; }

        /// <summary>Cantidad por hueco. Debe ser 0 si <see cref="ItemIds"/> es 0, y > 0 si no lo es.</summary>
        public ushort[] Counts { get; }

        /// <summary>Objeto equipado por hueco, en orden de <see cref="EquipSlot"/>; 0 significa vacío.</summary>
        public ushort[] Equipment { get; }
    }

    /// <summary>Progreso del personaje (I-06), enviado de forma fiable y ordenada.</summary>
    public readonly struct ProgressMessage
    {
        public ProgressMessage(byte level, uint experience, uint experienceToNext, uint maxHealth)
        {
            Level = level;
            Experience = experience;
            ExperienceToNext = experienceToNext;
            MaxHealth = maxHealth;
        }

        public byte Level { get; }
        public uint Experience { get; }
        public uint ExperienceToNext { get; }
        public uint MaxHealth { get; }
    }

    /// <summary>Atributos y estadísticas actuales del personaje (I-09).</summary>
    public readonly struct StatsMessage
    {
        public StatsMessage(
            byte classId,
            byte level,
            ushort str,
            ushort dex,
            ushort intelligence,
            ushort vit,
            ushort allocStr,
            ushort allocDex,
            ushort allocInt,
            ushort allocVit,
            ushort freePoints,
            uint maxHealth,
            ushort attackDamage,
            byte damageType,
            ushort defense,
            ushort magicResist,
            ushort resource,
            ushort maxResource,
            byte freeReset)
        {
            ClassId = classId;
            Level = level;
            Str = str;
            Dex = dex;
            Int = intelligence;
            Vit = vit;
            AllocStr = allocStr;
            AllocDex = allocDex;
            AllocInt = allocInt;
            AllocVit = allocVit;
            FreePoints = freePoints;
            MaxHealth = maxHealth;
            AttackDamage = attackDamage;
            DamageType = damageType;
            Defense = defense;
            MagicResist = magicResist;
            Resource = resource;
            MaxResource = maxResource;
            FreeReset = freeReset;
        }

        public byte ClassId { get; }
        public byte Level { get; }
        public ushort Str { get; }
        public ushort Dex { get; }
        public ushort Int { get; }
        public ushort Vit { get; }
        public ushort AllocStr { get; }
        public ushort AllocDex { get; }
        public ushort AllocInt { get; }
        public ushort AllocVit { get; }
        public ushort FreePoints { get; }
        public uint MaxHealth { get; }
        public ushort AttackDamage { get; }
        /// <summary>0 = físico; 1 = mágico.</summary>
        public byte DamageType { get; }
        public ushort Defense { get; }
        public ushort MagicResist { get; }
        public ushort Resource { get; }
        public ushort MaxResource { get; }
        public byte FreeReset { get; }
    }

    /// <summary>Mensaje del chat de zona aceptado por el servidor (I-07).</summary>
    public readonly struct ChatMessage
    {
        public ChatMessage(int senderEntityId, string senderName, string text)
        {
            SenderEntityId = senderEntityId;
            SenderName = senderName ?? string.Empty;
            Text = text ?? string.Empty;
        }

        public int SenderEntityId { get; }
        public string SenderName { get; }
        public string Text { get; }
    }
}
