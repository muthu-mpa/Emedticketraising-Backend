using Microsoft.EntityFrameworkCore;
using TeamTrack.Data;
using TeamTrack.Workspace.Dtos;

namespace TeamTrack.Workspace.Services
{
    public interface IPeopleService
    {
        /// <summary>role: Employee | ProductManager (a "Both" user matches either). search: name or role.</summary>
        Task<WsResult<PeopleListDto>> ListAsync(CurrentUser user, string? role = null, string? search = null);
        Task<WsResult<PersonProfileDto>> ProfileAsync(CurrentUser user, int personId);
    }

    /// <summary>Read-only directory over the existing users table. There is no team/location column, so none is exposed.</summary>
    public class PeopleService : IPeopleService
    {
        private readonly AppDbContext _app;
        private readonly ITimeOffService _timeOff;

        public PeopleService(AppDbContext app, ITimeOffService timeOff)
        {
            _app = app;
            _timeOff = timeOff;
        }

        private const string EmployeeRole = "Employee";

        public async Task<WsResult<PeopleListDto>> ListAsync(CurrentUser user, string? role = null, string? search = null)
        {
            if (!string.IsNullOrWhiteSpace(role) && role != EmployeeRole && role != CurrentUser.PmRole)
                return WsResult<PeopleListDto>.Fail(400, "Role must be Employee or ProductManager.");

            var users = await _app.Users.Where(u => u.IsActive).OrderBy(u => u.Name).ToListAsync();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                // "name or role": the role text people see is "Employee" / "Product Manager"
                users = users.Where(u => u.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                    || RoleLabel(u.UserType).Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            static bool Is(TeamTrack.Models.User u, string r) => u.UserType == r || u.UserType == "Both";
            var counts = new Dictionary<string, int>
            {
                ["all"] = users.Count,
                [EmployeeRole] = users.Count(u => Is(u, EmployeeRole)),
                [CurrentUser.PmRole] = users.Count(u => Is(u, CurrentUser.PmRole)),
            };
            if (!string.IsNullOrWhiteSpace(role)) users = users.Where(u => Is(u, role)).ToList();

            var projectsByUser = await ProjectNamesAsync(users.Select(u => u.Id).ToList());
            var away = (await _timeOff.AwayTodayAsync()).ToDictionary(a => a.UserId);

            var items = users.Select(u => Fill(new PersonDto(), u, projectsByUser, away)).ToList();
            return WsResult<PeopleListDto>.Ok(new PeopleListDto { TotalCount = counts["all"], RoleCounts = counts, Items = items });
        }

        private static string RoleLabel(string userType) => userType switch
        {
            "ProductManager" => "Product Manager",
            "Both" => "Employee Product Manager",
            _ => "Employee",
        };

        public async Task<WsResult<PersonProfileDto>> ProfileAsync(CurrentUser user, int personId)
        {
            var person = await _app.Users.FirstOrDefaultAsync(u => u.Id == personId && u.IsActive);
            if (person == null) return WsResult<PersonProfileDto>.Fail(404, "Person not found.");

            var projectsByUser = await ProjectNamesAsync([personId]);
            var away = (await _timeOff.AwayTodayAsync()).ToDictionary(a => a.UserId);
            var profile = Fill(new PersonProfileDto(), person, projectsByUser, away);
            profile.Email = person.Email;
            profile.JoinedAt = person.CreatedAt;
            profile.UpcomingTimeOff = await _timeOff.UpcomingAsync(user, personId);
            if (user.IsPM || user.Id == personId) profile.Balances = await _timeOff.BalancesAsync(personId);
            return WsResult<PersonProfileDto>.Ok(profile);
        }

        private async Task<Dictionary<int, List<string>>> ProjectNamesAsync(List<int> userIds)
        {
            var projects = await _app.Projects.Include(p => p.AssignedEmployees).OrderBy(p => p.Name).ToListAsync();
            var map = new Dictionary<int, List<string>>();
            foreach (var p in projects)
                foreach (var m in p.AssignedEmployees.Where(m => userIds.Contains(m.Id)))
                    (map.TryGetValue(m.Id, out var list) ? list : map[m.Id] = []).Add(p.Name);
            return map;
        }

        private static T Fill<T>(T dto, TeamTrack.Models.User u, Dictionary<int, List<string>> projects, Dictionary<int, AwayDto> away) where T : PersonDto
        {
            dto.Id = u.Id; dto.Name = u.Name; dto.Role = u.UserType; dto.ProfilePicture = u.ProfilePicture;
            dto.Projects = projects.GetValueOrDefault(u.Id) ?? [];
            if (away.TryGetValue(u.Id, out var a))
            {
                dto.TodayStatus = a.Kind == "leave" ? "on_leave" : "away";
                dto.TodayLabel = a.Label;
            }
            return dto;
        }
    }
}
