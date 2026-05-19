using System.Data;
using System.Data.Common;
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
        var id = NextId("users");
        ExecuteNonQuery(
            $"INSERT INTO {Quote("users")} (id, username, first_name, last_name, document_id, email, status, career_id, level_id, hash_method, password_hash, failed_attempts, locked_until, last_attempt_at) VALUES (@id, @username, @firstName, @lastName, @documentId, @email, @status, @careerId, @levelId, @hashMethod, @passwordHash, 0, NULL, NULL)",
            ("@id", id),
            ("@username", input.Username.Trim()),
            ("@firstName", input.FirstName.Trim()),
            ("@lastName", input.LastName.Trim()),
            ("@documentId", input.DocumentId.Trim()),
            ("@email", input.Email.Trim()),
            ("@status", ToStatus(input.Active)),
            ("@careerId", (object?)input.CareerId ?? DBNull.Value),
            ("@levelId", (object?)input.SemesterId ?? DBNull.Value),
            ("@hashMethod", PasswordHashService.NormalizeMethod(input.HashMethod)),
            ("@passwordHash", PasswordHashService.HashPassword(input.Password ?? input.DocumentId.Trim(), input.HashMethod)));

        return new UserAccount
        {
            Id = id,
            Username = input.Username.Trim(),
            FirstName = input.FirstName.Trim(),
            LastName = input.LastName.Trim(),
            Email = input.Email.Trim(),
            DocumentId = input.DocumentId.Trim(),
            CareerId = input.CareerId,
            SemesterId = input.SemesterId,
            Active = input.Active,
            HashMethod = PasswordHashService.NormalizeMethod(input.HashMethod)
        };
    }

    public UserAccount? UpdateUser(int id, UserInput input)
    {
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
            ("@hashMethod", PasswordHashService.NormalizeMethod(input.HashMethod))
        };
        if (!string.IsNullOrWhiteSpace(input.Password))
        {
            parameters.Add(("@passwordHash", PasswordHashService.HashPassword(input.Password, input.HashMethod)));
        }

        var affected = ExecuteNonQuery(sql, parameters.ToArray());

        return affected == 0 ? null : new UserAccount
        {
            Id = id,
            Username = input.Username.Trim(),
            FirstName = input.FirstName.Trim(),
            LastName = input.LastName.Trim(),
            Email = input.Email.Trim(),
            DocumentId = input.DocumentId.Trim(),
            CareerId = input.CareerId,
            SemesterId = input.SemesterId,
            Active = input.Active,
            HashMethod = PasswordHashService.NormalizeMethod(input.HashMethod)
        };
    }

    public bool DeleteUser(int id)
    {
        ExecuteNonQuery($"DELETE FROM {Quote("usage_records")} WHERE user_id = @id", ("@id", id));
        return ExecuteNonQuery($"DELETE FROM {Quote("users")} WHERE id = @id", ("@id", id)) > 0;
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

        var method = PasswordHashService.NormalizeMethod(input.HashMethod);
        var plainPassword = input.Generate || string.IsNullOrWhiteSpace(input.Password)
            ? PasswordHashService.GeneratePassword()
            : input.Password.Trim();

        using var update = CreateCommand(connection, $"UPDATE {Quote("users")} SET hash_method = @hashMethod, password_hash = @passwordHash WHERE id = @id");
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
                ("@hashMethod", PasswordHashService.NormalizeMethod(user.HashMethod)), ("@passwordHash", user.PasswordHash ?? PasswordHashService.HashPassword(user.DocumentId, user.HashMethod)));
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

        var latestSessions = new List<LoginSessionSnapshot>();
        var latestByKey = new Dictionary<string, LoginSessionSnapshot>(StringComparer.OrdinalIgnoreCase);
        using (var command = CreateCommand(connection, $"SELECT dbid, loginstamp, logoutstamp, username, machine, ipaddress, client_session_id, windows_session_id, session_state, last_heartbeat_at, session_end_reason FROM {Quote("login_sessions")} ORDER BY COALESCE(last_heartbeat_at, loginstamp) DESC, loginstamp DESC, dbid DESC"))
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
                    SessionEndReason = ReadFlexibleString(reader, 10)
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
                using var update = CreateCommand(connection,
                    $"UPDATE {Quote("computers")} SET name = @name, ip_address = @ip, last_seen_utc = @lastSeen WHERE id = @id");
                AddParameter(update, "@id", existingId.Value);
                AddParameter(update, "@name", session.Machine!);
                AddParameter(update, "@ip", string.IsNullOrWhiteSpace(session.IpAddress) ? DBNull.Value : session.IpAddress!);
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

        var heartbeatReference = session.LastHeartbeatAt ?? session.LoginStamp;
        var heartbeatAge = nowUtc - heartbeatReference;
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
            : (int?)null;

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
            HeartbeatAgeSeconds = heartbeatSeconds,
            IsStale = isStale,
            IsOrphaned = isOrphaned,
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
            DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            DateTimeOffset offset => offset.UtcDateTime,
            _ when DateTime.TryParse(Convert.ToString(value), out var parsed) => DateTime.SpecifyKind(parsed, DateTimeKind.Utc),
            _ => null
        };
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

    private bool TableExists(DbConnection connection, string table)
    {
        using var command = CreateCommand(connection,
            _isPostgreSql
                ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @table"
                : "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @table");
        AddParameter(command, "@table", table);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static bool ReadIntAsBool(IDataRecord record, int ordinal)
    {
        return !record.IsDBNull(ordinal) && Convert.ToInt32(record.GetValue(ordinal)) == 1;
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

    private static int ToStatus(bool active) => active ? 1 : 0;
}
