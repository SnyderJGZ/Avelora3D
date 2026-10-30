#nullable enable

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
        public const int MinCharacterLength = 3;
        public const int MaxCharacterLength = 16;
        public const int MinPasswordLength = 6;
        public const int MaxPasswordLength = 128;

        public static string NormalizeAccount(string? accountName) =>
            (accountName ?? string.Empty).Trim();

        private static readonly string[] ReservedCharacterNames =
        {
            "admin", "administrador", "gm", "moderador", "mod", "avelora",
            "sistema", "servidor", "server", "system",
        };

        /// <summary>Quita espacios extremos y recorta a los bytes de nombre que caben en el chat.</summary>
        public static string NormalizeCharacterName(string? characterName)
        {
            string trimmed = (characterName ?? string.Empty).Trim();
            if (trimmed.Length > MaxCharacterLength ||
                System.Text.Encoding.UTF8.GetByteCount(trimmed) <= Avelora.Shared.Protocol.ProtocolConstants.MaxChatNameBytes)
            {
                return trimmed;
            }

            var result = new System.Text.StringBuilder(trimmed.Length);
            int bytes = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                int charBytes = System.Text.Encoding.UTF8.GetByteCount(new[] { c });
                if (bytes + charBytes > Avelora.Shared.Protocol.ProtocolConstants.MaxChatNameBytes)
                {
                    break;
                }

                result.Append(c);
                bytes += charBytes;
            }

            return result.ToString().Trim();
        }

        /// <summary>Clave canónica del nombre, independiente de las mayúsculas Unicode.</summary>
        public static string CharacterNameKey(string characterName) => characterName.ToLowerInvariant();

        public static bool IsValidAccountName(string accountName) =>
            IsValidName(accountName, MinAccountLength, MaxAccountLength);

        public static bool IsValidCharacterName(string characterName)
        {
            if (characterName is null || characterName.Length < MinCharacterLength || characterName.Length > MaxCharacterLength ||
                !string.Equals(characterName, characterName.Trim(), System.StringComparison.Ordinal))
            {
                return false;
            }

            if (!IsLetter(characterName[0]))
            {
                return false;
            }

            bool previousSpace = false;
            for (int i = 0; i < characterName.Length; i++)
            {
                char c = characterName[i];
                if (c == ' ')
                {
                    if (previousSpace || i == 0 || i == characterName.Length - 1)
                    {
                        return false;
                    }

                    previousSpace = true;
                    continue;
                }

                previousSpace = false;
                if (!IsLetter(c) && !(c >= '0' && c <= '9'))
                {
                    return false;
                }
            }

            string key = CharacterNameKey(characterName);
            for (int i = 0; i < ReservedCharacterNames.Length; i++)
            {
                if (key == ReservedCharacterNames[i])
                {
                    return false;
                }
            }

            return System.Text.Encoding.UTF8.GetByteCount(characterName) <=
                Avelora.Shared.Protocol.ProtocolConstants.MaxChatNameBytes;
        }

        private static bool IsLetter(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
            c == 'á' || c == 'é' || c == 'í' || c == 'ó' || c == 'ú' ||
            c == 'Á' || c == 'É' || c == 'Í' || c == 'Ó' || c == 'Ú' ||
            c == 'ü' || c == 'Ü' || c == 'ñ' || c == 'Ñ';

        public static bool IsValidPassword(string password) =>
            password != null &&
            password.Length >= MinPasswordLength &&
            password.Length <= MaxPasswordLength &&
            !string.IsNullOrWhiteSpace(password);

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
