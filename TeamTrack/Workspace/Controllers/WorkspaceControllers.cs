using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TeamTrack.DTOs;
using TeamTrack.Workspace.Dtos;
using TeamTrack.Workspace.Services;

namespace TeamTrack.Workspace.Controllers
{
    /// <summary>Common base: auth, feature gate, and WsResult -> HTTP mapping.</summary>
    [ApiController]
    [Authorize]
    [WorkspaceEnabled]
    public abstract class WorkspaceControllerBase : ControllerBase
    {
        protected CurrentUser Me => CurrentUser.From(User);

        protected IActionResult Respond<T>(WsResult<T> result)
            => result.Success
                ? Ok(ApiResponse<T>.SuccessResponse(result.Data!, result.Message))
                : StatusCode(result.Status, ApiResponse<T>.FailureResponse(result.Message));
    }

    [Route("api/workspace/home")]
    public class WorkspaceHomeController : WorkspaceControllerBase
    {
        private readonly IWorkspaceHomeService _home;
        public WorkspaceHomeController(IWorkspaceHomeService home) => _home = home;

        [HttpGet]
        public async Task<IActionResult> Get() => Respond(await _home.GetAsync(Me));
    }

    [Route("api/workspace/campaigns")]
    public class CampaignsController : WorkspaceControllerBase
    {
        private readonly ICampaignService _campaigns;
        public CampaignsController(ICampaignService campaigns) => _campaigns = campaigns;

        [HttpGet]
        public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] bool onlyMine = false, [FromQuery] string? search = null)
            => Respond(await _campaigns.ListAsync(Me, status, onlyMine, search));

        [HttpGet("{id:int}")]
        public async Task<IActionResult> Get(int id) => Respond(await _campaigns.GetAsync(Me, id));

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CampaignInput input) => Respond(await _campaigns.CreateAsync(Me, input));

        [HttpPut("{id:int}")]
        public async Task<IActionResult> Update(int id, [FromBody] CampaignInput input) => Respond(await _campaigns.UpdateAsync(Me, id, input));

        [HttpPost("{id:int}/send")]
        public async Task<IActionResult> Send(int id, [FromBody] SendCampaignInput input) => Respond(await _campaigns.SendAsync(Me, id, input.Confirm));
    }

    [Route("api/workspace/timeoff")]
    public class TimeOffController : WorkspaceControllerBase
    {
        private readonly ITimeOffService _timeOff;
        public TimeOffController(ITimeOffService timeOff) => _timeOff = timeOff;

        [HttpPost("leave")]
        public async Task<IActionResult> RequestLeave([FromBody] LeaveRequestInput input) => Respond(await _timeOff.RequestLeaveAsync(Me, input));

        [HttpPost("permission")]
        public async Task<IActionResult> RequestPermission([FromBody] PermissionRequestInput input) => Respond(await _timeOff.RequestPermissionAsync(Me, input));

        [HttpPost("{id:int}/approve")]
        public async Task<IActionResult> Approve(int id) => Respond(await _timeOff.ApproveAsync(Me, id));

        [HttpPost("{id:int}/reject")]
        public async Task<IActionResult> Reject(int id, [FromBody] ReasonInput input) => Respond(await _timeOff.RejectAsync(Me, id, input.Reason));

        [HttpPost("{id:int}/cancel")]
        public async Task<IActionResult> Cancel(int id) => Respond(await _timeOff.CancelAsync(Me, id));

        [HttpGet("mine")]
        public async Task<IActionResult> Mine() => Respond(await _timeOff.MineAsync(Me));

        [HttpGet("team")]
        public async Task<IActionResult> Team() => Respond(await _timeOff.TeamAsync(Me));

        [HttpGet("balances")]
        public async Task<IActionResult> Balances()
            => Ok(ApiResponse<BalancesDto>.SuccessResponse(await _timeOff.BalancesAsync(Me.Id)));

        [HttpGet("calendar")]
        public async Task<IActionResult> Calendar([FromQuery] int year, [FromQuery] int month) => Respond(await _timeOff.CalendarAsync(year, month));

        [HttpGet("overlaps")]
        public async Task<IActionResult> Overlaps([FromQuery] DateOnly from, [FromQuery] DateOnly to) => Respond(await _timeOff.OverlapsAsync(Me, from, to));

        [HttpGet("away-today")]
        public async Task<IActionResult> AwayToday()
            => Ok(ApiResponse<List<AwayDto>>.SuccessResponse(await _timeOff.AwayTodayAsync()));
    }

    [Route("api/workspace/people")]
    public class PeopleController : WorkspaceControllerBase
    {
        private readonly IPeopleService _people;
        public PeopleController(IPeopleService people) => _people = people;

        [HttpGet]
        public async Task<IActionResult> List([FromQuery] string? role = null, [FromQuery] string? search = null) => Respond(await _people.ListAsync(Me, role, search));

        [HttpGet("{id:int}")]
        public async Task<IActionResult> Profile(int id) => Respond(await _people.ProfileAsync(Me, id));
    }

    [Route("api/workspace/projects")]
    public class WorkspaceProjectsController : WorkspaceControllerBase
    {
        private readonly IWorkspaceProjectService _projects;
        public WorkspaceProjectsController(IWorkspaceProjectService projects) => _projects = projects;

        [HttpGet]
        public async Task<IActionResult> List([FromQuery] bool mine = false) => Respond(await _projects.ListAsync(Me, mine));

        [HttpGet("{id:int}")]
        public async Task<IActionResult> Get(int id) => Respond(await _projects.GetAsync(Me, id));

        [HttpGet("{id:int}/documents")]
        public async Task<IActionResult> Documents(int id) => Respond(await _projects.ListDocumentsAsync(Me, id));

        [HttpGet("{id:int}/documents/{documentId:int}")]
        public async Task<IActionResult> Document(int id, int documentId) => Respond(await _projects.GetDocumentAsync(Me, id, documentId));

        [HttpPost("{id:int}/documents")]
        [RequestSizeLimit(WorkspaceProjectService.MaxFileBytes + 1024 * 1024)]
        public async Task<IActionResult> AddDocument(int id, [FromForm] DocumentForm form) => Respond(await _projects.AddDocumentAsync(Me, id, form));
    }

    [Route("api/workspace/notifications")]
    public class NotificationsController : WorkspaceControllerBase
    {
        private readonly INotificationService _notifications;
        public NotificationsController(INotificationService notifications) => _notifications = notifications;

        /// <summary>List + unread count. Polled every 60 s by the client; does not mark anything read.</summary>
        [HttpGet]
        public async Task<IActionResult> List([FromQuery] int take = 30)
            => Ok(ApiResponse<NotificationListDto>.SuccessResponse(await _notifications.ListAsync(Me.Id, take)));

        /// <summary>Called when the bell is opened.</summary>
        [HttpPost("read-all")]
        public async Task<IActionResult> ReadAll()
            => Ok(ApiResponse<int>.SuccessResponse(await _notifications.MarkAllReadAsync(Me.Id), "Marked as read."));
    }
}
