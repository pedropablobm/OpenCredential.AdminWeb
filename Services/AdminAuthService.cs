using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace OpenCredential.AdminWeb.Services;

public interface IAdminAuthService
{
    bool IsEnabled { get; }
    AdminIdentity? ValidateCredentials(string username, string password);
    AdminIdentity GetDefaultIdentity();
    string? GetSessionVersion(string username, bool repositoryUser);
}

public sealed class AdminAuthService : IAdminAuthService
{
    private readonly AdminAuthOptions _options;
    private readonly IServiceProvider _services;
    private IAdminRepository Repository => _services.GetRequiredService<IAdminRepository>();
    private readonly IHostEnvironment _environment;

    public AdminAuthService(IOptions<AdminAuthOptions> options, IServiceProvider services, IHostEnvironment environment)
    {
        _options = options.Value;
        _services = services;
        _environment = environment;
    }

    public bool IsEnabled => _options.Enabled;

    public AdminIdentity? ValidateCredentials(string username, string password)
    {
        if (!_options.Enabled)
        {
            return _environment.IsDevelopment() ? GetDefaultIdentity() : null;
        }

        foreach (var account in GetAccounts())
        {
            var configuredIdentity = ValidateConfiguredAccount(account, username, password);
            if (configuredIdentity is not null)
            {
                return configuredIdentity;
            }
        }

        return ValidateRepositoryUser(username, password);
    }

    public AdminIdentity GetDefaultIdentity()
    {
        return new AdminIdentity(string.IsNullOrWhiteSpace(_options.Username) ? "admin" : _options.Username, _options.Role);
    }

    public string? GetSessionVersion(string username, bool repositoryUser)
    {
        if (!repositoryUser)
        {
            var account = GetAccounts().FirstOrDefault(item => string.Equals(item.Username.Trim(), username, StringComparison.OrdinalIgnoreCase));
            return account is null ? null : SessionSecurity.Hash(System.Text.Json.JsonSerializer.Serialize(account));
        }
        var user = Repository.FindUserByUsername(username);
        var role = user?.Active == true ? ResolveRoleFromGroups(user.Groups.Select(group => group.Name).ToList()) : null;
        return role is not null ? SessionSecurity.Hash(SessionSecurity.Fingerprint(user!) + ":" + role) : null;
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

        if (string.IsNullOrWhiteSpace(_options.Username)
            || (string.IsNullOrWhiteSpace(_options.PasswordHash) && string.IsNullOrWhiteSpace(_options.Password)))
        {
            return [];
        }

        return
        [
            new AdminAccountOptions
            {
                Username = _options.Username,
                Password = _options.Password,
                PasswordHash = _options.PasswordHash,
                HashMethod = _options.HashMethod,
                Role = _options.Role
            }
        ];
    }

    private static AdminIdentity? ValidateConfiguredAccount(AdminAccountOptions account, string username, string password)
    {
        var normalizedUsername = account.Username.Trim();
        if (!string.Equals(username?.Trim(), normalizedUsername, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var passwordIsValid = !string.IsNullOrWhiteSpace(account.PasswordHash)
            ? PasswordHashService.VerifyPassword(password, account.PasswordHash, account.HashMethod)
            : string.Equals(password, account.Password, StringComparison.Ordinal);

        return passwordIsValid ? new AdminIdentity(normalizedUsername, account.Role) : null;
    }

    private AdminIdentity? ValidateRepositoryUser(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        try
        {
            var user = Repository.FindUserByUsername(username);
            if (user is null || !user.Active || string.IsNullOrWhiteSpace(user.PasswordHash))
            {
                return null;
            }

            if (user.LockedUntilUtc.HasValue && user.LockedUntilUtc.Value > DateTime.UtcNow)
            {
                return null;
            }

            if (!PasswordHashService.VerifyPassword(password, user.PasswordHash, user.HashMethod))
            {
                Repository.RegisterFailedSignIn(user.Username, _options.MaxFailedAttempts, _options.LockoutMinutes);
                return null;
            }

            Repository.ResetFailedSignIn(user.Username);

            if (PasswordHashService.IsWeakMethod(user.HashMethod) && System.Text.Encoding.UTF8.GetByteCount(password) <= 72)
            {
                Repository.UpdatePasswordByUsername(user.Username, password, "BCRYPT", verifyExistingPassword: true);
            }

            var groups = user.Groups
                .Select(group => group.Name)
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var role = ResolveRoleFromGroups(groups);
            return role is null ? null : new AdminIdentity(user.Username, role, groups, repositoryUser: true);
        }
        catch
        {
            return null;
        }
    }

    private string? ResolveRoleFromGroups(IReadOnlyCollection<string> groups)
    {
        if (groups.Count == 0)
        {
            return null;
        }

        if (MatchesAny(groups, _options.SuperAdminGroups))
        {
            return AdminRoles.SuperAdmin;
        }

        if (MatchesAny(groups, _options.CoordinatorGroups))
        {
            return AdminRoles.Coordinator;
        }

        if (MatchesAny(groups, _options.OperatorGroups))
        {
            return AdminRoles.Operator;
        }

        if (MatchesAny(groups, _options.ViewerGroups))
        {
            return AdminRoles.Viewer;
        }

        return null;
    }

    private static bool MatchesAny(IEnumerable<string> userGroups, IEnumerable<string> allowedGroups)
    {
        var allowed = new HashSet<string>(
            allowedGroups
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Select(group => group.Trim()),
            StringComparer.OrdinalIgnoreCase);

        return allowed.Count > 0 && userGroups.Any(allowed.Contains);
    }
}
