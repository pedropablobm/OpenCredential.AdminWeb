using Microsoft.AspNetCore.Http;

namespace OpenCredential.AdminWeb.Services;

public interface IAdminRepository
{
    AdminSnapshot GetSnapshot();
    DashboardResponse GetDashboard(int rangeDays, int? careerId, int? semesterId, string? status);
    List<AuditEntry> GetAuditEntries(int take);
    AuditEntry RecordAudit(AuditEntryInput input);
    Career CreateCareer(CareerInput input);
    Career? UpdateCareer(int id, CareerInput input);
    bool DeleteCareer(int id);
    Semester CreateSemester(SemesterInput input);
    Semester? UpdateSemester(int id, SemesterInput input);
    bool DeleteSemester(int id);
    Computer CreateComputer(ComputerInput input);
    Computer? UpdateComputer(int id, ComputerInput input);
    bool DeleteComputer(int id);
    UserAccount CreateUser(UserInput input);
    UserAccount? UpdateUser(int id, UserInput input);
    bool DeleteUser(int id);
    PasswordResetResult? ResetUserPassword(int id, PasswordResetInput input);
    UsageRecord CreateUsageRecord(UsageRecordInput input);
    Task<ImportUsersResult> ImportUsersAsync(IFormFile file);
}
