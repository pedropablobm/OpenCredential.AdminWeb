namespace OpenCredential.AdminWeb.Services;

public sealed record UserPageQuery(int Page = 1, int PageSize = 20, string? Search = null, string? Status = null, int? CareerId = null)
{
    public UserPageQuery Validate()
    {
        if (Page < 1 || Page > 1_000_000 || PageSize < 1 || PageSize > 100)
            throw new InvalidOperationException("Pagina o tamano de pagina fuera de rango (1–100 registros).");
        if ((Search?.Length ?? 0) > 200 || (CareerId.HasValue && CareerId < 1))
            throw new InvalidOperationException("Filtro de usuarios no valido.");
        if (!string.IsNullOrEmpty(Status) && Status is not "active" and not "inactive")
            throw new InvalidOperationException("Estado de usuario no valido.");
        return this with { Search = Search?.Trim(), Status = string.IsNullOrEmpty(Status) ? null : Status };
    }
}

// Public projection deliberately excludes credential material.
public sealed record UserListItem(int Id, string Username, string FirstName, string LastName,
    string Email, string DocumentId, int? CareerId, int? SemesterId, bool Active, string HashMethod, List<GroupInfo> Groups)
{
    public static UserListItem FromUser(UserAccount user) => new(user.Id, user.Username, user.FirstName,
        user.LastName, user.Email, user.DocumentId, user.CareerId, user.SemesterId, user.Active,
        user.HashMethod, user.Groups.Select(group => new GroupInfo { Id = group.Id, Name = group.Name }).ToList());
}

public sealed record UserPage(List<UserListItem> Items, int Page, int PageSize, int Total, int TotalPages,
    int RegisteredUsers, int ActiveUsers);

internal static class UserPaging
{
    public static UserPage FromSnapshot(AdminSnapshot snapshot, UserPageQuery input)
    {
        var query = input.Validate();
        var filtered = snapshot.Users.Where(user =>
            (string.IsNullOrEmpty(query.Search) || new[] { user.Username, user.DocumentId, user.Email, user.FirstName, user.LastName }
                .Any(value => value?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) == true)) &&
            (query.Status == null || user.Active == (query.Status == "active")) &&
            (!query.CareerId.HasValue || user.CareerId == query.CareerId))
            .OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase).ThenBy(user => user.Id).ToList();
        var pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)query.PageSize));
        var page = Math.Min(query.Page, pages);
        return new(filtered.Skip((page - 1) * query.PageSize).Take(query.PageSize).Select(UserListItem.FromUser).ToList(),
            page, query.PageSize, filtered.Count, pages, snapshot.Users.Count, snapshot.Users.Count(user => user.Active));
    }

    public static AdminSnapshot Compact(AdminSnapshot snapshot, bool includeAudit) => new()
    {
        Careers = snapshot.Careers, Semesters = snapshot.Semesters, Groups = snapshot.Groups, Users = [],
        Computers = snapshot.Computers, ComputedComputers = snapshot.ComputedComputers,
        Rooms = snapshot.Rooms, RoomLayoutItems = snapshot.RoomLayoutItems, UsageRecords = [],
        AuditEntries = includeAudit ? snapshot.AuditEntries : []
    };
}
