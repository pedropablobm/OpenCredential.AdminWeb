using System.Security.Cryptography;
using System.Text;

namespace OpenCredential.AdminWeb.Services;

public static class PasswordHashService
{
    public static readonly string[] SupportedMethods =
    {
        "BCRYPT",
        "SHA512",
        "SSHA512",
        "SHA384",
        "SSHA384",
        "SHA256",
        "SSHA256",
        "SHA1",
        "SSHA1",
        "MD5",
        "SMD5",
        "NONE"
    };

    public static string NormalizeMethod(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "BCRYPT" : value.Trim().ToUpperInvariant();
        return SupportedMethods.Contains(normalized) ? normalized : "BCRYPT";
    }

    public static string GeneratePassword(int length = 14)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%*?";
        var all = upper + lower + digits + symbols;

        var chars = new List<char>
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            symbols[RandomNumberGenerator.GetInt32(symbols.Length)]
        };

        while (chars.Count < Math.Max(10, length))
        {
            chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        }

        return new string(chars.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).ToArray());
    }

    public static string HashPassword(string password, string? method)
    {
        var normalized = NormalizeMethod(method);
        return normalized switch
        {
            "NONE" => password,
            "MD5" => ComputeHex(password, MD5.Create()),
            "SHA1" => ComputeHex(password, SHA1.Create()),
            "SHA256" => ComputeHex(password, SHA256.Create()),
            "SHA384" => ComputeHex(password, SHA384.Create()),
            "SHA512" => ComputeHex(password, SHA512.Create()),
            "SMD5" => ComputeSalted(password, MD5.Create(), 16),
            "SSHA1" => ComputeSalted(password, SHA1.Create(), 20),
            "SSHA256" => ComputeSalted(password, SHA256.Create(), 32),
            "SSHA384" => ComputeSalted(password, SHA384.Create(), 48),
            "SSHA512" => ComputeSalted(password, SHA512.Create(), 64),
            _ => BCrypt.Net.BCrypt.HashPassword(password, 10)
        };
    }

    public static bool VerifyPassword(string password, string? storedHash, string? method)
    {
        if (string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        var normalized = NormalizeMethod(method);
        return normalized switch
        {
            "NONE" => string.Equals(password, storedHash, StringComparison.Ordinal),
            "MD5" => FixedTimeEquals(ComputeHex(password, MD5.Create()), storedHash),
            "SHA1" => FixedTimeEquals(ComputeHex(password, SHA1.Create()), storedHash),
            "SHA256" => FixedTimeEquals(ComputeHex(password, SHA256.Create()), storedHash),
            "SHA384" => FixedTimeEquals(ComputeHex(password, SHA384.Create()), storedHash),
            "SHA512" => FixedTimeEquals(ComputeHex(password, SHA512.Create()), storedHash),
            "SMD5" => VerifySalted(password, storedHash, MD5.Create(), 16),
            "SSHA1" => VerifySalted(password, storedHash, SHA1.Create(), 20),
            "SSHA256" => VerifySalted(password, storedHash, SHA256.Create(), 32),
            "SSHA384" => VerifySalted(password, storedHash, SHA384.Create(), 48),
            "SSHA512" => VerifySalted(password, storedHash, SHA512.Create(), 64),
            _ => BCrypt.Net.BCrypt.Verify(password, storedHash)
        };
    }

    private static string ComputeHex(string password, HashAlgorithm algorithm)
    {
        using (algorithm)
        {
            return Convert.ToHexString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
        }
    }

    private static string ComputeSalted(string password, HashAlgorithm algorithm, int saltLength)
    {
        using (algorithm)
        {
            var salt = RandomNumberGenerator.GetBytes(Math.Max(8, saltLength / 2));
            var passwordBytes = Encoding.UTF8.GetBytes(password);
            var combined = new byte[passwordBytes.Length + salt.Length];
            Buffer.BlockCopy(passwordBytes, 0, combined, 0, passwordBytes.Length);
            Buffer.BlockCopy(salt, 0, combined, passwordBytes.Length, salt.Length);

            var hash = algorithm.ComputeHash(combined);
            var hashWithSalt = new byte[hash.Length + salt.Length];
            Buffer.BlockCopy(hash, 0, hashWithSalt, 0, hash.Length);
            Buffer.BlockCopy(salt, 0, hashWithSalt, hash.Length, salt.Length);
            return Convert.ToBase64String(hashWithSalt);
        }
    }

    private static bool VerifySalted(string password, string storedHash, HashAlgorithm algorithm, int hashLength)
    {
        using (algorithm)
        {
            byte[] hashWithSalt;
            try
            {
                hashWithSalt = Convert.FromBase64String(storedHash);
            }
            catch (FormatException)
            {
                return false;
            }

            if (hashWithSalt.Length <= hashLength)
            {
                return false;
            }

            var salt = hashWithSalt[hashLength..];
            var passwordBytes = Encoding.UTF8.GetBytes(password);
            var combined = new byte[passwordBytes.Length + salt.Length];
            Buffer.BlockCopy(passwordBytes, 0, combined, 0, passwordBytes.Length);
            Buffer.BlockCopy(salt, 0, combined, passwordBytes.Length, salt.Length);

            var hash = algorithm.ComputeHash(combined);
            var expected = new byte[hash.Length + salt.Length];
            Buffer.BlockCopy(hash, 0, expected, 0, hash.Length);
            Buffer.BlockCopy(salt, 0, expected, hash.Length, salt.Length);

            return CryptographicOperations.FixedTimeEquals(expected, hashWithSalt);
        }
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
    }
}
