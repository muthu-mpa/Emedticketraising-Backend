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
        private readonly ICampaignImportService _imports;
        public CampaignsController(ICampaignService campaigns, ICampaignImportService imports)
        {
            _campaigns = campaigns;
            _imports = imports;
        }

        [HttpGet]
        public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] bool onlyMine = false, [FromQuery] string? search = null, [FromQuery] int? importId = null)
            => Respond(await _campaigns.ListAsync(Me, status, onlyMine, search, importId));

        [HttpGet("templates")]
        public IActionResult Templates()
            => Ok(ApiResponse<IReadOnlyList<CampaignTemplateDto>>.SuccessResponse(_campaigns.Templates()));

        [HttpGet("{id:int}")]
        public async Task<IActionResult> Get(int id) => Respond(await _campaigns.GetAsync(Me, id));

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CampaignInput input) => Respond(await _campaigns.CreateAsync(Me, input));

        [HttpPut("{id:int}")]
        public async Task<IActionResult> Update(int id, [FromBody] CampaignInput input) => Respond(await _campaigns.UpdateAsync(Me, id, input));

        [HttpPost("{id:int}/submit")]
        public async Task<IActionResult> Submit(int id) => Respond(await _campaigns.SubmitAsync(Me, id));

        [HttpPost("{id:int}/approve")]
        public async Task<IActionResult> Approve(int id) => Respond(await _campaigns.ApproveAsync(Me, id));

        [HttpPost("{id:int}/ask-changes")]
        public async Task<IActionResult> AskChanges(int id, [FromBody] NoteInput input) => Respond(await _campaigns.AskChangesAsync(Me, id, input.Note));

        [HttpPost("{id:int}/take-back")]
        public async Task<IActionResult> TakeBack(int id) => Respond(await _campaigns.TakeBackAsync(Me, id));

        [HttpPost("{id:int}/send")]
        public async Task<IActionResult> Send(int id, [FromBody] SendCampaignInput input) => Respond(await _campaigns.SendAsync(Me, id, input.Confirm));

        // ---------- bulk ----------

        /// <summary>Send many drafts for approval. Each one follows the same rules as the single action; the result lists success or error per id.</summary>
        [HttpPost("bulk/submit")]
        public async Task<IActionResult> BulkSubmit([FromBody] BulkIdsInput input) => Respond(await _campaigns.BulkSubmitAsync(Me, input.Ids));

        /// <summary>Approve many pending mails (Product Manager). Per-item success or error, as for the single action.</summary>
        [HttpPost("bulk/approve")]
        public async Task<IActionResult> BulkApprove([FromBody] BulkIdsInput input) => Respond(await _campaigns.BulkApproveAsync(Me, input.Ids));

        // ---------- Excel import ----------

        /// <summary>Parse an .xlsx (max 5 MB, 500 rows) and return the rows with ERROR / WARNING flags. Nothing is saved.</summary>
        [HttpPost("import/preview")]
        [RequestSizeLimit(CampaignImportService.MaxFileBytes + 1024 * 1024)]
        public async Task<IActionResult> ImportPreview(IFormFile? file) => Respond(await _imports.PreviewAsync(Me, file));

        /// <summary>Create drafts for the selected rows, from the previewToken or by sending the file again.</summary>
        [HttpPost("import/commit")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(CampaignImportService.MaxFileBytes + 1024 * 1024)]
        public async Task<IActionResult> ImportCommit([FromForm] ImportCommitForm form)
            => Respond(await _imports.CommitAsync(Me, form.PreviewToken, form.File, form.SelectedRows));

        /// <summary>The same commit as JSON: { previewToken, selectedRows }. Hidden from Swagger, which shows the form version of this URL.</summary>
        [HttpPost("import/commit")]
        [Consumes("application/json")]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IActionResult> ImportCommitJson([FromBody] ImportCommitJson body)
            => Respond(await _imports.CommitAsync(Me, body.PreviewToken, null, body.SelectedRows));

        /// <summary>Download the simple template (Campaign name | Client | Contact person | Client email | Subject | Message).</summary>
        [HttpGet("import/template")]
        public IActionResult ImportTemplate()
            => File(_imports.Template(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "campaign-import-template.xlsx");

        /// <summary>Import batches, newest first: file name, who, when, counts. Use a batch id as ?importId= on the campaign list.</summary>
        [HttpGet("imports")]
        public async Task<IActionResult> Imports([FromQuery] int take = 50) => Respond(await _imports.ListBatchesAsync(Me, take));
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
