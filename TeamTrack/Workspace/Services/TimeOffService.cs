using Microsoft.EntityFrameworkCore;
using TeamTrack.Data;
using TeamTrack.Workspace.Data;
using TeamTrack.Workspace.Dtos;
using TeamTrack.Workspace.Models;

namespace TeamTrack.Workspace.Services
{
    public interface ITimeOffService
    {
        Task<WsResult<TimeOffDto>> RequestLeaveAsync(CurrentUser user, LeaveRequestInput input);
        Task<WsResult<TimeOffDto>> RequestPermissionAsync(CurrentUser user, PermissionRequestInput input);
        Task<WsResult<TimeOffDto>> ApproveAsync(CurrentUser user, int id);
        Task<WsResult<TimeOffDto>> RejectAsync(CurrentUser user, int id, string? reason);
        Task<WsResult<TimeOffDto>> CancelAsync(CurrentUser user, int id);
        Task<WsResult<List<TimeOffDto>>> MineAsync(CurrentUser user);
        Task<WsResult<TeamTimeOffDto>> TeamAsync(CurrentUser user);
        Task<WsResult<List<CalendarEntryDto>>> CalendarAsync(int year, int month);
        Task<WsResult<List<OverlapInfoDto>>> OverlapsAsync(CurrentUser user, DateOnly from, DateOnly to);
        Task<BalancesDto> BalancesAsync(int userId);
        Task<List<AwayDto>> AwayTodayAsync();
        /// <summary>A person's upcoming pending + approved time off. Reasons are only shown to PMs and the person themselves.</summary>
        Task<List<TimeOffDto>> UpcomingAsync(CurrentUser viewer, int personId);
    }

    public class TimeOffService : ITimeOffService
    {
        private readonly WorkspaceDbContext _db;
        private readonly AppDbContext _app;
        private readonly INotificationService _notifications;
        private readonly IWorkspaceClock _clock;

        public TimeOffService(WorkspaceDbContext db, AppDbContext app, INotificationService notifications, IWorkspaceClock clock)
        {
            _db = db;
            _app = app;
            _notifications = notifications;
            _clock = clock;
        }

        // ---------- requests ----------

        public async Task<WsResult<TimeOffDto>> RequestLeaveAsync(CurrentUser user, LeaveRequestInput input)
        {
            if (input.FromDate == default || input.ToDate == default)
                return WsResult<TimeOffDto>.Fail(400, "From and to dates are required.");
            var type = input.LeaveType?.Trim().ToLowerInvariant();

            var own = await _db.TimeOffRequests
                .Where(r => r.UserId == user.Id && r.Kind == TimeOffKind.Leave
                            && (r.Status == TimeOffStatus.Pending || r.Status == TimeOffStatus.Approved))
                .ToListAsync();
            var approvedOfType = own.Where(r => r.Status == TimeOffStatus.Approved && r.LeaveType == type).ToList();

            var error = LeaveRules.ValidateLeave(type, input.FromDate, input.ToDate, input.Reason,
                year => approvedOfType.Sum(r => LeaveRules.DaysInYear(r.FromDate, r.ToDate, year)),
                own.Select(r => (r.FromDate, r.ToDate)));
            if (error != null) return WsResult<TimeOffDto>.Fail(400, error);

            var request = new WsTimeOffRequest
            {
                Kind = TimeOffKind.Leave, UserId = user.Id, LeaveType = type,
                FromDate = input.FromDate, ToDate = input.ToDate,
                Days = LeaveRules.CountDays(input.FromDate, input.ToDate),
                Reason = input.Reason!.Trim(), CreatedAt = DateTime.UtcNow,
            };
            _db.TimeOffRequests.Add(request);
            await _db.SaveChangesAsync(); // need the id for the notification link

            _notifications.Queue(await _notifications.PmIdsAsync(user.Id), "time_off_requested",
                $"{user.Name} requested {request.Days} day(s) of {type} leave ({request.FromDate:dd MMM} – {request.ToDate:dd MMM}).",
                Link(request));
            await _db.SaveChangesAsync();
            return WsResult<TimeOffDto>.Ok(await ToDtoAsync(request, user), "Leave requested.");
        }

        public async Task<WsResult<TimeOffDto>> RequestPermissionAsync(CurrentUser user, PermissionRequestInput input)
        {
            if (input.Date == default) return WsResult<TimeOffDto>.Fail(400, "A date is required.");
            if (!LeaveRules.TryParseTime(input.StartTime, out var start) || !LeaveRules.TryParseTime(input.EndTime, out var end))
                return WsResult<TimeOffDto>.Fail(400, "Start and end times are required (HH:mm).");

            var monthStart = new DateOnly(input.Date.Year, input.Date.Month, 1);
            var monthEnd = monthStart.AddMonths(1);
            var usedMinutes = await _db.TimeOffRequests
                .Where(r => r.UserId == user.Id && r.Kind == TimeOffKind.Permission
                            && (r.Status == TimeOffStatus.Pending || r.Status == TimeOffStatus.Approved)
                            && r.FromDate >= monthStart && r.FromDate < monthEnd)
                .SumAsync(r => (int?)r.Minutes) ?? 0;

            var error = LeaveRules.ValidatePermission(start, end, input.Reason, usedMinutes);
            if (error != null) return WsResult<TimeOffDto>.Fail(400, error);

            var request = new WsTimeOffRequest
            {
                Kind = TimeOffKind.Permission, UserId = user.Id,
                FromDate = input.Date, ToDate = input.Date, StartTime = start, EndTime = end,
                Minutes = (int)(end - start).TotalMinutes,
                Reason = input.Reason!.Trim(), CreatedAt = DateTime.UtcNow,
            };
            _db.TimeOffRequests.Add(request);
            await _db.SaveChangesAsync();

            _notifications.Queue(await _notifications.PmIdsAsync(user.Id), "time_off_requested",
                $"{user.Name} asked for permission on {request.FromDate:dd MMM} ({start:HH:mm}–{end:HH:mm}).",
                Link(request));
            await _db.SaveChangesAsync();
            return WsResult<TimeOffDto>.Ok(await ToDtoAsync(request, user), "Permission requested.");
        }

        public async Task<WsResult<TimeOffDto>> ApproveAsync(CurrentUser user, int id)
        {
            var r = await _db.TimeOffRequests.FindAsync(id);
            if (r == null) return NotFound();
            var denied = CheckDecider(user, r, "approve");
            if (denied != null) return denied;

            if (r.Kind == TimeOffKind.Leave)
            {
                // Balance is only consumed on approval, so re-check it now.
                var approved = await _db.TimeOffRequests
                    .Where(x => x.UserId == r.UserId && x.Kind == TimeOffKind.Leave && x.LeaveType == r.LeaveType
                                && x.Status == TimeOffStatus.Approved)
                    .ToListAsync();
                var error = LeaveRules.CheckBalance(r.LeaveType!, r.FromDate, r.ToDate,
                    year => approved.Sum(a => LeaveRules.DaysInYear(a.FromDate, a.ToDate, year)));
                if (error != null) return WsResult<TimeOffDto>.Fail(409, error);
            }

            r.Status = TimeOffStatus.Approved;
            r.DecidedByUserId = user.Id;
            r.DecidedAt = DateTime.UtcNow;
            _notifications.Queue([r.UserId], "time_off_approved", $"{user.Name} approved your {Describe(r)}.", Link(r));
            await _db.SaveChangesAsync();
            return WsResult<TimeOffDto>.Ok(await ToDtoAsync(r, user), "Approved.");
        }

        public async Task<WsResult<TimeOffDto>> RejectAsync(CurrentUser user, int id, string? reason)
        {
            var r = await _db.TimeOffRequests.FindAsync(id);
            if (r == null) return NotFound();
            var denied = CheckDecider(user, r, "reject");
            if (denied != null) return denied;
            if (string.IsNullOrWhiteSpace(reason)) return WsResult<TimeOffDto>.Fail(400, "A reason is required to reject.");
            if (reason.Trim().Length > 1000) return WsResult<TimeOffDto>.Fail(400, "The reason is too long (max 1000 characters).");

            r.Status = TimeOffStatus.Rejected;
            r.DecisionReason = reason.Trim();
            r.DecidedByUserId = user.Id;
            r.DecidedAt = DateTime.UtcNow;
            _notifications.Queue([r.UserId], "time_off_rejected",
                $"{user.Name} rejected your {Describe(r)}: {r.DecisionReason}", Link(r));
            await _db.SaveChangesAsync();
            return WsResult<TimeOffDto>.Ok(await ToDtoAsync(r, user), "Rejected.");
        }

        public async Task<WsResult<TimeOffDto>> CancelAsync(CurrentUser user, int id)
        {
            var r = await _db.TimeOffRequests.FindAsync(id);
            if (r == null) return NotFound();
            if (r.UserId != user.Id) return WsResult<TimeOffDto>.Fail(403, "You can only cancel your own requests.");
            if (r.Status != TimeOffStatus.Pending) return WsResult<TimeOffDto>.Fail(409, "Only a pending request can be cancelled.");

            r.Status = TimeOffStatus.Cancelled;
            await _db.SaveChangesAsync();
            return WsResult<TimeOffDto>.Ok(await ToDtoAsync(r, user), "Cancelled.");
        }

        // ---------- queries ----------

        public async Task<WsResult<List<TimeOffDto>>> MineAsync(CurrentUser user)
        {
            var rows = await _db.TimeOffRequests.Where(r => r.UserId == user.Id)
                .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).ToListAsync();
            return WsResult<List<TimeOffDto>>.Ok(await ToDtosAsync(rows, user));
        }

        public async Task<WsResult<TeamTimeOffDto>> TeamAsync(CurrentUser user)
        {
            if (!user.IsPM) return WsResult<TeamTimeOffDto>.Fail(403, "Only a Product Manager can see team requests.");

            // A PM's own requests are decided by the other PMs, so they never show up here.
            var waiting = await _db.TimeOffRequests
                .Where(r => r.Status == TimeOffStatus.Pending && r.UserId != user.Id)
                .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync();
            var recent = await _db.TimeOffRequests
                .Where(r => (r.Status == TimeOffStatus.Approved || r.Status == TimeOffStatus.Rejected) && r.UserId != user.Id)
                .OrderByDescending(r => r.DecidedAt).ThenByDescending(r => r.Id).Take(6).ToListAsync();

            return WsResult<TeamTimeOffDto>.Ok(new TeamTimeOffDto
            {
                Waiting = await ToDtosAsync(waiting, user),
                RecentDecisions = await ToDtosAsync(recent, user),
                AwayToday = await AwayTodayAsync(),
            });
        }

        public async Task<WsResult<List<CalendarEntryDto>>> CalendarAsync(int year, int month)
        {
            if (month < 1 || month > 12 || year < 2000 || year > 2100)
                return WsResult<List<CalendarEntryDto>>.Fail(400, "Invalid year or month.");

            var first = new DateOnly(year, month, 1);
            var last = first.AddMonths(1).AddDays(-1);
            var rows = await _db.TimeOffRequests
                .Where(r => (r.Status == TimeOffStatus.Pending || r.Status == TimeOffStatus.Approved)
                            && r.FromDate <= last && r.ToDate >= first)
                .OrderBy(r => r.FromDate).ThenBy(r => r.Id).ToListAsync();
            var names = await NamesAsync(rows.Select(r => r.UserId));

            return WsResult<List<CalendarEntryDto>>.Ok(rows.Select(r => new CalendarEntryDto
            {
                RequestId = r.Id, UserId = r.UserId, UserName = names.GetValueOrDefault(r.UserId, ""),
                Kind = r.Kind, LeaveType = r.LeaveType, Status = r.Status,
                FromDate = r.FromDate, ToDate = r.ToDate,
                StartTime = Fmt(r.StartTime), EndTime = Fmt(r.EndTime),
            }).ToList());
        }

        /// <summary>Info only (not a block): other people on approved leave during the range.</summary>
        public async Task<WsResult<List<OverlapInfoDto>>> OverlapsAsync(CurrentUser user, DateOnly from, DateOnly to)
        {
            if (to < from) return WsResult<List<OverlapInfoDto>>.Fail(400, "The end date cannot be before the start date.");
            var rows = await _db.TimeOffRequests
                .Where(r => r.Kind == TimeOffKind.Leave && r.Status == TimeOffStatus.Approved && r.UserId != user.Id
                            && r.FromDate <= to && r.ToDate >= from)
                .OrderBy(r => r.FromDate).ToListAsync();
            var names = await NamesAsync(rows.Select(r => r.UserId));
            return WsResult<List<OverlapInfoDto>>.Ok(rows.Select(r => new OverlapInfoDto
            {
                UserId = r.UserId, UserName = names.GetValueOrDefault(r.UserId, ""), FromDate = r.FromDate, ToDate = r.ToDate,
            }).ToList());
        }

        public async Task<BalancesDto> BalancesAsync(int userId)
        {
            var today = _clock.Today;
            var approvedLeave = await _db.TimeOffRequests
                .Where(r => r.UserId == userId && r.Kind == TimeOffKind.Leave && r.Status == TimeOffStatus.Approved)
                .ToListAsync();

            var dto = new BalancesDto { Year = today.Year };
            foreach (var (type, quota) in LeaveRules.YearlyQuota)
            {
                var used = approvedLeave.Where(r => r.LeaveType == type).Sum(r => LeaveRules.DaysInYear(r.FromDate, r.ToDate, today.Year));
                dto.Leave.Add(new LeaveBalanceDto { LeaveType = type, Quota = quota, Used = used, Remaining = Math.Max(quota - used, 0) });
            }

            var monthStart = new DateOnly(today.Year, today.Month, 1);
            var monthEnd = monthStart.AddMonths(1);
            dto.PermissionMinutesUsedThisMonth = await _db.TimeOffRequests
                .Where(r => r.UserId == userId && r.Kind == TimeOffKind.Permission
                            && (r.Status == TimeOffStatus.Pending || r.Status == TimeOffStatus.Approved)
                            && r.FromDate >= monthStart && r.FromDate < monthEnd)
                .SumAsync(r => (int?)r.Minutes) ?? 0;
            dto.PermissionHoursLeftThisMonth = Math.Max(LeaveRules.PermissionMaxMinutesPerMonth - dto.PermissionMinutesUsedThisMonth, 0) / 60.0;
            return dto;
        }

        public async Task<List<AwayDto>> AwayTodayAsync()
        {
            var today = _clock.Today;
            var rows = await _db.TimeOffRequests
                .Where(r => r.Status == TimeOffStatus.Approved && r.FromDate <= today && r.ToDate >= today)
                .ToListAsync();
            var names = await NamesAsync(rows.Select(r => r.UserId));

            return rows
                .OrderBy(r => r.Kind == TimeOffKind.Leave ? 0 : 1) // leave wins over permission for the same person
                .GroupBy(r => r.UserId).Select(g => g.First())
                .Select(r => new AwayDto
                {
                    UserId = r.UserId, UserName = names.GetValueOrDefault(r.UserId, ""), Kind = r.Kind, LeaveType = r.LeaveType,
                    StartTime = Fmt(r.StartTime), EndTime = Fmt(r.EndTime),
                    Label = r.Kind == TimeOffKind.Leave ? "On leave" : $"Away {Fmt(r.StartTime)}–{Fmt(r.EndTime)}",
                })
                .OrderBy(a => a.UserName).ToList();
        }

        public async Task<List<TimeOffDto>> UpcomingAsync(CurrentUser viewer, int personId)
        {
            var today = _clock.Today;
            var rows = await _db.TimeOffRequests
                .Where(r => r.UserId == personId && r.ToDate >= today
                            && (r.Status == TimeOffStatus.Pending || r.Status == TimeOffStatus.Approved))
                .OrderBy(r => r.FromDate).ThenBy(r => r.Id).Take(20).ToListAsync();
            var dtos = await ToDtosAsync(rows, viewer);
            if (!viewer.IsPM && viewer.Id != personId)
                foreach (var d in dtos) d.Reason = "";
            return dtos;
        }

        // ---------- helpers ----------

        private static WsResult<TimeOffDto> NotFound() => WsResult<TimeOffDto>.Fail(404, "Request not found.");

        private static string Link(WsTimeOffRequest r) => $"/workspace/leave?request={r.Id}";

        private static string? Fmt(TimeOnly? t) => t?.ToString("HH:mm");

        private static string Describe(WsTimeOffRequest r) => r.Kind == TimeOffKind.Leave
            ? $"{r.LeaveType} leave ({r.FromDate:dd MMM} – {r.ToDate:dd MMM})"
            : $"permission on {r.FromDate:dd MMM} ({r.StartTime:HH:mm}–{r.EndTime:HH:mm})";

        private static WsResult<TimeOffDto>? CheckDecider(CurrentUser user, WsTimeOffRequest r, string verb)
        {
            if (!user.IsPM) return WsResult<TimeOffDto>.Fail(403, $"Only a Product Manager can {verb} requests.");
            if (r.UserId == user.Id) return WsResult<TimeOffDto>.Fail(403, $"You cannot {verb} your own request; another Product Manager must.");
            if (r.Status != TimeOffStatus.Pending) return WsResult<TimeOffDto>.Fail(409, "This request is no longer pending.");
            return null;
        }

        private async Task<Dictionary<int, string>> NamesAsync(IEnumerable<int> ids)
        {
            var distinct = ids.Distinct().ToList();
            return await _app.Users.Where(u => distinct.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name);
        }

        private async Task<List<TimeOffDto>> ToDtosAsync(List<WsTimeOffRequest> rows, CurrentUser viewer)
        {
            var names = await NamesAsync(rows.Select(r => r.UserId).Concat(rows.Where(r => r.DecidedByUserId != null).Select(r => r.DecidedByUserId!.Value)));
            return rows.Select(r => Map(r, viewer, names)).ToList();
        }

        private async Task<TimeOffDto> ToDtoAsync(WsTimeOffRequest r, CurrentUser viewer)
            => (await ToDtosAsync([r], viewer))[0];

        private static TimeOffDto Map(WsTimeOffRequest r, CurrentUser v, Dictionary<int, string> names) => new()
        {
            Id = r.Id, Kind = r.Kind, UserId = r.UserId, UserName = names.GetValueOrDefault(r.UserId, ""),
            LeaveType = r.LeaveType, FromDate = r.FromDate, ToDate = r.ToDate, Days = r.Days,
            StartTime = Fmt(r.StartTime), EndTime = Fmt(r.EndTime), Minutes = r.Minutes,
            Reason = r.Reason, Status = r.Status, DecisionReason = r.DecisionReason,
            DecidedByName = r.DecidedByUserId is int d ? names.GetValueOrDefault(d) : null,
            DecidedAt = r.DecidedAt, CreatedAt = r.CreatedAt,
            CanCancel = r.UserId == v.Id && r.Status == TimeOffStatus.Pending,
            CanDecide = v.IsPM && r.UserId != v.Id && r.Status == TimeOffStatus.Pending,
        };
    }
}
