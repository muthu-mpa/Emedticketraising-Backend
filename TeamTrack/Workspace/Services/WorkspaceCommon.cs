using System.Security.Claims;

namespace TeamTrack.Workspace.Services
{
    /// <summary>The caller, as seen by the services. IsPM reflects the role currently in the JWT.</summary>
    public sealed record CurrentUser(int Id, string Name, bool IsPM)
    {
        public const string PmRole = "ProductManager";

        public static CurrentUser From(ClaimsPrincipal principal)
        {
            var id = int.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var parsed) ? parsed : 0;
            var name = principal.FindFirst(ClaimTypes.Name)?.Value ?? "";
            return new CurrentUser(id, name, principal.IsInRole(PmRole));
        }
    }

    /// <summary>Service outcome mapped to an HTTP status by the controllers.</summary>
    public sealed record WsResult<T>(bool Success, T? Data, string Message, int Status)
    {
        public static WsResult<T> Ok(T data, string message = "Success") => new(true, data, message, 200);
        public static WsResult<T> Fail(int status, string message) => new(false, default, message, status);
    }

    public interface IWorkspaceClock
    {
        DateTime UtcNow { get; }
        /// <summary>Today in the company time zone.</summary>
        DateOnly Today { get; }
        /// <summary>[start, end) of the calendar month containing <paramref name="day"/>, as UTC instants.</summary>
        (DateTime StartUtc, DateTime EndUtc) MonthRangeUtc(DateOnly day);
    }

    public sealed class WorkspaceClock : IWorkspaceClock
    {
        private readonly TimeZoneInfo _zone;

        public WorkspaceClock(IConfiguration config, ILogger<WorkspaceClock> logger)
        {
            var id = config["Workspace:TimeZone"] ?? "Asia/Kolkata";
            try { _zone = TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Workspace time zone '{Zone}' not found; falling back to UTC.", id);
                _zone = TimeZoneInfo.Utc;
            }
        }

        public DateTime UtcNow => DateTime.UtcNow;

        public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _zone));

        public (DateTime StartUtc, DateTime EndUtc) MonthRangeUtc(DateOnly day)
        {
            var start = new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
            return (TimeZoneInfo.ConvertTimeToUtc(start, _zone), TimeZoneInfo.ConvertTimeToUtc(start.AddMonths(1), _zone));
        }
    }
}
