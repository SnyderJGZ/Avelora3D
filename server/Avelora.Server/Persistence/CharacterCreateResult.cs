#nullable enable
using Avelora.Shared.Protocol;

namespace Avelora.Server.Persistence
{
    /// <summary>Resultado de crear un personaje sin confundir rechazos con errores de almacenamiento.</summary>
    public readonly struct CharacterCreateResult
    {
        private CharacterCreateResult(bool success, RejectReason failure, CharacterRecord? character)
        {
            Success = success;
            Failure = failure;
            Character = character;
        }

        public bool Success { get; }

        public RejectReason Failure { get; }

        public CharacterRecord? Character { get; }

        public static CharacterCreateResult Created(CharacterRecord character) =>
            new CharacterCreateResult(true, RejectReason.Unknown, character);

        public static CharacterCreateResult Rejected(RejectReason reason) =>
            new CharacterCreateResult(false, reason, null);
    }
}
