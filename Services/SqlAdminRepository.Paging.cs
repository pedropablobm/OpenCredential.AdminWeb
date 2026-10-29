using System.Data.Common;
using System.Globalization;

namespace OpenCredential.AdminWeb.Services;

public sealed partial class SqlAdminRepository
{
    public async Task<UserPage> GetUsersAsync(UserPageQuery input, CancellationToken cancellationToken = default)
    {
        var query = input.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = _factory.CreateConnection() ?? throw new InvalidOperationException("No fue posible crear la conexion.");
        connection.ConnectionString = _options.ConnectionString;
        await connection.OpenAsync(cancellationToken);
        // Resolve legacy group-column names before starting the page's read transaction.
        var hasGroups = TableExists(connection, "groups") && TableExists(connection, "user_groups");
        var userIdColumn = hasGroups ? ResolveColumnName(connection, "user_groups", "user_id", "userid") : null;
        var usernameColumn = hasGroups ? ResolveColumnName(connection, "user_groups", "username", "user_username") : null;
        var groupIdColumn = hasGroups ? ResolveColumnName(connection, "user_groups", "group_id", "groupid") : null;
        var groups = hasGroups ? LoadGroups(connection).ToDictionary(group => group.Id) : [];
        cancellationToken.ThrowIfCancellationRequested();
        // Counts and page rows share a consistent snapshot during concurrent user edits.
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        DbCommand Command(string sql)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            command.CommandTimeout = 30;
            return command;
        }
        var conditions = new List<string>();
        var parameters = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(query.Search))
        {
            parameters["@search"] = "%" + query.Search.ToLowerInvariant().Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%";
            conditions.Add("(" + string.Join(" OR ", new[] { "username", "document_id", "email", "first_name", "last_name" }
                .Select(column => $"LOWER(COALESCE({Quote(column)}, '')) LIKE @search ESCAPE '!'")) + ")");
        }
        // Legacy PostgreSQL databases may store numeric status as varchar instead of integer.
        var statusText = _isPostgreSql ? "CAST(status AS TEXT)" : "CAST(status AS CHAR)";
        var activeCondition = $"TRIM({statusText}) = '1'";
        if (query.Status != null)
        {
            conditions.Add(query.Status == "active" ? activeCondition : $"({statusText} IS NULL OR NOT ({activeCondition}))");
        }
        if (query.CareerId.HasValue)
        {
            conditions.Add("career_id = @career");
            parameters["@career"] = query.CareerId.Value;
        }
        var where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
        void Bind(DbCommand command)
        {
            foreach (var parameter in parameters) AddParameter(command, parameter.Key, parameter.Value);
        }
        int registered, active;
        await using (var counts = Command($"SELECT COUNT(*), COALESCE(SUM(CASE WHEN {activeCondition} THEN 1 ELSE 0 END), 0) FROM {Quote("users")}"))
        await using (var reader = await counts.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            registered = Convert.ToInt32(reader.GetValue(0));
            active = Convert.ToInt32(reader.GetValue(1));
        }
        int total;
        await using (var count = Command($"SELECT COUNT(*) FROM {Quote("users")}{where}"))
        {
            Bind(count);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        }
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)query.PageSize));
        var page = Math.Min(query.Page, pages);
        var users = new List<UserAccount>();
        await using (var command = Command($"SELECT id, username, COALESCE(first_name,''), COALESCE(last_name,''), COALESCE(email,''), COALESCE(document_id,''), career_id, level_id, status, COALESCE(hash_method,'NONE') FROM {Quote("users")}{where} ORDER BY LOWER(username), id LIMIT @limit OFFSET @offset"))
        {
            Bind(command);
            AddParameter(command, "@limit", query.PageSize);
            AddParameter(command, "@offset", (page - 1) * query.PageSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) users.Add(new UserAccount
            {
                Id = reader.GetInt32(0), Username = reader.GetString(1), FirstName = reader.GetString(2), LastName = reader.GetString(3),
                Email = reader.GetString(4), DocumentId = reader.GetString(5),
                CareerId = reader.IsDBNull(6) ? null : reader.GetInt32(6), SemesterId = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                Active = ReadIntAsBool(reader, 8), HashMethod = reader.GetString(9)
            });
        }
        if (users.Count > 0 && groupIdColumn != null && (userIdColumn != null || usernameColumn != null))
        {
            var castType = _isPostgreSql ? "TEXT" : "CHAR";
            var userId = userIdColumn == null ? "NULL" : $"CAST({Quote(userIdColumn)} AS {castType})";
            var username = usernameColumn == null ? "NULL" : $"LOWER({Quote(usernameColumn)})";
            var ids = string.Join(",", users.Select((_, index) => "@id" + index));
            var names = string.Join(",", users.Select((_, index) => "@name" + index));
            await using var command = Command($"SELECT {userId}, {username}, {Quote(groupIdColumn)} FROM {Quote("user_groups")} WHERE {userId} IN ({ids}) OR {username} IN ({names})");
            for (var index = 0; index < users.Count; index++)
            {
                AddParameter(command, "@id" + index, users[index].Id.ToString(CultureInfo.InvariantCulture));
                AddParameter(command, "@name" + index, users[index].Username.ToLowerInvariant());
            }
            var byId = users.ToDictionary(user => user.Id.ToString(CultureInfo.InvariantCulture));
            var byName = users.ToDictionary(user => user.Username, StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if ((!byId.TryGetValue(id, out var user) && !byName.TryGetValue(name, out user)) || reader.IsDBNull(2) ||
                    !groups.TryGetValue(Convert.ToInt32(reader.GetValue(2)), out var group)) continue;
                if (user.Groups.All(value => value.Id != group.Id)) user.Groups.Add(group);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new(users.Select(UserListItem.FromUser).ToList(), page, query.PageSize, total, pages, registered, active);
    }
}
