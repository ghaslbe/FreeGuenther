using System.Security.Cryptography;
using System.Text;
using Foundation;
using Security;

namespace LmChat;

// Gegenstück zur Windows-Fassung (DPAPI): Geheimnisse liegen AES-verschlüsselt in der Datenbank,
// der Schlüssel dazu im Schlüsselbund des macOS-Benutzers
public static class Secret
{
    private const string Service = "de.guentherhaslbeck.freeguenther";
    private const string Account = "secret-key";
    private const int NonceSize = 12, TagSize = 16;

    private static byte[]? _key;

    public static string Protect(string value)
    {
        if (value.Length == 0) return "";
        var plain = Encoding.UTF8.GetBytes(value);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(Key(), TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public static string Unprotect(string protectedValue)
    {
        if (protectedValue.Length == 0) return "";
        try
        {
            var data = Convert.FromBase64String(protectedValue);
            if (data.Length < NonceSize + TagSize) return "";
            var plain = new byte[data.Length - NonceSize - TagSize];
            using var aes = new AesGcm(Key(), TagSize);
            aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";   // z. B. aus Windows übernommene, dort per DPAPI verschlüsselte Werte
        }
    }

    private static byte[] Key()
    {
        if (_key != null) return _key;
        var query = new SecRecord(SecKind.GenericPassword) { Service = Service, Account = Account };
        var stored = SecKeyChain.QueryAsData(query);
        if (stored is { Length: 32 })
            return _key = stored.ToArray();

        var key = RandomNumberGenerator.GetBytes(32);
        var status = SecKeyChain.Add(new SecRecord(SecKind.GenericPassword)
        {
            Service = Service,
            Account = Account,
            Label = "FreeGuenther",
            Accessible = SecAccessible.WhenUnlockedThisDeviceOnly,
            ValueData = NSData.FromArray(key),
        });
        if (status != SecStatusCode.Success)
            throw new CryptographicException($"Schlüsselbund nicht verfügbar ({status}).");
        return _key = key;
    }
}
