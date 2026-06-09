using Microsoft.Extensions.Options;

namespace OpenCredential.AdminWeb.Services;

public interface IPortalAuthService
{
    PortalIdentity? ValidateCredentials(string username, string password);
    bool IsPortalGroupAllowed(IEnumerable<string> groups);
}

public sealed class PortalAuthService : IPortalAuthService
{
    public const string AuthenticationScheme = "PortalCookie";

    private readonly AdminAuthOptions _options;
    private readonly IAdminRepository _repository;

    public PortalAuthService(IOptions<AdminAuthOptions> options, IAdminRepository repository)
    {
        _options = options.Value;
        _repository = repository;
    }

    public PortalIdentity? ValidateCredentials(string username, string password)
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
                .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!IsPortalGroupAllowed(groups))
            {
                return null;
            }

            return new PortalIdentity(
                user.Username,
                string.Join(" ", new[] { user.FirstName, user.LastName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim(),
                groups);
        }
        catch
        {
            return null;
        }
    }

    public bool IsPortalGroupAllowed(IEnumerable<string> groups)
    {
        var allowed = new HashSet<string>(
            _options.PortalAllowedGroups
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Select(group => group.Trim()),
            StringComparer.OrdinalIgnoreCase);

        return allowed.Count > 0 && groups.Any(allowed.Contains);
    }
}

public sealed class PortalIdentity
{
    public PortalIdentity(string username, string fullName, IEnumerable<string>? groups = null)
    {
        Username = username;
        FullName = fullName;
        Groups = groups?
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
    }

    public string Username { get; }
    public string FullName { get; }
    public IReadOnlyList<string> Groups { get; }
}
