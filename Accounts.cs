using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HB;

public sealed class UserAccount
{
    public string Name { get; set; } = "";
    public string Salt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string KeySalt { get; set; } = "";
}

/// <summary>A logged-in user: where their settings live and the key that protects their saved passwords.</summary>
public sealed class UserSession
{
    private readonly byte[] _key;

    public UserSession(string name, byte[] key)
    {
        Name = name;
        _key = key;
        SettingsPath = Path.Combine(
            Directory.CreateDirectory(Path.Combine(AppPaths.DataFolder, "users", name.ToLowerInvariant())).FullName,
            "settings.json");
    }

    public string Name { get; }
    public string SettingsPath { get; }

    /// <summary>Encrypts a secret with a key derived from the user's login password (AES-GCM).</summary>
    public string Protect(string secret)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(secret);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret)) return "";
        try
        {
            var data = Convert.FromBase64String(protectedSecret);
            var plain = new byte[data.Length - 28];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or OverflowException)
        {
            return "";
        }
    }
}

/// <summary>The local accounts of the app. Passwords are stored only as salted PBKDF2 hashes.</summary>
public static class UserStore
{
    private const int Iterations = 200_000;
    private static readonly string FilePath = Path.Combine(AppPaths.DataFolder, "users.json");
    private static readonly string LegacySettingsPath = Path.Combine(AppPaths.DataFolder, "settings.json");

    public static bool HasUsers => LoadAll().Count > 0;

    /// <summary>Returns the session, or null with <paramref name="error"/> explaining why.</summary>
    public static UserSession? Login(string name, string password, out string error)
    {
        error = "The user name or the password is not correct.";
        var account = Find(LoadAll(), name);
        if (account == null) return null;

        var hash = Derive(password, Convert.FromBase64String(account.Salt));
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(account.PasswordHash)))
            return null;

        error = "";
        return new UserSession(account.Name, Derive(password, Convert.FromBase64String(account.KeySalt)));
    }

    public static UserSession? Create(string name, string password, out string error)
    {
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9_][A-Za-z0-9_\-]{0,31}$"))
        {
            error = "The user name can have up to 32 letters, digits, dashes or underscores.";
            return null;
        }
        if (password.Length < 4)
        {
            error = "The password needs at least 4 characters.";
            return null;
        }

        var accounts = LoadAll();
        if (Find(accounts, name) != null)
        {
            error = "That user already exists. Use Log in instead.";
            return null;
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var keySalt = RandomNumberGenerator.GetBytes(16);
        accounts.Add(new UserAccount
        {
            Name = name,
            Salt = Convert.ToBase64String(salt),
            PasswordHash = Convert.ToBase64String(Derive(password, salt)),
            KeySalt = Convert.ToBase64String(keySalt),
        });
        File.WriteAllText(FilePath, JsonSerializer.Serialize(accounts, new JsonSerializerOptions { WriteIndented = true }));

        var session = new UserSession(name, Derive(password, keySalt));
        // The first account takes over the settings saved before accounts existed.
        if (accounts.Count == 1 && File.Exists(LegacySettingsPath) && !File.Exists(session.SettingsPath))
            File.Move(LegacySettingsPath, session.SettingsPath);

        error = "";
        return session;
    }

    private static UserAccount? Find(List<UserAccount> accounts, string name) =>
        accounts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    private static List<UserAccount> LoadAll()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<UserAccount>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new();
    }
}
