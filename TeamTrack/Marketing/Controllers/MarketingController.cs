using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TeamTrack.DTOs;
using TeamTrack.Marketing.Dtos;
using TeamTrack.Marketing.Services;
using TeamTrack.Workspace.Services;

namespace TeamTrack.Marketing.Controllers
{
    /// <summary>
    /// The Lead Desk API. Every answer is the usual ApiResponse envelope with the real HTTP status
    /// (400 validation, 403 not the owner / wrong role, 404, 409 already picked up), except the attachment download,
    /// which is the raw file. There is deliberately no [ApiController] here: it would turn binding errors into
    /// ProblemDetails, and the frontend's API client expects the envelope everywhere.
    /// </summary>
    [Authorize(Roles = Role)]
    [MarketingEnabled]
    [Route("api/marketing")]
    public class MarketingController : ControllerBase
    {
        public const string Role = "Marketing";
        public const long MaxUploadRequestBytes = 5 * MarketingService.MaxFileBytes + 1024 * 1024;

        private readonly IMarketingService _marketing;
        public MarketingController(IMarketingService marketing) => _marketing = marketing;

        private CurrentUser Me => CurrentUser.From(User);

        private IActionResult Respond<T>(WsResult<T> result)
            => result.Success
                ? Ok(ApiResponse<T>.SuccessResponse(result.Data!, result.Message))
                : StatusCode(result.Status, ApiResponse<T>.FailureResponse(result.Message));

        private IActionResult BadBody() => StatusCode(400, ApiResponse<string>.FailureResponse("The request body is missing or not valid."));

        [HttpGet("lookups")]
        public IActionResult Lookups() => Ok(ApiResponse<LookupsDto>.SuccessResponse(_marketing.Lookups()));

        [HttpGet("dashboard")]
        public async Task<IActionResult> Dashboard() => Respond(await _marketing.DashboardAsync(Me));

        [HttpGet("leads/queue")]
        public async Task<IActionResult> Queue([FromQuery] string? search, [FromQuery] string? channel, [FromQuery] string? campaign,
            [FromQuery] string? temperature, [FromQuery] string? stage, [FromQuery] string? sort, [FromQuery] string? dir)
            => Respond(await _marketing.QueueAsync(Me, new QueueQuery
            { Search = search, Channel = channel, Campaign = campaign, Temperature = temperature, Stage = stage, Sort = sort, Dir = dir }));

        /// <summary>Imports leads from an Excel file (.xlsx or .xls, form field "file") into the work queue.</summary>
        [HttpPost("leads/import")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(MarketingService.MaxImportBytes + 1024 * 1024)]
        public async Task<IActionResult> Import(IFormFile? file)
        {
            if (file == null) return StatusCode(400, ApiResponse<string>.FailureResponse("Choose an Excel file to upload."));
            return Respond(await _marketing.ImportAsync(Me, new IncomingFile(file.FileName, file.Length, file.OpenReadStream)));
        }

        /// <summary>The upload history (newest first), each with what can still be undone.</summary>
        [HttpGet("leads/imports")]
        public async Task<IActionResult> Imports() => Respond(await _marketing.ImportsAsync(Me));

        /// <summary>Undo an upload: deletes its leads that are still untouched in the queue.</summary>
        [HttpPost("leads/imports/{id:guid}/undo")]
        public async Task<IActionResult> UndoImport(Guid id) => Respond(await _marketing.UndoImportAsync(Me, id));

        /// <summary>Removes (deletes) a lead that is waiting in the work queue.</summary>
        [HttpDelete("leads/{id:guid}")]
        public async Task<IActionResult> Remove(Guid id) => Respond(await _marketing.RemoveFromQueueAsync(Me, id));

        [HttpGet("leads/bench")]
        public async Task<IActionResult> Bench([FromQuery] string? status) => Respond(await _marketing.BenchAsync(Me, status));

        [HttpGet("leads/{id:guid}")]
        public async Task<IActionResult> Get(Guid id) => Respond(await _marketing.GetAsync(Me, id));

        [HttpPost("leads/{id:guid}/pick-up")]
        public async Task<IActionResult> PickUp(Guid id) => Respond(await _marketing.PickUpAsync(Me, id));

        [HttpPost("leads/{id:guid}/return")]
        public async Task<IActionResult> Return(Guid id) => Respond(await _marketing.ReturnAsync(Me, id));

        [HttpPut("leads/{id:guid}/stage")]
        public async Task<IActionResult> Stage(Guid id, [FromBody] StageInput? body)
            => body == null ? BadBody() : Respond(await _marketing.SetStageAsync(Me, id, body.Stage));

        [HttpPut("leads/{id:guid}/score")]
        public async Task<IActionResult> Score(Guid id, [FromBody] ScoreInput? body)
            => body == null ? BadBody() : Respond(await _marketing.SetScoreAsync(Me, id, body.Score));

        [HttpPost("leads/{id:guid}/close")]
        public async Task<IActionResult> Close(Guid id, [FromBody] CloseInput? body)
            => body == null ? BadBody() : Respond(await _marketing.CloseAsync(Me, id, body.Reason));

        /// <summary>Log an email, reply, call or note, with up to 5 files (10 MB each).</summary>
        [HttpPost("leads/{id:guid}/history")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(MaxUploadRequestBytes)]
        public async Task<IActionResult> AddHistory(Guid id, [FromForm] HistoryForm? form)
        {
            if (form == null) return BadBody();
            var input = new HistoryInput
            {
                Kind = form.Kind, OccurredAt = form.OccurredAt, Subject = form.Subject, Body = form.Body, Text = form.Text,
                FilesMeta = form.FilesMeta,
                Files = (form.Files ?? []).Select(f => new IncomingFile(f.FileName, f.Length, f.OpenReadStream)).ToList(),
            };
            return Respond(await _marketing.AddHistoryAsync(Me, id, input));
        }

        /// <summary>The raw file (not the envelope) when the caller may see the lead it belongs to.</summary>
        [HttpGet("attachments/{id:guid}")]
        public async Task<IActionResult> Attachment(Guid id)
        {
            var result = await _marketing.GetAttachmentAsync(Me, id);
            if (!result.Success) return Respond(result);
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(result.Data!.Content, result.Data.ContentType, result.Data.FileName); // sets Content-Disposition: attachment; filename=...
        }
    }
}
