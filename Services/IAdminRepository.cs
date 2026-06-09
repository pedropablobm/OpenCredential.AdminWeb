using Microsoft.AspNetCore.Http;

namespace OpenCredential.AdminWeb.Services;

public interface IAdminRepository
{
    AdminSnapshot GetSnapshot();
    DashboardResponse GetDashboard(int rangeDays, int? careerId, int? semesterId, string? status);
    ReportsResponse GetReports(DateTime fromUtc, DateTime toUtc, int? careerId, int? semesterId, int? groupId, string? username, string? sessionOrigin, string? sessionState, string? operationalStatus);
    List<GroupInfo> GetGroups();
    UserAccount? FindUserByUsername(string username);
    void RegisterFailedSignIn(string username, int maxFailedAttempts, int lockoutMinutes);
    void ResetFailedSignIn(string username);
    PortalProfile? GetPortalProfile(string username);
    PortalProfile? UpdatePortalProfile(string username, PortalProfileUpdateInput input);
    PasswordResetResult? UpdatePasswordByUsername(string username, string plainPassword, string hashMethod);
    PortalPasswordRecoveryResult RecoverPortalPassword(PortalPasswordRecoveryInput input, int tokenLifetimeMinutes);
    bool ResetPortalPasswordWithToken(PortalPasswordResetWithTokenInput input, out string message);
    List<PortalSessionEntry> GetPortalSessions(string username, int take);
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
    Room CreateRoom(RoomInput input);
    Room? UpdateRoom(int id, RoomInput input);
    bool DeleteRoom(int id);
    List<RoomLayoutItem> SaveRoomLayout(int roomId, RoomLayoutInput input);
    UserAccount CreateUser(UserInput input);
    UserAccount? UpdateUser(int id, UserInput input);
    bool DeleteUser(int id);
    PasswordResetResult? ResetUserPassword(int id, PasswordResetInput input);
    UsageRecord CreateUsageRecord(UsageRecordInput input);
    Task<ImportUsersResult> ImportUsersAsync(IFormFile file);
}
