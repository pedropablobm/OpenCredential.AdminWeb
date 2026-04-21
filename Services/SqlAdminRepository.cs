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
        SyncComputersFromLoginSessions(connection);

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
        return ExecuteNonQuery($"DELETE FROM {Quote("computers")} WHERE id = @id", ("@id", id)) > 0;
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
        foreach (var sql in GetSchemaStatements())
        {
            using var command = CreateCommand(connection, sql);
            command.ExecuteNonQuery();
        }
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

    private IEnumerable<string> GetSchemaStatements()
    {
        if (_isPostgreSql)
        {
            return new[]
            {
                """
                CREATE TABLE IF NOT EXISTS "careers" (
                  "id" INT PRIMARY KEY,
                  "name" VARCHAR(255) NOT NULL,
                  "status" INT NOT NULL DEFAULT 1
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "levels" (
                  "id" INT PRIMARY KEY,
                  "name" VARCHAR(100) NOT NULL,
                  "status" INT NOT NULL DEFAULT 1
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "users" (
                  "id" INT NOT NULL,
                  "username" VARCHAR(50) NOT NULL,
                  "first_name" VARCHAR(100),
                  "last_name" VARCHAR(100),
                  "document_id" VARCHAR(15),
                  "email" VARCHAR(200),
                  "status" INT NOT NULL DEFAULT 1,
                  "career_id" INT NULL,
                  "level_id" INT NULL,
                  "hash_method" TEXT NOT NULL DEFAULT 'NONE',
                  "password_hash" TEXT NULL,
                  "failed_attempts" INT NOT NULL DEFAULT 0,
                  "locked_until" TIMESTAMP NULL,
                  "last_attempt_at" TIMESTAMP NULL,
                  CONSTRAINT "pk_users_id" PRIMARY KEY ("id"),
                  CONSTRAINT "uq_users_username" UNIQUE ("username")
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "computers" (
                  "id" INT PRIMARY KEY,
                  "name" VARCHAR(128) NOT NULL,
                  "location" VARCHAR(150) NOT NULL,
                  "inventory_tag" VARCHAR(80) NOT NULL,
                  "ip_address" VARCHAR(45) NULL,
                  "status" VARCHAR(20) NOT NULL,
                  "current_username" VARCHAR(128) NULL,
                  "last_seen_utc" TIMESTAMP NOT NULL
                )
                """,
                """
                ALTER TABLE "computers" ADD COLUMN IF NOT EXISTS "ip_address" VARCHAR(45) NULL
                """,
                """
                CREATE TABLE IF NOT EXISTS "usage_records" (
                  "id" INT PRIMARY KEY,
                  "user_id" INT NOT NULL,
                  "computer_id" INT NOT NULL,
                  "start_utc" TIMESTAMP NOT NULL,
                  "end_utc" TIMESTAMP NOT NULL
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "admin_audit_log" (
                  "id" INT PRIMARY KEY,
                  "actor_username" VARCHAR(100) NOT NULL,
                  "action" VARCHAR(60) NOT NULL,
                  "entity_type" VARCHAR(80) NOT NULL,
                  "entity_key" VARCHAR(120) NOT NULL,
                  "summary" TEXT NOT NULL,
                  "remote_ip" VARCHAR(64) NULL,
                  "created_utc" TIMESTAMP NOT NULL
                )
                """
            };
        }

        return new[]
        {
            """
            CREATE TABLE IF NOT EXISTS `careers` (
              `id` INT NOT NULL,
              `name` VARCHAR(255) NOT NULL,
              `status` INT NOT NULL DEFAULT 1,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            CREATE TABLE IF NOT EXISTS `levels` (
              `id` INT NOT NULL,
              `name` VARCHAR(100) NOT NULL,
              `status` INT NOT NULL DEFAULT 1,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            CREATE TABLE IF NOT EXISTS `users` (
              `id` INT NOT NULL,
              `username` VARCHAR(50) NOT NULL,
              `first_name` VARCHAR(100) NULL,
              `last_name` VARCHAR(100) NULL,
              `document_id` VARCHAR(15) NULL,
              `email` VARCHAR(200) NULL,
              `status` INT NOT NULL DEFAULT 1,
              `career_id` INT NULL,
              `level_id` INT NULL,
              `hash_method` TEXT NOT NULL,
              `password_hash` TEXT NULL,
              `failed_attempts` INT NOT NULL DEFAULT 0,
              `locked_until` DATETIME NULL,
              `last_attempt_at` DATETIME NULL,
              PRIMARY KEY (`id`),
              UNIQUE KEY `uq_users_username` (`username`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            CREATE TABLE IF NOT EXISTS `computers` (
              `id` INT NOT NULL,
              `name` VARCHAR(128) NOT NULL,
              `location` VARCHAR(150) NOT NULL,
              `inventory_tag` VARCHAR(80) NOT NULL,
              `ip_address` VARCHAR(45) NULL,
              `status` VARCHAR(20) NOT NULL,
              `current_username` VARCHAR(128) NULL,
              `last_seen_utc` DATETIME NOT NULL,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            ALTER TABLE `computers` ADD COLUMN IF NOT EXISTS `ip_address` VARCHAR(45) NULL
            """,
            """
            CREATE TABLE IF NOT EXISTS `usage_records` (
              `id` INT NOT NULL,
              `user_id` INT NOT NULL,
              `computer_id` INT NOT NULL,
              `start_utc` DATETIME NOT NULL,
              `end_utc` DATETIME NOT NULL,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            CREATE TABLE IF NOT EXISTS `admin_audit_log` (
              `id` INT NOT NULL,
              `actor_username` VARCHAR(100) NOT NULL,
              `action` VARCHAR(60) NOT NULL,
              `entity_type` VARCHAR(80) NOT NULL,
              `entity_key` VARCHAR(120) NOT NULL,
              `summary` TEXT NOT NULL,
              `remote_ip` VARCHAR(64) NULL,
              `created_utc` DATETIME NOT NULL,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """
        };
    }

    private void SyncComputersFromLoginSessions(DbConnection connection)
    {
        if (!TableExists(connection, "login_sessions"))
        {
            return;
        }

        var activeSessions = new List<(string Username, string Machine, string IpAddress, DateTime LoginStamp)>();
        using (var command = CreateCommand(connection, $"SELECT username, machine, ipaddress, loginstamp FROM {Quote("login_sessions")} WHERE logoutstamp IS NULL"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                activeSessions.Add((
                    reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.GetDateTime(3)));
            }
        }

        foreach (var session in activeSessions.Where(item => !string.IsNullOrWhiteSpace(item.Machine)))
        {
            var existingId = FindComputerId(connection, session.Machine, session.IpAddress);
            if (existingId.HasValue)
            {
                using var update = CreateCommand(connection,
                    $"UPDATE {Quote("computers")} SET name = @name, ip_address = @ip, status = @status, current_username = @username, last_seen_utc = @lastSeen WHERE id = @id");
                AddParameter(update, "@id", existingId.Value);
                AddParameter(update, "@name", session.Machine);
                AddParameter(update, "@ip", string.IsNullOrWhiteSpace(session.IpAddress) ? DBNull.Value : session.IpAddress);
                AddParameter(update, "@status", ComputerStatus.InUse.ToString());
                AddParameter(update, "@username", string.IsNullOrWhiteSpace(session.Username) ? DBNull.Value : session.Username);
                AddParameter(update, "@lastSeen", session.LoginStamp.ToUniversalTime());
                update.ExecuteNonQuery();
            }
            else
            {
                using var insert = CreateCommand(connection,
                    $"INSERT INTO {Quote("computers")} (id, name, location, inventory_tag, ip_address, status, current_username, last_seen_utc) VALUES (@id, @name, @location, @inventory, @ip, @status, @username, @lastSeen)");
                AddParameter(insert, "@id", NextId(connection, "computers"));
                AddParameter(insert, "@name", session.Machine);
                AddParameter(insert, "@location", "Detectado por login_sessions");
                AddParameter(insert, "@inventory", $"AUTO-{session.Machine}");
                AddParameter(insert, "@ip", string.IsNullOrWhiteSpace(session.IpAddress) ? DBNull.Value : session.IpAddress);
                AddParameter(insert, "@status", ComputerStatus.InUse.ToString());
                AddParameter(insert, "@username", string.IsNullOrWhiteSpace(session.Username) ? DBNull.Value : session.Username);
                AddParameter(insert, "@lastSeen", session.LoginStamp.ToUniversalTime());
                insert.ExecuteNonQuery();
            }
        }

        using var clear = CreateCommand(connection,
            $"UPDATE {Quote("computers")} SET status = CASE WHEN status = @inUse THEN @available ELSE status END, current_username = CASE WHEN status = @inUse THEN NULL ELSE current_username END WHERE NOT EXISTS (SELECT 1 FROM {Quote("login_sessions")} ls WHERE ls.logoutstamp IS NULL AND (LOWER(ls.machine) = LOWER({Quote("computers")}.name) OR ({Quote("computers")}.ip_address IS NOT NULL AND ls.ipaddress = {Quote("computers")}.ip_address)))");
        AddParameter(clear, "@inUse", ComputerStatus.InUse.ToString());
        AddParameter(clear, "@available", ComputerStatus.Available.ToString());
        clear.ExecuteNonQuery();
    }

    private int? FindComputerId(DbConnection connection, string machine, string ipAddress)
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

    private static int ToStatus(bool active) => active ? 1 : 0;
}
