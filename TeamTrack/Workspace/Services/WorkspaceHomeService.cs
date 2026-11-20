using Microsoft.EntityFrameworkCore;
using TeamTrack.Data;
using TeamTrack.Workspace.Data;
using TeamTrack.Workspace.Dtos;
using TeamTrack.Workspace.Models;

namespace TeamTrack.Workspace.Services
{
    public interface IWorkspaceHomeService
    {
        Task<WsResult<HomeDto>> GetAsync(CurrentUser user);
    }

    public class WorkspaceHomeService : IWorkspaceHomeService
    {
        private readonly WorkspaceDbContext _db;
        private readonly AppDbContext _app;
        private readonly ITimeOffService _timeOff;
        private readonly IWorkspaceProjectService _projects;
        private readonly IWorkspaceClock _clock;

        public WorkspaceHomeService(WorkspaceDbContext db, AppDbContext app, ITimeOffService timeOff,
            IWorkspaceProjectService projects, IWorkspaceClock clock)
        {
            _db = db;
            _app = app;
            _timeOff = timeOff;
            _projects = projects;
            _clock = clock;
        }

        public async Task<WsResult<HomeDto>> GetAsync(CurrentUser user)
        {
            var today = _clock.Today;
            var (monthStart, monthEnd) = _clock.MonthRangeUtc(today);
            var away = await _timeOff.AwayTodayAsync();

            var home = new HomeDto
            {
                Today = today,
                Role = user.IsPM ? CurrentUser.PmRole : "Employee",
                AwayToday = away,
            };

            var projects = await _projects.ListAsync(user, mineOnly: true);
            if (projects.Success)
                home.MyProjects = projects.Data!.Select(p => new HomeProjectDto { Id = p.Id, Name = p.Name, ProgressPercent = p.ProgressPercent }).ToList();

            if (user.IsPM) await FillPmAsync(home, user, away.Select(a => a.UserId).Distinct().Count(), monthStart, monthEnd);
            else await FillEmployeeAsync(home, user, monthStart, monthEnd);

            return WsResult<HomeDto>.Ok(home);
        }

        // A PM cannot act on their own items, so own pending items are excluded from both tiles and to-dos.
        private async Task FillPmAsync(HomeDto home, CurrentUser user, int awayCount, DateTime monthStart, DateTime monthEnd)
        {
            var pendingTimeOff = await _db.TimeOffRequests
                .Where(r => r.Status == TimeOffStatus.Pending && r.UserId != user.Id)
                .OrderBy(r => r.CreatedAt).ToListAsync();
            var sentThisMonth = await _db.Campaigns.CountAsync(c => c.Status == CampaignStatus.Sent && c.SentAt >= monthStart && c.SentAt < monthEnd);

            home.Tiles["timeOffWaiting"] = pendingTimeOff.Count;
            home.Tiles["peopleAwayToday"] = awayCount;
            home.Tiles["mailsSentThisMonth"] = sentThisMonth;

            var requesterIds = pendingTimeOff.Select(r => r.UserId).Distinct().ToList();
            var names = await _app.Users.Where(u => requesterIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.Name);

            home.Todo.AddRange(pendingTimeOff.Select(r => new HomeTodoDto
            {
                Kind = "time_off", Id = r.Id, Link = $"/workspace/leave?request={r.Id}",
                Label = r.Kind == TimeOffKind.Leave
                    ? $"Approve leave: {names.GetValueOrDefault(r.UserId, "")} ({r.LeaveType}, {r.Days} day(s))"
                    : $"Approve permission: {names.GetValueOrDefault(r.UserId, "")} ({r.StartTime:HH:mm}–{r.EndTime:HH:mm} on {r.FromDate:dd MMM})",
            }));
        }

        private async Task FillEmployeeAsync(HomeDto home, CurrentUser user, DateTime monthStart, DateTime monthEnd)
        {
            var mine = await _db.Campaigns.Where(c => c.CreatedByUserId == user.Id).OrderBy(c => c.UpdatedAt).ToListAsync();
            var balances = await _timeOff.BalancesAsync(user.Id);

            home.Tiles["mySentThisMonth"] = mine.Count(c => c.Status == CampaignStatus.Sent && c.SentAt >= monthStart && c.SentAt < monthEnd);
            home.Tiles["casualLeaveLeft"] = balances.Leave.First(l => l.LeaveType == LeaveRules.Casual).Remaining;
            home.Tiles["permissionHoursLeft"] = balances.PermissionHoursLeftThisMonth;

            HomeTodoDto Todo(WsCampaign c, string prefix) => new()
            {
                Kind = "campaign", Id = c.Id, Label = $"{prefix}: {c.Title}", Link = $"/workspace/campaigns/{c.Id}",
            };

            home.Todo.AddRange(mine.Where(c => c.Status == CampaignStatus.Draft).Select(c => Todo(c, "Finish draft")));
        }
    }
}
