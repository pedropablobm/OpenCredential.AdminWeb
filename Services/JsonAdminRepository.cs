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

    public ReportsResponse GetReports(DateTime fromUtc, DateTime toUtc, int? careerId, int? semesterId, int? groupId, string? username, string? sessionOrigin, string? sessionState, string? operationalStatus)
    {
        lock (_sync)
        {
            var snapshot = Clone(_snapshot);
            var usersById = snapshot.Users.ToDictionary(item => item.Id);
            var careersById = snapshot.Careers.ToDictionary(item => item.Id);
            var semestersById = snapshot.Semesters.ToDictionary(item => item.Id);
            var computersById = snapshot.Computers.ToDictionary(item => item.Id);
            var roomByComputerId = snapshot.RoomLayoutItems
                .Where(item => item.ComputerId.HasValue)
                .Join(snapshot.Rooms, item => item.RoomId, room => room.Id, (item, room) => new { item.ComputerId, room.Name })
                .GroupBy(item => item.ComputerId!.Value)
                .ToDictionary(group => group.Key, group => group.First().Name);

            var rows = snapshot.UsageRecords
                .Where(item => item.StartUtc <= toUtc && item.EndUtc >= fromUtc)
                .Select(item =>
                {
                    usersById.TryGetValue(item.UserId, out var user);
                    computersById.TryGetValue(item.ComputerId, out var computer);
                    var groups = user?.Groups.Select(group => group.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
                    return new ReportSessionRow
                    {
                        SessionId = item.Id,
                        Username = user?.Username ?? $"user-{item.UserId}",
                        FullName = user is null ? string.Empty : $"{user.FirstName} {user.LastName}".Trim(),
                        DocumentId = user?.DocumentId ?? string.Empty,
                        CareerName = user?.CareerId is { } careerIdValue && careersById.TryGetValue(careerIdValue, out var career) ? career.Name : null,
                        SemesterName = user?.SemesterId is { } semesterIdValue && semestersById.TryGetValue(semesterIdValue, out var semester) ? semester.Name : null,
                        Groups = groups,
                        Machine = computer?.Name ?? $"equipo-{item.ComputerId}",
                        RoomName = computersById.TryGetValue(item.ComputerId, out var mappedComputer) && roomByComputerId.TryGetValue(item.ComputerId, out var roomName) ? roomName : mappedComputer?.Location,
                        InventoryTag = computer?.InventoryTag,
                        IpAddress = computer?.IpAddress,
                        SessionState = "ended",
                        SessionOrigin = "online",
                        OperationalStatus = OperationalComputerStatus.Available.ToString(),
                        OperationalStatusLabel = RepositorySupport.TranslateOperationalStatus(OperationalComputerStatus.Available),
                        LoginStamp = item.StartUtc,
                        LogoutStamp = item.EndUtc,
                        DurationHours = Math.Round(Math.Max(0, (item.EndUtc - item.StartUtc).TotalHours), 2),
                        IsRecoveredOffline = false,
                        IsOrphaned = false
                    };
                })
                .Where(item => !careerId.HasValue || string.Equals(item.CareerName, snapshot.Careers.FirstOrDefault(c => c.Id == careerId.Value)?.Name, StringComparison.OrdinalIgnoreCase))
                .Where(item => !semesterId.HasValue || string.Equals(item.SemesterName, snapshot.Semesters.FirstOrDefault(s => s.Id == semesterId.Value)?.Name, StringComparison.OrdinalIgnoreCase))
                .Where(item => !groupId.HasValue || item.Groups.Any(group => string.Equals(group, snapshot.Groups.FirstOrDefault(entry => entry.Id == groupId.Value)?.Name, StringComparison.OrdinalIgnoreCase)))
                .Where(item => string.IsNullOrWhiteSpace(username) || item.Username.Contains(username.Trim(), StringComparison.OrdinalIgnoreCase) || item.FullName.Contains(username.Trim(), StringComparison.OrdinalIgnoreCase))
                .Where(item => string.IsNullOrWhiteSpace(sessionOrigin) || string.Equals(item.SessionOrigin, sessionOrigin, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.IsNullOrWhiteSpace(sessionState) || string.Equals(item.SessionState, sessionState, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.IsNullOrWhiteSpace(operationalStatus) || string.Equals(item.OperationalStatus, operationalStatus, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return RepositorySupport.BuildReportsResponse(rows);
        }
    }

    public List<GroupInfo> GetGroups()
    {
        lock (_sync)
        {
            return _snapshot.Groups
                .Select(item => new GroupInfo { Id = item.Id, Name = item.Name })
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public UserAccount? FindUserByUsername(string username)
    {
        lock (_sync)
        {
            var user = _snapshot.Users.FirstOrDefault(item =>
                string.Equals(item.Username, username?.Trim(), StringComparison.OrdinalIgnoreCase));

            return user is null ? null : Clone(_snapshot).Users.FirstOrDefault(item => item.Id == user.Id);
        }
    }

    public void RegisterFailedSignIn(string username, int maxFailedAttempts, int lockoutMinutes)
    {
        lock (_sync)
        {
            var user = _snapshot.Users.FirstOrDefault(item =>
                string.Equals(item.Username, username?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                return;
            }

            user.FailedAttempts = Math.Max(0, user.FailedAttempts) + 1;
            user.LastAttemptAtUtc = DateTime.UtcNow;
            if (user.FailedAttempts >= Math.Max(1, maxFailedAttempts))
            {
                user.LockedUntilUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, lockoutMinutes));
            }

            SaveSnapshot();
        }
    }

    public void ResetFailedSignIn(string username)
    {
        lock (_sync)
        {
            var user = _snapshot.Users.FirstOrDefault(item =>
                string.Equals(item.Username, username?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                return;
            }

            user.FailedAttempts = 0;
            user.LockedUntilUtc = null;
            SaveSnapshot();
        }
    }

    public PortalProfile? GetPortalProfile(string username)
    {
        lock (_sync)
        {
            var snapshot = Clone(_snapshot);
            var user = snapshot.Users.FirstOrDefault(item =>
                string.Equals(item.Username, username?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                return null;
            }

            var careerName = snapshot.Careers.FirstOrDefault(item => item.Id == user.CareerId)?.Name;
            var semesterName = snapshot.Semesters.FirstOrDefault(item => item.Id == user.SemesterId)?.Name;
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
    }

    public PortalProfile? UpdatePortalProfile(string username, PortalProfileUpdateInput input)
    {
        lock (_sync)
        {
            var user = _snapshot.Users.FirstOrDefault(item =>
                string.Equals(item.Username, username?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                return null;
            }

            user.FirstName = input.FirstName.Trim();
            user.LastName = input.LastName.Trim();
            user.Email = input.Email.Trim();
            SaveSnapshot();

            return GetPortalProfile(user.Username);
        }
    }

    public PasswordResetResult? UpdatePasswordByUsername(string username, string plainPassword, string hashMethod)
    {
        lock (_sync)
        {
            var user = _snapshot.Users.FirstOrDefault(item =>
                string.Equals(item.Username, username?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                return null;
            }

            var method = PasswordHashService.NormalizeInteractiveMethod(hashMethod);
            user.HashMethod = method;
            user.PasswordHash = PasswordHashService.HashPassword(plainPassword.Trim(), method);
            user.FailedAttempts = 0;
            user.LockedUntilUtc = null;
            SaveSnapshot();

            return new PasswordResetResult
            {
                UserId = user.Id,
                Username = user.Username,
                HashMethod = method,
                GeneratedPassword = plainPassword.Trim()
            };
        }
    }

    public PortalPasswordRecoveryResult RecoverPortalPassword(PortalPasswordRecoveryInput input, int tokenLifetimeMinutes)
    {
        lock (_sync)
        {
            CleanupPortalResetTokens();
            var user = _snapshot.Users.FirstOrDefault(item =>
                string.Equals(item.Username, input.Username?.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.DocumentId, input.DocumentId?.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Email, input.Email?.Trim(), StringComparison.OrdinalIgnoreCase)
                && item.Active);

            if (user is null)
            {
                return new PortalPasswordRecoveryResult
                {
                    Success = false,
                    Message = "No fue posible validar los datos de recuperacion."
                };
            }

            var token = PasswordHashService.GenerateOpaqueToken();
            var tokenHash = PasswordHashService.HashOpaqueToken(token);
            var expiresAtUtc = DateTime.UtcNow.AddMinutes(Math.Max(5, tokenLifetimeMinutes));
            var nextId = RepositorySupport.NextId(_snapshot.PortalPasswordResetTokens.Select(item => item.Id));
            _snapshot.PortalPasswordResetTokens.RemoveAll(item =>
                item.UserId == user.Id
                || string.Equals(item.Username, user.Username, StringComparison.OrdinalIgnoreCase));
            _snapshot.PortalPasswordResetTokens.Add(new PortalPasswordResetTokenRecord
            {
                Id = nextId,
                UserId = user.Id,
                Username = user.Username,
                Email = user.Email,
                Token = tokenHash,
                CreatedUtc = DateTime.UtcNow,
                ExpiresAtUtc = expiresAtUtc
            });
            SaveSnapshot();

            return new PortalPasswordRecoveryResult
            {
                Success = true,
                Message = "Se genero un token temporal de recuperacion. Usalo para definir una nueva clave.",
                ResetToken = token,
                ExpiresAtUtc = expiresAtUtc,
                DeliveryHint = $"Token visible solo para pruebas internas. Debe enviarse al correo {user.Email} en una integracion posterior."
            };
        }
    }

    public bool ResetPortalPasswordWithToken(PortalPasswordResetWithTokenInput input, out string message)
    {
        lock (_sync)
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

            var tokenRecord = _snapshot.PortalPasswordResetTokens
                .OrderByDescending(item => item.CreatedUtc)
                .FirstOrDefault(item =>
                    string.Equals(item.Token, PasswordHashService.HashOpaqueToken(input.Token.Trim()), StringComparison.OrdinalIgnoreCase)
                    && item.ConsumedUtc is null);

            if (tokenRecord is null || tokenRecord.ExpiresAtUtc < DateTime.UtcNow)
            {
                message = "El token no existe, ya fue usado o expiro.";
                return false;
            }

            var user = _snapshot.Users.FirstOrDefault(item =>
                item.Id == tokenRecord.UserId
                || string.Equals(item.Username, tokenRecord.Username, StringComparison.OrdinalIgnoreCase));
            if (user is null || !user.Active)
            {
                message = "El usuario asociado al token no esta disponible.";
                return false;
            }

            var method = PasswordHashService.NormalizeInteractiveMethod(input.HashMethod);
            user.HashMethod = method;
            user.PasswordHash = PasswordHashService.HashPassword(input.NewPassword.Trim(), method);
            user.FailedAttempts = 0;
            user.LockedUntilUtc = null;
            tokenRecord.ConsumedUtc = DateTime.UtcNow;
            SaveSnapshot();
            message = "La clave fue restablecida correctamente.";
            return true;
        }
    }

    private void CleanupPortalResetTokens()
    {
        _snapshot.PortalPasswordResetTokens.RemoveAll(item =>
            item.ConsumedUtc.HasValue
            || item.ExpiresAtUtc < DateTime.UtcNow);
    }

    public List<PortalSessionEntry> GetPortalSessions(string username, int take)
    {
        lock (_sync)
        {
            var snapshot = Clone(_snapshot);
            var user = snapshot.Users.FirstOrDefault(item =>
                string.Equals(item.Username, username?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                return [];
            }

            var computersById = snapshot.Computers.ToDictionary(item => item.Id);
            var roomByComputerId = snapshot.RoomLayoutItems
                .Where(item => item.ComputerId.HasValue)
                .Join(snapshot.Rooms, item => item.RoomId, room => room.Id, (item, room) => new { item.ComputerId, room.Name })
                .GroupBy(item => item.ComputerId!.Value)
                .ToDictionary(group => group.Key, group => group.First().Name);

            return snapshot.UsageRecords
                .Where(item => item.UserId == user.Id)
                .OrderByDescending(item => item.StartUtc)
                .Take(Math.Max(1, take))
                .Select(item =>
                {
                    computersById.TryGetValue(item.ComputerId, out var computer);
                    roomByComputerId.TryGetValue(item.ComputerId, out var roomName);
                    return new PortalSessionEntry
                    {
                        SessionId = item.Id,
                        Machine = computer?.Name ?? $"equipo-{item.ComputerId}",
                        RoomName = roomName ?? computer?.Location,
                        InventoryTag = computer?.InventoryTag,
                        SessionState = "ended",
                        SessionStateLabel = "Finalizada",
                        SessionOrigin = "online",
                        OriginLabel = RepositorySupport.TranslateSessionOrigin("online"),
                        OperationalStatus = OperationalComputerStatus.Available.ToString(),
                        OperationalStatusLabel = RepositorySupport.TranslateOperationalStatus(OperationalComputerStatus.Available),
                        LoginStamp = item.StartUtc,
                        LogoutStamp = item.EndUtc,
                        DurationHours = Math.Round(Math.Max(0, (item.EndUtc - item.StartUtc).TotalHours), 2)
                    };
                })
                .ToList();
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
            foreach (var item in _snapshot.RoomLayoutItems.Where(layoutItem => layoutItem.ComputerId == id))
            {
                item.ComputerId = null;
            }
            SaveSnapshot();
            return true;
        }
    }

    public Room CreateRoom(RoomInput input)
    {
        lock (_sync)
        {
            var room = new Room
            {
                Id = RepositorySupport.NextId(_snapshot.Rooms.Select(item => item.Id)),
                Name = input.Name.Trim(),
                Code = input.Code.Trim(),
                CanvasWidth = Math.Max(640, input.CanvasWidth),
                CanvasHeight = Math.Max(360, input.CanvasHeight),
                Active = input.Active
            };

            _snapshot.Rooms.Add(room);
            SaveSnapshot();
            return room;
        }
    }

    public Room? UpdateRoom(int id, RoomInput input)
    {
        lock (_sync)
        {
            var room = _snapshot.Rooms.FirstOrDefault(item => item.Id == id);
            if (room is null) return null;
            room.Name = input.Name.Trim();
            room.Code = input.Code.Trim();
            room.CanvasWidth = Math.Max(640, input.CanvasWidth);
            room.CanvasHeight = Math.Max(360, input.CanvasHeight);
            room.Active = input.Active;
            SaveSnapshot();
            return room;
        }
    }

    public bool DeleteRoom(int id)
    {
        lock (_sync)
        {
            var removed = _snapshot.Rooms.RemoveAll(item => item.Id == id) > 0;
            if (!removed) return false;
            _snapshot.RoomLayoutItems.RemoveAll(item => item.RoomId == id);
            SaveSnapshot();
            return true;
        }
    }

    public List<RoomLayoutItem> SaveRoomLayout(int roomId, RoomLayoutInput input)
    {
        lock (_sync)
        {
            var room = _snapshot.Rooms.FirstOrDefault(item => item.Id == roomId);
            if (room is null)
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
                var duplicateNames = _snapshot.Computers
                    .Where(computer => duplicateComputerIds.Contains(computer.Id))
                    .Select(computer => computer.Name)
                    .OrderBy(name => name)
                    .ToList();
                throw new InvalidOperationException($"Cada equipo solo puede estar una vez en el mapa visual. Duplicados detectados: {string.Join(", ", duplicateNames)}.");
            }

            room.CanvasWidth = Math.Max(640, input.CanvasWidth);
            room.CanvasHeight = Math.Max(360, input.CanvasHeight);
            _snapshot.RoomLayoutItems.RemoveAll(item => item.RoomId == roomId);

            var nextId = RepositorySupport.NextId(_snapshot.RoomLayoutItems.Select(item => item.Id));
            foreach (var layoutItem in input.Items)
            {
                _snapshot.RoomLayoutItems.Add(new RoomLayoutItem
                {
                    Id = nextId++,
                    RoomId = roomId,
                    Label = layoutItem.Label.Trim(),
                    ItemType = ParseRoomLayoutItemType(layoutItem.ItemType),
                    X = Math.Max(0, layoutItem.X),
                    Y = Math.Max(0, layoutItem.Y),
                    Width = Math.Max(40, layoutItem.Width),
                    Height = Math.Max(40, layoutItem.Height),
                    Orientation = NormalizeOrientation(layoutItem.Orientation),
                    Capacity = NormalizeCapacity(layoutItem.Capacity),
                    ComputerId = layoutItem.ComputerId
                });
            }

            SaveSnapshot();
            return _snapshot.RoomLayoutItems
                .Where(item => item.RoomId == roomId)
                .Select(item => new RoomLayoutItem
                {
                    Id = item.Id,
                    RoomId = item.RoomId,
                    Label = item.Label,
                    ItemType = item.ItemType,
                    X = item.X,
                    Y = item.Y,
                    Width = item.Width,
                    Height = item.Height,
                    Orientation = item.Orientation,
                    Capacity = item.Capacity,
                    ComputerId = item.ComputerId
                })
                .ToList();
        }
    }

    public UserAccount CreateUser(UserInput input)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(input.Password))
            {
                throw new InvalidOperationException("Debes definir una clave inicial segura al crear un usuario. No se permite usar el documento como clave por defecto.");
            }

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
                HashMethod = PasswordHashService.NormalizeInteractiveMethod(input.HashMethod),
                PasswordHash = PasswordHashService.HashPassword(input.Password.Trim(), PasswordHashService.NormalizeInteractiveMethod(input.HashMethod)),
                Groups = ResolveGroups(input.GroupIds)
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
            user.HashMethod = PasswordHashService.NormalizeInteractiveMethod(input.HashMethod);
            user.Groups = ResolveGroups(input.GroupIds);
            if (!string.IsNullOrWhiteSpace(input.Password))
            {
                user.PasswordHash = PasswordHashService.HashPassword(input.Password.Trim(), PasswordHashService.NormalizeInteractiveMethod(input.HashMethod));
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

            var method = PasswordHashService.NormalizeInteractiveMethod(input.HashMethod);
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
                        PasswordHash = PasswordHashService.HashPassword(PasswordHashService.GeneratePassword(), "BCRYPT"),
                        FailedAttempts = 0
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
                        existing.PasswordHash = PasswordHashService.HashPassword(PasswordHashService.GeneratePassword(), existing.HashMethod);
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
                    Groups = snapshot.Groups ?? new List<GroupInfo>(),
                    Users = snapshot.Users ?? new List<UserAccount>(),
                    Computers = snapshot.Computers ?? new List<Computer>(),
                    Rooms = (snapshot.Rooms ?? new List<Room>()).Select(item => new Room
                    {
                        Id = item.Id,
                        Name = item.Name,
                        Code = item.Code,
                        CanvasWidth = item.CanvasWidth > 0 ? item.CanvasWidth : 1200,
                        CanvasHeight = item.CanvasHeight > 0 ? item.CanvasHeight : 720,
                        Active = item.Active
                    }).ToList(),
                    RoomLayoutItems = snapshot.RoomLayoutItems ?? new List<RoomLayoutItem>(),
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
            Groups = snapshot.Groups.Select(item => new GroupInfo { Id = item.Id, Name = item.Name }).ToList(),
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
                Groups = item.Groups.Select(group => new GroupInfo { Id = group.Id, Name = group.Name }).ToList(),
                FailedAttempts = item.FailedAttempts,
                LockedUntilUtc = item.LockedUntilUtc,
                LastAttemptAtUtc = item.LastAttemptAtUtc,
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
            Rooms = snapshot.Rooms.Select(item => new Room
            {
                Id = item.Id,
                Name = item.Name,
                Code = item.Code,
                CanvasWidth = item.CanvasWidth,
                CanvasHeight = item.CanvasHeight,
                Active = item.Active
            }).ToList(),
            RoomLayoutItems = snapshot.RoomLayoutItems.Select(item => new RoomLayoutItem
            {
                Id = item.Id,
                RoomId = item.RoomId,
                Label = item.Label,
                ItemType = item.ItemType,
                X = item.X,
                Y = item.Y,
                Width = item.Width,
                Height = item.Height,
                Orientation = item.Orientation,
                Capacity = item.Capacity,
                ComputerId = item.ComputerId
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

    private List<GroupInfo> ResolveGroups(IEnumerable<int> groupIds)
    {
        var selected = new HashSet<int>(groupIds);
        return _snapshot.Groups
            .Where(group => selected.Contains(group.Id))
            .Select(group => new GroupInfo { Id = group.Id, Name = group.Name })
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
}
