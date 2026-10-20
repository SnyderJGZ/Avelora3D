#nullable enable
using Avelora.Shared.Protocol;

namespace Avelora.Server.Persistence
{
    /// <summary>
    /// Reglas de los datos de login. Se validan en el servidor, no en el cliente: el códec solo
    /// comprueba tamaños y UTF-8.
    /// </summary>
    public static class LoginValidation
    {
        public const int MinAccountLength = 3;
        public const int MaxAccountLength = 32;
        public const int MinCharacterLength = 2;
        public const int MaxCharacterLength = 32;
        public const int MinPasswordLength = 6;
        public const int MaxPasswordLength = 128;

        public static string NormalizeAccount(string? accountName) =>
            (accountName ?? string.Empty).Trim();

        public static string ResolveCharacterName(string? characterName, string accountName)
        {
            string trimmed = (characterName ?? string.Empty).Trim();
            return trimmed.Length == 0 ? accountName : trimmed;
        }

        public static bool IsValidAccountName(string accountName) =>
            IsValidName(accountName, MinAccountLength, MaxAccountLength);

        public static bool IsValidCharacterName(string characterName) =>
            IsValidName(characterName, MinCharacterLength, MaxCharacterLength);

        public static bool IsValidPassword(string password) =>
            password != null &&
            password.Length >= MinPasswordLength &&
            password.Length <= MaxPasswordLength &&
            !string.IsNullOrWhiteSpace(password);

        public static bool IsValidInvitation(string invitationCode) =>
            invitationCode != null &&
            invitationCode.Length <= ProtocolConstants.MaxInvitationCodeBytes;

        private static bool IsValidName(string value, int minLength, int maxLength)
        {
            if (value is null || value.Length < minLength || value.Length > maxLength)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];

                bool allowed =
                    (c >= 'a' && c <= 'z') ||
                    (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') ||
                    c == '_' ||
                    c == '-';

                if (!allowed)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
