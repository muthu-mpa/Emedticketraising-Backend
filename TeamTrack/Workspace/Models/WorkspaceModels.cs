namespace TeamTrack.Workspace.Models
{
    public static class CampaignStatus
    {
        public const string Draft = "draft";
        public const string Sending = "sending"; // claimed by a send in progress
        public const string Sent = "sent";
    }

    public static class TimeOffStatus
    {
        public const string Pending = "pending";
        public const string Approved = "approved";
        public const string Rejected = "rejected";
        public const string Cancelled = "cancelled";
    }

    public static class TimeOffKind
    {
        public const string Leave = "leave";
        public const string Permission = "permission";
    }

    public class WsCampaign
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string ClientName { get; set; } = string.Empty;
        public string ContactName { get; set; } = string.Empty;
        public string ClientEmail { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string Status { get; set; } = CampaignStatus.Draft;
        public int CreatedByUserId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? SentAt { get; set; }

        public ICollection<WsCampaignHistory> History { get; set; } = [];
    }

    public class WsCampaignHistory
    {
        public int Id { get; set; }
        public int CampaignId { get; set; }
        public int UserId { get; set; }
        public string Action { get; set; } = string.Empty;
        public string? Note { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public WsCampaign? Campaign { get; set; }
    }

    /// <summary>One table for both leave (date range) and permission (single day, time range).</summary>
    public class WsTimeOffRequest
    {
        public int Id { get; set; }
        public string Kind { get; set; } = TimeOffKind.Leave;
        public int UserId { get; set; }
        public string? LeaveType { get; set; } // casual | sick | earned (leave only)
        public DateOnly FromDate { get; set; }
        public DateOnly ToDate { get; set; } // equals FromDate for permission
        public int Days { get; set; } // inclusive; 0 for permission
        public TimeOnly? StartTime { get; set; } // permission only
        public TimeOnly? EndTime { get; set; }
        public int Minutes { get; set; } // permission only
        public string Reason { get; set; } = string.Empty;
        public string Status { get; set; } = TimeOffStatus.Pending;
        public string? DecisionReason { get; set; }
        public int? DecidedByUserId { get; set; }
        public DateTime? DecidedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class WsProjectDocument
    {
        public int Id { get; set; }
        public int ProjectId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string DocType { get; set; } = "doc"; // doc | sheet | slides | link
        public string? Content { get; set; }
        public string? Url { get; set; }
        public string? FileUrl { get; set; }
        public int UploadedByUserId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class WsNotification
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Link { get; set; }
        public bool IsRead { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    // ---------- Shared Excel sheets ----------

    public class WsSharedSheet
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public int OwnerId { get; set; }
        /// <summary>Server-side path of the uploaded file; null for a blank sheet.</summary>
        public string? OriginalFilePath { get; set; }
        /// <summary>The sheet content as JSON (see SheetBook). Kept as text in the entity, jsonb in PostgreSQL.</summary>
        public string Data { get; set; } = "{\"sheets\":[]}";
        public int Version { get; set; } = 1;
        public int? UpdatedBy { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class WsSharedSheetAccess
    {
        public int SheetId { get; set; }
        public int UserId { get; set; }
        public bool CanEdit { get; set; }
    }

    public class WsSharedSheetChange
    {
        public int Id { get; set; }
        public int SheetId { get; set; }
        public int UserId { get; set; }
        /// <summary>The sheet version this save produced.</summary>
        public int Version { get; set; }
        /// <summary>JSON array of { sheet, row, col, value }.</summary>
        public string ChangedCells { get; set; } = "[]";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
