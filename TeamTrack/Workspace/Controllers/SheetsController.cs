using Microsoft.AspNetCore.Mvc;
using TeamTrack.DTOs;
using TeamTrack.Workspace.Dtos;
using TeamTrack.Workspace.Services;

namespace TeamTrack.Workspace.Controllers
{
    [Route("api/workspace/sheets")]
    public class SheetsController : WorkspaceControllerBase
    {
        private readonly ISharedSheetService _sheets;
        public SheetsController(ISharedSheetService sheets) => _sheets = sheets;

        /// <summary>Upload an .xlsx (max 10 MB) as a form, or send only a title for a blank sheet.</summary>
        [HttpPost]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(SharedSheetService.MaxFileBytes + 1024 * 1024)]
        public async Task<IActionResult> Create([FromForm] SheetCreateForm form) => Respond(await _sheets.CreateAsync(Me, form.Title, form.File));

        /// <summary>A blank sheet as JSON: { title }. Hidden from Swagger, which shows the form version of this URL.</summary>
        [HttpPost]
        [Consumes("application/json")]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IActionResult> CreateBlank([FromBody] SheetCreateJson body) => Respond(await _sheets.CreateAsync(Me, body.Title, null));

        /// <summary>Sheets I own or that are shared with me.</summary>
        [HttpGet]
        public async Task<IActionResult> List() => Respond(await _sheets.ListAsync(Me));

        [HttpGet("{id:int}")]
        public async Task<IActionResult> Get(int id) => Respond(await _sheets.GetAsync(Me, id));

        /// <summary>Tiny response for polling (every 20 s): { version, updatedBy, updatedAt }.</summary>
        [HttpGet("{id:int}/version")]
        public async Task<IActionResult> Version(int id) => Respond(await _sheets.VersionAsync(Me, id));

        /// <summary>Save cell changes made on top of baseVersion. A 409 (nothing applied) lists the cells someone else changed since.</summary>
        [HttpPatch("{id:int}")]
        public async Task<IActionResult> Patch(int id, [FromBody] SheetPatchInput input)
        {
            var result = await _sheets.PatchAsync(Me, id, input);
            if (!result.Success && result.Data != null)
                return StatusCode(result.Status, new ApiResponse<SheetPatchResultDto> { Success = false, Message = result.Message, Data = result.Data });
            return Respond(result);
        }

        /// <summary>Owner only. Replaces the list of people the sheet is shared with.</summary>
        [HttpPut("{id:int}/access")]
        public async Task<IActionResult> SetAccess(int id, [FromBody] List<SheetAccessInput> input) => Respond(await _sheets.SetAccessAsync(Me, id, input));

        /// <summary>Export the current data as .xlsx (values; formulas where stored). Formatting is not included.</summary>
        [HttpGet("{id:int}/download")]
        public async Task<IActionResult> Download(int id)
        {
            var result = await _sheets.DownloadAsync(Me, id);
            if (!result.Success) return Respond(result);
            return File(result.Data!.Bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", result.Data.FileName);
        }

        /// <summary>Who saved, when, and how many cells (newest first).</summary>
        [HttpGet("{id:int}/history")]
        public async Task<IActionResult> History(int id) => Respond(await _sheets.HistoryAsync(Me, id));

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete(int id) => Respond(await _sheets.DeleteAsync(Me, id));
    }
}
