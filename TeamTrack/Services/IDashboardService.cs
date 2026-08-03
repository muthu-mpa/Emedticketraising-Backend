using TeamTrack.DTOs;

namespace TeamTrack.Services
{
    public interface IDashboardService
    {
        Task<DashboardResponseDto> GetDashboardAsync();
        Task<AdminDashboardResponseDto> GetAdminDashboardAsync(int? projectId, int? employeeId, DateTime? fromDate, DateTime? toDate);
        Task<EmployeeDashboardResponseDto> GetEmployeeDashboardAsync(int userId, int? projectId, DateTime? fromDate, DateTime? toDate);
        Task<byte[]> ExportAdminCsvAsync(int? projectId, int? employeeId, DateTime? fromDate, DateTime? toDate, string? status, string? priority, string? workType, int? moduleId, int? clientId, string? raisedBuild, string? fixedBuild, string? columns);
        Task<byte[]> ExportEmployeeCsvAsync(int userId, int? projectId, DateTime? fromDate, DateTime? toDate, string? status, string? priority, string? workType, int? moduleId, int? clientId, string? raisedBuild, string? fixedBuild, string? columns);
        Task<PagedResult<WorkItemResponseDto>> GetAdminWorkItemsAsync(int? projectId, string tab, int page, int pageSize);
        Task<PagedResult<WorkItemResponseDto>> GetEmployeeWorkItemsAsync(int userId, int? projectId, string tab, int page, int pageSize, string? search, string? status, int? assignedToUserId, DateTime? fromDueDate, DateTime? toDueDate);
    }
}