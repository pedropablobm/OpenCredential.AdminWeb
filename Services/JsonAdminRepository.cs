using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace OpenCredential.AdminWeb.Services;

public sealed class JsonAdminRepository : IAdminRepository
{
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _dataPath;
    private AdminSnapshot _snapshot;

    public JsonAdminRepository(IWebHostEnvironment environment)
    {
        var dataDirectory = RepositorySupport.ResolveDataDirectory(environment);
        _dataPath = Path.Combine(dataDirectory, "admin-store.json");
        _serializerOptions.Converters.Add(new JsonStringEnumConverter());
        _snapshot = LoadSnapshot();
    }

    public AdminSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return Clone(_snapshot);
        }
    }

    public DashboardResponse GetDashboard(int rangeDays, int? careerId, int? semesterId, string? status)
    {
        lock (_sync)
        {
            return RepositorySupport.BuildDashboard(Clone(_snapshot), rangeDays, careerId, semesterId, status);
        }
    }

    public List<AuditEntry> GetAuditEntries(int take)
    {
        lock (_sync)
        {
            return _snapshot.AuditEntries
                .OrderByDescending(item => item.CreatedUtc)
                .Take(Math.Max(1, take))
                .Select(CloneAuditEntry)
                .ToList();
        }
    }

    public AuditEntry RecordAudit(AuditEntryInput input)
    {
        lock (_sync)
        {
            var entry = new AuditEntry
            {
                Id = RepositorySupport.NextId(_snapshot.AuditEntries.Select(item => item.Id)),
                ActorUsername = input.ActorUsername.Trim(),
                Action = input.Action.Trim(),
                EntityType = input.EntityType.Trim(),
                EntityKey = input.EntityKey.Trim(),
                Summary = input.Summary.Trim(),
                RemoteIp = RepositorySupport.CleanOptional(input.RemoteIp),
                CreatedUtc = DateTime.UtcNow
            };

            _snapshot.AuditEntries.Add(entry);
            SaveSnapshot();
            return CloneAuditEntry(entry);
        }
    }

    public Career CreateCareer(CareerInput input)
    {
        lock (_sync)
        {
            var career = new Career
            {
                Id = RepositorySupport.NextId(_snapshot.Careers.Select(item => item.Id)),
                Name = input.Name.Trim(),
                Active = input.Active
            };

            _snapshot.Careers.Add(career);
            SaveSnapshot();
            return career;
        }
    }

    public Career? UpdateCareer(int id, CareerInput input)
    {
        lock (_sync)
        {
            var career = _snapshot.Careers.FirstOrDefault(item => item.Id == id);
            if (career is null) return null;
            career.Name = input.Name.Trim();
            career.Active = input.Active;
            SaveSnapshot();
            return career;
        }
    }

    public bool DeleteCareer(int id)
    {
        lock (_sync)
        {
            var removed = _snapshot.Careers.RemoveAll(item => item.Id == id) > 0;
            if (!removed) return false;
            foreach (var user in _snapshot.Users.Where(user => user.CareerId == id))
            {
                user.CareerId = null;
            }

            SaveSnapshot();
            return true;
        }
    }

    public Semester CreateSemester(SemesterInput input)
    {
        lock (_sync)
        {
            var semester = new Semester
            {
                Id = RepositorySupport.NextId(_snapshot.Semesters.Select(item => item.Id)),
                Name = input.Name.Trim(),
                Active = input.Active
            };

            _snapshot.Semesters.Add(semester);
            SaveSnapshot();
            return semester;
        }
    }

    public Semester? UpdateSemester(int id, SemesterInput input)
    {
        lock (_sync)
        {
            var semester = _snapshot.Semesters.FirstOrDefault(item => item.Id == id);
            if (semester is null) return null;
            semester.Name = input.Name.Trim();
            semester.Active = input.Active;
            SaveSnapshot();
            return semester;
        }
    }

    public bool DeleteSemester(int id)
    {
        lock (_sync)
        {
            var removed = _snapshot.Semesters.RemoveAll(item => item.Id == id) > 0;
            if (!removed) return false;
            foreach (var user in _snapshot.Users.Where(user => user.SemesterId == id))
            {
                user.SemesterId = null;
            }

            SaveSnapshot();
            return true;
        }
    }

    public Computer CreateComputer(ComputerInput input)
    {
        lock (_sync)
        {
            var computer = new Computer
            {
                Id = RepositorySupport.NextId(_snapshot.Computers.Select(item => item.Id)),
                Name = input.Name.Trim(),
                Location = input.Location.Trim(),
                InventoryTag = input.InventoryTag.Trim(),
                IpAddress = RepositorySupport.CleanOptional(input.IpAddress),
                Status = RepositorySupport.ParseStatus(input.Status),
                CurrentUsername = RepositorySupport.CleanOptional(input.CurrentUsername),
                LastSeenUtc = DateTime.UtcNow
            };

            _snapshot.Computers.Add(computer);
            SaveSnapshot();
            return computer;
        }
    }

    public Computer? UpdateComputer(int id, ComputerInput input)
    {
        lock (_sync)
        {
            var computer = _snapshot.Computers.FirstOrDefault(item => item.Id == id);
            if (computer is null) return null;

            computer.Name = input.Name.Trim();
            computer.Location = input.Location.Trim();
            computer.InventoryTag = input.InventoryTag.Trim();
            computer.IpAddress = RepositorySupport.CleanOptional(input.IpAddress);
            computer.Status = RepositorySupport.ParseStatus(input.Status);
            computer.CurrentUsername = RepositorySupport.CleanOptional(input.CurrentUsername);
            computer.LastSeenUtc = DateTime.UtcNow;
            SaveSnapshot();
            return computer;
        }
    }

    public bool DeleteComputer(int id)
    {
        lock (_sync)
        {
            var removed = _snapshot.Computers.RemoveAll(item => item.Id == id) > 0;
            if (!removed) return false;
            _snapshot.UsageRecords.RemoveAll(item => item.ComputerId == id);
            SaveSnapshot();
            return true;
        }
    }

    public UserAccount CreateUser(UserInput input)
    {
        lock (_sync)
        {
            var user = new UserAccount
            {
                Id = RepositorySupport.NextId(_snapshot.Users.Select(item => item.Id)),
                Username = input.Username.Trim(),
                FirstName = input.FirstName.Trim(),
                LastName = input.LastName.Trim(),
                Email = input.Email.Trim(),
                DocumentId = input.DocumentId.Trim(),
                CareerId = input.CareerId,
                SemesterId = input.SemesterId,
                Active = input.Active,
                HashMethod = PasswordHashService.NormalizeMethod(input.HashMethod),
                PasswordHash = PasswordHashService.HashPassword(input.Password ?? string.Empty, input.HashMethod)
            };

            _snapshot.Users.Add(user);
            SaveSnapshot();
            return user;
        }
    }

    public UserAccount? UpdateUser(int id, UserInput input)
    {
        lock (_sync)
        {
            var user = _snapshot.Users.FirstOrDefault(item => item.Id == id);
            if (user is null) return null;
            user.Username = input.Username.Trim();
            user.FirstName = input.FirstName.Trim();
            user.LastName = input.LastName.Trim();
            user.Email = input.Email.Trim();
            user.DocumentId = input.DocumentId.Trim();
            user.CareerId = input.CareerId;
            user.SemesterId = input.SemesterId;
            user.Active = input.Active;
            user.HashMethod = PasswordHashService.NormalizeMethod(input.HashMethod);
            if (!string.IsNullOrWhiteSpace(input.Password))
            {
                user.PasswordHash = PasswordHashService.HashPassword(input.Password, input.HashMethod);
            }
            SaveSnapshot();
            return user;
        }
    }

    public bool DeleteUser(int id)
    {
        lock (_sync)
        {
            var removed = _snapshot.Users.RemoveAll(item => item.Id == id) > 0;
            if (!removed) return false;
            _snapshot.UsageRecords.RemoveAll(item => item.UserId == id);
            SaveSnapshot();
            return true;
        }
    }

    public PasswordResetResult? ResetUserPassword(int id, PasswordResetInput input)
    {
        lock (_sync)
        {
            var user = _snapshot.Users.FirstOrDefault(item => item.Id == id);
            if (user is null) return null;

            var method = PasswordHashService.NormalizeMethod(input.HashMethod);
            var plainPassword = input.Generate || string.IsNullOrWhiteSpace(input.Password)
                ? PasswordHashService.GeneratePassword()
                : input.Password.Trim();

            user.HashMethod = method;
            user.PasswordHash = PasswordHashService.HashPassword(plainPassword, method);
            SaveSnapshot();

            return new PasswordResetResult
            {
                UserId = user.Id,
                Username = user.Username,
                HashMethod = method,
                GeneratedPassword = plainPassword
            };
        }
    }

    public UsageRecord CreateUsageRecord(UsageRecordInput input)
    {
        lock (_sync)
        {
            var record = new UsageRecord
            {
                Id = RepositorySupport.NextId(_snapshot.UsageRecords.Select(item => item.Id)),
                UserId = input.UserId,
                ComputerId = input.ComputerId,
                StartUtc = input.StartUtc,
                EndUtc = input.EndUtc
            };

            _snapshot.UsageRecords.Add(record);
            SaveSnapshot();
            return record;
        }
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
        var warnings = new List<string>();
        var imported = 0;
        var updated = 0;

        lock (_sync)
        {
            var header = RepositorySupport.SplitLine(lines[0], delimiter);
            var map = header
                .Select((value, index) => new { Key = RepositorySupport.NormalizeHeader(value), Index = index })
                .ToDictionary(item => item.Key, item => item.Index);

            for (var i = 1; i < lines.Count; i++)
            {
                var values = RepositorySupport.SplitLine(lines[i], delimiter);
                if (values.Count == 0) continue;

                var username = RepositorySupport.GetValue(values, map, "username");
                if (string.IsNullOrWhiteSpace(username))
                {
                    warnings.Add($"Fila {i + 1}: username vacio, se omite.");
                    continue;
                }

                var firstName = RepositorySupport.GetValue(values, map, "firstname", "nombres", "nombre");
                var lastName = RepositorySupport.GetValue(values, map, "lastname", "apellidos", "apellido");
                var email = RepositorySupport.GetValue(values, map, "email", "correo");
                var documentId = RepositorySupport.GetValue(values, map, "documentid", "documento", "cedula");
                var careerName = RepositorySupport.GetValue(values, map, "career", "carrera");
                var semesterName = RepositorySupport.GetValue(values, map, "semester", "semestre", "level");
                var active = RepositorySupport.ParseBoolean(RepositorySupport.GetValue(values, map, "active", "estado", "status"), true);

                var existing = _snapshot.Users.FirstOrDefault(user => user.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                var careerId = EnsureCareer(careerName);
                var semesterId = EnsureSemester(semesterName);

                if (existing is null)
                {
                    _snapshot.Users.Add(new UserAccount
                    {
                        Id = RepositorySupport.NextId(_snapshot.Users.Select(item => item.Id)),
                        Username = username.Trim(),
                        FirstName = firstName,
                        LastName = lastName,
                        Email = email,
                        DocumentId = documentId,
                        CareerId = careerId,
                        SemesterId = semesterId,
                        Active = active,
                        HashMethod = "BCRYPT",
                        PasswordHash = PasswordHashService.HashPassword(documentId, "BCRYPT")
                    });
                    imported++;
                }
                else
                {
                    existing.FirstName = firstName;
                    existing.LastName = lastName;
                    existing.Email = email;
                    existing.DocumentId = documentId;
                    existing.CareerId = careerId;
                    existing.SemesterId = semesterId;
                    existing.Active = active;
                    existing.HashMethod = existing.HashMethod ?? "BCRYPT";
                    if (string.IsNullOrWhiteSpace(existing.PasswordHash))
                    {
                        existing.PasswordHash = PasswordHashService.HashPassword(documentId, existing.HashMethod);
                    }
                    updated++;
                }
            }

            SaveSnapshot();
        }

        return new ImportUsersResult { Imported = imported, Updated = updated, Warnings = warnings };
    }

    private AdminSnapshot LoadSnapshot()
    {
        if (File.Exists(_dataPath))
        {
            var json = File.ReadAllText(_dataPath);
            var snapshot = JsonSerializer.Deserialize<AdminSnapshot>(json, _serializerOptions);
            if (snapshot is not null)
            {
                return new AdminSnapshot
                {
                    Careers = snapshot.Careers ?? new List<Career>(),
                    Semesters = snapshot.Semesters ?? new List<Semester>(),
                    Users = snapshot.Users ?? new List<UserAccount>(),
                    Computers = snapshot.Computers ?? new List<Computer>(),
                    UsageRecords = snapshot.UsageRecords ?? new List<UsageRecord>(),
                    AuditEntries = snapshot.AuditEntries ?? new List<AuditEntry>()
                };
            }
        }

        var seeded = RepositorySupport.CreateSeedSnapshot();
        File.WriteAllText(_dataPath, JsonSerializer.Serialize(seeded, _serializerOptions));
        return seeded;
    }

    private void SaveSnapshot()
    {
        File.WriteAllText(_dataPath, JsonSerializer.Serialize(_snapshot, _serializerOptions));
    }

    private static AdminSnapshot Clone(AdminSnapshot snapshot)
    {
        return new AdminSnapshot
        {
            Careers = snapshot.Careers.Select(item => new Career { Id = item.Id, Name = item.Name, Active = item.Active }).ToList(),
            Semesters = snapshot.Semesters.Select(item => new Semester { Id = item.Id, Name = item.Name, Active = item.Active }).ToList(),
            Users = snapshot.Users.Select(item => new UserAccount
            {
                Id = item.Id,
                Username = item.Username,
                FirstName = item.FirstName,
                LastName = item.LastName,
                Email = item.Email,
                DocumentId = item.DocumentId,
                CareerId = item.CareerId,
                SemesterId = item.SemesterId,
                Active = item.Active,
                HashMethod = item.HashMethod,
                PasswordHash = item.PasswordHash
            }).ToList(),
            Computers = snapshot.Computers.Select(item => new Computer
            {
                Id = item.Id,
                Name = item.Name,
                Location = item.Location,
                InventoryTag = item.InventoryTag,
                IpAddress = item.IpAddress,
                Status = item.Status,
                CurrentUsername = item.CurrentUsername,
                LastSeenUtc = item.LastSeenUtc
            }).ToList(),
            UsageRecords = snapshot.UsageRecords.Select(item => new UsageRecord
            {
                Id = item.Id,
                UserId = item.UserId,
                ComputerId = item.ComputerId,
                StartUtc = item.StartUtc,
                EndUtc = item.EndUtc
            }).ToList(),
            AuditEntries = snapshot.AuditEntries.Select(CloneAuditEntry).ToList()
        };
    }

    private static AuditEntry CloneAuditEntry(AuditEntry item)
    {
        return new AuditEntry
        {
            Id = item.Id,
            ActorUsername = item.ActorUsername,
            Action = item.Action,
            EntityType = item.EntityType,
            EntityKey = item.EntityKey,
            Summary = item.Summary,
            RemoteIp = item.RemoteIp,
            CreatedUtc = item.CreatedUtc
        };
    }

    private int? EnsureCareer(string? careerName)
    {
        if (string.IsNullOrWhiteSpace(careerName)) return null;
        var normalized = careerName.Trim();
        var existing = _snapshot.Careers.FirstOrDefault(item => item.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing.Id;
        var career = new Career { Id = RepositorySupport.NextId(_snapshot.Careers.Select(item => item.Id)), Name = normalized, Active = true };
        _snapshot.Careers.Add(career);
        return career.Id;
    }

    private int? EnsureSemester(string? semesterName)
    {
        if (string.IsNullOrWhiteSpace(semesterName)) return null;
        var normalized = semesterName.Trim();
        var existing = _snapshot.Semesters.FirstOrDefault(item => item.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing.Id;
        var semester = new Semester { Id = RepositorySupport.NextId(_snapshot.Semesters.Select(item => item.Id)), Name = normalized, Active = true };
        _snapshot.Semesters.Add(semester);
        return semester.Id;
    }
}
