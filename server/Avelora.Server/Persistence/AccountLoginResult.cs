#nullable enable
using Avelora.Shared.Protocol;

namespace Avelora.Server.Persistence
{
    /// <summary>Resultado de autenticar (o registrar) una cuenta.</summary>
    public readonly struct AccountLoginResult
    {
        private AccountLoginResult(bool success, int accountId, RejectReason failure)
        {
            Success = success;
            AccountId = accountId;
            Failure = failure;
        }

        public bool Success { get; }

        public int AccountId { get; }

        /// <summary>Motivo para el <c>Reject</c> cuando <see cref="Success"/> es falso.</summary>
        public RejectReason Failure { get; }

        public static AccountLoginResult Ok(int accountId) =>
            new AccountLoginResult(true, accountId, RejectReason.Unknown);

        public static AccountLoginResult Failed(RejectReason reason) =>
            new AccountLoginResult(false, 0, reason);
    }
}
