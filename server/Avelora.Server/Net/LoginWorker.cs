#nullable enable
using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading;
using Avelora.Server.Persistence;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Net
{
    /// <summary>
    /// Hilo propio para autenticar: PBKDF2 y SQLite nunca frenan la red (plan del servidor §8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// El hilo de red solo valida lo barato (versión, tick, mapa, nombres) y encola una
    /// <see cref="LoginRequest"/>. Este hilo hace <see cref="IPlayerRepository.Login"/> y todas las
    /// lecturas/escrituras de personajes, y deja el <see cref="LoginResult"/> en una cola que el hilo
    /// de red vacía en cada <c>PollOnce</c>.
    /// </para>
    /// <para>
    /// No toca el transporte ni el mundo: no puede, no tiene referencias a ellos. Las excepciones
    /// del repositorio se capturan y viajan en el resultado, para que la red decida qué contestar.
    /// </para>
    /// </remarks>
    public sealed class LoginWorker : IDisposable
    {
        private readonly IPlayerRepository _repository;
        private readonly string _registrationCode;
        private readonly RSAParameters _serverKey;
        private readonly ConcurrentQueue<WorkerRequest> _requests = new ConcurrentQueue<WorkerRequest>();
        private readonly ConcurrentQueue<LoginResult> _results = new ConcurrentQueue<LoginResult>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _thread;

        private volatile bool _stopping;
        private bool _disposed;
        private int _inFlight;

        public LoginWorker(IPlayerRepository repository, string? registrationCode, RSAParameters serverKey)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _registrationCode = registrationCode ?? string.Empty;
            _serverKey = serverKey;

            _thread = new Thread(Run)
            {
                Name = "avelora-login",
                IsBackground = true,
            };

            _thread.Start();
        }

        /// <summary>
        /// Peticiones en vuelo: las que esperan turno en la cola y la que se está calculando.
        /// El resultado solo se descuenta cuando la red lo saca, así que una conexión que se va
        /// sin esperar no libera el tope (si no, se podría llenar la cola de PBKDF2 gratis).
        /// </summary>
        public int InFlight => Volatile.Read(ref _inFlight);

        /// <summary>Encola una petición y despierta al hilo. Nunca bloquea al que llama.</summary>
        public void Enqueue(in LoginRequest request)
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.Increment(ref _inFlight);
            _requests.Enqueue(WorkerRequest.ForLogin(request));
            _signal.Set();
        }

        /// <summary>Encola una operación de personajes fuera de los hilos de red y simulación.</summary>
        public void EnqueueCharacterOperation(in CharacterOperationRequest request)
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.Increment(ref _inFlight);
            _requests.Enqueue(WorkerRequest.ForCharacter(request));
            _signal.Set();
        }

        /// <summary>Intenta sacar un resultado ya calculado. La cola la vacía el hilo de red.</summary>
        public bool TryDequeueResult([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out LoginResult? result)
        {
            if (!_results.TryDequeue(out result))
            {
                return false;
            }

            Interlocked.Decrement(ref _inFlight);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopping = true;
            _signal.Set();
            _thread.Join(TimeSpan.FromSeconds(2));
            _signal.Dispose();
        }

        private void Run()
        {
            while (true)
            {
                _signal.WaitOne();

                while (!_stopping && _requests.TryDequeue(out WorkerRequest request))
                {
                    _results.Enqueue(request.IsLogin
                        ? ProcessLogin(request.Login)
                        : ProcessCharacterOperation(request.Character));
                }

                if (_stopping)
                {
                    return;
                }
            }
        }

        private LoginResult ProcessLogin(LoginRequest request)
        {
            try
            {
                // Descifrar aquí, en el hilo de login, quita unos milisegundos al hilo de red. Si
                // el bloque no se descifra o el nonce no es el de esta conexión, no se llega al
                // repositorio: es un saludo reenviado o manipulado.
                if (!HelloSecrets.TryDecrypt(
                    _serverKey,
                    request.EncryptedSecrets,
                    request.Nonce,
                    out string password,
                    out string invitationCode,
                    out string cryptoError))
                {
                    return LoginResult.CryptoRejected(
                        request.ConnectionId,
                        request.ConnectionGeneration,
                        request.AccountName,
                        cryptoError);
                }

                AccountLoginResult login = request.Intent == HelloIntent.Register
                    ? _repository.Register(
                        request.AccountName,
                        password,
                        invitationCode,
                        _registrationCode)
                    : _repository.Login(request.AccountName, password);

                if (!login.Success)
                {
                    return LoginResult.Rejected(
                        request.ConnectionId,
                        request.ConnectionGeneration,
                        request.AccountName,
                        login.Failure,
                        login.CountsAsFailedAttempt);
                }

                return LoginResult.Authenticated(
                    request.ConnectionId,
                    request.ConnectionGeneration,
                    login.AccountId,
                    request.AccountName,
                    _repository.ListCharacters(login.AccountId));
            }
            catch (Exception ex)
            {
                // El hilo de login no puede tumbar el servidor: se devuelve el fallo y la red
                // contesta como siempre.
                return LoginResult.Faulted(
                    request.ConnectionId,
                    request.ConnectionGeneration,
                    request.AccountName,
                    ex.Message);
            }
        }

        private LoginResult ProcessCharacterOperation(CharacterOperationRequest request)
        {
            try
            {
                switch (request.Operation)
                {
                    case CharacterOperationKind.Create:
                        CharacterCreateResult created = _repository.CreateCharacter(
                            request.AccountId,
                            request.Name,
                            request.ClassId,
                            request.MaxCharacters,
                            request.ZoneId,
                            request.Spawn,
                            request.MapHash,
                            request.Sex);
                        return created.Success
                            ? LoginResult.CharacterListUpdated(
                                request.ConnectionId,
                                request.ConnectionGeneration,
                                request.AccountId,
                                request.Operation,
                                _repository.ListCharacters(request.AccountId))
                            : LoginResult.CharacterOperationRejected(
                                request.ConnectionId,
                                request.ConnectionGeneration,
                                request.AccountId,
                                request.Operation,
                                created.Failure);

                    case CharacterOperationKind.Delete:
                        RejectReason deleted = _repository.DeleteCharacter(
                            request.AccountId,
                            request.CharacterId,
                            request.Name);
                        return deleted == RejectReason.Unknown
                            ? LoginResult.CharacterListUpdated(
                                request.ConnectionId,
                                request.ConnectionGeneration,
                                request.AccountId,
                                request.Operation,
                                _repository.ListCharacters(request.AccountId))
                            : LoginResult.CharacterOperationRejected(
                                request.ConnectionId,
                                request.ConnectionGeneration,
                                request.AccountId,
                                request.Operation,
                                deleted);

                    case CharacterOperationKind.Select:
                        return _repository.TryGetCharacter(
                            request.AccountId,
                            request.CharacterId,
                            out CharacterRecord? selected) && selected != null
                            ? LoginResult.CharacterSelected(
                                request.ConnectionId,
                                request.ConnectionGeneration,
                                request.AccountId,
                                selected)
                            : LoginResult.CharacterOperationRejected(
                                request.ConnectionId,
                                request.ConnectionGeneration,
                                request.AccountId,
                                request.Operation,
                                RejectReason.CharacterNotFound);

                    default:
                        return LoginResult.CharacterOperationRejected(
                            request.ConnectionId,
                            request.ConnectionGeneration,
                            request.AccountId,
                            request.Operation,
                            RejectReason.Unknown);
                }
            }
            catch (Exception ex)
            {
                return LoginResult.CharacterOperationFaulted(
                    request.ConnectionId,
                    request.ConnectionGeneration,
                    request.AccountId,
                    request.Operation,
                    ex.Message);
            }
        }
    }

    /// <summary>Todo lo que necesita el hilo de login para autenticar.</summary>
    public readonly struct LoginRequest
    {
        public LoginRequest(
            int connectionId,
            long connectionGeneration,
            string accountName,
            byte[] encryptedSecrets,
            byte[] nonce,
            int zoneId,
            Vec2 spawn,
            byte[] mapHash,
            HelloIntent intent)
        {
            ConnectionId = connectionId;
            ConnectionGeneration = connectionGeneration;
            AccountName = accountName;
            EncryptedSecrets = encryptedSecrets;
            Nonce = nonce;
            ZoneId = zoneId;
            Spawn = spawn;
            MapHash = mapHash;
            Intent = intent;
        }

        public int ConnectionId { get; }

        public long ConnectionGeneration { get; }

        public string AccountName { get; }

        /// <summary>Bloque RSA-OAEP-SHA1 del saludo; se descifra en este hilo.</summary>
        public byte[] EncryptedSecrets { get; }

        /// <summary>Nonce del desafío de esta conexión; el bloque tiene que llevarlo dentro.</summary>
        public byte[] Nonce { get; }

        public int ZoneId { get; }

        public Vec2 Spawn { get; }

        public byte[] MapHash { get; }

        public HelloIntent Intent { get; }
    }

    public enum CharacterOperationKind : byte
    {
        Login = 0,
        Create = 1,
        Delete = 2,
        Select = 3,
    }

    /// <summary>Operación de personajes que debe ejecutarse en el hilo de login.</summary>
    public readonly struct CharacterOperationRequest
    {
        public CharacterOperationRequest(
            int connectionId,
            long connectionGeneration,
            int accountId,
            string accountName,
            CharacterOperationKind operation,
            string name,
            byte classId,
            int characterId,
            int maxCharacters,
            int zoneId,
            Vec2 spawn,
            byte[] mapHash,
            byte sex = 0)
        {
            ConnectionId = connectionId;
            ConnectionGeneration = connectionGeneration;
            AccountId = accountId;
            AccountName = accountName ?? string.Empty;
            Operation = operation;
            Name = name ?? string.Empty;
            ClassId = classId;
            CharacterId = characterId;
            MaxCharacters = maxCharacters;
            ZoneId = zoneId;
            Spawn = spawn;
            MapHash = mapHash;
            Sex = sex;
        }

        public int ConnectionId { get; }
        public long ConnectionGeneration { get; }
        public int AccountId { get; }
        public string AccountName { get; }
        public CharacterOperationKind Operation { get; }
        public string Name { get; }
        public byte ClassId { get; }
        public int CharacterId { get; }
        public int MaxCharacters { get; }
        public int ZoneId { get; }
        public Vec2 Spawn { get; }
        public byte[] MapHash { get; }
        public byte Sex { get; }
    }

    internal readonly struct WorkerRequest
    {
        private WorkerRequest(bool isLogin, LoginRequest login, CharacterOperationRequest character)
        {
            IsLogin = isLogin;
            Login = login;
            Character = character;
        }

        public bool IsLogin { get; }
        public LoginRequest Login { get; }
        public CharacterOperationRequest Character { get; }

        public static WorkerRequest ForLogin(LoginRequest request) => new WorkerRequest(true, request, default);
        public static WorkerRequest ForCharacter(CharacterOperationRequest request) => new WorkerRequest(false, default, request);
    }

    /// <summary>Resultado de una petición de login o personaje, ya fuera del hilo de red.</summary>
    public sealed class LoginResult
    {
        private LoginResult(
            int connectionId,
            long connectionGeneration,
            string accountName,
            CharacterOperationKind operation,
            bool success,
            RejectReason failure,
            bool countsAsFailedAttempt,
            int accountId,
            CharacterRecord? character,
            CharacterRecord[]? characters,
            string? error,
            string? cryptoFailure)
        {
            ConnectionId = connectionId;
            ConnectionGeneration = connectionGeneration;
            AccountName = accountName;
            Operation = operation;
            Success = success;
            Failure = failure;
            CountsAsFailedAttempt = countsAsFailedAttempt;
            AccountId = accountId;
            Character = character;
            Characters = characters ?? Array.Empty<CharacterRecord>();
            Error = error;
            CryptoFailure = cryptoFailure;
        }

        public int ConnectionId { get; }

        public long ConnectionGeneration { get; }

        /// <summary>Cuenta normalizada; se usa para contar los fallos por cuenta.</summary>
        public string AccountName { get; }

        public CharacterOperationKind Operation { get; }

        public bool Success { get; }

        /// <summary>Motivo del rechazo cuando <see cref="Success"/> es falso y no hubo excepción.</summary>
        public RejectReason Failure { get; }

        /// <summary>Si ese fallo debe sumar al cupo de intentos de la cuenta.</summary>
        public bool CountsAsFailedAttempt { get; }

        public int AccountId { get; }

        public CharacterRecord? Character { get; }

        public CharacterRecord[] Characters { get; }

        /// <summary>Mensaje de la excepción del repositorio, si la hubo.</summary>
        public string? Error { get; }

        /// <summary>
        /// Motivo por el que no se pudo descifrar el saludo, si fue ese el fallo. No suma al cupo
        /// de la cuenta: un bloque manipulado no es un intento de adivinar la contraseña.
        /// </summary>
        public string? CryptoFailure { get; }

        public static LoginResult Authenticated(
            int connectionId,
            long connectionGeneration,
            int accountId,
            string accountName,
            CharacterRecord[] characters) =>
            new LoginResult(
                connectionId,
                connectionGeneration,
                accountName,
                CharacterOperationKind.Login,
                true,
                RejectReason.Unknown,
                false,
                accountId,
                null,
                characters,
                null,
                null);

        public static LoginResult Rejected(
            int connectionId,
            long connectionGeneration,
            string accountName,
            RejectReason reason,
            bool countsAsFailedAttempt = false) =>
            new LoginResult(
                connectionId,
                connectionGeneration,
                accountName,
                CharacterOperationKind.Login,
                false,
                reason,
                countsAsFailedAttempt,
                0,
                null,
                null,
                null,
                null);

        public static LoginResult CharacterListUpdated(
            int connectionId,
            long connectionGeneration,
            int accountId,
            CharacterOperationKind operation,
            CharacterRecord[] characters) =>
            new LoginResult(
                connectionId,
                connectionGeneration,
                string.Empty,
                operation,
                true,
                RejectReason.Unknown,
                false,
                accountId,
                null,
                characters,
                null,
                null);

        public static LoginResult CharacterSelected(
            int connectionId,
            long connectionGeneration,
            int accountId,
            CharacterRecord character) =>
            new LoginResult(
                connectionId,
                connectionGeneration,
                string.Empty,
                CharacterOperationKind.Select,
                true,
                RejectReason.Unknown,
                false,
                accountId,
                character,
                null,
                null,
                null);

        public static LoginResult CharacterOperationRejected(
            int connectionId,
            long connectionGeneration,
            int accountId,
            CharacterOperationKind operation,
            RejectReason reason) =>
            new LoginResult(
                connectionId,
                connectionGeneration,
                string.Empty,
                operation,
                false,
                reason,
                false,
                accountId,
                null,
                null,
                null,
                null);

        /// <summary>
        /// El saludo no se pudo descifrar (o el nonce no era el de esta conexión). Se responde
        /// <see cref="RejectReason.InvalidCredentials"/> sin tocar el repositorio.
        /// </summary>
        public static LoginResult CryptoRejected(
            int connectionId,
            long connectionGeneration,
            string accountName,
            string reason) =>
            new LoginResult(
                connectionId,
                connectionGeneration,
                accountName,
                CharacterOperationKind.Login,
                false,
                RejectReason.InvalidCredentials,
                false,
                0,
                null,
                null,
                null,
                reason);

        public static LoginResult Faulted(
            int connectionId,
            long connectionGeneration,
            string accountName,
            string error) =>
            new LoginResult(
                connectionId,
                connectionGeneration,
                accountName,
                CharacterOperationKind.Login,
                false,
                RejectReason.Unknown,
                false,
                0,
                null,
                null,
                error,
                null);

        public static LoginResult CharacterOperationFaulted(
            int connectionId,
            long connectionGeneration,
            int accountId,
            CharacterOperationKind operation,
            string error) =>
            new LoginResult(
                connectionId,
                connectionGeneration,
                string.Empty,
                operation,
                false,
                RejectReason.Unknown,
                false,
                accountId,
                null,
                null,
                error,
                null);
    }
}
