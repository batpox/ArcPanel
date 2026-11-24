using System.Security.Cryptography;
using System.Text;

namespace Arc.Services;

/// <summary>Validates the temporary management PIN used to enter Arc's administration UI.</summary>
public sealed class ArcAdminAccess
{
    private readonly byte[] _pin;

    public ArcAdminAccess(IConfiguration configuration)
    {
        string? configuredPin = configuration["ARC_ADMIN_PIN"] ?? Environment.GetEnvironmentVariable("ARC_ADMIN_PIN");
        bool generated = string.IsNullOrWhiteSpace(configuredPin);
        string pin = generated ? RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString() : configuredPin!;
        _pin = Encoding.UTF8.GetBytes(pin);
        if (generated)
        {
            Console.WriteLine("Arc management PIN (valid until Arc restarts): " + pin);
            Console.WriteLine("Set ARC_ADMIN_PIN to use a persistent management PIN.");
        }
        else Console.WriteLine("Arc management PIN configured using ARC_ADMIN_PIN.");
    }

    public bool IsValid(string candidate)
    {
        byte[] supplied = Encoding.UTF8.GetBytes(candidate ?? string.Empty);
        return supplied.Length == _pin.Length && CryptographicOperations.FixedTimeEquals(supplied, _pin);
    }
}

/// <summary>Holds authentication state for one interactive Arc browser circuit.</summary>
public sealed class ArcSession
{
    public bool IsAuthenticated { get; private set; }

    public void SignOut() => IsAuthenticated = false;

    public bool TryAuthenticate(string pin, ArcAdminAccess access)
    {
        IsAuthenticated = access.IsValid(pin);
        return IsAuthenticated;
    }
}
