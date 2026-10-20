#nullable enable
using Avelora.Shared;

namespace Avelora.Shared.Protocol
{
    /// <summary>
    /// Saludo inicial del cliente: versión, tick, mapa y credenciales (plan §4 y §8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// El cliente no manda ni su posición ni su tiempo. Manda lo que el servidor necesita para
    /// decidir si puede jugar: versión del protocolo, tick al que está compilado, qué mapa tiene,
    /// quién dice ser y qué personaje quiere usar.
    /// </para>
    /// <para>
    /// La contraseña viaja en claro sobre UDP en esta etapa. El MVP está pensado para localhost;
    /// el cifrado del transporte y los límites de intentos llegan en la Etapa H.
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
                "avelora-dev",
                string.Empty,
                "avelora-dev")
        {
        }

        public HelloMessage(
            ushort protocolVersion,
            ushort tickRate,
            byte[] mapHash,
            uint clientToken,
            string accountName,
            string password,
            string characterName,
            string invitationCode)
        {
            ProtocolVersion = protocolVersion;
            TickRate = tickRate;
            MapHash = mapHash;
            ClientToken = clientToken;
            AccountName = accountName ?? string.Empty;
            Password = password ?? string.Empty;
            CharacterName = characterName ?? string.Empty;
            InvitationCode = invitationCode ?? string.Empty;
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

        /// <summary>Contraseña de la cuenta. No se registra en logs.</summary>
        public string Password { get; }

        /// <summary>Personaje que se quiere usar. Vacío significa «el mismo nombre que la cuenta».</summary>
        public string CharacterName { get; }

        /// <summary>Código necesario para registrar una cuenta nueva (el MVP no tiene registro abierto).</summary>
        public string InvitationCode { get; }
    }

    /// <summary>
    /// Intención de movimiento de un tick: solo dirección y número de secuencia (guía §4.3).
    /// </summary>
    /// <remarks>
    /// <c>Sequence</c> crece de uno en uno por input. El servidor la usa para descartar repetidos
    /// y viejos y, en la Etapa D, el cliente para saber qué ha confirmado ya el servidor.
    /// </remarks>
    public readonly struct MoveInputMessage
    {
        public MoveInputMessage(uint sequence, sbyte directionX, sbyte directionY)
        {
            Sequence = sequence;
            DirectionX = directionX;
            DirectionY = directionY;
        }

        public uint Sequence { get; }

        /// <summary>-1, 0 o 1. La validación semántica la hace la simulación, en la frontera.</summary>
        public sbyte DirectionX { get; }

        /// <inheritdoc cref="DirectionX"/>
        public sbyte DirectionY { get; }
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
        RegistrationClosed = 9,

        /// <summary>Ese personaje ya está jugando en otra conexión.</summary>
        CharacterInUse = 10,
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
    /// <remarks>Ocupa 11 bytes: Id (4), posición cuantizada (2+2) y dirección y tipo (1+1+1).</remarks>
    public readonly struct EntityState
    {
        public EntityState(int id, short x, short y, sbyte directionX, sbyte directionY, EntityKind kind)
        {
            Id = id;
            X = x;
            Y = y;
            DirectionX = directionX;
            DirectionY = directionY;
            Kind = kind;
        }

        public int Id { get; }

        /// <summary>Posición X en unidades de 1/64 de tile.</summary>
        public short X { get; }

        /// <summary>Posición Y en unidades de 1/64 de tile.</summary>
        public short Y { get; }

        public sbyte DirectionX { get; }

        public sbyte DirectionY { get; }

        public EntityKind Kind { get; }
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
}
