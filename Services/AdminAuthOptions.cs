namespace OpenCredential.AdminWeb.Services;

public sealed class AdminAuthOptions
{
    public bool Enabled { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string HashMethod { get; set; } = "BCRYPT";
    public string Role { get; set; } = AdminRoles.SuperAdmin;
    public string CookieName { get; set; } = "opencredential_admin";
    public string PortalCookieName { get; set; } = "opencredential_portal";
    public int SessionHours { get; set; } = 12;
    public int PortalSessionHours { get; set; } = 12;
    public int PortalRecoveryTokenLifetimeMinutes { get; set; } = 20;
    public bool PortalRevealRecoveryTokenInResponse { get; set; }
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int AdminLoginRateLimitPerMinute { get; set; } = 10;
    public int PortalLoginRateLimitPerMinute { get; set; } = 10;
    public int PortalRecoveryRateLimitPerHour { get; set; } = 5;
    public int PortalResetRateLimitPerHour { get; set; } = 8;
    public List<AdminAccountOptions> Accounts { get; set; } = new();
    public List<string> SuperAdminGroups { get; set; } = new() { "AdminWeb-SuperAdmin" };
    public List<string> CoordinatorGroups { get; set; } = new() { "AdminWeb-Coordinador" };
    public List<string> OperatorGroups { get; set; } = new() { "AdminWeb-Operador" };
    public List<string> ViewerGroups { get; set; } = new();
    public List<string> PortalAllowedGroups { get; set; } = new()
    {
        "Estudiantes",
        "Docentes",
        "Funcionarios",
        "AdminWeb-SuperAdmin",
        "AdminWeb-Coordinador",
        "AdminWeb-Operador"
    };
}

public sealed class AdminAccountOptions
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string HashMethod { get; set; } = "BCRYPT";
    public string Role { get; set; } = AdminRoles.Viewer;
}

public sealed class AdminIdentity
{
    public AdminIdentity(string username, string role, IEnumerable<string>? groups = null)
    {
        Username = username;
        Role = AdminRoles.Normalize(role);
        Groups = groups?
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
    }

    public string Username { get; }
    public string Role { get; }
    public IReadOnlyList<string> Groups { get; }
}

public static class AdminRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Coordinator = "Coordinator";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";

    private static readonly HashSet<string> SupportedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        SuperAdmin,
        Coordinator,
        Operator,
        Viewer
    };

    public static string Normalize(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return Viewer;
        }

        return SupportedRoles.TryGetValue(role.Trim(), out var supportedRole) ? supportedRole : Viewer;
    }
}
