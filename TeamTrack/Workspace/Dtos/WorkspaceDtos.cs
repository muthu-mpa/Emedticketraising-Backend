using Microsoft.AspNetCore.Http;

namespace TeamTrack.Workspace.Dtos
{
    // ---------- Campaigns ----------
    public class CampaignInput
    {
        public string? Title { get; set; }
        public string? ClientName { get; set; }
        public string? ContactName { get; set; }
        public string? ClientEmail { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
    }

    public class NoteInput
    {
        public string? Note { get; set; }
    }

    public class SendCampaignInput
    {
        /// <summary>Must be true: the client shows "Send to {contact} at {email}?" first.</summary>
        public bool Confirm { get; set; }
    }

    public class CampaignDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string ClientName { get; set; } = "";
        public string ContactName { get; set; } = "";
        public string ClientEmail { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
        public string Status { get; set; } = "";
        public string? ManagerNote { get; set; }
        public int CreatedByUserId { get; set; }
        public string CreatedByName { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? SentAt { get; set; }

        // filled for drafts created by an Excel import
        public int? ImportId { get; set; }
        public int? SourceRow { get; set; }
        public string? Segment { get; set; }
        public string? Priority { get; set; }
        public string? SendWave { get; set; }
        /// <summary>Why a draft cannot be sent for approval yet (missing/invalid email, placeholders left). Empty when nothing blocks it.</summary>
        public List<string> SubmitBlockers { get; set; } = [];

        /// <summary>changes_asked | needs_your_approval | ready_to_send | null</summary>
        public string? Badge { get; set; }
        public bool CanEdit { get; set; }
        public bool CanSubmit { get; set; }
        public bool CanApprove { get; set; }
        public bool CanAskChanges { get; set; }
        public bool CanTakeBack { get; set; }
        public bool CanSend { get; set; }
    }

    public class CampaignHistoryDto
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string UserName { get; set; } = "";
        public string Action { get; set; } = "";
        public string? Note { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class CampaignDetailDto : CampaignDto
    {
        public string FromName { get; set; } = "";
        public string FromEmail { get; set; } = "";
        /// <summary>Plain-text footer that is appended to every mail.</summary>
        public string Footer { get; set; } = "";
        public List<CampaignHistoryDto> History { get; set; } = [];
    }

    public class CampaignListDto
    {
        /// <summary>Counts per status for the current search / "only mine" filter (ignores the status filter).</summary>
        public Dictionary<string, int> Counts { get; set; } = new();
        public List<CampaignDto> Items { get; set; } = [];
    }

    public class CampaignTemplateDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
    }

    // ---------- Leave & permission ----------
    public class LeaveRequestInput
    {
        public string? LeaveType { get; set; }
        public DateOnly FromDate { get; set; }
        public DateOnly ToDate { get; set; }
        public string? Reason { get; set; }
    }

    public class PermissionRequestInput
    {
        public DateOnly Date { get; set; }
        /// <summary>"HH:mm"</summary>
        public string? StartTime { get; set; }
        /// <summary>"HH:mm"</summary>
        public string? EndTime { get; set; }
        public string? Reason { get; set; }
    }

    public class ReasonInput
    {
        public string? Reason { get; set; }
    }

    public class TimeOffDto
    {
        public int Id { get; set; }
        public string Kind { get; set; } = "";
        public int UserId { get; set; }
        public string UserName { get; set; } = "";
        public string? LeaveType { get; set; }
        public DateOnly FromDate { get; set; }
        public DateOnly ToDate { get; set; }
        public int Days { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public int Minutes { get; set; }
        public string Reason { get; set; } = "";
        public string Status { get; set; } = "";
        public string? DecisionReason { get; set; }
        public string? DecidedByName { get; set; }
        public DateTime? DecidedAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool CanCancel { get; set; }
        public bool CanDecide { get; set; }
    }

    public class LeaveBalanceDto
    {
        public string LeaveType { get; set; } = "";
        public int Quota { get; set; }
        public int Used { get; set; }
        public int Remaining { get; set; }
    }

    public class BalancesDto
    {
        public int Year { get; set; }
        public List<LeaveBalanceDto> Leave { get; set; } = [];
        public double PermissionHoursLeftThisMonth { get; set; }
        public int PermissionMinutesUsedThisMonth { get; set; }
    }

    public class AwayDto
    {
        public int UserId { get; set; }
        public string UserName { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? LeaveType { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        /// <summary>"On leave" or "Away 10:00–11:30"</summary>
        public string Label { get; set; } = "";
    }

    public class TeamTimeOffDto
    {
        public List<TimeOffDto> Waiting { get; set; } = [];
        public List<TimeOffDto> RecentDecisions { get; set; } = [];
        public List<AwayDto> AwayToday { get; set; } = [];
    }

    public class CalendarEntryDto
    {
        public int RequestId { get; set; }
        public int UserId { get; set; }
        public string UserName { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? LeaveType { get; set; }
        /// <summary>pending | approved</summary>
        public string Status { get; set; } = "";
        public DateOnly FromDate { get; set; }
        public DateOnly ToDate { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
    }

    public class OverlapInfoDto
    {
        public int UserId { get; set; }
        public string UserName { get; set; } = "";
        public DateOnly FromDate { get; set; }
        public DateOnly ToDate { get; set; }
    }

    // ---------- People ----------
    public class PersonDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        /// <summary>Employee | ProductManager | Both</summary>
        public string Role { get; set; } = "";
        public string? ProfilePicture { get; set; }
        public List<string> Projects { get; set; } = [];
        /// <summary>Always "working" (the leave backend is not used by the people panel).</summary>
        public string TodayStatus { get; set; } = "working";
        public string TodayLabel { get; set; } = "Working";
    }

    public class PeopleListDto
    {
        /// <summary>People matching the search, ignoring the role filter.</summary>
        public int TotalCount { get; set; }
        /// <summary>Counts for the role tabs: all, Employee, ProductManager. A "Both" user counts in both roles.</summary>
        public Dictionary<string, int> RoleCounts { get; set; } = new();
        public List<PersonDto> Items { get; set; } = [];
    }

    public class PersonProfileDto : PersonDto
    {
        public string Email { get; set; } = "";
        public DateTime JoinedAt { get; set; }
        /// <summary>No longer filled: the people panel has no leave information.</summary>
        public List<TimeOffDto> UpcomingTimeOff { get; set; } = [];
        /// <summary>No longer filled: the people panel has no leave information.</summary>
        public BalancesDto? Balances { get; set; }
    }

    // ---------- Projects & documents ----------
    public class MemberDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
        public string? ProfilePicture { get; set; }
    }

    public class WorkspaceProjectDto
    {
        public int Id { get; set; }
        public string ProjectNumber { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public int ProgressPercent { get; set; }
        public int TasksDone { get; set; }
        public int TasksTotal { get; set; }
        public int DocumentCount { get; set; }
        public List<MemberDto> Members { get; set; } = [];
    }

    public class WorkspaceProjectDetailDto : WorkspaceProjectDto
    {
        public string? Description { get; set; }
        public string LeadName { get; set; } = "";
        public List<DocumentDto> Documents { get; set; } = [];
    }

    public class DocumentDto
    {
        public int Id { get; set; }
        public int ProjectId { get; set; }
        public string Title { get; set; } = "";
        public string DocType { get; set; } = "";
        public string? Content { get; set; }
        public string? Url { get; set; }
        public string? FileUrl { get; set; }
        public int UploadedByUserId { get; set; }
        public string UploadedByName { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class DocumentForm
    {
        public string? Title { get; set; }
        /// <summary>doc | sheet | slides | link</summary>
        public string? DocType { get; set; }
        public string? Content { get; set; }
        public string? Url { get; set; }
        public IFormFile? File { get; set; }
    }

    // ---------- Notifications ----------
    public class NotificationDto
    {
        public int Id { get; set; }
        public string Type { get; set; } = "";
        public string Message { get; set; } = "";
        public string? Link { get; set; }
        public bool IsRead { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class NotificationListDto
    {
        public int UnreadCount { get; set; }
        public List<NotificationDto> Items { get; set; } = [];
    }

    // ---------- Home ----------
    public class HomeTodoDto
    {
        /// <summary>campaign | time_off</summary>
        public string Kind { get; set; } = "";
        public int Id { get; set; }
        public string Label { get; set; } = "";
        public string Link { get; set; } = "";
    }

    public class HomeProjectDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int ProgressPercent { get; set; }
    }

    public class HomeDto
    {
        public DateOnly Today { get; set; }
        /// <summary>The role the user is acting as: ProductManager | Employee</summary>
        public string Role { get; set; } = "";
        public Dictionary<string, double> Tiles { get; set; } = new();
        public List<HomeTodoDto> Todo { get; set; } = [];
        public List<AwayDto> AwayToday { get; set; } = [];
        public List<HomeProjectDto> MyProjects { get; set; } = [];
    }
}
