using System.Text;

namespace Iemas.Infrastructure.Security;

public enum JwtSecretStrength { Ok, WeakWarning, WeakError, WeakFatal }

/// <summary>Startup check that HMAC JWT signing secrets are long enough (HS256 expects >= 32 bytes).</summary>
public static class JwtSecretValidator
{
    public const int MinSecretBytes = 32;

    /// <summary>
    /// Development: warn only. Elsewhere: fatal, unless <paramref name="failFastOutsideDevelopment"/> is false
    /// (then an error-level result so a short value in an existing deployment is loudly reported but not bricked).
    /// </summary>
    public static JwtSecretStrength Evaluate(string? secret, bool isDevelopment, bool failFastOutsideDevelopment = true)
    {
        if (secret is not null && Encoding.UTF8.GetByteCount(secret) >= MinSecretBytes) return JwtSecretStrength.Ok;
        if (isDevelopment) return JwtSecretStrength.WeakWarning;
        return failFastOutsideDevelopment ? JwtSecretStrength.WeakFatal : JwtSecretStrength.WeakError;
    }

    public static string Describe(string settingName) =>
        $"{settingName} is shorter than {MinSecretBytes} bytes. Use a random value of at least {MinSecretBytes} bytes (e.g. `openssl rand -base64 48`).";
}
