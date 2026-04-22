using System.Text.Json.Serialization;

namespace OpenCredential.AdminWeb;

public sealed class AdminSnapshot
{
    public required List<Career> Careers { get; init; }
    public required List<Semester> Semesters { get; init; }
    public required List<UserAccount> Users { get; init; }
    public required List<Computer> Computers { get; init; }
    public required List<UsageRecord> UsageRecords { get; init; }
    public required List<AuditEntry> AuditEntries { get; init; }
}

public sealed class DashboardResponse
{
    public required DashboardKpis Kpis { get; init; }
    public required List<ChartPoint> EquipmentStatus { get; init; }
    public required List<ChartPoint> UsageByCareer { get; init; }
    public required List<ChartPoint> UsageBySemester { get; init; }
    public required List<TrendPoint> DailyUsageTrend { get; init; }
    public required List<ComputerStatusCard> ComputerCards { get; init; }
}

public sealed class DashboardKpis
{
    public int TotalUsers { get; init; }
    public int ActiveUsers { get; init; }
    public int AvailableComputers { get; init; }
    public int InUseComputers { get; init; }
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
