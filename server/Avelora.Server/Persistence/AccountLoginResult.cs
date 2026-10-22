#nullable enable
using Avelora.Shared.Protocol;

namespace Avelora.Server.Persistence
{
    /// <summary>Resultado de autenticar (o registrar) una cuenta.</summary>
    public readonly struct AccountLoginResult
    {
        private AccountLoginResult(
            bool success,
            int accountId,
            RejectReason failure,
            bool countsAsFailedAttempt)
        {
            Success = success;
            AccountId = accountId;
            Failure = failure;
            CountsAsFailedAttempt = countsAsFailedAttempt;
        }

        public bool Success { get; }

        public int AccountId { get; }

        /// <summary>Motivo para el <c>Reject</c> cuando <see cref="Success"/> es falso.</summary>
        public RejectReason Failure { get; }

        /// <summary>
        /// Si el fallo debe sumar al límite de intentos de esa cuenta. Es un dato interno (no viaja
        /// en el protocolo): una cuenta inexistente responde como credenciales inválidas, pero no
        /// debe gastar el cupo de un nombre que ni siquiera existe.
        /// </summary>
        public bool CountsAsFailedAttempt { get; }

        public static AccountLoginResult Ok(int accountId) =>
            new AccountLoginResult(true, accountId, RejectReason.Unknown, false);

        public static AccountLoginResult Failed(RejectReason reason, bool countsAsFailedAttempt = false) =>
            new AccountLoginResult(false, 0, reason, countsAsFailedAttempt);
    }
}
