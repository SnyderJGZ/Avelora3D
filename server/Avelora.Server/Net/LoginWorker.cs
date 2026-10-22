#nullable enable
using System;
using System.Collections.Concurrent;
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
    /// <see cref="LoginRequest"/>. Este hilo hace <see cref="IPlayerRepository.Login"/> y
    /// <see cref="IPlayerRepository.GetOrCreateCharacter"/> y deja el <see cref="LoginResult"/> en
    /// una cola que el hilo de red vacía en cada <c>PollOnce</c>.
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
        private readonly ConcurrentQueue<LoginRequest> _requests = new ConcurrentQueue<LoginRequest>();
        private readonly ConcurrentQueue<LoginResult> _results = new ConcurrentQueue<LoginResult>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _thread;

        private volatile bool _stopping;
        private bool _disposed;
        private int _inFlight;

        public LoginWorker(IPlayerRepository repository, string? registrationCode)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _registrationCode = registrationCode ?? string.Empty;

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
            _requests.Enqueue(request);
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

                while (!_stopping && _requests.TryDequeue(out LoginRequest request))
                {
                    _results.Enqueue(Process(request));
                }

                if (_stopping)
                {
                    return;
                }
            }
        }

        private LoginResult Process(LoginRequest request)
        {
            try
            {
                AccountLoginResult login = _repository.Login(
                    request.AccountName,
                    request.Password,
                    request.InvitationCode,
                    _registrationCode);

                if (!login.Success)
                {
                    return LoginResult.Rejected(
                        request.ConnectionId,
                        request.AccountName,
                        login.Failure,
                        login.CountsAsFailedAttempt);
                }

                CharacterRecord? character = _repository.GetOrCreateCharacter(
                    login.AccountId,
                    request.CharacterName,
                    request.ZoneId,
                    request.Spawn,
                    request.MapHash);

                if (character is null)
                {
                    // Credenciales válidas, pero la cuenta ya no admite más personajes.
                    return LoginResult.Rejected(
                        request.ConnectionId,
                        request.AccountName,
                        RejectReason.CharacterLimit);
                }

                return LoginResult.Accepted(
                    request.ConnectionId,
                    login.AccountId,
                    request.AccountName,
                    character);
            }
            catch (Exception ex)
            {
                // El hilo de login no puede tumbar el servidor: se devuelve el fallo y la red
                // contesta como siempre.
                return LoginResult.Faulted(request.ConnectionId, request.AccountName, ex.Message);
            }
        }
    }

    /// <summary>Todo lo que necesita el hilo de login para autenticar y cargar el personaje.</summary>
    public readonly struct LoginRequest
    {
        public LoginRequest(
            int connectionId,
            string accountName,
            string password,
            string invitationCode,
            string characterName,
            int zoneId,
            Vec2 spawn,
            byte[] mapHash)
        {
            ConnectionId = connectionId;
            AccountName = accountName;
            Password = password;
            InvitationCode = invitationCode;
            CharacterName = characterName;
            ZoneId = zoneId;
            Spawn = spawn;
            MapHash = mapHash;
        }

        public int ConnectionId { get; }

        public string AccountName { get; }

        public string Password { get; }

        public string InvitationCode { get; }

        public string CharacterName { get; }

        public int ZoneId { get; }

        public Vec2 Spawn { get; }

        public byte[] MapHash { get; }
    }

    /// <summary>Resultado de una petición de login, ya fuera del hilo de red.</summary>
    public sealed class LoginResult
    {
        private LoginResult(
            int connectionId,
            string accountName,
            bool success,
            RejectReason failure,
            bool countsAsFailedAttempt,
            int accountId,
            CharacterRecord? character,
            string? error)
        {
            ConnectionId = connectionId;
            AccountName = accountName;
            Success = success;
            Failure = failure;
            CountsAsFailedAttempt = countsAsFailedAttempt;
            AccountId = accountId;
            Character = character;
            Error = error;
        }

        public int ConnectionId { get; }

        /// <summary>Cuenta normalizada; se usa para contar los fallos por cuenta.</summary>
        public string AccountName { get; }

        public bool Success { get; }

        /// <summary>Motivo del rechazo cuando <see cref="Success"/> es falso y no hubo excepción.</summary>
        public RejectReason Failure { get; }

        /// <summary>Si ese fallo debe sumar al cupo de intentos de la cuenta.</summary>
        public bool CountsAsFailedAttempt { get; }

        public int AccountId { get; }

        public CharacterRecord? Character { get; }

        /// <summary>Mensaje de la excepción del repositorio, si la hubo.</summary>
        public string? Error { get; }

        public static LoginResult Accepted(
            int connectionId,
            int accountId,
            string accountName,
            CharacterRecord character) =>
            new LoginResult(
                connectionId,
                accountName,
                true,
                RejectReason.Unknown,
                false,
                accountId,
                character,
                null);

        public static LoginResult Rejected(
            int connectionId,
            string accountName,
            RejectReason reason,
            bool countsAsFailedAttempt = false) =>
            new LoginResult(
                connectionId,
                accountName,
                false,
                reason,
                countsAsFailedAttempt,
                0,
                null,
                null);

        public static LoginResult Faulted(int connectionId, string accountName, string error) =>
            new LoginResult(
                connectionId,
                accountName,
                false,
                RejectReason.Unknown,
                false,
                0,
                null,
                error);
    }
}
