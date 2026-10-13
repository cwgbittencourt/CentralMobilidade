using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CentralMobilidade.Telemetry.Application.Security;

public static class OpaqueBearerTokens
{
    public static string Generate(BearerCredentialKind kind) => Prefix(kind) + Encode(RandomNumberGenerator.GetBytes(32));
    public static string Prefix(BearerCredentialKind kind) => kind switch
    {
        BearerCredentialKind.Producer => "cm_p_",
        BearerCredentialKind.HumanOperator => "cm_o_",
        BearerCredentialKind.ViewerPresentationService => "cm_v_",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static bool HasValidFormat(string token, BearerCredentialKind kind)
    {
        if (token.Length != 48 || !token.StartsWith(Prefix(kind), StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            var bytes = Convert.FromBase64String(token[5..].Replace('-', '+').Replace('_', '/') + "=");
            return bytes.Length == 32 && Encode(bytes) == token[5..];
        }
        catch (FormatException)
        {
            return false;
        }
    }
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record BearerTokenHash(string LookupDigest, string Salt, string Verifier, int Iterations = 600000)
{
    public static string Lookup(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static BearerTokenHash Create(string token)
    {
        if (token is null || !Enum.GetValues<BearerCredentialKind>().Any(kind => OpaqueBearerTokens.HasValidFormat(token, kind)))
        {
            throw new ArgumentException("A generated opaque bearer token is required.", nameof(token));
        }
        var salt = RandomNumberGenerator.GetBytes(16);
        var verifier = Rfc2898DeriveBytes.Pbkdf2(token, salt, 600000, HashAlgorithmName.SHA256, 32);
        try
        {
            return new(Lookup(token), Convert.ToBase64String(salt), Convert.ToBase64String(verifier));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(verifier);
        }
    }
    public bool IsValid()
    {
        if (Iterations is < 600000 or > 1000000 || LookupDigest is null || LookupDigest.Length != 64 || Salt is null || Verifier is null)
        {
            return false;
        }
        foreach (var character in LookupDigest)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        try
        {
            return Convert.FromBase64String(Salt).Length == 16 && Convert.FromBase64String(Verifier).Length == 32;
        }
        catch (FormatException) { return false; }
    }
    public bool Matches(string token)
    {
        if (!IsValid())
        {
            return false;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(token, Convert.FromBase64String(Salt), Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(Verifier));
        }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }
    public override string ToString() => "[BearerTokenHash]";
}
