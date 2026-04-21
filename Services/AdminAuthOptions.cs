namespace OpenCredential.AdminWeb.Services;

public sealed class AdminAuthOptions
{
    public bool Enabled { get; set; } = true;
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "AdminWeb2026!";
    public string PasswordHash { get; set; } = string.Empty;
    public string HashMethod { get; set; } = "BCRYPT";
    public string Role { get; set; } = AdminRoles.SuperAdmin;
    public string CookieName { get; set; } = "opencredential_admin";
    public int SessionHours { get; set; } = 12;
    public List<AdminAccountOptions> Accounts { get; set; } = new();
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
    public AdminIdentity(string username, string role)
    {
        Username = username;
        Role = AdminRoles.Normalize(role);
    }

    public string Username { get; }
    public string Role { get; }
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
