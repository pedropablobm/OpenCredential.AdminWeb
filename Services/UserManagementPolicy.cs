using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace OpenCredential.AdminWeb.Services;

public sealed class UserManagementPolicy(IOptions<AdminAuthOptions> options)
{
    public bool CanManage(ClaimsPrincipal actor, UserAccount? target, UserInput? input = null, IEnumerable<GroupInfo>? groups = null)
    {
        if (actor.IsInRole(AdminRoles.SuperAdmin)) return true;
        if (!actor.IsInRole(AdminRoles.Coordinator)) return false;
        var settings = options.Value;
        var privileged = new HashSet<string>(settings.SuperAdminGroups.Concat(settings.CoordinatorGroups)
            .Concat(settings.OperatorGroups).Concat(settings.ViewerGroups), StringComparer.OrdinalIgnoreCase);
        bool Configured(string? name) => !string.IsNullOrWhiteSpace(name) &&
            (string.Equals(settings.Username, name, StringComparison.OrdinalIgnoreCase) ||
             settings.Accounts.Any(account => string.Equals(account.Username, name, StringComparison.OrdinalIgnoreCase)));
        if (Configured(target?.Username) || Configured(input?.Username)) return false;
        if (target?.Groups.Any(group => privileged.Contains(group.Name)) == true) return false;
        if (groups?.Any(group => input?.GroupIds?.Contains(group.Id) == true && privileged.Contains(group.Name)) == true) return false;
        return true;
    }

    public AdminSnapshot FilterSnapshot(ClaimsPrincipal actor, AdminSnapshot snapshot)
    {
        var manageUsers = actor.IsInRole(AdminRoles.SuperAdmin) || actor.IsInRole(AdminRoles.Coordinator);
        return new AdminSnapshot
        {
            Careers = snapshot.Careers, Semesters = snapshot.Semesters,
            Groups = manageUsers ? snapshot.Groups : [],
            Users = manageUsers ? snapshot.Users : [],
            Computers = snapshot.Computers, ComputedComputers = snapshot.ComputedComputers,
            Rooms = snapshot.Rooms, RoomLayoutItems = snapshot.RoomLayoutItems,
            UsageRecords = actor.IsInRole(AdminRoles.Viewer) ? [] : snapshot.UsageRecords,
            AuditEntries = actor.IsInRole(AdminRoles.SuperAdmin) ? snapshot.AuditEntries : []
        };
    }
}
