using System;
using System.Text;

namespace CentralMobilidade.Telemetry.Domain.ValueObjects;

internal static class IdentifierValidation
{
    internal const int MaxUtf8Bytes = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxUtf8Bytes)
        {
            throw new ArgumentException("Identifier must be nonempty and at most 256 UTF-8 bytes.", nameof(value));
        }

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException("Identifier must not contain control characters.", nameof(value));
            }
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Identifier must contain valid Unicode.", nameof(value));
        }

        if (byteCount > MaxUtf8Bytes)
        {
            throw new ArgumentException("Identifier must be at most 256 UTF-8 bytes.", nameof(value));
        }

        return value;
    }
}
