using Microsoft.AspNetCore.Http;
using TeamTrack.Workspace.Services;

namespace TeamTrack.Workspace.Dtos
{
    // ---------- Excel import -> campaign drafts ----------

    public class ImportFlagDto
    {
        /// <summary>error | warning</summary>
        public string Level { get; set; } = "";
        /// <summary>email_missing | email_invalid | placeholder | subject_missing | body_missing | autonom | duplicate | truncated</summary>
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public class ImportRowDto
    {
        /// <summary>Row number in the Excel sheet. Pass these in selectedRows.</summary>
        public int RowNumber { get; set; }
        public string Title { get; set; } = "";
        public string ClientName { get; set; } = "";
        public string ContactName { get; set; } = "";
        public string ClientEmail { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
        public string? Segment { get; set; }
        public string? Priority { get; set; }
        public string? SendWave { get; set; }
        /// <summary>ok | warning | error. An error row can be imported but not sent for approval until fixed.</summary>
        public string Severity { get; set; } = "ok";
        public List<ImportFlagDto> Flags { get; set; } = [];
    }

    public class ImportPreviewDto
    {
        /// <summary>Send this to import/commit. Valid for 30 minutes, for the same user, once.</summary>
        public string PreviewToken { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
        public string FileName { get; set; } = "";
        /// <summary>prospect_workbook | simple_template</summary>
        public string Format { get; set; } = "";
        public int TotalRows { get; set; }
        public int OkRows { get; set; }
        public int WarningRows { get; set; }
        public int ErrorRows { get; set; }
        public List<ImportRowDto> Rows { get; set; } = [];
    }

    /// <summary>multipart/form-data: either previewToken, or the same file again.</summary>
    public class ImportCommitForm
    {
        public string? PreviewToken { get; set; }
        public IFormFile? File { get; set; }
        public List<int>? SelectedRows { get; set; }
    }

    public class ImportCommitJson
    {
        public string? PreviewToken { get; set; }
        public List<int>? SelectedRows { get; set; }
    }

    public class ImportCommitResultDto
    {
        public int ImportId { get; set; }
        public string FileName { get; set; } = "";
        public int ImportedRows { get; set; }
        public int SkippedRows { get; set; }
        public List<ImportedCampaignDto> Campaigns { get; set; } = [];
    }

    public class ImportedCampaignDto
    {
        public int RowNumber { get; set; }
        public int CampaignId { get; set; }
    }

    public class ImportBatchDto
    {
        public int Id { get; set; }
        public string FileName { get; set; } = "";
        public int ImportedBy { get; set; }
        public string ImportedByName { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public int TotalRows { get; set; }
        public int ImportedRows { get; set; }
    }

    // ---------- Bulk campaign actions ----------

    public class BulkIdsInput
    {
        public List<int>? Ids { get; set; }
    }

    public class BulkItemResultDto
    {
        public int Id { get; set; }
        public bool Success { get; set; }
        /// <summary>HTTP-style status of the single action (200, 400, 403, 404, 409 ...).</summary>
        public int Status { get; set; }
        /// <summary>The success message, or the error.</summary>
        public string Message { get; set; } = "";
        /// <summary>The campaign's status after the action, when it exists.</summary>
        public string? CampaignStatus { get; set; }
    }

    public class BulkResultDto
    {
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public List<BulkItemResultDto> Items { get; set; } = [];
    }

    // ---------- Shared sheets ----------

    /// <summary>multipart/form-data: an .xlsx file, or only a title for a blank sheet.</summary>
    public class SheetCreateForm
    {
        public string? Title { get; set; }
        public IFormFile? File { get; set; }
    }

    public class SheetCreateJson
    {
        public string? Title { get; set; }
    }

    public class SheetListItemDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public int OwnerId { get; set; }
        public string OwnerName { get; set; } = "";
        public bool IsOwner { get; set; }
        public bool CanEdit { get; set; }
        public int Version { get; set; }
        public int? UpdatedBy { get; set; }
        public string? UpdatedByName { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class SheetAccessDto
    {
        public int UserId { get; set; }
        public string UserName { get; set; } = "";
        public bool CanEdit { get; set; }
    }

    public class SheetDetailDto : SheetListItemDto
    {
        public SheetBook Data { get; set; } = new();
        /// <summary>True when the sheet came from an uploaded file: colours, merged cells and other formatting are not kept.</summary>
        public bool FormattingLost { get; set; }
        /// <summary>Who the sheet is shared with. Only filled for the owner.</summary>
        public List<SheetAccessDto>? SharedWith { get; set; }
    }

    public class SheetVersionDto
    {
        public int Version { get; set; }
        public int? UpdatedBy { get; set; }
        public string? UpdatedByName { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class SheetCellChangeInput
    {
        /// <summary>Sheet (tab) name.</summary>
        public string? Sheet { get; set; }
        /// <summary>1-based row.</summary>
        public int Row { get; set; }
        /// <summary>1-based column (A = 1).</summary>
        public int Col { get; set; }
        /// <summary>A string, number or boolean. A string starting with "=" is stored as a formula. null or "" clears the cell.</summary>
        public object? Value { get; set; }
    }

    public class SheetPatchInput
    {
        /// <summary>The version the client last loaded.</summary>
        public int BaseVersion { get; set; }
        public List<SheetCellChangeInput>? Changes { get; set; }
    }

    public class SheetCellConflictDto
    {
        public string Sheet { get; set; } = "";
        public int Row { get; set; }
        public int Col { get; set; }
        /// <summary>What the cell holds now (the formula if it has one).</summary>
        public object? CurrentValue { get; set; }
        public int ChangedInVersion { get; set; }
        public string ChangedByName { get; set; } = "";
    }

    public class SheetPatchResultDto
    {
        public int Version { get; set; }
        public int AppliedCells { get; set; }
        public DateTime UpdatedAt { get; set; }
        /// <summary>Filled only with a 409: nothing was applied.</summary>
        public List<SheetCellConflictDto> Conflicts { get; set; } = [];
    }

    public class SheetAccessInput
    {
        public int UserId { get; set; }
        public bool CanEdit { get; set; }
    }

    public class SheetHistoryDto
    {
        public int Version { get; set; }
        public int UserId { get; set; }
        public string UserName { get; set; } = "";
        public int CellCount { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class SheetFileDto
    {
        public byte[] Bytes { get; set; } = [];
        public string FileName { get; set; } = "";
    }
}
