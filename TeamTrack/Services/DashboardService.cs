using Microsoft.EntityFrameworkCore;
using TeamTrack.DTOs;
using TeamTrack.Models;
using TeamTrack.Repositories;

namespace TeamTrack.Services
{
    public class DashboardService : IDashboardService
    {
        private readonly IRepository<Project> _projectRepo;
        private readonly IRepository<WorkItem> _workItemRepo;
        private readonly IRepository<Bug> _bugRepo;
        private readonly IRepository<User> _userRepo;
        private readonly IRepository<DailyStatusNote> _noteRepo;

        public DashboardService(
            IRepository<Project> projectRepo,
            IRepository<WorkItem> workItemRepo,
            IRepository<Bug> bugRepo,
            IRepository<User> userRepo,
            IRepository<DailyStatusNote> noteRepo)
        {
            _projectRepo = projectRepo;
            _workItemRepo = workItemRepo;
            _bugRepo = bugRepo;
            _userRepo = userRepo;
            _noteRepo = noteRepo;
        }

        public async Task<DashboardResponseDto> GetDashboardAsync()
        {
            // Totals
            var totalProjects = await _projectRepo.Query().CountAsync();

            var workItems = await _workItemRepo.Query()
                .Include(w => w.Project)
                    .ThenInclude(p => p.Client)
                .Include(w => w.Module)
                    .ThenInclude(m => m.Product)
                .ToListAsync();

            var bugs = await _bugRepo.Query().ToListAsync();

            // Separate WorkItems into normal tasks and bugs based on WorkType
            var workItemBugs = workItems.Where(w => w.WorkType != null && w.WorkType.Equals("Bug", System.StringComparison.OrdinalIgnoreCase)).ToList();
            var normalWorkItems = workItems.Where(w => w.WorkType == null || !w.WorkType.Equals("Bug", System.StringComparison.OrdinalIgnoreCase)).ToList();

            var totalWorkItems = normalWorkItems.Count;
            var totalBugs = bugs.Count;

            // WorkItem status count (excluding bugs)
            var workItemStatusCount = new WorkItemStatusCountDto
            {
                Pending = normalWorkItems.Count(w => w.Status == "pending" || w.Status == "assigned" || w.Status == "reopened"),
                InProgress = normalWorkItems.Count(w => w.Status == "in_progress" || w.Status == "future_release"),
                Completed = normalWorkItems.Count(w => w.Status == "completed"),
                Testing = normalWorkItems.Count(w => w.Status == "testing"),
                BugFound = normalWorkItems.Count(w => w.Status == "bug_found"),
                Closed = normalWorkItems.Count(w => w.Status == "closed")
            };

            // Bug status count (only from Bug entities, since they already sync/include top-level bugs)
            var bugStatusCount = new BugStatusCountDto
            {
                Open = bugs.Count(b => b.Status == "open"),
                InProgress = bugs.Count(b => b.Status == "in_progress"),
                Fixed = bugs.Count(b => b.Status == "fixed"),
                Closed = bugs.Count(b => b.Status == "closed")
            };

            // Employee-wise workitem count (excluding bugs to match their dashboard)
            var employees = await _userRepo.Query()
                .Where(u => (u.UserType == "Employee" || u.UserType == "Both") && u.IsActive)
                .ToListAsync();

            var allNotes = await _noteRepo.Query()
                .Include(n => n.CreatedBy)
                .OrderByDescending(n => n.CreatedAt)
                .ToListAsync();

            var employeeCounts = new List<EmployeeWorkItemCountDto>();

            foreach (var emp in employees)
            {
                var empWorkItems = normalWorkItems.Where(w => w.AssignedToUserId == emp.Id).ToList();
                var empNotes = allNotes.Where(n => n.EmployeeId == emp.Id).ToList();
                var latestNote = empNotes.FirstOrDefault();

                employeeCounts.Add(new EmployeeWorkItemCountDto
                {
                    EmployeeId = emp.Id,
                    EmployeeName = emp.Name,
                    TotalAssigned = empWorkItems.Count,
                    Pending = empWorkItems.Count(w => w.Status == "pending" || w.Status == "assigned" || w.Status == "reopened"),
                    InProgress = empWorkItems.Count(w => w.Status == "in_progress" || w.Status == "future_release"),
                    Completed = empWorkItems.Count(w => w.Status == "completed"),
                    Testing = empWorkItems.Count(w => w.Status == "testing"),
                    BugFound = empWorkItems.Count(w => w.Status == "bug_found"),
                    Closed = empWorkItems.Count(w => w.Status == "closed"),
                    AssignedTasks = empWorkItems.Select(w => new EmployeeTaskDto
                    {
                        Id = w.Id,
                        WorkNumber = w.WorkNumber,
                        Title = w.Title,
                        Status = w.Status,
                        Priority = w.Priority,
                        ProjectId = w.ProjectId,
                        ProjectName = w.Project?.Name ?? "Unknown Project",
                        ClientName = w.Project?.Client?.Name,
                        ProductName = w.Module?.Product?.Name,
                        ModuleName = w.Module?.Name
                    }).ToList(),
                    LatestNote = latestNote?.NoteText,
                    LatestNoteDate = latestNote?.CreatedAt,
                    NoteHistory = empNotes.Select(n => new DailyStatusNoteDto
                    {
                        Id = n.Id,
                        NoteText = n.NoteText,
                        CreatedAt = n.CreatedAt,
                        CreatedByName = n.CreatedBy?.Name ?? "Unknown PM"
                    }).ToList()
                });
            }

            return new DashboardResponseDto
            {
                TotalProjects = totalProjects,
                TotalWorkItems = totalWorkItems,
                TotalBugs = totalBugs,
                WorkItemStatusCount = workItemStatusCount,
                BugStatusCount = bugStatusCount,
                EmployeeWorkItemCounts = employeeCounts
            };
        }


        public async Task<AdminDashboardResponseDto> GetAdminDashboardAsync(int? projectId, int? employeeId, DateTime? fromDate, DateTime? toDate)
        {
            // Total counts (unfiltered)
            var totalProjects = await _projectRepo.Query().CountAsync();
            var totalEmployees = await _userRepo.Query()
                .Where(u => (u.UserType == "Employee" || u.UserType == "Both") && u.IsActive)
                .CountAsync();

            // Build filtered WorkItems query
            var query = _workItemRepo.Query()
                .Include(w => w.AssignedTo)
                .AsQueryable();

            if (projectId.HasValue)
                query = query.Where(w => w.ProjectId == projectId.Value);

            if (employeeId.HasValue)
                query = query.Where(w => w.AssignedToUserId == employeeId.Value);

            if (fromDate.HasValue)
                query = query.Where(w => w.CreatedAt >= fromDate.Value);

            if (toDate.HasValue)
            {
                var toDateEnd = toDate.Value.Date.AddDays(1);
                query = query.Where(w => w.CreatedAt < toDateEnd);
            }

            var workItems = await query.ToListAsync();

            // Separate tasks and bugs
            var allItems = workItems;
            var bugItems = allItems.Where(w => w.WorkType != null && w.WorkType.Equals("Bug", StringComparison.OrdinalIgnoreCase)).ToList();
            var taskItems = allItems.Where(w => w.WorkType == null || !w.WorkType.Equals("Bug", StringComparison.OrdinalIgnoreCase)).ToList();

            // Status counts (all items — tasks + bugs combined)
            var statusCounts = new AdminStatusCountDto
            {
                Todo = allItems.Count(w => w.Status == "pending"),
                Assigned = allItems.Count(w => w.Status == "assigned"),
                Reopen = allItems.Count(w => w.Status == "reopened"),
                InProgress = allItems.Count(w => w.Status == "in_progress"),
                FutureRelease = allItems.Count(w => w.Status == "future_release"),
                Fixed = allItems.Count(w => w.Status == "fixed"),
                Completed = allItems.Count(w => w.Status == "completed"),
                Closed = allItems.Count(w => w.Status == "closed"),
                Open = allItems.Count(w => w.Status == "open")
            };

            // Employee breakdown
            var employeeBreakdown = new List<AdminEmployeeBreakdownDto>();

            var groupedByEmployee = allItems
                .Where(w => w.AssignedToUserId != null)
                .GroupBy(w => new { w.AssignedToUserId, Name = w.AssignedTo?.Name ?? "Unknown" });

            foreach (var group in groupedByEmployee)
            {
                var items = group.ToList();
                employeeBreakdown.Add(new AdminEmployeeBreakdownDto
                {
                    EmployeeId = group.Key.AssignedToUserId ?? 0,
                    EmployeeName = group.Key.Name,
                    TotalAssigned = items.Count,
                    Todo = items.Count(w => w.Status == "pending"),
                    Assigned = items.Count(w => w.Status == "assigned"),
                    Reopen = items.Count(w => w.Status == "reopened"),
                    InProgress = items.Count(w => w.Status == "in_progress"),
                    FutureRelease = items.Count(w => w.Status == "future_release"),
                    Fixed = items.Count(w => w.Status == "fixed"),
                    Completed = items.Count(w => w.Status == "completed"),
                    Closed = items.Count(w => w.Status == "closed"),
                    Open = items.Count(w => w.Status == "open")
                });
            }

            return new AdminDashboardResponseDto
            {
                TotalProjects = totalProjects,
                TotalEmployees = totalEmployees,
                TotalWorkItems = taskItems.Count,
                TotalBugs = bugItems.Count,
                StatusCounts = statusCounts,
                EmployeeBreakdown = employeeBreakdown.OrderBy(e => e.EmployeeName).ToList()
            };
        }

        public async Task<EmployeeDashboardResponseDto> GetEmployeeDashboardAsync(int userId, int? projectId, DateTime? fromDate, DateTime? toDate)
        {
            // Get only this employee's assigned work items
            var query = _workItemRepo.Query()
                .Include(w => w.Project)
                .Where(w => w.AssignedToUserId == userId);

            if (projectId.HasValue)
                query = query.Where(w => w.ProjectId == projectId.Value);

            if (fromDate.HasValue)
                query = query.Where(w => w.CreatedAt >= fromDate.Value);

            if (toDate.HasValue)
            {
                var toDateEnd = toDate.Value.Date.AddDays(1);
                query = query.Where(w => w.CreatedAt < toDateEnd);
            }

            var workItems = await query.ToListAsync();

            // Status counts
            var statusCounts = new AdminStatusCountDto
            {
                Todo = workItems.Count(w => w.Status == "pending"),
                Assigned = workItems.Count(w => w.Status == "assigned"),
                Reopen = workItems.Count(w => w.Status == "reopened"),
                InProgress = workItems.Count(w => w.Status == "in_progress"),
                FutureRelease = workItems.Count(w => w.Status == "future_release"),
                Fixed = workItems.Count(w => w.Status == "fixed"),
                Completed = workItems.Count(w => w.Status == "completed"),
                Closed = workItems.Count(w => w.Status == "closed"),
                Open = workItems.Count(w => w.Status == "open")
            };

            // Only projects assigned to this employee (for dropdown)
            var assignedProjects = await _workItemRepo.Query()
                .Where(w => w.AssignedToUserId == userId)
                .Include(w => w.Project)
                .Select(w => new EmployeeProjectDto
                {
                    ProjectId = w.ProjectId,
                    ProjectName = w.Project!.Name
                })
                .Distinct()
                .OrderBy(p => p.ProjectName)
                .ToListAsync();

            return new EmployeeDashboardResponseDto
            {
                TotalAssigned = workItems.Count,
                TotalProjects = assignedProjects.Count,
                StatusCounts = statusCounts,
                AssignedProjects = assignedProjects
            };
        }

        public async Task<byte[]> ExportAdminCsvAsync(int? projectId, int? employeeId, DateTime? fromDate, DateTime? toDate, string? status, string? priority, string? workType, int? moduleId, int? clientId, string? raisedBuild, string? fixedBuild, string? columns)
        {
            var query = BuildExportQuery(projectId, employeeId, fromDate, toDate, status, priority, workType, moduleId, clientId, raisedBuild, fixedBuild);
            var workItems = await query.OrderBy(w => w.ProjectId).ThenBy(w => w.CreatedAt).ToListAsync();
            return GenerateCsv(workItems, columns);
        }

        public async Task<byte[]> ExportEmployeeCsvAsync(int userId, int? projectId, DateTime? fromDate, DateTime? toDate, string? status, string? priority, string? workType, int? moduleId, int? clientId, string? raisedBuild, string? fixedBuild, string? columns)
        {
            var query = BuildExportQuery(projectId, userId, fromDate, toDate, status, priority, workType, moduleId, clientId, raisedBuild, fixedBuild);
            var workItems = await query.OrderBy(w => w.ProjectId).ThenBy(w => w.CreatedAt).ToListAsync();
            return GenerateCsv(workItems, columns);
        }

        private static readonly Dictionary<string, Func<WorkItem, string>> ColumnMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "WorkNumber", w => Escape(w.WorkNumber) },
            { "Title", w => Escape(w.Title) },
            { "Description", w => Escape(w.Description) },
            { "Status", w => Escape(w.Status) },
            { "Priority", w => Escape(w.Priority) },
            { "WorkType", w => Escape(w.WorkType) },
            { "AssignedTo", w => Escape(w.AssignedTo?.Name) },
            { "CreatedBy", w => Escape(w.CreatedBy?.Name) },
            { "ProjectName", w => Escape(w.Project?.Name) },
            { "ModuleName", w => Escape(w.Module?.Name) },
            { "Severity", w => "" },
            { "IssueType", w => "" },
            { "EpicName", w => Escape(w.EpicName) },
            { "RaisedBuild", w => Escape(w.RaisedBuild) },
            { "FixedBuild", w => Escape(w.FixedBuild) },
            { "Labels", w => Escape(w.Labels) },
            { "Team", w => Escape(w.Team) },
            { "DueDate", w => w.DueDate?.ToString("dd/MM/yyyy") ?? "" },
            { "StartDate", w => w.StartDate?.ToString("dd/MM/yyyy") ?? "" },
            { "CreatedAt", w => w.CreatedAt.ToString("dd/MM/yyyy HH:mm") },
            { "UpdatedAt", w => w.UpdatedAt.ToString("dd/MM/yyyy HH:mm") }
        };

        private static byte[] GenerateCsv(List<WorkItem> workItems, string? columns)
        {
            // If columns specified, use only those; otherwise use all
            var selectedColumns = new List<string>();

            if (!string.IsNullOrWhiteSpace(columns))
            {
                foreach (var col in columns.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = col.Trim();
                    if (ColumnMap.ContainsKey(trimmed))
                        selectedColumns.Add(trimmed);
                }
            }

            if (selectedColumns.Count == 0)
                selectedColumns = ColumnMap.Keys.ToList();

            var sb = new System.Text.StringBuilder();

            // Header row
            sb.AppendLine(string.Join(",", selectedColumns));

            // Data rows
            foreach (var w in workItems)
            {
                var values = selectedColumns.Select(col => ColumnMap[col](w));
                sb.AppendLine(string.Join(",", values));
            }

            return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        }


        private IQueryable<WorkItem> BuildExportQuery(int? projectId, int? employeeId, DateTime? fromDate, DateTime? toDate, string? status, string? priority, string? workType, int? moduleId, int? clientId, string? raisedBuild, string? fixedBuild)
        {
            var query = _workItemRepo.Query()
                .Include(w => w.Project)
                .Include(w => w.AssignedTo)
                .Include(w => w.CreatedBy)
                .Include(w => w.Module)
                .AsQueryable();

            if (projectId.HasValue)
                query = query.Where(w => w.ProjectId == projectId.Value);

            if (employeeId.HasValue)
                query = query.Where(w => w.AssignedToUserId == employeeId.Value);

            if (fromDate.HasValue)
                query = query.Where(w => w.CreatedAt >= fromDate.Value);

            if (toDate.HasValue)
            {
                var toDateEnd = toDate.Value.Date.AddDays(1);
                query = query.Where(w => w.CreatedAt < toDateEnd);
            }

            if (!string.IsNullOrEmpty(status))
                query = query.Where(w => w.Status == status.ToLower());

            if (!string.IsNullOrEmpty(priority))
                query = query.Where(w => w.Priority == priority.ToLower());

            if (!string.IsNullOrEmpty(workType))
                query = query.Where(w => w.WorkType != null && w.WorkType.ToLower() == workType.ToLower());

            if (moduleId.HasValue)
                query = query.Where(w => w.ModuleId == moduleId.Value);

            if (clientId.HasValue)
                query = query.Where(w => w.Project != null && w.Project.ClientId == clientId.Value);

            if (!string.IsNullOrEmpty(raisedBuild))
                query = query.Where(w => w.RaisedBuild == raisedBuild);

            if (!string.IsNullOrEmpty(fixedBuild))
                query = query.Where(w => w.FixedBuild == fixedBuild);

            return query;
        }

        private static string Escape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}