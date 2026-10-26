#nullable enable
using System.Globalization;
using System.Text;

namespace Avelora.Server.Net
{
    /// <summary>Resultado de comprobar y normalizar el texto del chat.</summary>
    public enum ChatTextValidationResult : byte
    {
        Valid = 0,
        Invalid = 1,
        TooLong = 2,
    }

    /// <summary>Validación defensiva del texto no confiable que llega al chat de zona.</summary>
    public static class ChatTextValidator
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>Recorta extremos, comprueba UTF-8, tamaño y caracteres prohibidos.</summary>
        public static ChatTextValidationResult Validate(string? text, int maxBytes, out string normalized)
        {
            normalized = (text ?? string.Empty).Trim();

            int byteCount;
            try
            {
                byteCount = StrictUtf8.GetByteCount(normalized);
            }
            catch (EncoderFallbackException)
            {
                return ChatTextValidationResult.Invalid;
            }

            if (byteCount == 0)
            {
                return ChatTextValidationResult.Invalid;
            }

            if (byteCount > maxBytes)
            {
                return ChatTextValidationResult.TooLong;
            }

            for (int i = 0; i < normalized.Length; i++)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(normalized, i);
                if (category == UnicodeCategory.Control || category == UnicodeCategory.Format)
                {
                    return ChatTextValidationResult.Invalid;
                }

                if (char.IsHighSurrogate(normalized[i]) && i + 1 < normalized.Length &&
                    char.IsLowSurrogate(normalized[i + 1]))
                {
                    i++;
                }
            }

            return ChatTextValidationResult.Valid;
        }
    }
}
