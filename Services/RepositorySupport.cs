using System.Globalization;

namespace OpenCredential.AdminWeb.Services;

internal static class RepositorySupport
{
    public static string ResolveDataDirectory(IWebHostEnvironment environment)
    {
        var configuredDataDirectory = Environment.GetEnvironmentVariable("ADMINWEB_DATA_DIR");
        var dataDirectory = string.IsNullOrWhiteSpace(configuredDataDirectory)
            ? Path.Combine(environment.ContentRootPath, "App_Data")
            : Path.GetFullPath(NormalizeContainerDataDirectory(configuredDataDirectory));

        Directory.CreateDirectory(dataDirectory);
        return dataDirectory;
    }

    private static string NormalizeContainerDataDirectory(string configuredDataDirectory)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return configuredDataDirectory;
        }

        const string gitBashPrefix = "C:/Program Files/Git/";
        if (configuredDataDirectory.StartsWith(gitBashPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "/" + configuredDataDirectory[gitBashPrefix.Length..].TrimStart('/');
        }

        return configuredDataDirectory;
    }

    public static DashboardResponse BuildDashboard(AdminSnapshot snapshot, int rangeDays, int? careerId, int? semesterId, string? status)
    {
        var untilUtc = DateTime.UtcNow;
        var fromUtc = untilUtc.AddDays(-Math.Max(1, rangeDays));
        var computedComputers = snapshot.ComputedComputers.Count > 0
            ? snapshot.ComputedComputers
            : snapshot.Computers.Select(CreateFallbackComputedState).ToList();

        var filteredUsers = snapshot.Users
            .Where(user => !careerId.HasValue || user.CareerId == careerId)
            .Where(user => !semesterId.HasValue || user.SemesterId == semesterId)
            .ToDictionary(user => user.Id);

        var computerCards = computedComputers
            .Where(computer =>
                string.IsNullOrWhiteSpace(status) ||
                computer.OperationalStatus.ToString().Equals(status, StringComparison.OrdinalIgnoreCase) ||
                computer.AdministrativeStatus.ToString().Equals(status, StringComparison.OrdinalIgnoreCase))
            .Select(ToComputerCard)
            .OrderBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var filteredUsage = snapshot.UsageRecords
            .Where(record => record.StartUtc >= fromUtc && record.StartUtc <= untilUtc)
            .Where(record => filteredUsers.ContainsKey(record.UserId))
            .ToList();

        return new DashboardResponse
        {
            Kpis = new DashboardKpis
            {
                TotalUsers = snapshot.Users.Count,
                ActiveUsers = snapshot.Users.Count(user => user.Active),
                AvailableComputers = computedComputers.Count(computer => computer.OperationalStatus == OperationalComputerStatus.Available),
                InUseComputers = computedComputers.Count(computer =>
                    computer.OperationalStatus is OperationalComputerStatus.Occupied or OperationalComputerStatus.Locked or OperationalComputerStatus.Disconnected),
                OccupiedComputers = computedComputers.Count(computer => computer.OperationalStatus == OperationalComputerStatus.Occupied),
                LockedComputers = computedComputers.Count(computer => computer.OperationalStatus == OperationalComputerStatus.Locked),
                DisconnectedComputers = computedComputers.Count(computer => computer.OperationalStatus == OperationalComputerStatus.Disconnected),
                OrphanedComputers = computedComputers.Count(computer => computer.OperationalStatus == OperationalComputerStatus.Orphaned),
                DisabledComputers = computedComputers.Count(computer => computer.OperationalStatus == OperationalComputerStatus.Disabled),
                HoursInRange = Math.Round(filteredUsage.Sum(GetDurationHours), 1)
            },
            EquipmentStatus = new List<ChartPoint>
                {
                    new() { Label = TranslateStatus(ComputerStatus.Available), Value = computedComputers.Count(computer => computer.OperationalStatus == OperationalComputerStatus.Available) },
                    new() { Label = TranslateStatus(ComputerStatus.InUse), Value = computedComputers.Count(computer => computer.OperationalStatus is OperationalComputerStatus.Occupied or OperationalComputerStatus.Locked or OperationalComputerStatus.Disconnected) },
                    new() { Label = TranslateStatus(ComputerStatus.Disabled), Value = computedComputers.Count(computer => computer.OperationalStatus == OperationalComputerStatus.Disabled) }
                }
                .ToList(),
            OperationalStatus = Enum.GetValues<OperationalComputerStatus>()
                .Select(value => new ChartPoint
                {
                    Label = TranslateOperationalStatus(value),
                    Value = computedComputers.Count(computer => computer.OperationalStatus == value)
                })
                .ToList(),
            UsageByCareer = snapshot.Careers
                .Select(career => new ChartPoint
                {
                    Label = career.Name,
                    Value = Math.Round(filteredUsage
                        .Where(record => filteredUsers.TryGetValue(record.UserId, out var user) && user.CareerId == career.Id)
                        .Sum(GetDurationHours), 1)
                })
                .Where(point => point.Value > 0)
                .OrderByDescending(point => point.Value)
                .ToList(),
            UsageBySemester = snapshot.Semesters
                .Select(semester => new ChartPoint
                {
                    Label = semester.Name,
                    Value = Math.Round(filteredUsage
                        .Where(record => filteredUsers.TryGetValue(record.UserId, out var user) && user.SemesterId == semester.Id)
                        .Sum(GetDurationHours), 1)
                })
                .Where(point => point.Value > 0)
                .OrderByDescending(point => point.Value)
                .ToList(),
            DailyUsageTrend = Enumerable.Range(0, Math.Max(1, rangeDays))
                .Select(offset =>
                {
                    var date = fromUtc.Date.AddDays(offset);
                    return new TrendPoint
                    {
                        Label = date.ToString("dd/MM", CultureInfo.InvariantCulture),
                        Hours = Math.Round(filteredUsage
                            .Where(record => record.StartUtc.Date == date)
                            .Sum(GetDurationHours), 1)
                    };
                })
                .ToList(),
            ComputerCards = computerCards,
            SessionAlerts = computedComputers
                .Where(computer => computer.OperationalStatus is OperationalComputerStatus.Disconnected or OperationalComputerStatus.Orphaned)
                .OrderByDescending(computer => computer.LastHeartbeatAt ?? computer.LoginStamp ?? computer.LastSeenUtc)
                .ThenBy(computer => computer.ComputerName, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList()
        };
    }

    public static double GetDurationHours(UsageRecord record)
    {
        return Math.Max(0, (record.EndUtc - record.StartUtc).TotalHours);
    }

    public static ComputerStatusCard ToComputerCard(ComputedComputerState computer)
    {
        return new ComputerStatusCard
        {
            Id = computer.ComputerId,
            Name = computer.ComputerName,
            Location = computer.Location,
            InventoryTag = computer.InventoryTag,
            IpAddress = computer.IpAddress,
            Status = computer.OperationalStatusLabel,
            CurrentUsername = computer.SessionUsername,
            LastSeenLabel = computer.LastSeenUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
            OperationalStatus = computer.OperationalStatus.ToString(),
            SessionState = computer.SessionState,
            SessionEndReason = computer.SessionEndReason,
            LastHeartbeatLabel = computer.LastHeartbeatAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
            HeartbeatAgeSeconds = computer.HeartbeatAgeSeconds,
            IsOrphaned = computer.IsOrphaned
        };
    }

    public static string TranslateStatus(ComputerStatus status)
    {
        return status switch
        {
            ComputerStatus.Available => "Disponible",
            ComputerStatus.InUse => "En uso",
            ComputerStatus.Disabled => "Deshabilitado",
            _ => status.ToString()
        };
    }

    public static string TranslateOperationalStatus(OperationalComputerStatus status)
    {
        return status switch
        {
            OperationalComputerStatus.Available => "Disponible",
            OperationalComputerStatus.Occupied => "Ocupado",
            OperationalComputerStatus.Locked => "Bloqueado",
            OperationalComputerStatus.Disconnected => "Desconectado",
            OperationalComputerStatus.Orphaned => "Sesion huerfana",
            OperationalComputerStatus.Disabled => "Deshabilitado",
            _ => status.ToString()
        };
    }

    public static ComputedComputerState CreateFallbackComputedState(Computer computer)
    {
        var operationalStatus = computer.Status switch
        {
            ComputerStatus.Disabled => OperationalComputerStatus.Disabled,
            ComputerStatus.InUse => OperationalComputerStatus.Occupied,
            _ => OperationalComputerStatus.Available
        };

        return new ComputedComputerState
        {
            ComputerId = computer.Id,
            ComputerName = computer.Name,
            Location = computer.Location,
            InventoryTag = computer.InventoryTag,
            IpAddress = computer.IpAddress,
            AdministrativeStatus = computer.Status,
            OperationalStatus = operationalStatus,
            OperationalStatusLabel = TranslateOperationalStatus(operationalStatus),
            SessionUsername = computer.CurrentUsername,
            LastSeenUtc = computer.LastSeenUtc,
            StatusReason = operationalStatus == OperationalComputerStatus.Disabled
                ? "Equipo deshabilitado administrativamente."
                : operationalStatus == OperationalComputerStatus.Occupied
                    ? "Actividad detectada con el modelo heredado."
                    : "Sin sesion operativa confirmada."
        };
    }

    public static string? CleanOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static string FormatAuditTimestamp(DateTime value)
    {
        return value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    public static int NextId(IEnumerable<int> ids)
    {
        return ids.DefaultIfEmpty(0).Max() + 1;
    }

    public static ComputerStatus ParseStatus(string? value)
    {
        return Enum.TryParse<ComputerStatus>(value, ignoreCase: true, out var status)
            ? status
            : ComputerStatus.Available;
    }

    public static char DetectDelimiter(string line)
    {
        var candidates = new[] { ';', ',', '\t', '|' };
        return candidates
            .OrderByDescending(candidate => line.Count(character => character == candidate))
            .First();
    }

    public static List<string> SplitLine(string line, char delimiter)
    {
        return line.Split(delimiter).Select(value => value.Trim()).ToList();
    }

    public static string NormalizeHeader(string value)
    {
        return new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(character => char.IsLetterOrDigit(character))
            .ToArray());
    }

    public static string GetValue(IReadOnlyList<string> values, IReadOnlyDictionary<string, int> map, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (map.TryGetValue(NormalizeHeader(key), out var index) && index < values.Count)
            {
                return values[index].Trim();
            }
        }

        return string.Empty;
    }

    public static bool ParseBoolean(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "activo" or "active" or "si" or "yes" => true,
            "0" or "false" or "inactivo" or "inactive" or "no" => false,
            _ => defaultValue
        };
    }

    public static AdminSnapshot CreateSeedSnapshot()
    {
        var now = DateTime.UtcNow;

        var careers = new List<Career>
        {
            new() { Id = 1, Name = "Ingenieria de Sistemas", Active = true },
            new() { Id = 2, Name = "Diseno Multimedia", Active = true },
            new() { Id = 3, Name = "Contaduria", Active = true }
        };

        var semesters = new List<Semester>
        {
            new() { Id = 1, Name = "Semestre 1", Active = true },
            new() { Id = 2, Name = "Semestre 4", Active = true },
            new() { Id = 3, Name = "Semestre 8", Active = true }
        };

        var users = new List<UserAccount>
        {
            new() { Id = 1, Username = "amartinez", FirstName = "Ana", LastName = "Martinez", Email = "ana.martinez@campus.edu", DocumentId = "10001", CareerId = 1, SemesterId = 3, Active = true, HashMethod = "BCRYPT", PasswordHash = PasswordHashService.HashPassword("Ana2026!", "BCRYPT") },
            new() { Id = 2, Username = "jlopez", FirstName = "Jorge", LastName = "Lopez", Email = "jorge.lopez@campus.edu", DocumentId = "10002", CareerId = 2, SemesterId = 2, Active = true, HashMethod = "SHA256", PasswordHash = PasswordHashService.HashPassword("Jorge2026!", "SHA256") },
            new() { Id = 3, Username = "mrojas", FirstName = "Maria", LastName = "Rojas", Email = "maria.rojas@campus.edu", DocumentId = "10003", CareerId = 1, SemesterId = 1, Active = true, HashMethod = "SSHA512", PasswordHash = PasswordHashService.HashPassword("Maria2026!", "SSHA512") },
            new() { Id = 4, Username = "cgarcia", FirstName = "Carlos", LastName = "Garcia", Email = "carlos.garcia@campus.edu", DocumentId = "10004", CareerId = 3, SemesterId = 2, Active = false, HashMethod = "MD5", PasswordHash = PasswordHashService.HashPassword("Carlos2026!", "MD5") }
        };

        var computers = new List<Computer>
        {
            new() { Id = 1, Name = "LAB-A-01", Location = "Laboratorio A", InventoryTag = "EQ-001", IpAddress = "192.168.14.101", Status = ComputerStatus.InUse, CurrentUsername = "amartinez", LastSeenUtc = now.AddMinutes(-2) },
            new() { Id = 2, Name = "LAB-A-02", Location = "Laboratorio A", InventoryTag = "EQ-002", IpAddress = "192.168.14.102", Status = ComputerStatus.Available, CurrentUsername = null, LastSeenUtc = now.AddMinutes(-5) },
            new() { Id = 3, Name = "LAB-B-03", Location = "Laboratorio B", InventoryTag = "EQ-003", IpAddress = "192.168.14.103", Status = ComputerStatus.Disabled, CurrentUsername = null, LastSeenUtc = now.AddHours(-6) },
            new() { Id = 4, Name = "BIB-04", Location = "Biblioteca", InventoryTag = "EQ-004", IpAddress = "192.168.14.104", Status = ComputerStatus.InUse, CurrentUsername = "jlopez", LastSeenUtc = now.AddMinutes(-1) },
            new() { Id = 5, Name = "BIB-05", Location = "Biblioteca", InventoryTag = "EQ-005", IpAddress = "192.168.14.105", Status = ComputerStatus.Available, CurrentUsername = null, LastSeenUtc = now.AddMinutes(-9) }
        };

        var rooms = new List<Room>
        {
            new() { Id = 1, Name = "Laboratorio A", Code = "LAB-A", CanvasWidth = 1180, CanvasHeight = 620, Active = true },
            new() { Id = 2, Name = "Biblioteca", Code = "BIB", CanvasWidth = 880, CanvasHeight = 420, Active = true }
        };

        var roomLayoutItems = new List<RoomLayoutItem>
        {
            new() { Id = 1, RoomId = 1, Label = "Equipo docente", ItemType = RoomLayoutItemType.TeacherDesk, X = 70, Y = 60, Width = 140, Height = 120, Orientation = "Horizontal", Capacity = 1, ComputerId = 1 },
            new() { Id = 2, RoomId = 1, Label = "Mesa isla A", ItemType = RoomLayoutItemType.Table, X = 310, Y = 52, Width = 430, Height = 92, Orientation = "Horizontal", Capacity = 4, ComputerId = null },
            new() { Id = 3, RoomId = 1, Label = "Puesto 01", ItemType = RoomLayoutItemType.Computer, X = 360, Y = 70, Width = 120, Height = 110, Orientation = "Horizontal", Capacity = 1, ComputerId = 2 },
            new() { Id = 4, RoomId = 1, Label = "Puesto 02", ItemType = RoomLayoutItemType.Computer, X = 560, Y = 70, Width = 120, Height = 110, Orientation = "Horizontal", Capacity = 1, ComputerId = 3 },
            new() { Id = 5, RoomId = 1, Label = "Pasillo central", ItemType = RoomLayoutItemType.EmptySpace, X = 300, Y = 230, Width = 380, Height = 80, Orientation = "Horizontal", Capacity = 1, ComputerId = null },
            new() { Id = 6, RoomId = 1, Label = "Mesa isla B", ItemType = RoomLayoutItemType.Table, X = 300, Y = 330, Width = 120, Height = 320, Orientation = "Vertical", Capacity = 3, ComputerId = null },
            new() { Id = 7, RoomId = 1, Label = "Puesto 03", ItemType = RoomLayoutItemType.Computer, X = 360, Y = 360, Width = 120, Height = 110, Orientation = "Horizontal", Capacity = 1, ComputerId = null },
            new() { Id = 8, RoomId = 2, Label = "Biblioteca 01", ItemType = RoomLayoutItemType.Computer, X = 160, Y = 120, Width = 120, Height = 110, Orientation = "Horizontal", Capacity = 1, ComputerId = 4 },
            new() { Id = 9, RoomId = 2, Label = "Biblioteca 02", ItemType = RoomLayoutItemType.Computer, X = 360, Y = 120, Width = 120, Height = 110, Orientation = "Horizontal", Capacity = 1, ComputerId = 5 },
            new() { Id = 10, RoomId = 2, Label = "Mesa de consulta", ItemType = RoomLayoutItemType.Table, X = 120, Y = 90, Width = 420, Height = 170, Orientation = "Horizontal", Capacity = 4, ComputerId = null },
            new() { Id = 11, RoomId = 2, Label = "Zona de consulta", ItemType = RoomLayoutItemType.EmptySpace, X = 580, Y = 100, Width = 180, Height = 140, Orientation = "Horizontal", Capacity = 1, ComputerId = null }
        };

        var usage = new List<UsageRecord>();
        var usageId = 1;
        for (var dayOffset = 0; dayOffset < 14; dayOffset++)
        {
            var baseDate = now.Date.AddDays(-dayOffset).AddHours(8);
            usage.Add(new UsageRecord { Id = usageId++, UserId = 1, ComputerId = 1, StartUtc = baseDate, EndUtc = baseDate.AddHours(2) });
            usage.Add(new UsageRecord { Id = usageId++, UserId = 2, ComputerId = 4, StartUtc = baseDate.AddHours(1), EndUtc = baseDate.AddHours(3.5) });
            usage.Add(new UsageRecord { Id = usageId++, UserId = 3, ComputerId = 2, StartUtc = baseDate.AddHours(2), EndUtc = baseDate.AddHours(4) });
            if (dayOffset % 3 == 0)
            {
                usage.Add(new UsageRecord { Id = usageId++, UserId = 4, ComputerId = 5, StartUtc = baseDate.AddHours(4), EndUtc = baseDate.AddHours(5) });
            }
        }

        return new AdminSnapshot
        {
            Careers = careers,
            Semesters = semesters,
            Users = users,
            Computers = computers,
            Rooms = rooms,
            RoomLayoutItems = roomLayoutItems,
            UsageRecords = usage,
            AuditEntries = new List<AuditEntry>
            {
                new()
                {
                    Id = 1,
                    ActorUsername = "admin",
                    Action = "Login",
                    EntityType = "Security",
                    EntityKey = "bootstrap",
                    Summary = "Ingreso inicial a la consola administrativa",
                    RemoteIp = "127.0.0.1",
                    CreatedUtc = now.AddMinutes(-45)
                }
            }
        };
    }
}
