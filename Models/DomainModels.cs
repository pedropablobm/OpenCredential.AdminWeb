using System.Text.Json.Serialization;

namespace OpenCredential.AdminWeb;

public sealed class AdminSnapshot
{
    public required List<Career> Careers { get; init; }
    public required List<Semester> Semesters { get; init; }
    public required List<UserAccount> Users { get; init; }
    public required List<Computer> Computers { get; init; }
    public List<ComputedComputerState> ComputedComputers { get; init; } = [];
    public required List<Room> Rooms { get; init; }
    public required List<RoomLayoutItem> RoomLayoutItems { get; init; }
    public required List<UsageRecord> UsageRecords { get; init; }
    public required List<AuditEntry> AuditEntries { get; init; }
}

public sealed class DashboardResponse
{
    public required DashboardKpis Kpis { get; init; }
    public required List<ChartPoint> EquipmentStatus { get; init; }
    public List<ChartPoint> OperationalStatus { get; init; } = [];
    public required List<ChartPoint> UsageByCareer { get; init; }
    public required List<ChartPoint> UsageBySemester { get; init; }
    public required List<TrendPoint> DailyUsageTrend { get; init; }
    public required List<ComputerStatusCard> ComputerCards { get; init; }
    public List<ComputedComputerState> SessionAlerts { get; init; } = [];
}

public sealed class DashboardKpis
{
    public int TotalUsers { get; init; }
    public int ActiveUsers { get; init; }
    public int AvailableComputers { get; init; }
    public int InUseComputers { get; init; }
    public int OccupiedComputers { get; init; }
    public int LockedComputers { get; init; }
    public int DisconnectedComputers { get; init; }
    public int OrphanedComputers { get; init; }
    public int DisabledComputers { get; init; }
    public double HoursInRange { get; init; }
}

public sealed class Career
{
    public int Id { get; init; }
    public required string Name { get; set; }
    public bool Active { get; set; }
}

public sealed class Semester
{
    public int Id { get; init; }
    public required string Name { get; set; }
    public bool Active { get; set; }
}

public sealed class UserAccount
{
    public int Id { get; init; }
    public required string Username { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public required string Email { get; set; }
    public required string DocumentId { get; set; }
    public int? CareerId { get; set; }
    public int? SemesterId { get; set; }
    public bool Active { get; set; }
    public string HashMethod { get; set; } = "BCRYPT";
    [JsonIgnore]
    public string? PasswordHash { get; set; }
}

public enum ComputerStatus
{
    Available,
    InUse,
    Disabled
}

public enum OperationalComputerStatus
{
    Available,
    Occupied,
    Locked,
    Disconnected,
    Orphaned,
    Disabled
}

public sealed class Computer
{
    public int Id { get; init; }
    public required string Name { get; set; }
    public required string Location { get; set; }
    public required string InventoryTag { get; set; }
    public string? IpAddress { get; set; }
    public ComputerStatus Status { get; set; }
    public string? CurrentUsername { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

public sealed class LoginSessionSnapshot
{
    public int DbId { get; init; }
    public DateTime LoginStamp { get; init; }
    public DateTime? LogoutStamp { get; init; }
    public string? Username { get; init; }
    public string? Machine { get; init; }
    public string? IpAddress { get; init; }
    public string? ClientSessionId { get; init; }
    public int? WindowsSessionId { get; init; }
    public string? SessionState { get; init; }
    public DateTime? LastHeartbeatAt { get; init; }
    public string? SessionEndReason { get; init; }
}

public sealed class ComputedComputerState
{
    public int ComputerId { get; init; }
    public required string ComputerName { get; init; }
    public required string Location { get; init; }
    public required string InventoryTag { get; init; }
    public string? IpAddress { get; init; }
    public ComputerStatus AdministrativeStatus { get; init; }
    public OperationalComputerStatus OperationalStatus { get; init; } = OperationalComputerStatus.Available;
    public string OperationalStatusLabel { get; init; } = "Disponible";
    public string? StatusReason { get; init; }
    public string? SessionUsername { get; init; }
    public string? Machine { get; init; }
    public string? ClientSessionId { get; init; }
    public int? WindowsSessionId { get; init; }
    public string? SessionState { get; init; }
    public DateTime? LoginStamp { get; init; }
    public DateTime? LogoutStamp { get; init; }
    public DateTime? LastHeartbeatAt { get; init; }
    public string? SessionEndReason { get; init; }
    public int? HeartbeatAgeSeconds { get; init; }
    public bool IsStale { get; init; }
    public bool IsOrphaned { get; init; }
    public DateTime LastSeenUtc { get; init; }
}

public sealed class Room
{
    public int Id { get; init; }
    public required string Name { get; set; }
    public string Code { get; set; } = string.Empty;
    public int CanvasWidth { get; set; } = 1200;
    public int CanvasHeight { get; set; } = 720;
    public bool Active { get; set; }
}

public enum RoomLayoutItemType
{
    Computer,
    EmptySpace,
    TeacherDesk,
    Table,
    Reference
}

public sealed class RoomLayoutItem
{
    public int Id { get; init; }
    public int RoomId { get; set; }
    public required string Label { get; set; }
    public RoomLayoutItemType ItemType { get; set; } = RoomLayoutItemType.Computer;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 120;
    public int Height { get; set; } = 110;
    public string Orientation { get; set; } = "Horizontal";
    public int Capacity { get; set; } = 1;
    public int? ComputerId { get; set; }
}

public sealed class UsageRecord
{
    public int Id { get; init; }
    public int UserId { get; set; }
    public int ComputerId { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
}

public sealed class ChartPoint
{
    public required string Label { get; init; }
    public double Value { get; init; }
}

public sealed class TrendPoint
{
    public required string Label { get; init; }
    public double Hours { get; init; }
}

public sealed class ComputerStatusCard
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public required string Location { get; init; }
    public required string InventoryTag { get; init; }
    public string? IpAddress { get; init; }
    public required string Status { get; init; }
    public string? CurrentUsername { get; init; }
    public string LastSeenLabel { get; init; } = string.Empty;
    public string? OperationalStatus { get; init; }
    public string? SessionState { get; init; }
    public string? SessionEndReason { get; init; }
    public string? LastHeartbeatLabel { get; init; }
    public int? HeartbeatAgeSeconds { get; init; }
    public bool IsOrphaned { get; init; }
}

public sealed class ImportUsersResult
{
    public int Imported { get; init; }
    public int Updated { get; init; }
    public required List<string> Warnings { get; init; }
}

public sealed class CareerInput
{
    public required string Name { get; init; }
    public bool Active { get; init; }
}

public sealed class SemesterInput
{
    public required string Name { get; init; }
    public bool Active { get; init; }
}

public sealed class ComputerInput
{
    public required string Name { get; init; }
    public required string Location { get; init; }
    public required string InventoryTag { get; init; }
    public string? IpAddress { get; init; }
    public string Status { get; init; } = ComputerStatus.Available.ToString();
    public string? CurrentUsername { get; init; }
}

public sealed class RoomInput
{
    public required string Name { get; init; }
    public string Code { get; init; } = string.Empty;
    public int CanvasWidth { get; init; } = 1200;
    public int CanvasHeight { get; init; } = 720;
    public bool Active { get; init; } = true;
}

public sealed class RoomLayoutItemInput
{
    public required string Label { get; init; }
    public string ItemType { get; init; } = RoomLayoutItemType.Computer.ToString();
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; } = 120;
    public int Height { get; init; } = 110;
    public string Orientation { get; init; } = "Horizontal";
    public int Capacity { get; init; } = 1;
    public int? ComputerId { get; init; }
}

public sealed class RoomLayoutInput
{
    public int CanvasWidth { get; init; } = 1200;
    public int CanvasHeight { get; init; } = 720;
    public required List<RoomLayoutItemInput> Items { get; init; }
}

public sealed class UserInput
{
    public required string Username { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public required string Email { get; init; }
    public required string DocumentId { get; init; }
    public int? CareerId { get; init; }
    public int? SemesterId { get; init; }
    public bool Active { get; init; }
    public string HashMethod { get; init; } = "BCRYPT";
    public string? Password { get; init; }
}

public sealed class UsageRecordInput
{
    public int UserId { get; init; }
    public int ComputerId { get; init; }
    public DateTime StartUtc { get; init; }
    public DateTime EndUtc { get; init; }
}

public sealed class PasswordResetInput
{
    public string HashMethod { get; init; } = "BCRYPT";
    public string? Password { get; init; }
    public bool Generate { get; init; } = true;
}

public sealed class PasswordResetResult
{
    public int UserId { get; init; }
    public required string Username { get; init; }
    public required string HashMethod { get; init; }
    public required string GeneratedPassword { get; init; }
}

public sealed class AuditEntry
{
    public int Id { get; init; }
    public required string ActorUsername { get; init; }
    public required string Action { get; init; }
    public required string EntityType { get; init; }
    public required string EntityKey { get; init; }
    public required string Summary { get; init; }
    public string? RemoteIp { get; init; }
    public DateTime CreatedUtc { get; init; }
}

public sealed class AuditEntryInput
{
    public required string ActorUsername { get; init; }
    public required string Action { get; init; }
    public required string EntityType { get; init; }
    public required string EntityKey { get; init; }
    public required string Summary { get; init; }
    public string? RemoteIp { get; init; }
}

public sealed class AdminLoginInput
{
    public required string Username { get; init; }
    public required string Password { get; init; }
}

public sealed class AdminSessionInfo
{
    public bool Authenticated { get; init; }
    public required string Username { get; init; }
    public required string Role { get; init; }
    public bool AuthenticationEnabled { get; init; }
}

public sealed class DatabaseConfigurationInput
{
    public string Provider { get; init; } = "PostgreSql";
    public required string Host { get; init; }
    public int Port { get; init; } = 5432;
    public required string DatabaseName { get; init; }
    public required string Username { get; init; }
    public string? Password { get; init; }
    public string SslMode { get; init; } = "Disable";
    public bool AutoInitialize { get; init; } = true;
}

public sealed class DatabaseConfigurationResponse
{
    public bool SqlEnabled { get; init; }
    public required string Provider { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; }
    public required string DatabaseName { get; init; }
    public required string Username { get; init; }
    public required string SslMode { get; init; }
    public bool AutoInitialize { get; init; }
    public bool RuntimeConfigurationExists { get; init; }
    public bool RequiresRestart { get; init; }
    public bool PasswordConfigured { get; init; }
}

public sealed class DatabaseConfigurationResult
{
    public bool Success { get; init; }
    public required string Message { get; init; }
    public bool RequiresRestart { get; init; }
}
