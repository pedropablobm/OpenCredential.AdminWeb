using Microsoft.Extensions.Options;

namespace OpenCredential.AdminWeb.Services;

public interface IAdminAuthService
{
    bool IsEnabled { get; }
    AdminIdentity? ValidateCredentials(string username, string password);
    AdminIdentity GetDefaultIdentity();
}

public sealed class AdminAuthService : IAdminAuthService
{
    private readonly AdminAuthOptions _options;

    public AdminAuthService(IOptions<AdminAuthOptions> options)
    {
        _options = options.Value;
    }

    public bool IsEnabled => _options.Enabled;

    public AdminIdentity? ValidateCredentials(string username, string password)
    {
        if (!_options.Enabled)
        {
            return GetDefaultIdentity();
        }

        foreach (var account in GetAccounts())
        {
            var normalizedUsername = account.Username.Trim();
            if (!string.Equals(username?.Trim(), normalizedUsername, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var passwordIsValid = !string.IsNullOrWhiteSpace(account.PasswordHash)
                ? PasswordHashService.VerifyPassword(password, account.PasswordHash, account.HashMethod)
                : string.Equals(password, account.Password, StringComparison.Ordinal);

            return passwordIsValid ? new AdminIdentity(normalizedUsername, account.Role) : null;
        }

        return null;
    }

    public AdminIdentity GetDefaultIdentity()
    {
        return new AdminIdentity(_options.Username, _options.Role);
    }

    private IEnumerable<AdminAccountOptions> GetAccounts()
    {
        var configuredAccounts = _options.Accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.Username))
            .ToList();

        if (configuredAccounts.Count > 0)
        {
            return configuredAccounts;
        }

        return new[]
        {
            new AdminAccountOptions
            {
                Username = _options.Username,
                Password = _options.Password,
                PasswordHash = _options.PasswordHash,
                HashMethod = _options.HashMethod,
                Role = _options.Role
            }
        };
    }
}
