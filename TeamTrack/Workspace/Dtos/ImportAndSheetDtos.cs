using Microsoft.AspNetCore.Http;
using TeamTrack.Workspace.Services;

namespace TeamTrack.Workspace.Dtos
{
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
        /// <summary>Who the sheet is shared with (the list endpoint fills it for every row; the detail endpoint only for the owner).</summary>
        public List<SheetAccessDto>? SharedWith { get; set; }
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
