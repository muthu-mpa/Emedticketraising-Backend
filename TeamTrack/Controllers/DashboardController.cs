using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TeamTrack.DTOs;
using TeamTrack.Services;

namespace TeamTrack.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class DashboardController : ControllerBase
    {
        private readonly IDashboardService _dashboardService;

        public DashboardController(IDashboardService dashboardService)
        {
            _dashboardService = dashboardService;
        }
        [HttpGet]
        [Authorize(Roles = "ProductManager")]
        public async Task<IActionResult> GetDashboard()
        {
            var result = await _dashboardService.GetDashboardAsync();
            return Ok
            (ApiResponse<DashboardResponseDto>
            .SuccessResponse
            (result, "Dashboard fetched successfully"));
        }

        [HttpGet("admin")]
        [Authorize(Roles = "ProductManager")]
        public async Task<IActionResult> GetAdminDashboard(
            [FromQuery] int? projectId,
            [FromQuery] int? employeeId,
            [FromQuery] DateTime? fromDate,
            [FromQuery] DateTime? toDate)
        {
            var result = await _dashboardService.GetAdminDashboardAsync(projectId, employeeId, fromDate, toDate);
            return Ok(ApiResponse<AdminDashboardResponseDto>.SuccessResponse(result, "Admin dashboard fetched successfully"));
        }

        [HttpGet("employee")]
        [Authorize(Roles = "Employee,ProductManager")]
        public async Task<IActionResult> GetEmployeeDashboard(
            [FromQuery] int? projectId,
            [FromQuery] DateTime? fromDate,
            [FromQuery] DateTime? toDate)
        {
            var userId = int.Parse(User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)!);
            var result = await _dashboardService.GetEmployeeDashboardAsync(userId, projectId, fromDate, toDate);
            return Ok(ApiResponse<EmployeeDashboardResponseDto>.SuccessResponse(result, "Employee dashboard fetched successfully"));
        }


        [HttpGet("admin/export")]
        [Authorize(Roles = "ProductManager")]
        public async Task<IActionResult> ExportAdminCsv(
             [FromQuery] int? projectId,
             [FromQuery] int? employeeId,
             [FromQuery] DateTime? fromDate,
             [FromQuery] DateTime? toDate,
             [FromQuery] string? status,
             [FromQuery] string? priority,
             [FromQuery] string? workType,
             [FromQuery] int? moduleId,
             [FromQuery] int? clientId,
             [FromQuery] string? raisedBuild,
             [FromQuery] string? fixedBuild,
              [FromQuery] string? columns)
        {
            var csvBytes = await _dashboardService.ExportAdminCsvAsync(projectId, employeeId, fromDate, toDate, status, priority, workType, moduleId, clientId, raisedBuild, fixedBuild, columns);
            return File(csvBytes, "text/csv", "TeamTrack_Export.csv");
        }

        [HttpGet("employee/export")]
        [Authorize(Roles = "Employee,ProductManager")]
        public async Task<IActionResult> ExportEmployeeCsv(
             [FromQuery] int? projectId,
             [FromQuery] DateTime? fromDate,
             [FromQuery] DateTime? toDate,
             [FromQuery] string? status,
             [FromQuery] string? priority,
             [FromQuery] string? workType,
             [FromQuery] int? moduleId,
             [FromQuery] int? clientId,
             [FromQuery] string? raisedBuild,
             [FromQuery] string? fixedBuild,
              [FromQuery] string? columns)
        {
            var userId = int.Parse(User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)!);
            var csvBytes = await _dashboardService.ExportEmployeeCsvAsync(userId, projectId, fromDate, toDate, status, priority, workType, moduleId, clientId, raisedBuild, fixedBuild, columns);
            return File(csvBytes, "text/csv", "MyTasks_Export.csv");


        }


        [HttpGet("export/columns")]
        [Authorize]
        public IActionResult GetExportColumns()
        {
            var columns = new[]
            {
                "WorkNumber", "Title", "Description", "Status", "Priority",
                "WorkType", "AssignedTo", "CreatedBy", "ProjectName", "ModuleName",
                "Severity", "IssueType", "EpicName", "RaisedBuild", "FixedBuild",
                "Labels", "Team", "DueDate", "StartDate", "CreatedAt", "UpdatedAt"
            };
            return Ok(ApiResponse<string[]>.SuccessResponse(columns, "Available export columns"));
        }
    }
}