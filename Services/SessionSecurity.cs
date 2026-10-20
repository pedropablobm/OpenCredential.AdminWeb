using System.Security.Cryptography;
using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace OpenCredential.AdminWeb.Services;

public static class SessionSecurity
{
    public const string VersionClaim = "credential-version";
    public const string SourceClaim = "credential-source";
    public static string Fingerprint(UserAccount user) => Hash(JsonSerializerSafe(user));
    private static string JsonSerializerSafe(UserAccount user) => System.Text.Json.JsonSerializer.Serialize(new
    {
        user.Username, user.PasswordHash, user.Active,
        Groups = user.Groups.Select(group => group.Name.ToUpperInvariant()).OrderBy(name => name).ToArray()
    });
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static async Task ValidateAsync(CookieValidatePrincipalContext context, bool portal)
    {
        var name = context.Principal?.Identity?.Name;
        var version = context.Principal?.FindFirstValue(VersionClaim);
        string? current = null;
        try
        {
            if (portal)
            {
                var repository = context.HttpContext.RequestServices.GetRequiredService<IAdminRepository>();
                var user = repository.FindUserByUsername(name ?? "");
                var auth = context.HttpContext.RequestServices.GetRequiredService<IPortalAuthService>();
                if (user?.Active == true && auth.IsPortalGroupAllowed(user.Groups.Select(group => group.Name)))
                    current = Fingerprint(user);
            }
            else
            {
                var auth = context.HttpContext.RequestServices.GetRequiredService<IAdminAuthService>();
                current = auth.GetSessionVersion(name ?? "", context.Principal?.FindFirstValue(SourceClaim) == "repository");
            }
        }
        catch (Exception)
        {
            // Fail closed if the identity store cannot be checked. Never log credentials.
        }
        if (string.IsNullOrEmpty(version) || current != version)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
        }
    }
}
