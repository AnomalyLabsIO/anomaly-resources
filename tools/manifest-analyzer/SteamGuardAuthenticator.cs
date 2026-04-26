using System.Security.Cryptography;
using SteamKit2.Authentication;

namespace ManifestAnalyzer;

internal sealed class SteamGuardAuthenticator : IAuthenticator
{
    private static readonly char[] SteamAlphabet =
        ['2','3','4','5','6','7','8','9','B','C','D','F','G','H','J','K','M','N','P','Q','R','T','V','W','X','Y'];

    private readonly byte[] _sharedSecret;

    public SteamGuardAuthenticator(string sharedSecretBase64)
    {
        if (string.IsNullOrWhiteSpace(sharedSecretBase64))
            throw new ArgumentException("Shared secret must not be empty.", nameof(sharedSecretBase64));

        _sharedSecret = Convert.FromBase64String(sharedSecretBase64.Trim());
    }

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        if (previousCodeWasIncorrect)
        {
            Console.WriteLine("Steam rejected the previous TOTP code; generating a new one.");
        }

        var code = GenerateSteamGuardCode(_sharedSecret, DateTimeOffset.UtcNow);
        return Task.FromResult(code);
    }

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
    {
        throw new SteamSessionExpiredException(
            $"Steam asked for an email-based 2FA code (sent to {email}). The dedicated CI account must be enrolled in the Steam mobile authenticator with STEAM_SHARED_SECRET configured.");
    }

    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        return Task.FromResult(false);
    }

    private static string GenerateSteamGuardCode(byte[] sharedSecret, DateTimeOffset now)
    {
        var interval = now.ToUnixTimeSeconds() / 30L;
        var timeBytes = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            timeBytes[i] = (byte)(interval & 0xFF);
            interval >>= 8;
        }

        using var hmac = new HMACSHA1(sharedSecret);
        var hash = hmac.ComputeHash(timeBytes);

        var offset = hash[^1] & 0x0F;
        var truncated = ((hash[offset] & 0x7F) << 24)
                      | ((hash[offset + 1] & 0xFF) << 16)
                      | ((hash[offset + 2] & 0xFF) << 8)
                      | (hash[offset + 3] & 0xFF);

        var code = new char[5];
        for (var i = 0; i < 5; i++)
        {
            code[i] = SteamAlphabet[truncated % SteamAlphabet.Length];
            truncated /= SteamAlphabet.Length;
        }

        return new string(code);
    }
}
