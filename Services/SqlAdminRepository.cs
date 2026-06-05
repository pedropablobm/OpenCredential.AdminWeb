using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;

namespace OpenCredential.AdminWeb.Services;

public sealed class SqlAdminRepository : IAdminRepository
{
    private static readonly TimeSpan HeartbeatFreshThreshold = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan HeartbeatStaleThreshold = TimeSpan.FromMinutes(10);
    private static readonly TimeZoneInfo LoginSessionTimeZone = ResolveLoginSessionTimeZone();
    private readonly DatabaseOptions _options;
    private readonly DbProviderFactory _factory;
    private readonly bool _isPostgreSql;

    public SqlAdminRepository(IOptions<DatabaseOptions> options)
    {
        _options = options.Value;
        _isPostgreSql = _options.Provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase) ||
                        _options.Provider.Equals("postgres", StringComparison.OrdinalIgnoreCase);
        _factory = _isPostgreSql ? NpgsqlFactory.Instance : MySqlConnectorFactory.Instance;

        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new InvalidOperationException("Database:ConnectionString es obligatorio cuando Database:Mode=Sql.");
        }

        if (_options.AutoInitialize)
        {
            EnsureSchema();
            SeedIfEmpty();
        }
    }

    public AdminSnapshot GetSnapshot()
    {
        using var connection = OpenConnection();
        var latestSessions = LoadLatestLoginSessions(connection);
        EnsureComputersDiscoveredFromSessions(connection, latestSessions);

        var careers = new List<Career>();
        using (var command = CreateCommand(connection, $"SELECT id, name, status FROM {Quote("careers")} ORDER BY name"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                careers.Add(new Career
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Active = ReadIntAsBool(reader, 2)
                });
            }
        }

        var semesters = new List<Semester>();
        using (var command = CreateCommand(connection, $"SELECT id, name, status FROM {Quote("levels")} ORDER BY id"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                semesters.Add(new Semester
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Active = ReadIntAsBool(reader, 2)
                });
            }
        }

        var users = new List<UserAccount>();
        using (var command = CreateCommand(connection, $"SELECT id, username, COALESCE(first_name,''), COALESCE(last_name,''), COALESCE(email,''), COALESCE(document_id,''), career_id, level_id, status, COALESCE(hash_method,'NONE'), password_hash FROM {Quote("users")} ORDER BY username"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                users.Add(new UserAccount
                {
                    Id = reader.GetInt32(0),
                    Username = reader.GetString(1),
                    FirstName = reader.GetString(2),
                    LastName = reader.GetString(3),
                    Email = reader.GetString(4),
                    DocumentId = reader.GetString(5),
                    CareerId = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    SemesterId = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    Active = ReadIntAsBool(reader, 8),
                    HashMethod = reader.GetString(9),
                    PasswordHash = reader.IsDBNull(10) ? null : reader.GetString(10)
                });
            }
        }

        var groups = LoadGroups(connection);
        ApplyGroupsToUsers(connection, users, groups);

        var computers = new List<Computer>();
        using (var command = CreateCommand(connection, $"SELECT id, name, location, inventory_tag, ip_address, status, current_username, last_seen_utc FROM {Quote("computers")} ORDER BY name"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                computers.Add(new Computer
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Location = reader.GetString(2),
                    InventoryTag = reader.GetString(3),
                    IpAddress = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Status = RepositorySupport.ParseStatus(reader.GetString(5)),
                    CurrentUsername = reader.IsDBNull(6) ? null : reader.GetString(6),
                    LastSeenUtc = reader.GetDateTime(7)
                });
            }
        }

        var computedComputers = BuildComputedComputerStates(computers, latestSessions);
        ApplyComputedStatesToLegacyComputers(computers, computedComputers);

        var rooms = new List<Room>();
        if (TableExists(connection, "rooms"))
        {
            using var roomCommand = CreateCommand(connection, $"SELECT id, name, code, canvas_width, canvas_height, status FROM {Quote("rooms")} ORDER BY name");
            using var roomReader = roomCommand.ExecuteReader();
            while (roomReader.Read())
            {
                rooms.Add(new Room
                {
                    Id = roomReader.GetInt32(0),
                    Name = roomReader.GetString(1),
                    Code = roomReader.GetString(2),
                    CanvasWidth = roomReader.GetInt32(3),
                    CanvasHeight = roomReader.GetInt32(4),
                    Active = ReadIntAsBool(roomReader, 5)
                });
            }
        }

        var roomLayoutItems = new List<RoomLayoutItem>();
        if (TableExists(connection, "room_positions"))
        {
            using var positionCommand = CreateCommand(connection, $"SELECT id, room_id, label, item_type, pos_x, pos_y, item_width, item_height, item_orientation, seat_capacity, computer_id, row_number, column_number FROM {Quote("room_positions")} ORDER BY room_id, pos_y, pos_x, id");
            using var positionReader = positionCommand.ExecuteReader();
            while (positionReader.Read())
            {
                var fallbackRow = positionReader.IsDBNull(11) ? 1 : positionReader.GetInt32(11);
                var fallbackColumn = positionReader.IsDBNull(12) ? 1 : positionReader.GetInt32(12);
                roomLayoutItems.Add(new RoomLayoutItem
                {
                    Id = positionReader.GetInt32(0),
                    RoomId = positionReader.GetInt32(1),
                    Label = positionReader.GetString(2),
                    ItemType = ParseRoomLayoutItemType(positionReader.IsDBNull(3) ? null : positionReader.GetString(3)),
                    X = positionReader.IsDBNull(4) ? (fallbackColumn - 1) * 160 : positionReader.GetInt32(4),
                    Y = positionReader.IsDBNull(5) ? (fallbackRow - 1) * 140 : positionReader.GetInt32(5),
                    Width = positionReader.IsDBNull(6) ? 120 : positionReader.GetInt32(6),
                    Height = positionReader.IsDBNull(7) ? 110 : positionReader.GetInt32(7),
                    Orientation = NormalizeOrientation(positionReader.IsDBNull(8) ? null : positionReader.GetString(8)),
                    Capacity = NormalizeCapacity(positionReader.IsDBNull(9) ? 1 : positionReader.GetInt32(9)),
                    ComputerId = positionReader.IsDBNull(10) ? null : positionReader.GetInt32(10)
                });
            }
        }

        var usageRecords = new List<UsageRecord>();
        using (var command = CreateCommand(connection, $"SELECT id, user_id, computer_id, start_utc, end_utc FROM {Quote("usage_records")} ORDER BY start_utc DESC"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                usageRecords.Add(new UsageRecord
                {
                    Id = reader.GetInt32(0),
                    UserId = reader.GetInt32(1),
                    ComputerId = reader.GetInt32(2),
                    StartUtc = reader.GetDateTime(3),
                    EndUtc = reader.GetDateTime(4)
                });
            }
        }

        return new AdminSnapshot
        {
            Careers = careers,
            Semesters = semesters,
            Groups = groups,
            Users = users,
            Computers = computers,
            ComputedComputers = computedComputers,
            Rooms = rooms,
            RoomLayoutItems = roomLayoutItems,
            UsageRecords = usageRecords,
            AuditEntries = GetAuditEntries(50)
        };
    }

    public DashboardResponse GetDashboard(int rangeDays, int? careerId, int? semesterId, string? status)
    {
        return RepositorySupport.BuildDashboard(GetSnapshot(), rangeDays, careerId, semesterId, status);
    }

    public ReportsResponse GetReports(DateTime fromUtc, DateTime toUtc, int? careerId, int? semesterId, int? groupId, string? username, string? sessionOrigin, string? sessionState, string? operationalStatus)
    {
        using var connection = OpenConnection();
        return RepositorySupport.BuildReportsResponse(LoadReportSessions(connection, fromUtc, toUtc, careerId, semesterId, groupId, username, sessionOrigin, sessionState, operationalStatus));
    }

    public List<GroupInfo> GetGroups()
    {
        using var connection = OpenConnection();
        return LoadGroups(connection);
    }

    public UserAccount? FindUserByUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        using var connection = OpenConnection();
        UserAccount? user;
        using (var command = CreateCommand(connection, $"SELECT id, username, COALESCE(first_name,''), COALESCE(last_name,''), COALESCE(email,''), COALESCE(document_id,''), career_id, level_id, status, COALESCE(hash_method,'NONE'), password_hash, COALESCE(failed_attempts, 0), locked_until, last_attempt_at FROM {Quote("users")} WHERE LOWER(username) = LOWER(@username)"))
        {
            AddParameter(command, "@username", username.Trim());
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            user = new UserAccount
            {
                Id = reader.GetInt32(0),
                Username = reader.GetString(1),
                FirstName = reader.GetString(2),
                LastName = reader.GetString(3),
                Email = reader.GetString(4),
                DocumentId = reader.GetString(5),
                CareerId = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                SemesterId = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                Active = ReadIntAsBool(reader, 8),
                HashMethod = reader.GetString(9),
                PasswordHash = reader.IsDBNull(10) ? null : reader.GetString(10),
                FailedAttempts = ReadFlexibleInt32(reader, 11) ?? 0,
                LockedUntilUtc = ReadFlexibleDateTimeUtc(reader, 12),
                LastAttemptAtUtc = ReadFlexibleDateTimeUtc(reader, 13)
            };
        }

        var groups = LoadGroups(connection);
        ApplyGroupsToUsers(connection, new List<UserAccount> { user! }, groups);
        return user;
    }

    public void RegisterFailedSignIn(string username, int maxFailedAttempts, int lockoutMinutes)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        using var connection = OpenConnection();
        using var command = CreateCommand(connection,
            $"UPDATE {Quote("users")} SET failed_attempts = COALESCE(failed_attempts, 0) + 1, last_attempt_at = @nowUtc, locked_until = CASE WHEN COALESCE(failed_attempts, 0) + 1 >= @maxFailedAttempts THEN @lockedUntilUtc ELSE locked_until END WHERE LOWER(username) = LOWER(@username)");
        var nowUtc = DateTime.UtcNow;
        AddParameter(command, "@nowUtc", nowUtc);
        AddParameter(command, "@lockedUntilUtc", nowUtc.AddMinutes(Math.Max(1, lockoutMinutes)));
        AddParameter(command, "@maxFailedAttempts", Math.Max(1, maxFailedAttempts));
        AddParameter(command, "@username", username.Trim());
        command.ExecuteNonQuery();
    }

    public void ResetFailedSignIn(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        using var connection = OpenConnection();
        using var command = CreateCommand(connection,
            $"UPDATE {Quote("users")} SET failed_attempts = 0, locked_until = NULL WHERE LOWER(username) = LOWER(@username)");
        AddParameter(command, "@username", username.Trim());
        command.ExecuteNonQuery();
    }

    public PortalProfile? GetPortalProfile(string username)
    {
        var user = FindUserByUsername(username);
        if (user is null)
        {
            return null;
        }

        using var connection = OpenConnection();
        var careerName = LoadLookupName(connection, "careers", user.CareerId);
        var semesterName = LoadLookupName(connection, "levels", user.SemesterId);
        return new PortalProfile
        {
            UserId = user.Id,
            Username = user.Username,
            FirstName = user.FirstName,
            LastName = user.LastName,
            FullName = $"{user.FirstName} {user.LastName}".Trim(),
            Email = user.Email,
            DocumentId = user.DocumentId,
            CareerId = user.CareerId,
            CareerName = careerName,
            SemesterId = user.SemesterId,
            SemesterName = semesterName,
            Active = user.Active,
            HashMethod = user.HashMethod,
            Groups = user.Groups
        };
    }

    public PortalProfile? UpdatePortalProfile(string username, PortalProfileUpdateInput input)
    {
        var normalizedUsername = username?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedUsername))
        {
            return null;
        }

        using var connection = OpenConnection();
        using var command = CreateCommand(connection,
            $"UPDATE {Quote("users")} SET first_name = @firstName, last_name = @lastName, email = @email WHERE LOWER(username) = LOWER(@username)");
        AddParameter(command, "@firstName", input.FirstName.Trim());
        AddParameter(command, "@lastName", input.LastName.Trim());
        AddParameter(command, "@email", input.Email.Trim());
        AddParameter(command, "@username", normalizedUsername);
        var affected = command.ExecuteNonQuery();

        return affected == 0 ? null : GetPortalProfile(normalizedUsername);
    }

    public PasswordResetResult? UpdatePasswordByUsername(string username, string plainPassword, string hashMethod)
    {
        var normalizedUsername = username?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedUsername) || string.IsNullOrWhiteSpace(plainPassword))
        {
            return null;
        }

        using var connection = OpenConnection();
        var user = FindUserByUsername(normalizedUsername);
        if (user is null)
        {
            return null;
        }

        var method = PasswordHashService.NormalizeInteractiveMethod(hashMethod);
        using var update = CreateCommand(connection, $"UPDATE {Quote("users")} SET hash_method = @hashMethod, password_hash = @passwordHash, failed_attempts = 0, locked_until = NULL WHERE LOWER(username) = LOWER(@username)");
        AddParameter(update, "@hashMethod", method);
        AddParameter(update, "@passwordHash", PasswordHashService.HashPassword(plainPassword.Trim(), method));
        AddParameter(update, "@username", normalizedUsername);
        update.ExecuteNonQuery();

        return new PasswordResetResult
        {
            UserId = user.Id,
            Username = user.Username,
            HashMethod = method,
            GeneratedPassword = plainPassword.Trim()
        };
    }

    public PortalPasswordRecoveryResult RecoverPortalPassword(PortalPasswordRecoveryInput input, int tokenLifetimeMinutes)
    {
        var normalizedUsername = input.Username?.Trim();
        var normalizedDocument = input.DocumentId?.Trim();
        var normalizedEmail = input.Email?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedUsername) || string.IsNullOrWhiteSpace(normalizedDocument) || string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return new PortalPasswordRecoveryResult
            {
                Success = false,
                Message = "Completa usuario, documento y correo institucional para recuperar la clave."
            };
        }

        using var connection = OpenConnection();
        if (!HasPortalResetTokenSchema(connection))
        {
            return new PortalPasswordRecoveryResult
            {
                Success = false,
                Message = "La tabla de recuperacion no esta actualizada. Entra a Configuracion y usa 'Ajustar tablas de AdminWeb'."
            };
        }

        CleanupPortalResetTokens(connection);
        using var find = CreateCommand(connection,
            $"SELECT id, username, status FROM {Quote("users")} WHERE LOWER(username) = LOWER(@username) AND LOWER(document_id) = LOWER(@documentId) AND LOWER(email) = LOWER(@email)");
        AddParameter(find, "@username", normalizedUsername);
        AddParameter(find, "@documentId", normalizedDocument);
        AddParameter(find, "@email", normalizedEmail);
        using var reader = find.ExecuteReader();
        if (!reader.Read())
        {
            return new PortalPasswordRecoveryResult
            {
                Success = false,
                Message = "No fue posible validar los datos de recuperacion."
            };
        }

        var isActive = ReadStatusAsBool(reader, 2);
        if (!isActive)
        {
            return new PortalPasswordRecoveryResult
            {
                Success = false,
                Message = "El usuario no se encuentra habilitado para recuperar la clave."
            };
        }

        var userId = ReadFlexibleInt32(reader, 0);
        var usernameValue = ReadFlexibleString(reader, 1) ?? normalizedUsername;
        reader.Close();

        var token = PasswordHashService.GenerateOpaqueToken();
        var tokenHash = PasswordHashService.HashOpaqueToken(token);
        var expiresAtUtc = DateTime.UtcNow.AddMinutes(Math.Max(5, tokenLifetimeMinutes));
        using (var deleteExisting = CreateCommand(connection, $"DELETE FROM {Quote("portal_password_reset_tokens")} WHERE (user_id = @userId OR LOWER(username) = LOWER(@username))"))
        {
            AddParameter(deleteExisting, "@userId", (object?)userId ?? DBNull.Value);
            AddParameter(deleteExisting, "@username", usernameValue);
            deleteExisting.ExecuteNonQuery();
        }
        using var insert = CreateCommand(connection,
            $"INSERT INTO {Quote("portal_password_reset_tokens")} (id, user_id, username, email, reset_token, created_utc, expires_utc, consumed_utc) VALUES (@id, @userId, @username, @email, @token, @createdUtc, @expiresUtc, NULL)");
        AddParameter(insert, "@id", NextId(connection, "portal_password_reset_tokens"));
        AddParameter(insert, "@userId", (object?)userId ?? DBNull.Value);
        AddParameter(insert, "@username", usernameValue);
        AddParameter(insert, "@email", normalizedEmail);
        AddParameter(insert, "@token", tokenHash);
        AddParameter(insert, "@createdUtc", DateTime.UtcNow);
        AddParameter(insert, "@expiresUtc", expiresAtUtc);
        insert.ExecuteNonQuery();

        return new PortalPasswordRecoveryResult
        {
            Success = true,
            Message = "Se genero un token temporal de recuperacion. Usalo para definir una nueva clave.",
            ResetToken = token,
            ExpiresAtUtc = expiresAtUtc,
            DeliveryHint = $"Token visible solo para pruebas internas. Debe enviarse al correo {normalizedEmail} en una integracion posterior."
        };
    }

    public bool ResetPortalPasswordWithToken(PortalPasswordResetWithTokenInput input, out string message)
    {
        message = "No fue posible restablecer la clave.";
        if (string.IsNullOrWhiteSpace(input.Token) || string.IsNullOrWhiteSpace(input.NewPassword) || string.IsNullOrWhiteSpace(input.ConfirmPassword))
        {
            message = "Completa token, nueva clave y confirmacion.";
            return false;
        }

        if (!string.Equals(input.NewPassword, input.ConfirmPassword, StringComparison.Ordinal))
        {
            message = "La confirmacion no coincide con la nueva clave.";
            return false;
        }

        using var connection = OpenConnection();
        if (!HasPortalResetTokenSchema(connection))
        {
            message = "La tabla de tokens de recuperacion no esta actualizada. Ajusta las tablas de AdminWeb desde Configuracion.";
            return false;
        }

        CleanupPortalResetTokens(connection);

        using var find = CreateCommand(connection,
            $"SELECT id, user_id, username, expires_utc, consumed_utc FROM {Quote("portal_password_reset_tokens")} WHERE UPPER(reset_token) = UPPER(@token) ORDER BY created_utc DESC");
        AddParameter(find, "@token", PasswordHashService.HashOpaqueToken(input.Token.Trim()));
        using var reader = find.ExecuteReader();
        if (!reader.Read())
        {
            message = "El token no existe o ya no es valido.";
            return false;
        }

        var tokenId = ReadFlexibleInt32(reader, 0) ?? 0;
        var userId = ReadFlexibleInt32(reader, 1);
        var username = ReadFlexibleString(reader, 2);
        var expiresAtUtc = ReadFlexibleDateTimeUtc(reader, 3);
        var consumedUtc = ReadFlexibleDateTimeUtc(reader, 4);
        reader.Close();

        if (consumedUtc.HasValue || !expiresAtUtc.HasValue || expiresAtUtc.Value < DateTime.UtcNow)
        {
            message = "El token ya fue usado o expiro.";
            return false;
        }

        var user = !string.IsNullOrWhiteSpace(username) ? FindUserByUsername(username) : null;
        if (user is null && userId.HasValue)
        {
            user = GetSnapshot().Users.FirstOrDefault(item => item.Id == userId.Value);
        }

        if (user is null || !user.Active)
        {
            message = "El usuario asociado al token no esta disponible.";
            return false;
        }

        var result = UpdatePasswordByUsername(user.Username, input.NewPassword.Trim(), input.HashMethod);
        if (result is null)
        {
            message = "No fue posible actualizar la clave.";
            return false;
        }

        using var consume = CreateCommand(connection, $"UPDATE {Quote("portal_password_reset_tokens")} SET consumed_utc = @consumedUtc WHERE id = @id");
        AddParameter(consume, "@consumedUtc", DateTime.UtcNow);
        AddParameter(consume, "@id", tokenId);
        consume.ExecuteNonQuery();

        message = "La clave fue restablecida correctamente.";
        return true;
    }

    public List<PortalSessionEntry> GetPortalSessions(string username, int take)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return [];
        }

        using var connection = OpenConnection();
        return LoadReportSessions(
                connection,
                DateTime.UtcNow.AddDays(-90),
                DateTime.UtcNow,
                null,
                null,
                null,
                username.Trim(),
                null,
                null,
                null)
            .Where(item => string.Equals(item.Username, username.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(Math.Max(1, take))
            .Select(item => new PortalSessionEntry
            {
                SessionId = item.SessionId,
                Machine = item.Machine,
                RoomName = item.RoomName,
                InventoryTag = item.InventoryTag,
                SessionState = item.SessionState,
                SessionStateLabel = TranslateSessionStateLabel(item.SessionState),
                SessionEndReason = item.SessionEndReason,
                SessionOrigin = item.SessionOrigin,
                OriginLabel = RepositorySupport.TranslateSessionOrigin(item.SessionOrigin),
                OperationalStatus = item.OperationalStatus,
                OperationalStatusLabel = item.OperationalStatusLabel,
                LoginStamp = item.LoginStamp,
                LogoutStamp = item.LogoutStamp,
                LastHeartbeatAt = item.LastHeartbeatAt,
                DurationHours = item.DurationHours
            })
            .ToList();
    }

    public List<AuditEntry> GetAuditEntries(int take)
    {
        using var connection = OpenConnection();
        var entries = new List<AuditEntry>();
        using var command = CreateCommand(connection,
            $"SELECT id, actor_username, action, entity_type, entity_key, summary, remote_ip, created_utc FROM {Quote("admin_audit_log")} ORDER BY created_utc DESC, id DESC LIMIT @take");
        AddParameter(command, "@take", Math.Max(1, take));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new AuditEntry
            {
                Id = reader.GetInt32(0),
                ActorUsername = reader.GetString(1),
                Action = reader.GetString(2),
                EntityType = reader.GetString(3),
                EntityKey = reader.GetString(4),
                Summary = reader.GetString(5),
                RemoteIp = reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedUtc = reader.GetDateTime(7)
            });
        }

        return entries;
    }

    private List<ReportSessionRow> LoadReportSessions(
        DbConnection connection,
        DateTime fromUtc,
        DateTime toUtc,
        int? careerId,
        int? semesterId,
        int? groupId,
        string? username,
        string? sessionOrigin,
        string? sessionState,
        string? operationalStatus)
    {
        if (!TableExists(connection, "login_sessions"))
        {
            return [];
        }

        var hasClientSessionId = ColumnExists(connection, "login_sessions", "client_session_id");
        var hasWindowsSessionId = ColumnExists(connection, "login_sessions", "windows_session_id");
        var hasSessionState = ColumnExists(connection, "login_sessions", "session_state");
        var hasLastHeartbeatAt = ColumnExists(connection, "login_sessions", "last_heartbeat_at");
        var hasSessionEndReason = ColumnExists(connection, "login_sessions", "session_end_reason");
        var hasSessionOrigin = ColumnExists(connection, "login_sessions", "session_origin");
        var lastHeartbeatExpression = hasLastHeartbeatAt ? "ls.last_heartbeat_at" : "NULL";
        var activityReferenceExpression = hasLastHeartbeatAt ? "COALESCE(ls.last_heartbeat_at, ls.loginstamp)" : "ls.loginstamp";
        var heartbeatAgeSql = _isPostgreSql
            ? $"GREATEST(0, EXTRACT(EPOCH FROM (CURRENT_TIMESTAMP - {activityReferenceExpression})))::INT"
            : $"GREATEST(0, TIMESTAMPDIFF(SECOND, {activityReferenceExpression}, CURRENT_TIMESTAMP))";
        var hasRooms = TableExists(connection, "rooms");
        var hasRoomPositions = TableExists(connection, "room_positions");
        var hasGroups = TableExists(connection, "groups");
        var hasUserGroups = TableExists(connection, "user_groups");
        var sessionStateExpression = hasSessionState ? "COALESCE(ls.session_state, '')" : "''";
        var sessionEndReasonExpression = hasSessionEndReason ? "COALESCE(ls.session_end_reason, '')" : "''";
        var sessionOriginExpression = hasSessionOrigin ? "COALESCE(ls.session_origin, '')" : "''";
        var groupNamesSql = hasGroups && hasUserGroups
            ? (_isPostgreSql
                ? "COALESCE(string_agg(DISTINCT g.group_name, ' | ' ORDER BY g.group_name), '')"
                : "COALESCE(GROUP_CONCAT(DISTINCT g.group_name ORDER BY g.group_name SEPARATOR ' | '), '')")
            : "''";
        var roomNameSql = hasRooms && hasRoomPositions
            ? "COALESCE(r.name, comp.location, '')"
            : "COALESCE(comp.location, '')";
        var roomJoinSql = hasRooms && hasRoomPositions
            ? $"LEFT JOIN {Quote("room_positions")} rp ON rp.computer_id = comp.id LEFT JOIN {Quote("rooms")} r ON r.id = rp.room_id"
            : string.Empty;
        var groupJoinSql = hasGroups && hasUserGroups
            ? $@"LEFT JOIN {Quote("user_groups")} ug ON {(ColumnExists(connection, "user_groups", "user_id") ? "ug.user_id = u.id" : "LOWER(ug.username) = LOWER(u.username)")}
LEFT JOIN {Quote("groups")} g ON g.group_id = ug.group_id"
            : string.Empty;
        var whereConditions = new List<string>
        {
            "ls.loginstamp <= @toUtc",
            $"COALESCE(ls.logoutstamp, {activityReferenceExpression}) >= @fromUtc"
        };
        if (careerId.HasValue)
        {
            whereConditions.Add("u.career_id = @careerId");
        }

        if (semesterId.HasValue)
        {
            whereConditions.Add("u.level_id = @semesterId");
        }

        if (hasGroups && hasUserGroups && groupId.HasValue)
        {
            whereConditions.Add("ug.group_id = @groupId");
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            whereConditions.Add("LOWER(COALESCE(ls.username, '')) LIKE LOWER(@usernameLike)");
        }

        if (!string.IsNullOrWhiteSpace(sessionOrigin))
        {
            whereConditions.Add($"LOWER({sessionOriginExpression}) = LOWER(@sessionOrigin)");
        }

        if (!string.IsNullOrWhiteSpace(sessionState))
        {
            whereConditions.Add($"LOWER({sessionStateExpression}) = LOWER(@sessionState)");
        }

        using var command = CreateCommand(connection, $@"
SELECT
    ls.dbid,
    ls.username,
    COALESCE(u.first_name, '') AS first_name,
    COALESCE(u.last_name, '') AS last_name,
    COALESCE(u.document_id, '') AS document_id,
    COALESCE(ca.name, '') AS career_name,
    COALESCE(le.name, '') AS semester_name,
    COALESCE(comp.name, ls.machine, '') AS machine_name,
    {roomNameSql} AS room_name,
    COALESCE(comp.inventory_tag, '') AS inventory_tag,
    COALESCE(ls.ipaddress, comp.ip_address, '') AS ip_address,
    {sessionStateExpression} AS session_state,
    {sessionEndReasonExpression} AS session_end_reason,
    {sessionOriginExpression} AS session_origin,
    ls.loginstamp,
    ls.logoutstamp,
    {lastHeartbeatExpression} AS last_heartbeat_at,
    {heartbeatAgeSql} AS heartbeat_age_seconds,
    {groupNamesSql} AS group_names
FROM {Quote("login_sessions")} ls
LEFT JOIN {Quote("users")} u ON LOWER(u.username) = LOWER(ls.username)
LEFT JOIN {Quote("careers")} ca ON ca.id = u.career_id
LEFT JOIN {Quote("levels")} le ON le.id = u.level_id
LEFT JOIN {Quote("computers")} comp ON LOWER(comp.name) = LOWER(ls.machine)
    OR (comp.ip_address IS NOT NULL AND ls.ipaddress = comp.ip_address)
{roomJoinSql}
{groupJoinSql}
WHERE {string.Join(Environment.NewLine + "  AND ", whereConditions)}
GROUP BY
    ls.dbid, ls.username, u.first_name, u.last_name, u.document_id, ca.name, le.name,
    comp.name, ls.machine, {roomNameSql}, comp.inventory_tag, ls.ipaddress, comp.ip_address,
    {sessionStateExpression}, {sessionEndReasonExpression}, {sessionOriginExpression}, ls.loginstamp, ls.logoutstamp, {lastHeartbeatExpression}
ORDER BY ls.loginstamp DESC, ls.dbid DESC");
        AddParameter(command, "@fromUtc", fromUtc);
        AddParameter(command, "@toUtc", toUtc);
        if (careerId.HasValue)
        {
            AddParameter(command, "@careerId", careerId.Value);
        }

        if (semesterId.HasValue)
        {
            AddParameter(command, "@semesterId", semesterId.Value);
        }

        if (hasGroups && hasUserGroups && groupId.HasValue)
        {
            AddParameter(command, "@groupId", groupId.Value);
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            AddParameter(command, "@usernameLike", $"%{username.Trim()}%");
        }

        if (!string.IsNullOrWhiteSpace(sessionOrigin))
        {
            AddParameter(command, "@sessionOrigin", sessionOrigin.Trim());
        }

        if (!string.IsNullOrWhiteSpace(sessionState))
        {
            AddParameter(command, "@sessionState", sessionState.Trim());
        }

        var rows = new List<ReportSessionRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var loginStamp = ReadFlexibleDateTimeUtc(reader, 14) ?? DateTime.UtcNow;
            var logoutStamp = ReadFlexibleDateTimeUtc(reader, 15);
            var heartbeat = ReadFlexibleDateTimeUtc(reader, 16);
            var heartbeatAgeSeconds = ReadFlexibleInt32(reader, 17) ?? 0;
            var sessionStateValue = ReadFlexibleString(reader, 11);
            var sessionEndReasonValue = ReadFlexibleString(reader, 12);
            var sessionOriginValue = ReadFlexibleString(reader, 13);
            var operationalStatusValue = DeriveOperationalStatus(sessionStateValue, logoutStamp, heartbeatAgeSeconds);
            if (!string.IsNullOrWhiteSpace(operationalStatus) &&
                !string.Equals(operationalStatusValue.ToString(), operationalStatus, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var durationReference = logoutStamp ?? heartbeat ?? loginStamp;
            var durationHours = Math.Max(0, (durationReference - loginStamp).TotalHours);
            rows.Add(new ReportSessionRow
            {
                SessionId = ReadFlexibleInt32(reader, 0) ?? 0,
                Username = NormalizeSessionUsername(ReadFlexibleString(reader, 1)),
                FullName = string.Join(" ", new[] { ReadFlexibleString(reader, 2), ReadFlexibleString(reader, 3) }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim(),
                DocumentId = ReadFlexibleString(reader, 4) ?? string.Empty,
                CareerName = CleanOptionalSessionValue(reader.IsDBNull(5) ? null : reader.GetString(5)),
                SemesterName = CleanOptionalSessionValue(reader.IsDBNull(6) ? null : reader.GetString(6)),
                Groups = SplitGroupNames(ReadFlexibleString(reader, 18)),
                Machine = ReadFlexibleString(reader, 7) ?? "Sin equipo",
                RoomName = CleanOptionalSessionValue(ReadFlexibleString(reader, 8)),
                InventoryTag = CleanOptionalSessionValue(ReadFlexibleString(reader, 9)),
                IpAddress = CleanOptionalSessionValue(ReadFlexibleString(reader, 10)),
                SessionState = sessionStateValue,
                SessionEndReason = sessionEndReasonValue,
                SessionOrigin = sessionOriginValue,
                OperationalStatus = operationalStatusValue.ToString(),
                OperationalStatusLabel = RepositorySupport.TranslateOperationalStatus(operationalStatusValue),
                LoginStamp = loginStamp,
                LogoutStamp = logoutStamp,
                LastHeartbeatAt = heartbeat,
                DurationHours = Math.Round(durationHours, 2),
                IsRecoveredOffline = string.Equals(sessionOriginValue, "offline_cache", StringComparison.OrdinalIgnoreCase),
                IsOrphaned = operationalStatusValue == OperationalComputerStatus.Orphaned
            });
        }

        return rows;
    }

    public AuditEntry RecordAudit(AuditEntryInput input)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var id = NextId("admin_audit_log");
            var createdUtc = DateTime.UtcNow;

            try
            {
                ExecuteNonQuery(
                    $"INSERT INTO {Quote("admin_audit_log")} (id, actor_username, action, entity_type, entity_key, summary, remote_ip, created_utc) VALUES (@id, @actor, @action, @entityType, @entityKey, @summary, @remoteIp, @createdUtc)",
                    ("@id", id),
                    ("@actor", input.ActorUsername.Trim()),
                    ("@action", input.Action.Trim()),
                    ("@entityType", input.EntityType.Trim()),
                    ("@entityKey", input.EntityKey.Trim()),
                    ("@summary", input.Summary.Trim()),
                    ("@remoteIp", (object?)RepositorySupport.CleanOptional(input.RemoteIp) ?? DBNull.Value),
                    ("@createdUtc", createdUtc));

                return new AuditEntry
                {
                    Id = id,
                    ActorUsername = input.ActorUsername.Trim(),
                    Action = input.Action.Trim(),
                    EntityType = input.EntityType.Trim(),
                    EntityKey = input.EntityKey.Trim(),
                    Summary = input.Summary.Trim(),
                    RemoteIp = RepositorySupport.CleanOptional(input.RemoteIp),
                    CreatedUtc = createdUtc
                };
            }
            catch (PostgresException exception) when (exception.SqlState == "23505")
            {
            }
            catch (MySqlException exception) when (exception.Number == 1062)
            {
            }
        }

        throw new InvalidOperationException("No fue posible registrar el evento de auditoria por colision de identificador.");
    }

    public Career CreateCareer(CareerInput input)
    {
        var id = NextId("careers");
        ExecuteNonQuery(
            $"INSERT INTO {Quote("careers")} (id, name, status) VALUES (@id, @name, @status)",
            ("@id", id), ("@name", input.Name.Trim()), ("@status", ToStatus(input.Active)));
        return new Career { Id = id, Name = input.Name.Trim(), Active = input.Active };
    }

    public Career? UpdateCareer(int id, CareerInput input)
    {
        var affected = ExecuteNonQuery(
            $"UPDATE {Quote("careers")} SET name = @name, status = @status WHERE id = @id",
            ("@id", id), ("@name", input.Name.Trim()), ("@status", ToStatus(input.Active)));
        return affected == 0 ? null : new Career { Id = id, Name = input.Name.Trim(), Active = input.Active };
    }

    public bool DeleteCareer(int id)
    {
        ExecuteNonQuery($"UPDATE {Quote("users")} SET career_id = NULL WHERE career_id = @id", ("@id", id));
        return ExecuteNonQuery($"DELETE FROM {Quote("careers")} WHERE id = @id", ("@id", id)) > 0;
    }

    public Semester CreateSemester(SemesterInput input)
    {
        var id = NextId("levels");
        ExecuteNonQuery(
            $"INSERT INTO {Quote("levels")} (id, name, status) VALUES (@id, @name, @status)",
            ("@id", id), ("@name", input.Name.Trim()), ("@status", ToStatus(input.Active)));
        return new Semester { Id = id, Name = input.Name.Trim(), Active = input.Active };
    }

    public Semester? UpdateSemester(int id, SemesterInput input)
    {
        var affected = ExecuteNonQuery(
            $"UPDATE {Quote("levels")} SET name = @name, status = @status WHERE id = @id",
            ("@id", id), ("@name", input.Name.Trim()), ("@status", ToStatus(input.Active)));
        return affected == 0 ? null : new Semester { Id = id, Name = input.Name.Trim(), Active = input.Active };
    }

    public bool DeleteSemester(int id)
    {
        ExecuteNonQuery($"UPDATE {Quote("users")} SET level_id = NULL WHERE level_id = @id", ("@id", id));
        return ExecuteNonQuery($"DELETE FROM {Quote("levels")} WHERE id = @id", ("@id", id)) > 0;
    }

    public Computer CreateComputer(ComputerInput input)
    {
        var id = NextId("computers");
        var now = DateTime.UtcNow;
        ExecuteNonQuery(
            $"INSERT INTO {Quote("computers")} (id, name, location, inventory_tag, ip_address, status, current_username, last_seen_utc) VALUES (@id, @name, @location, @inventory, @ip, @status, @current, @lastSeen)",
            ("@id", id),
            ("@name", input.Name.Trim()),
            ("@location", input.Location.Trim()),
            ("@inventory", input.InventoryTag.Trim()),
            ("@ip", (object?)RepositorySupport.CleanOptional(input.IpAddress) ?? DBNull.Value),
            ("@status", RepositorySupport.ParseStatus(input.Status).ToString()),
            ("@current", (object?)RepositorySupport.CleanOptional(input.CurrentUsername) ?? DBNull.Value),
            ("@lastSeen", now));

        return new Computer
        {
            Id = id,
            Name = input.Name.Trim(),
            Location = input.Location.Trim(),
            InventoryTag = input.InventoryTag.Trim(),
            IpAddress = RepositorySupport.CleanOptional(input.IpAddress),
            Status = RepositorySupport.ParseStatus(input.Status),
            CurrentUsername = RepositorySupport.CleanOptional(input.CurrentUsername),
            LastSeenUtc = now
        };
    }

    public Computer? UpdateComputer(int id, ComputerInput input)
    {
        var now = DateTime.UtcNow;
        var status = RepositorySupport.ParseStatus(input.Status);
        var affected = ExecuteNonQuery(
            $"UPDATE {Quote("computers")} SET name = @name, location = @location, inventory_tag = @inventory, ip_address = @ip, status = @status, current_username = @current, last_seen_utc = @lastSeen WHERE id = @id",
            ("@id", id),
            ("@name", input.Name.Trim()),
            ("@location", input.Location.Trim()),
            ("@inventory", input.InventoryTag.Trim()),
            ("@ip", (object?)RepositorySupport.CleanOptional(input.IpAddress) ?? DBNull.Value),
            ("@status", status.ToString()),
            ("@current", (object?)RepositorySupport.CleanOptional(input.CurrentUsername) ?? DBNull.Value),
            ("@lastSeen", now));

        return affected == 0 ? null : new Computer
        {
            Id = id,
            Name = input.Name.Trim(),
            Location = input.Location.Trim(),
            InventoryTag = input.InventoryTag.Trim(),
            IpAddress = RepositorySupport.CleanOptional(input.IpAddress),
            Status = status,
            CurrentUsername = RepositorySupport.CleanOptional(input.CurrentUsername),
            LastSeenUtc = now
        };
    }

    public bool DeleteComputer(int id)
    {
        ExecuteNonQuery($"DELETE FROM {Quote("usage_records")} WHERE computer_id = @id", ("@id", id));
        ExecuteNonQuery($"UPDATE {Quote("room_positions")} SET computer_id = NULL WHERE computer_id = @id", ("@id", id));
        return ExecuteNonQuery($"DELETE FROM {Quote("computers")} WHERE id = @id", ("@id", id)) > 0;
    }

    public Room CreateRoom(RoomInput input)
    {
        var id = NextId("rooms");
        ExecuteNonQuery(
            $"INSERT INTO {Quote("rooms")} (id, name, code, canvas_width, canvas_height, status) VALUES (@id, @name, @code, @canvasWidth, @canvasHeight, @status)",
            ("@id", id),
            ("@name", input.Name.Trim()),
            ("@code", input.Code.Trim()),
            ("@canvasWidth", Math.Max(640, input.CanvasWidth)),
            ("@canvasHeight", Math.Max(360, input.CanvasHeight)),
            ("@status", ToStatus(input.Active)));

        return new Room
        {
            Id = id,
            Name = input.Name.Trim(),
            Code = input.Code.Trim(),
            CanvasWidth = Math.Max(640, input.CanvasWidth),
            CanvasHeight = Math.Max(360, input.CanvasHeight),
            Active = input.Active
        };
    }

    public Room? UpdateRoom(int id, RoomInput input)
    {
        var affected = ExecuteNonQuery(
            $"UPDATE {Quote("rooms")} SET name = @name, code = @code, canvas_width = @canvasWidth, canvas_height = @canvasHeight, status = @status WHERE id = @id",
            ("@id", id),
            ("@name", input.Name.Trim()),
            ("@code", input.Code.Trim()),
            ("@canvasWidth", Math.Max(640, input.CanvasWidth)),
            ("@canvasHeight", Math.Max(360, input.CanvasHeight)),
            ("@status", ToStatus(input.Active)));

        return affected == 0
            ? null
            : new Room
            {
                Id = id,
                Name = input.Name.Trim(),
                Code = input.Code.Trim(),
                CanvasWidth = Math.Max(640, input.CanvasWidth),
                CanvasHeight = Math.Max(360, input.CanvasHeight),
                Active = input.Active
            };
    }

    public bool DeleteRoom(int id)
    {
        ExecuteNonQuery($"DELETE FROM {Quote("room_positions")} WHERE room_id = @id", ("@id", id));
        return ExecuteNonQuery($"DELETE FROM {Quote("rooms")} WHERE id = @id", ("@id", id)) > 0;
    }

    public List<RoomLayoutItem> SaveRoomLayout(int roomId, RoomLayoutInput input)
    {
        using var connection = OpenConnection();
        using var existsCommand = CreateCommand(connection, $"SELECT COUNT(*) FROM {Quote("rooms")} WHERE id = @id");
        AddParameter(existsCommand, "@id", roomId);
        if (Convert.ToInt32(existsCommand.ExecuteScalar()) == 0)
        {
            throw new InvalidOperationException("La sala indicada no existe.");
        }

        var duplicateComputerIds = input.Items
            .Where(item => item.ComputerId.HasValue)
            .GroupBy(item => item.ComputerId!.Value)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicateComputerIds.Count > 0)
        {
            var duplicateNames = GetSnapshot().Computers
                .Where(computer => duplicateComputerIds.Contains(computer.Id))
                .Select(computer => computer.Name)
                .OrderBy(name => name)
                .ToList();
            throw new InvalidOperationException($"Cada equipo solo puede estar una vez en el mapa visual. Duplicados detectados: {string.Join(", ", duplicateNames)}.");
        }

        using (var updateRoom = CreateCommand(connection, $"UPDATE {Quote("rooms")} SET canvas_width = @canvasWidth, canvas_height = @canvasHeight WHERE id = @id"))
        {
            AddParameter(updateRoom, "@id", roomId);
            AddParameter(updateRoom, "@canvasWidth", Math.Max(640, input.CanvasWidth));
            AddParameter(updateRoom, "@canvasHeight", Math.Max(360, input.CanvasHeight));
            updateRoom.ExecuteNonQuery();
        }

        using (var deletePositions = CreateCommand(connection, $"DELETE FROM {Quote("room_positions")} WHERE room_id = @roomId"))
        {
            AddParameter(deletePositions, "@roomId", roomId);
            deletePositions.ExecuteNonQuery();
        }

        var nextId = NextId(connection, "room_positions");
        foreach (var item in input.Items.OrderBy(layoutItem => layoutItem.Y).ThenBy(layoutItem => layoutItem.X))
        {
            var rowNumber = Math.Max(1, (int)Math.Floor(Math.Max(0, item.Y) / 40.0) + 1);
            var columnNumber = Math.Max(1, (int)Math.Floor(Math.Max(0, item.X) / 40.0) + 1);
            using var insertPosition = CreateCommand(connection,
                $"INSERT INTO {Quote("room_positions")} (id, room_id, label, item_type, pos_x, pos_y, item_width, item_height, item_orientation, seat_capacity, computer_id, row_number, column_number) VALUES (@id, @roomId, @label, @itemType, @x, @y, @width, @height, @orientation, @capacity, @computerId, @rowNumber, @columnNumber)");
            AddParameter(insertPosition, "@id", nextId++);
            AddParameter(insertPosition, "@roomId", roomId);
            AddParameter(insertPosition, "@label", item.Label.Trim());
            AddParameter(insertPosition, "@itemType", ParseRoomLayoutItemType(item.ItemType).ToString());
            AddParameter(insertPosition, "@x", Math.Max(0, item.X));
            AddParameter(insertPosition, "@y", Math.Max(0, item.Y));
            AddParameter(insertPosition, "@width", Math.Max(40, item.Width));
            AddParameter(insertPosition, "@height", Math.Max(40, item.Height));
            AddParameter(insertPosition, "@orientation", NormalizeOrientation(item.Orientation));
            AddParameter(insertPosition, "@capacity", NormalizeCapacity(item.Capacity));
            AddParameter(insertPosition, "@computerId", (object?)item.ComputerId ?? DBNull.Value);
            AddParameter(insertPosition, "@rowNumber", rowNumber);
            AddParameter(insertPosition, "@columnNumber", columnNumber);
            insertPosition.ExecuteNonQuery();
        }

        var saved = new List<RoomLayoutItem>();
        using var loadPositions = CreateCommand(connection, $"SELECT id, room_id, label, item_type, pos_x, pos_y, item_width, item_height, item_orientation, seat_capacity, computer_id FROM {Quote("room_positions")} WHERE room_id = @roomId ORDER BY pos_y, pos_x, id");
        AddParameter(loadPositions, "@roomId", roomId);
        using var reader = loadPositions.ExecuteReader();
        while (reader.Read())
        {
            saved.Add(new RoomLayoutItem
            {
                Id = reader.GetInt32(0),
                RoomId = reader.GetInt32(1),
                Label = reader.GetString(2),
                ItemType = ParseRoomLayoutItemType(reader.IsDBNull(3) ? null : reader.GetString(3)),
                X = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                Y = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Width = reader.IsDBNull(6) ? 120 : reader.GetInt32(6),
                Height = reader.IsDBNull(7) ? 110 : reader.GetInt32(7),
                Orientation = NormalizeOrientation(reader.IsDBNull(8) ? null : reader.GetString(8)),
                Capacity = NormalizeCapacity(reader.IsDBNull(9) ? 1 : reader.GetInt32(9)),
                ComputerId = reader.IsDBNull(10) ? null : reader.GetInt32(10)
            });
        }

        return saved;
    }

    public UserAccount CreateUser(UserInput input)
    {
        using var connection = OpenConnection();
        if (string.IsNullOrWhiteSpace(input.Password))
        {
            throw new InvalidOperationException("Debes definir una clave inicial segura al crear un usuario. No se permite usar el documento como clave por defecto.");
        }

        var id = NextId(connection, "users");
        var username = input.Username.Trim();
        using (var command = CreateCommand(connection,
                   $"INSERT INTO {Quote("users")} (id, username, first_name, last_name, document_id, email, status, career_id, level_id, hash_method, password_hash, failed_attempts, locked_until, last_attempt_at) VALUES (@id, @username, @firstName, @lastName, @documentId, @email, @status, @careerId, @levelId, @hashMethod, @passwordHash, 0, NULL, NULL)"))
        {
            AddParameter(command, "@id", id);
            AddParameter(command, "@username", username);
            AddParameter(command, "@firstName", input.FirstName.Trim());
            AddParameter(command, "@lastName", input.LastName.Trim());
            AddParameter(command, "@documentId", input.DocumentId.Trim());
            AddParameter(command, "@email", input.Email.Trim());
            AddParameter(command, "@status", ToStatus(input.Active));
            AddParameter(command, "@careerId", (object?)input.CareerId ?? DBNull.Value);
            AddParameter(command, "@levelId", (object?)input.SemesterId ?? DBNull.Value);
            var method = PasswordHashService.NormalizeInteractiveMethod(input.HashMethod);
            AddParameter(command, "@hashMethod", method);
            AddParameter(command, "@passwordHash", PasswordHashService.HashPassword(input.Password.Trim(), method));
            command.ExecuteNonQuery();
        }

        ReplaceUserGroups(connection, id, null, username, input.GroupIds);

        return new UserAccount
        {
            Id = id,
            Username = username,
            FirstName = input.FirstName.Trim(),
            LastName = input.LastName.Trim(),
            Email = input.Email.Trim(),
            DocumentId = input.DocumentId.Trim(),
            CareerId = input.CareerId,
            SemesterId = input.SemesterId,
            Active = input.Active,
            HashMethod = PasswordHashService.NormalizeInteractiveMethod(input.HashMethod),
            Groups = LoadGroups(connection).Where(group => input.GroupIds.Contains(group.Id)).OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    public UserAccount? UpdateUser(int id, UserInput input)
    {
        using var connection = OpenConnection();
        var previousUsername = LoadUsernameByUserId(connection, id);
        var sql = string.IsNullOrWhiteSpace(input.Password)
            ? $"UPDATE {Quote("users")} SET username = @username, first_name = @firstName, last_name = @lastName, document_id = @documentId, email = @email, status = @status, career_id = @careerId, level_id = @levelId, hash_method = @hashMethod WHERE id = @id"
            : $"UPDATE {Quote("users")} SET username = @username, first_name = @firstName, last_name = @lastName, document_id = @documentId, email = @email, status = @status, career_id = @careerId, level_id = @levelId, hash_method = @hashMethod, password_hash = @passwordHash WHERE id = @id";

        var parameters = new List<(string Name, object Value)>
        {
            ("@id", id),
            ("@username", input.Username.Trim()),
            ("@firstName", input.FirstName.Trim()),
            ("@lastName", input.LastName.Trim()),
            ("@documentId", input.DocumentId.Trim()),
            ("@email", input.Email.Trim()),
            ("@status", ToStatus(input.Active)),
            ("@careerId", (object?)input.CareerId ?? DBNull.Value),
            ("@levelId", (object?)input.SemesterId ?? DBNull.Value),
            ("@hashMethod", PasswordHashService.NormalizeInteractiveMethod(input.HashMethod))
        };
        if (!string.IsNullOrWhiteSpace(input.Password))
        {
            parameters.Add(("@passwordHash", PasswordHashService.HashPassword(input.Password.Trim(), PasswordHashService.NormalizeInteractiveMethod(input.HashMethod))));
        }

        using var command = CreateCommand(connection, sql);
        foreach (var parameter in parameters)
        {
            AddParameter(command, parameter.Name, parameter.Value);
        }
        var affected = command.ExecuteNonQuery();

        if (affected == 0)
        {
            return null;
        }

        var username = input.Username.Trim();
        ReplaceUserGroups(connection, id, previousUsername, username, input.GroupIds);
        var groups = LoadGroups(connection)
            .Where(group => input.GroupIds.Contains(group.Id))
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new UserAccount
        {
            Id = id,
            Username = username,
            FirstName = input.FirstName.Trim(),
            LastName = input.LastName.Trim(),
            Email = input.Email.Trim(),
            DocumentId = input.DocumentId.Trim(),
            CareerId = input.CareerId,
            SemesterId = input.SemesterId,
            Active = input.Active,
            HashMethod = PasswordHashService.NormalizeInteractiveMethod(input.HashMethod),
            Groups = groups
        };
    }

    public bool DeleteUser(int id)
    {
        using var connection = OpenConnection();
        var username = LoadUsernameByUserId(connection, id);
        using (var deleteUsage = CreateCommand(connection, $"DELETE FROM {Quote("usage_records")} WHERE user_id = @id"))
        {
            AddParameter(deleteUsage, "@id", id);
            deleteUsage.ExecuteNonQuery();
        }

        ReplaceUserGroups(connection, id, username, username ?? string.Empty, []);

        using var deleteUser = CreateCommand(connection, $"DELETE FROM {Quote("users")} WHERE id = @id");
        AddParameter(deleteUser, "@id", id);
        return deleteUser.ExecuteNonQuery() > 0;
    }

    public PasswordResetResult? ResetUserPassword(int id, PasswordResetInput input)
    {
        using var connection = OpenConnection();
        using var find = CreateCommand(connection, $"SELECT username FROM {Quote("users")} WHERE id = @id");
        AddParameter(find, "@id", id);
        var username = Convert.ToString(find.ExecuteScalar());
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var method = PasswordHashService.NormalizeInteractiveMethod(input.HashMethod);
        var plainPassword = input.Generate || string.IsNullOrWhiteSpace(input.Password)
            ? PasswordHashService.GeneratePassword()
            : input.Password.Trim();

        using var update = CreateCommand(connection, $"UPDATE {Quote("users")} SET hash_method = @hashMethod, password_hash = @passwordHash, failed_attempts = 0, locked_until = NULL WHERE id = @id");
        AddParameter(update, "@id", id);
        AddParameter(update, "@hashMethod", method);
        AddParameter(update, "@passwordHash", PasswordHashService.HashPassword(plainPassword, method));
        update.ExecuteNonQuery();

        return new PasswordResetResult
        {
            UserId = id,
            Username = username,
            HashMethod = method,
            GeneratedPassword = plainPassword
        };
    }

    public UsageRecord CreateUsageRecord(UsageRecordInput input)
    {
        var id = NextId("usage_records");
        ExecuteNonQuery(
            $"INSERT INTO {Quote("usage_records")} (id, user_id, computer_id, start_utc, end_utc) VALUES (@id, @userId, @computerId, @startUtc, @endUtc)",
            ("@id", id), ("@userId", input.UserId), ("@computerId", input.ComputerId), ("@startUtc", input.StartUtc), ("@endUtc", input.EndUtc));
        return new UsageRecord { Id = id, UserId = input.UserId, ComputerId = input.ComputerId, StartUtc = input.StartUtc, EndUtc = input.EndUtc };
    }

    public async Task<ImportUsersResult> ImportUsersAsync(IFormFile file)
    {
        using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = await reader.ReadToEndAsync();
        var lines = content
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count == 0)
        {
            return new ImportUsersResult { Imported = 0, Updated = 0, Warnings = new List<string> { "El archivo esta vacio." } };
        }

        var delimiter = RepositorySupport.DetectDelimiter(lines[0]);
        var header = RepositorySupport.SplitLine(lines[0], delimiter);
        var map = header
            .Select((value, index) => new { Key = RepositorySupport.NormalizeHeader(value), Index = index })
            .ToDictionary(item => item.Key, item => item.Index);

        var imported = 0;
        var updated = 0;
        var warnings = new List<string>();

        foreach (var tuple in lines.Skip(1).Select((line, index) => (line, index)))
        {
            var values = RepositorySupport.SplitLine(tuple.line, delimiter);
            var username = RepositorySupport.GetValue(values, map, "username");
            if (string.IsNullOrWhiteSpace(username))
            {
                warnings.Add($"Fila {tuple.index + 2}: username vacio, se omite.");
                continue;
            }

            var user = new UserInput
            {
                Username = username.Trim(),
                FirstName = RepositorySupport.GetValue(values, map, "firstname", "nombres", "nombre"),
                LastName = RepositorySupport.GetValue(values, map, "lastname", "apellidos", "apellido"),
                Email = RepositorySupport.GetValue(values, map, "email", "correo"),
                DocumentId = RepositorySupport.GetValue(values, map, "documentid", "documento", "cedula"),
                CareerId = EnsureCareer(RepositorySupport.GetValue(values, map, "career", "carrera")),
                SemesterId = EnsureSemester(RepositorySupport.GetValue(values, map, "semester", "semestre", "level")),
                Active = RepositorySupport.ParseBoolean(RepositorySupport.GetValue(values, map, "active", "estado", "status"), true),
                HashMethod = PasswordHashService.NormalizeMethod(RepositorySupport.GetValue(values, map, "hashmethod", "hash_method", "algoritmo")),
                Password = RepositorySupport.GetValue(values, map, "password", "clave", "contrasena")
            };

            var existingId = FindUserIdByUsername(user.Username);
            if (existingId.HasValue)
            {
                UpdateUser(existingId.Value, user);
                updated++;
            }
            else
            {
                CreateUser(user);
                imported++;
            }
        }

        return new ImportUsersResult { Imported = imported, Updated = updated, Warnings = warnings };
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        DatabaseSchemaManager.ApplySchema(connection, _isPostgreSql);
    }

    private void SeedIfEmpty()
    {
        using var connection = OpenConnection();
        using var command = CreateCommand(connection, $"SELECT COUNT(*) FROM {Quote("users")}");
        var count = Convert.ToInt32(command.ExecuteScalar());
        if (count > 0)
        {
            return;
        }

        var snapshot = RepositorySupport.CreateSeedSnapshot();

        foreach (var career in snapshot.Careers)
        {
            ExecuteNonQuery($"INSERT INTO {Quote("careers")} (id, name, status) VALUES (@id, @name, @status)",
                ("@id", career.Id), ("@name", career.Name), ("@status", ToStatus(career.Active)));
        }

        foreach (var semester in snapshot.Semesters)
        {
            ExecuteNonQuery($"INSERT INTO {Quote("levels")} (id, name, status) VALUES (@id, @name, @status)",
                ("@id", semester.Id), ("@name", semester.Name), ("@status", ToStatus(semester.Active)));
        }

        foreach (var user in snapshot.Users)
        {
            ExecuteNonQuery(
                $"INSERT INTO {Quote("users")} (id, username, first_name, last_name, document_id, email, status, career_id, level_id, hash_method, password_hash, failed_attempts, locked_until, last_attempt_at) VALUES (@id, @username, @firstName, @lastName, @documentId, @email, @status, @careerId, @levelId, @hashMethod, @passwordHash, 0, NULL, NULL)",
                ("@id", user.Id), ("@username", user.Username), ("@firstName", user.FirstName), ("@lastName", user.LastName),
                ("@documentId", user.DocumentId), ("@email", user.Email), ("@status", ToStatus(user.Active)),
                ("@careerId", (object?)user.CareerId ?? DBNull.Value), ("@levelId", (object?)user.SemesterId ?? DBNull.Value),
                ("@hashMethod", PasswordHashService.NormalizeMethod(user.HashMethod)), ("@passwordHash", user.PasswordHash ?? PasswordHashService.HashPassword(PasswordHashService.GeneratePassword(), user.HashMethod)));
        }

        foreach (var computer in snapshot.Computers)
        {
            ExecuteNonQuery(
                $"INSERT INTO {Quote("computers")} (id, name, location, inventory_tag, ip_address, status, current_username, last_seen_utc) VALUES (@id, @name, @location, @inventory, @ip, @status, @current, @lastSeen)",
                ("@id", computer.Id), ("@name", computer.Name), ("@location", computer.Location), ("@inventory", computer.InventoryTag),
                ("@ip", (object?)computer.IpAddress ?? DBNull.Value),
                ("@status", computer.Status.ToString()), ("@current", (object?)computer.CurrentUsername ?? DBNull.Value), ("@lastSeen", computer.LastSeenUtc));
        }

        foreach (var room in snapshot.Rooms)
        {
            ExecuteNonQuery(
                $"INSERT INTO {Quote("rooms")} (id, name, code, canvas_width, canvas_height, status) VALUES (@id, @name, @code, @canvasWidth, @canvasHeight, @status)",
                ("@id", room.Id), ("@name", room.Name), ("@code", room.Code), ("@canvasWidth", room.CanvasWidth), ("@canvasHeight", room.CanvasHeight), ("@status", ToStatus(room.Active)));
        }

        foreach (var item in snapshot.RoomLayoutItems)
        {
            var rowNumber = Math.Max(1, (int)Math.Floor(Math.Max(0, item.Y) / 40.0) + 1);
            var columnNumber = Math.Max(1, (int)Math.Floor(Math.Max(0, item.X) / 40.0) + 1);
            ExecuteNonQuery(
                $"INSERT INTO {Quote("room_positions")} (id, room_id, label, item_type, pos_x, pos_y, item_width, item_height, computer_id, row_number, column_number) VALUES (@id, @roomId, @label, @itemType, @x, @y, @width, @height, @computerId, @rowNumber, @columnNumber)",
                ("@id", item.Id), ("@roomId", item.RoomId), ("@label", item.Label), ("@itemType", item.ItemType.ToString()), ("@x", item.X), ("@y", item.Y), ("@width", item.Width), ("@height", item.Height), ("@computerId", (object?)item.ComputerId ?? DBNull.Value), ("@rowNumber", rowNumber), ("@columnNumber", columnNumber));
        }

        foreach (var usage in snapshot.UsageRecords)
        {
            ExecuteNonQuery(
                $"INSERT INTO {Quote("usage_records")} (id, user_id, computer_id, start_utc, end_utc) VALUES (@id, @userId, @computerId, @startUtc, @endUtc)",
                ("@id", usage.Id), ("@userId", usage.UserId), ("@computerId", usage.ComputerId), ("@startUtc", usage.StartUtc), ("@endUtc", usage.EndUtc));
        }
    }

    private int? EnsureCareer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        using var connection = OpenConnection();
        using var command = CreateCommand(connection, $"SELECT id FROM {Quote("careers")} WHERE LOWER(name) = LOWER(@name)");
        AddParameter(command, "@name", name.Trim());
        var existing = command.ExecuteScalar();
        if (existing is not null) return Convert.ToInt32(existing);
        return CreateCareer(new CareerInput { Name = name.Trim(), Active = true }).Id;
    }

    private int? EnsureSemester(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        using var connection = OpenConnection();
        using var command = CreateCommand(connection, $"SELECT id FROM {Quote("levels")} WHERE LOWER(name) = LOWER(@name)");
        AddParameter(command, "@name", name.Trim());
        var existing = command.ExecuteScalar();
        if (existing is not null) return Convert.ToInt32(existing);
        return CreateSemester(new SemesterInput { Name = name.Trim(), Active = true }).Id;
    }

    private int? FindUserIdByUsername(string username)
    {
        using var connection = OpenConnection();
        using var command = CreateCommand(connection, $"SELECT id FROM {Quote("users")} WHERE LOWER(username) = LOWER(@username)");
        AddParameter(command, "@username", username);
        var value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt32(value);
    }

    private int NextId(string table)
    {
        using var connection = OpenConnection();
        return NextId(connection, table);
    }

    private int NextId(DbConnection connection, string table)
    {
        using var command = CreateCommand(connection, $"SELECT COALESCE(MAX(id), 0) + 1 FROM {Quote(table)}");
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private int ExecuteNonQuery(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = OpenConnection();
        using var command = CreateCommand(connection, sql);
        foreach (var parameter in parameters)
        {
            AddParameter(command, parameter.Name, parameter.Value);
        }

        return command.ExecuteNonQuery();
    }

    private DbConnection OpenConnection()
    {
        var connection = _factory.CreateConnection() ?? throw new InvalidOperationException("No fue posible crear la conexion.");
        connection.ConnectionString = _options.ConnectionString;
        connection.Open();
        return connection;
    }

    private DbCommand CreateCommand(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private string Quote(string identifier)
    {
        return _isPostgreSql
            ? $"\"{identifier.Replace("\"", "\"\"")}\""
            : $"`{identifier.Replace("`", "``")}`";
    }

    private List<LoginSessionSnapshot> LoadLatestLoginSessions(DbConnection connection)
    {
        if (!TableExists(connection, "login_sessions"))
        {
            return [];
        }

        var hasClientSessionId = ColumnExists(connection, "login_sessions", "client_session_id");
        var hasWindowsSessionId = ColumnExists(connection, "login_sessions", "windows_session_id");
        var hasSessionState = ColumnExists(connection, "login_sessions", "session_state");
        var hasLastHeartbeatAt = ColumnExists(connection, "login_sessions", "last_heartbeat_at");
        var hasSessionEndReason = ColumnExists(connection, "login_sessions", "session_end_reason");
        var hasSessionOrigin = ColumnExists(connection, "login_sessions", "session_origin");
        var clientSessionIdExpression = hasClientSessionId ? "client_session_id" : "NULL";
        var windowsSessionIdExpression = hasWindowsSessionId ? "windows_session_id" : "NULL";
        var sessionStateExpression = hasSessionState ? "session_state" : "NULL";
        var lastHeartbeatExpression = hasLastHeartbeatAt ? "last_heartbeat_at" : "NULL";
        var sessionEndReasonExpression = hasSessionEndReason ? "session_end_reason" : "NULL";
        var sessionOriginExpression = hasSessionOrigin ? "session_origin" : "NULL";
        var activityReferenceExpression = hasLastHeartbeatAt ? "COALESCE(last_heartbeat_at, loginstamp)" : "loginstamp";
        var heartbeatAgeSql = _isPostgreSql
            ? $"GREATEST(0, EXTRACT(EPOCH FROM (CURRENT_TIMESTAMP - {activityReferenceExpression})))::INT"
            : $"GREATEST(0, TIMESTAMPDIFF(SECOND, {activityReferenceExpression}, CURRENT_TIMESTAMP))";

        var latestSessions = new List<LoginSessionSnapshot>();
        var latestByKey = new Dictionary<string, LoginSessionSnapshot>(StringComparer.OrdinalIgnoreCase);
        using (var command = CreateCommand(connection, $"SELECT dbid, loginstamp, logoutstamp, username, machine, ipaddress, {clientSessionIdExpression} AS client_session_id, {windowsSessionIdExpression} AS windows_session_id, {sessionStateExpression} AS session_state, {lastHeartbeatExpression} AS last_heartbeat_at, {sessionEndReasonExpression} AS session_end_reason, {sessionOriginExpression} AS session_origin, {heartbeatAgeSql} AS heartbeat_age_seconds FROM {Quote("login_sessions")} ORDER BY {activityReferenceExpression} DESC, loginstamp DESC, dbid DESC"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var session = new LoginSessionSnapshot
                {
                    DbId = ReadFlexibleInt32(reader, 0) ?? 0,
                    LoginStamp = ReadFlexibleDateTimeUtc(reader, 1) ?? DateTime.UtcNow,
                    LogoutStamp = ReadFlexibleDateTimeUtc(reader, 2),
                    Username = ReadFlexibleString(reader, 3),
                    Machine = ReadFlexibleString(reader, 4),
                    IpAddress = ReadFlexibleString(reader, 5),
                    ClientSessionId = ReadFlexibleString(reader, 6),
                    WindowsSessionId = ReadFlexibleInt32(reader, 7),
                    SessionState = ReadFlexibleString(reader, 8),
                    LastHeartbeatAt = ReadFlexibleDateTimeUtc(reader, 9),
                    SessionEndReason = ReadFlexibleString(reader, 10),
                    SessionOrigin = ReadFlexibleString(reader, 11),
                    HeartbeatAgeSeconds = ReadFlexibleInt32(reader, 12)
                };

                var sessionKey = BuildSessionLookupKey(session);
                if (string.IsNullOrWhiteSpace(sessionKey) || latestByKey.ContainsKey(sessionKey))
                {
                    continue;
                }

                latestByKey[sessionKey] = session;
                latestSessions.Add(session);
            }
        }

        return latestSessions;
    }

    private static string TranslateSessionStateLabel(string? sessionState)
    {
        return sessionState?.Trim().ToLowerInvariant() switch
        {
            "active" => "Activa",
            "locked" => "Bloqueada",
            "disconnected" => "Desconectada",
            "ended" => "Finalizada",
            _ => string.IsNullOrWhiteSpace(sessionState) ? "Sin estado" : sessionState.Trim()
        };
    }

    private List<GroupInfo> LoadGroups(DbConnection connection)
    {
        if (!TableExists(connection, "groups"))
        {
            return [];
        }

        var idColumn = ResolveColumnName(connection, "groups", "group_id", "groupid", "id");
        var nameColumn = ResolveColumnName(connection, "groups", "group_name", "groupname", "name");
        if (idColumn is null || nameColumn is null)
        {
            return [];
        }

        var groups = new List<GroupInfo>();
        using var command = CreateCommand(connection, $"SELECT {Quote(idColumn)}, {Quote(nameColumn)} FROM {Quote("groups")} ORDER BY {Quote(nameColumn)}");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = ReadFlexibleInt32(reader, 0);
            var name = ReadFlexibleString(reader, 1);
            if (!id.HasValue || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            groups.Add(new GroupInfo
            {
                Id = id.Value,
                Name = name
            });
        }

        return groups;
    }

    private string? LoadLookupName(DbConnection connection, string tableName, int? id)
    {
        if (!id.HasValue || !TableExists(connection, tableName))
        {
            return null;
        }

        using var command = CreateCommand(connection, $"SELECT name FROM {Quote(tableName)} WHERE id = @id");
        AddParameter(command, "@id", id.Value);
        return RepositorySupport.CleanOptional(Convert.ToString(command.ExecuteScalar()));
    }

    private void CleanupPortalResetTokens(DbConnection connection)
    {
        if (!HasPortalResetTokenSchema(connection))
        {
            return;
        }

        using var command = CreateCommand(connection,
            $"DELETE FROM {Quote("portal_password_reset_tokens")} WHERE consumed_utc IS NOT NULL OR expires_utc < @nowUtc");
        AddParameter(command, "@nowUtc", DateTime.UtcNow);
        command.ExecuteNonQuery();
    }

    private bool HasPortalResetTokenSchema(DbConnection connection)
    {
        return TableExists(connection, "portal_password_reset_tokens")
               && ColumnExists(connection, "portal_password_reset_tokens", "user_id")
               && ColumnExists(connection, "portal_password_reset_tokens", "username")
               && ColumnExists(connection, "portal_password_reset_tokens", "email")
               && ColumnExists(connection, "portal_password_reset_tokens", "reset_token")
               && ColumnExists(connection, "portal_password_reset_tokens", "created_utc")
               && ColumnExists(connection, "portal_password_reset_tokens", "expires_utc")
               && ColumnExists(connection, "portal_password_reset_tokens", "consumed_utc");
    }

    private void ApplyGroupsToUsers(DbConnection connection, List<UserAccount> users, IReadOnlyCollection<GroupInfo> groups)
    {
        foreach (var user in users)
        {
            user.Groups = [];
        }

        if (users.Count == 0 || groups.Count == 0 || !TableExists(connection, "user_groups"))
        {
            return;
        }

        var groupIdColumn = ResolveColumnName(connection, "user_groups", "group_id", "groupid");
        var userIdColumn = ResolveColumnName(connection, "user_groups", "user_id", "userid");
        var usernameColumn = ResolveColumnName(connection, "user_groups", "username", "user_username");
        if (groupIdColumn is null || (userIdColumn is null && usernameColumn is null))
        {
            return;
        }

        var usersById = users.ToDictionary(user => user.Id);
        var usersByUsername = users
            .GroupBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var groupsById = groups.ToDictionary(group => group.Id);

        var selectedColumns = new List<string>();
        if (userIdColumn is not null)
        {
            selectedColumns.Add(Quote(userIdColumn));
        }
        if (usernameColumn is not null)
        {
            selectedColumns.Add(Quote(usernameColumn));
        }
        selectedColumns.Add(Quote(groupIdColumn));

        using var command = CreateCommand(connection, $"SELECT {string.Join(", ", selectedColumns)} FROM {Quote("user_groups")}");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var offset = 0;
            int? userId = null;
            string? username = null;

            if (userIdColumn is not null)
            {
                userId = ReadFlexibleInt32(reader, offset++);
            }

            if (usernameColumn is not null)
            {
                username = ReadFlexibleString(reader, offset++);
            }

            var groupId = ReadFlexibleInt32(reader, offset);
            if (!groupId.HasValue || !groupsById.TryGetValue(groupId.Value, out var group))
            {
                continue;
            }

            UserAccount? user = null;
            if (userId.HasValue)
            {
                usersById.TryGetValue(userId.Value, out user);
            }

            if (user is null && !string.IsNullOrWhiteSpace(username))
            {
                usersByUsername.TryGetValue(username.Trim(), out user);
            }

            if (user is null || user.Groups.Any(item => item.Id == group.Id))
            {
                continue;
            }

            user.Groups.Add(new GroupInfo
            {
                Id = group.Id,
                Name = group.Name
            });
        }

        foreach (var user in users)
        {
            user.Groups = user.Groups
                .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private void EnsureComputersDiscoveredFromSessions(DbConnection connection, IReadOnlyCollection<LoginSessionSnapshot> latestSessions)
    {
        var discoveryCutoff = DateTime.UtcNow - HeartbeatStaleThreshold;
        foreach (var session in latestSessions
                     .Where(item => !string.IsNullOrWhiteSpace(item.Machine))
                     .Where(item => item.LogoutStamp is null)
                     .Where(item => !string.Equals(item.SessionState, "ended", StringComparison.OrdinalIgnoreCase))
                     .Where(item => (item.LastHeartbeatAt ?? item.LoginStamp) >= discoveryCutoff))
        {
            var existingId = FindComputerId(connection, session.Machine!, session.IpAddress);
            if (existingId.HasValue)
            {
                var currentIpAddress = LoadComputerIpAddress(connection, existingId.Value);
                var preferredIpAddress = SelectPreferredComputerIpAddress(currentIpAddress, session.IpAddress);
                using var update = CreateCommand(connection,
                    $"UPDATE {Quote("computers")} SET name = @name, ip_address = @ip, last_seen_utc = @lastSeen WHERE id = @id");
                AddParameter(update, "@id", existingId.Value);
                AddParameter(update, "@name", session.Machine!);
                AddParameter(update, "@ip", (object?)preferredIpAddress ?? DBNull.Value);
                AddParameter(update, "@lastSeen", (object?)(session.LastHeartbeatAt ?? session.LoginStamp) ?? DBNull.Value);
                update.ExecuteNonQuery();
            }
            else
            {
                using var insert = CreateCommand(connection,
                    $"INSERT INTO {Quote("computers")} (id, name, location, inventory_tag, ip_address, status, current_username, last_seen_utc) VALUES (@id, @name, @location, @inventory, @ip, @status, @username, @lastSeen)");
                AddParameter(insert, "@id", NextId(connection, "computers"));
                AddParameter(insert, "@name", session.Machine!);
                AddParameter(insert, "@location", "Detectado por login_sessions");
                AddParameter(insert, "@inventory", $"AUTO-{session.Machine!}");
                AddParameter(insert, "@ip", string.IsNullOrWhiteSpace(session.IpAddress) ? DBNull.Value : session.IpAddress!);
                AddParameter(insert, "@status", ComputerStatus.Available.ToString());
                AddParameter(insert, "@username", DBNull.Value);
                AddParameter(insert, "@lastSeen", (object?)(session.LastHeartbeatAt ?? session.LoginStamp) ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }
        }
    }

    private string? LoadComputerIpAddress(DbConnection connection, int id)
    {
        using var command = CreateCommand(connection, $"SELECT ip_address FROM {Quote("computers")} WHERE id = @id");
        AddParameter(command, "@id", id);
        return RepositorySupport.CleanOptional(Convert.ToString(command.ExecuteScalar()));
    }

    private static string? SelectPreferredComputerIpAddress(string? currentIpAddress, string? detectedIpAddress)
    {
        var current = RepositorySupport.CleanOptional(currentIpAddress);
        var detected = RepositorySupport.CleanOptional(detectedIpAddress);

        if (string.IsNullOrWhiteSpace(detected))
        {
            return current;
        }

        if (string.IsNullOrWhiteSpace(current))
        {
            return detected;
        }

        var currentIsLinkLocal = IsLinkLocalAutoConfiguredIp(current);
        var detectedIsLinkLocal = IsLinkLocalAutoConfiguredIp(detected);

        if (detectedIsLinkLocal && !currentIsLinkLocal)
        {
            return current;
        }

        if (!detectedIsLinkLocal && currentIsLinkLocal)
        {
            return detected;
        }

        return current;
    }

    private static bool IsLinkLocalAutoConfiguredIp(string value)
    {
        return value.StartsWith("169.254.", StringComparison.OrdinalIgnoreCase);
    }

    private List<ComputedComputerState> BuildComputedComputerStates(IReadOnlyCollection<Computer> computers, IReadOnlyCollection<LoginSessionSnapshot> latestSessions)
    {
        var nowUtc = DateTime.UtcNow;
        var sessionsByMachine = latestSessions
            .Where(session => !string.IsNullOrWhiteSpace(session.Machine))
            .GroupBy(session => session.Machine!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var sessionsByIp = latestSessions
            .Where(session => !string.IsNullOrWhiteSpace(session.IpAddress))
            .GroupBy(session => session.IpAddress!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return computers
            .Select(computer =>
            {
                var matchedSession = TryMatchSessionForComputer(computer, sessionsByMachine, sessionsByIp);
                return BuildComputedComputerState(computer, matchedSession, nowUtc);
            })
            .OrderBy(state => state.ComputerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ApplyComputedStatesToLegacyComputers(List<Computer> computers, IReadOnlyCollection<ComputedComputerState> computedComputers)
    {
        var computedById = computedComputers.ToDictionary(item => item.ComputerId);
        foreach (var computer in computers)
        {
            if (!computedById.TryGetValue(computer.Id, out var computed))
            {
                continue;
            }

            computer.Status = MapOperationalToLegacyStatus(computed.OperationalStatus);
            computer.CurrentUsername = computed.OperationalStatus == OperationalComputerStatus.Orphaned
                ? null
                : computed.SessionUsername;
            computer.LastSeenUtc = computed.LastHeartbeatAt ?? computed.LoginStamp ?? computer.LastSeenUtc;
        }
    }

    private LoginSessionSnapshot? TryMatchSessionForComputer(
        Computer computer,
        IReadOnlyDictionary<string, LoginSessionSnapshot> sessionsByMachine,
        IReadOnlyDictionary<string, LoginSessionSnapshot> sessionsByIp)
    {
        if (!string.IsNullOrWhiteSpace(computer.Name) && sessionsByMachine.TryGetValue(computer.Name, out var machineSession))
        {
            return machineSession;
        }

        if (!string.IsNullOrWhiteSpace(computer.IpAddress) && sessionsByIp.TryGetValue(computer.IpAddress, out var ipSession))
        {
            return ipSession;
        }

        return null;
    }

    private ComputedComputerState BuildComputedComputerState(Computer computer, LoginSessionSnapshot? session, DateTime nowUtc)
    {
        if (computer.Status == ComputerStatus.Disabled)
        {
            return CreateComputedComputerState(
                computer,
                session,
                OperationalComputerStatus.Disabled,
                "Equipo deshabilitado administrativamente.");
        }

        if (session is null)
        {
            return CreateComputedComputerState(
                computer,
                null,
                OperationalComputerStatus.Available,
                "No hay sesion vigente asociada al equipo.");
        }

        if (session.LogoutStamp.HasValue || string.Equals(session.SessionState, "ended", StringComparison.OrdinalIgnoreCase))
        {
            return CreateComputedComputerState(
                computer,
                session,
                OperationalComputerStatus.Available,
                "La ultima sesion ya fue cerrada correctamente.");
        }

        var heartbeatSeconds = session.HeartbeatAgeSeconds
            ?? Math.Max(0, (int)Math.Floor((nowUtc - (session.LastHeartbeatAt ?? session.LoginStamp)).TotalSeconds));
        var heartbeatAge = TimeSpan.FromSeconds(heartbeatSeconds);
        var isStale = heartbeatAge > HeartbeatFreshThreshold;
        var isOrphaned = heartbeatAge > HeartbeatStaleThreshold;

        if (isOrphaned)
        {
            var reason = string.IsNullOrWhiteSpace(session.SessionEndReason)
                ? "Sesion abierta sin heartbeat reciente."
                : $"Sesion abierta reconciliada o vencida: {session.SessionEndReason}.";
            return CreateComputedComputerState(
                computer,
                session,
                OperationalComputerStatus.Orphaned,
                reason,
                heartbeatAge,
                isStale: true,
                isOrphaned: true);
        }

        var sessionState = (session.SessionState ?? string.Empty).Trim().ToLowerInvariant();
        var operationalStatus = sessionState switch
        {
            "active" => OperationalComputerStatus.Occupied,
            "locked" => OperationalComputerStatus.Locked,
            "disconnected" => OperationalComputerStatus.Disconnected,
            _ => OperationalComputerStatus.Orphaned
        };

        var statusReason = operationalStatus switch
        {
            OperationalComputerStatus.Occupied => "Sesion activa con heartbeat reciente.",
            OperationalComputerStatus.Locked => "Sesion bloqueada con heartbeat reciente.",
            OperationalComputerStatus.Disconnected => "Sesion desconectada con heartbeat todavia vigente.",
            _ => "Estado de sesion no reconocido; requiere revision."
        };

        if (operationalStatus == OperationalComputerStatus.Occupied &&
            string.Equals(session.SessionOrigin, "offline_cache", StringComparison.OrdinalIgnoreCase))
        {
            statusReason = "Sesion activa sincronizada desde cache offline con heartbeat reciente.";
        }

        return CreateComputedComputerState(
            computer,
            session,
            operationalStatus,
            statusReason,
            heartbeatAge,
            isStale,
            operationalStatus == OperationalComputerStatus.Orphaned);
    }

    private ComputedComputerState CreateComputedComputerState(
        Computer computer,
        LoginSessionSnapshot? session,
        OperationalComputerStatus operationalStatus,
        string statusReason,
        TimeSpan? heartbeatAge = null,
        bool isStale = false,
        bool isOrphaned = false)
    {
        var heartbeatSeconds = heartbeatAge.HasValue
            ? Math.Max(0, (int)Math.Floor(heartbeatAge.Value.TotalSeconds))
            : session?.HeartbeatAgeSeconds;
        var sessionOrigin = CleanOptionalSessionValue(session?.SessionOrigin);
        var originLabel = TranslateSessionOrigin(sessionOrigin);
        var isRecoveredOffline = string.Equals(sessionOrigin, "offline_cache", StringComparison.OrdinalIgnoreCase);
        var isSuperseded = string.Equals(session?.SessionEndReason, "superseded_by_logon", StringComparison.OrdinalIgnoreCase);
        var isUnexpectedShutdown = string.Equals(session?.SessionEndReason, "unexpected_shutdown", StringComparison.OrdinalIgnoreCase);
        var isHeartbeatTimeout = string.Equals(session?.SessionEndReason, "heartbeat_timeout", StringComparison.OrdinalIgnoreCase);
        var alertFlags = BuildAlertFlags(isRecoveredOffline, isSuperseded, isUnexpectedShutdown, isHeartbeatTimeout, isOrphaned);

        return new ComputedComputerState
        {
            ComputerId = computer.Id,
            ComputerName = computer.Name,
            Location = computer.Location,
            InventoryTag = computer.InventoryTag,
            IpAddress = computer.IpAddress,
            AdministrativeStatus = computer.Status,
            OperationalStatus = operationalStatus,
            OperationalStatusLabel = RepositorySupport.TranslateOperationalStatus(operationalStatus),
            StatusReason = statusReason,
            SessionUsername = CleanOptionalSessionValue(session?.Username),
            Machine = CleanOptionalSessionValue(session?.Machine),
            ClientSessionId = CleanOptionalSessionValue(session?.ClientSessionId),
            WindowsSessionId = session?.WindowsSessionId,
            SessionState = CleanOptionalSessionValue(session?.SessionState),
            LoginStamp = session?.LoginStamp,
            LogoutStamp = session?.LogoutStamp,
            LastHeartbeatAt = session?.LastHeartbeatAt,
            SessionEndReason = CleanOptionalSessionValue(session?.SessionEndReason),
            SessionOrigin = sessionOrigin,
            OriginLabel = originLabel,
            AlertFlags = alertFlags,
            HeartbeatAgeSeconds = heartbeatSeconds,
            IsStale = isStale,
            IsOrphaned = isOrphaned,
            HasRecoveredOfflineSession = isRecoveredOffline,
            HasSessionWarning = alertFlags.Count > 0,
            IsSuperseded = isSuperseded,
            IsUnexpectedShutdown = isUnexpectedShutdown,
            IsHeartbeatTimeout = isHeartbeatTimeout,
            LastSeenUtc = session?.LastHeartbeatAt ?? session?.LoginStamp ?? computer.LastSeenUtc
        };
    }

    private static string? BuildSessionLookupKey(LoginSessionSnapshot session)
    {
        if (!string.IsNullOrWhiteSpace(session.Machine))
        {
            return $"machine:{session.Machine.Trim().ToLowerInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(session.IpAddress))
        {
            return $"ip:{session.IpAddress.Trim().ToLowerInvariant()}";
        }

        return null;
    }

    private static string? CleanOptionalSessionValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? TranslateSessionOrigin(string? origin)
    {
        return origin?.Trim().ToLowerInvariant() switch
        {
            "online" => "Online",
            "offline_cache" => "Offline recuperado",
            _ => CleanOptionalSessionValue(origin)
        };
    }

    private static List<string> BuildAlertFlags(
        bool isRecoveredOffline,
        bool isSuperseded,
        bool isUnexpectedShutdown,
        bool isHeartbeatTimeout,
        bool isOrphaned)
    {
        var flags = new List<string>();
        if (isRecoveredOffline)
        {
            flags.Add("offline_recovered");
        }
        if (isSuperseded)
        {
            flags.Add("superseded_by_logon");
        }
        if (isUnexpectedShutdown)
        {
            flags.Add("unexpected_shutdown");
        }
        if (isHeartbeatTimeout)
        {
            flags.Add("heartbeat_timeout");
        }
        if (isOrphaned)
        {
            flags.Add("orphaned");
        }

        return flags;
    }

    private static OperationalComputerStatus DeriveOperationalStatus(string? sessionState, DateTime? logoutStamp, int heartbeatAgeSeconds)
    {
        if (logoutStamp.HasValue || string.Equals(sessionState, "ended", StringComparison.OrdinalIgnoreCase))
        {
            return OperationalComputerStatus.Available;
        }

        if (heartbeatAgeSeconds > HeartbeatStaleThreshold.TotalSeconds)
        {
            return OperationalComputerStatus.Orphaned;
        }

        return (sessionState ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "active" => OperationalComputerStatus.Occupied,
            "locked" => OperationalComputerStatus.Locked,
            "disconnected" => OperationalComputerStatus.Disconnected,
            _ => OperationalComputerStatus.Orphaned
        };
    }

    private static List<string> SplitGroupNames(string? groupNames)
    {
        return string.IsNullOrWhiteSpace(groupNames)
            ? []
            : groupNames
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    private static string? ReadFlexibleString(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return Convert.ToString(reader.GetValue(ordinal))?.Trim();
    }

    private static int? ReadFlexibleInt32(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            int intValue => intValue,
            long longValue => checked((int)longValue),
            short shortValue => shortValue,
            byte byteValue => byteValue,
            _ when int.TryParse(Convert.ToString(value), out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTime? ReadFlexibleDateTimeUtc(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime dateTime => NormalizeDatabaseDateTimeUtc(dateTime),
            DateTimeOffset offset => offset.UtcDateTime,
            _ when DateTime.TryParse(
                Convert.ToString(value),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var parsed) => NormalizeDatabaseDateTimeUtc(parsed),
            _ => null
        };
    }

    private static DateTime NormalizeDatabaseDateTimeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => TimeZoneInfo.ConvertTimeToUtc(value, LoginSessionTimeZone)
        };
    }

    private static TimeZoneInfo ResolveLoginSessionTimeZone()
    {
        var candidates = new[]
        {
            "America/Bogota",
            "SA Pacific Standard Time"
        };

        foreach (var candidate in candidates)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch
            {
                // Prueba siguiente identificador compatible con el sistema actual.
            }
        }

        return TimeZoneInfo.Local;
    }

    private static ComputerStatus MapOperationalToLegacyStatus(OperationalComputerStatus status)
    {
        return status switch
        {
            OperationalComputerStatus.Disabled => ComputerStatus.Disabled,
            OperationalComputerStatus.Occupied or OperationalComputerStatus.Locked or OperationalComputerStatus.Disconnected => ComputerStatus.InUse,
            _ => ComputerStatus.Available
        };
    }

    private int? FindComputerId(DbConnection connection, string machine, string? ipAddress)
    {
        using var command = CreateCommand(connection,
            $"SELECT id FROM {Quote("computers")} WHERE LOWER(name) = LOWER(@machine) OR (ip_address IS NOT NULL AND ip_address = @ip) ORDER BY id");
        AddParameter(command, "@machine", machine);
        AddParameter(command, "@ip", string.IsNullOrWhiteSpace(ipAddress) ? DBNull.Value : ipAddress);
        var value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt32(value);
    }

    private string? LoadUsernameByUserId(DbConnection connection, int userId)
    {
        using var command = CreateCommand(connection, $"SELECT username FROM {Quote("users")} WHERE id = @id");
        AddParameter(command, "@id", userId);
        return Convert.ToString(command.ExecuteScalar())?.Trim();
    }

    private void ReplaceUserGroups(DbConnection connection, int userId, string? previousUsername, string currentUsername, IEnumerable<int> groupIds)
    {
        if (!TableExists(connection, "user_groups"))
        {
            return;
        }

        var groupIdColumn = ResolveColumnName(connection, "user_groups", "group_id", "groupid");
        var userIdColumn = ResolveColumnName(connection, "user_groups", "user_id", "userid");
        var usernameColumn = ResolveColumnName(connection, "user_groups", "username", "user_username");
        if (groupIdColumn is null || (userIdColumn is null && usernameColumn is null))
        {
            return;
        }

        using (var delete = CreateCommand(connection, BuildDeleteUserGroupsSql(userIdColumn, usernameColumn)))
        {
            if (userIdColumn is not null)
            {
                AddParameter(delete, "@userId", userId);
            }
            if (usernameColumn is not null)
            {
                AddParameter(delete, "@username", previousUsername ?? currentUsername);
            }
            delete.ExecuteNonQuery();
        }

        foreach (var groupId in groupIds.Where(groupId => groupId > 0).Distinct())
        {
            var columns = new List<string>();
            var values = new List<string>();
            using var insert = CreateCommand(connection, string.Empty);

            if (userIdColumn is not null)
            {
                columns.Add(Quote(userIdColumn));
                values.Add("@userId");
                AddParameter(insert, "@userId", userId);
            }

            if (usernameColumn is not null)
            {
                columns.Add(Quote(usernameColumn));
                values.Add("@username");
                AddParameter(insert, "@username", currentUsername);
            }

            columns.Add(Quote(groupIdColumn));
            values.Add("@groupId");
            AddParameter(insert, "@groupId", groupId);

            insert.CommandText = $"INSERT INTO {Quote("user_groups")} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)})";
            insert.ExecuteNonQuery();
        }
    }

    private string BuildDeleteUserGroupsSql(string? userIdColumn, string? usernameColumn)
    {
        var predicates = new List<string>();
        if (userIdColumn is not null)
        {
            predicates.Add($"{Quote(userIdColumn)} = @userId");
        }
        if (usernameColumn is not null)
        {
            predicates.Add($"LOWER({Quote(usernameColumn)}) = LOWER(@username)");
        }

        return $"DELETE FROM {Quote("user_groups")} WHERE {string.Join(" OR ", predicates)}";
    }

    private string? ResolveColumnName(DbConnection connection, string table, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (ColumnExists(connection, table, candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private bool TableExists(DbConnection connection, string table)
    {
        using var command = CreateCommand(connection,
            _isPostgreSql
                ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @table"
                : "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @table");
        AddParameter(command, "@table", table);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private bool ColumnExists(DbConnection connection, string table, string column)
    {
        using var command = CreateCommand(connection,
            _isPostgreSql
                ? "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @table AND column_name = @column"
                : "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @table AND column_name = @column");
        AddParameter(command, "@table", table);
        AddParameter(command, "@column", column);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static bool ReadIntAsBool(IDataRecord record, int ordinal)
    {
        return !record.IsDBNull(ordinal) && Convert.ToInt32(record.GetValue(ordinal)) == 1;
    }

    private static bool ReadStatusAsBool(IDataRecord record, int ordinal)
    {
        if (record.IsDBNull(ordinal))
        {
            return false;
        }

        var value = record.GetValue(ordinal);
        if (value is bool boolValue)
        {
            return boolValue;
        }

        if (value is string text)
        {
            var normalized = text.Trim().ToLowerInvariant();
            if (normalized is "1" or "true" or "activo" or "active" or "enabled")
            {
                return true;
            }

            if (normalized is "0" or "false" or "inactivo" or "inactive" or "disabled")
            {
                return false;
            }
        }

        return Convert.ToInt32(value) == 1;
    }

    private static RoomLayoutItemType ParseRoomLayoutItemType(string? value)
    {
        return Enum.TryParse<RoomLayoutItemType>(value, true, out var parsed)
            ? parsed
            : RoomLayoutItemType.Computer;
    }

    private static string NormalizeOrientation(string? value)
    {
        return string.Equals(value, "Vertical", StringComparison.OrdinalIgnoreCase) ? "Vertical" : "Horizontal";
    }

    private static int NormalizeCapacity(int value)
    {
        return Math.Clamp(value, 1, 6);
    }

    private static string NormalizeSessionUsername(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Sin usuario identificado";
        }

        if (normalized.Equals("--UNKNOWN--", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("-UNKNOWN-", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
        {
            return "Sin usuario identificado";
        }

        return normalized;
    }

    private static int ToStatus(bool active) => active ? 1 : 0;
}
