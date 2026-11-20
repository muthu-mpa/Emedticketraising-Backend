namespace TeamTrack.Marketing.Models
{
    /// <summary>The fixed value lists of the Lead Desk. Served by GET /api/marketing/lookups and used to validate input.</summary>
    public static class MarketingValues
    {
        public const string Closed = "Closed";

        public static readonly string[] Stages = ["New", "Contacted", "Replied", "Interested", "Closed"];
        public static readonly string[] OpenStages = ["New", "Contacted", "Replied", "Interested"];
        public static readonly string[] Channels = ["Paid search", "Social ads", "Email", "Webinar", "Content download", "Event", "Organic", "Referral", "Excel import"];
        public static readonly string[] Campaigns =
            ["Spring product launch", "Q4 webinar series", "Buyer’s guide ebook", "Pricing page retargeting", "SaaS Connect trade show", "Newsletter", "Excel import"];
        public static readonly string[] Interests = ["Starter plan", "Pro plan", "Enterprise", "Integrations", "Not sure yet"];
        public static readonly string[] CloseReasons = ["Became a customer", "Not interested", "No response", "Wrong contact", "Duplicate"];
        public static readonly string[] HistoryKinds =
            ["Captured", "Picked up", "Status", "Score", "Email sent", "Reply received", "Call", "Note", "Returned", "Closed"];
        public static readonly string[] LoggableKinds = ["Email sent", "Reply received", "Call", "Note"];
        public static readonly string[] Temperatures = ["Hot", "Warm", "Cold"];

        public static bool IsOpen(string stage) => stage != Closed;

        public static string TemperatureOf(int score) => score >= 70 ? "Hot" : score >= 40 ? "Warm" : "Cold";
    }

    public class MkLead
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Company { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Phone { get; set; }
        /// <summary>Filled by the Excel import only.</summary>
        public string? OrgPhone { get; set; }
        public string? LinkedInUrl { get; set; }
        public string? PossibleEmail { get; set; }
        public string? OrgEmail { get; set; }
        public string Channel { get; set; } = string.Empty;
        public string Campaign { get; set; } = string.Empty;
        public string Interest { get; set; } = string.Empty;
        public int Score { get; set; }
        public string Stage { get; set; } = "New";
        public string? CloseReason { get; set; }
        public bool Consent { get; set; }
        public int? OwnerUserId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? PickedUpAt { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<MkLeadHistoryEntry> History { get; set; } = [];
    }

    public class MkLeadHistoryEntry
    {
        public Guid Id { get; set; }
        public Guid LeadId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? Text { get; set; }
        public int? CreatedByUserId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public MkLead? Lead { get; set; }
        public ICollection<MkLeadAttachment> Attachments { get; set; } = [];
    }

    public class MkLeadAttachment
    {
        public Guid Id { get; set; }
        public Guid HistoryEntryId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        /// <summary>The generated file name inside the marketing storage folder (never the client's name).</summary>
        public string StoragePath { get; set; } = string.Empty;
        public string? SignedBy { get; set; }
        public DateTime? SignedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public MkLeadHistoryEntry? HistoryEntry { get; set; }
    }

    /// <summary>One line of the dashboard's recent activity.</summary>
    public class MkActivity
    {
        public Guid Id { get; set; }
        public string Text { get; set; } = string.Empty;
        public DateTime At { get; set; } = DateTime.UtcNow;
        public int? UserId { get; set; }
    }
}
