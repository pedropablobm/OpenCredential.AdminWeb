using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

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
    private readonly IAdminRepository _repository;
    private readonly IHostEnvironment _environment;

    public AdminAuthService(IOptions<AdminAuthOptions> options, IAdminRepository repository, IHostEnvironment environment)
    {
        _options = options.Value;
        _repository = repository;
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
            var user = _repository.FindUserByUsername(username);
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
                _repository.RegisterFailedSignIn(user.Username, _options.MaxFailedAttempts, _options.LockoutMinutes);
                return null;
            }

            _repository.ResetFailedSignIn(user.Username);

            if (PasswordHashService.IsWeakMethod(user.HashMethod))
            {
                _repository.UpdatePasswordByUsername(user.Username, password, "BCRYPT");
            }

            var groups = user.Groups
                .Select(group => group.Name)
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var role = ResolveRoleFromGroups(groups);
            return role is null ? null : new AdminIdentity(user.Username, role, groups);
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
