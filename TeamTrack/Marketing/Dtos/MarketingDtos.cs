using Microsoft.AspNetCore.Http;

namespace TeamTrack.Marketing.Dtos
{
    // ---------- responses (JSON is camelCase) ----------

    public class LookupsDto
    {
        public string[] Stages { get; set; } = [];
        public string[] OpenStages { get; set; } = [];
        public string[] Channels { get; set; } = [];
        public string[] Campaigns { get; set; } = [];
        public string[] Interests { get; set; } = [];
        public string[] CloseReasons { get; set; } = [];
        public string[] HistoryKinds { get; set; } = [];
        public string[] LoggableKinds { get; set; } = [];
        public string[] Temperatures { get; set; } = [];
    }

    public class LeadSummaryDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Title { get; set; } = "";
        public string Company { get; set; } = "";
        public string Email { get; set; } = "";
        public string? Phone { get; set; }
        public string? OrgPhone { get; set; }
        public string? LinkedInUrl { get; set; }
        public string? PossibleEmail { get; set; }
        public string? OrgEmail { get; set; }
        public string Channel { get; set; } = "";
        public string Campaign { get; set; } = "";
        public string Interest { get; set; } = "";
        public int Score { get; set; }
        public string Temperature { get; set; } = "";
        public string Stage { get; set; } = "";
        public string? CloseReason { get; set; }
        public bool Consent { get; set; }
        public string? OwnerUserId { get; set; }
        public string? OwnerName { get; set; }
        public bool IsMine { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? PickedUpAt { get; set; }
        public DateTime? LastContactAt { get; set; }
        public int EmailsSent { get; set; }
    }

    public class LeadDetailDto : LeadSummaryDto
    {
        /// <summary>Newest first.</summary>
        public List<HistoryEntryDto> History { get; set; } = [];
    }

    public class HistoryEntryDto
    {
        public Guid Id { get; set; }
        public string Kind { get; set; } = "";
        public DateTime OccurredAt { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? Text { get; set; }
        public string? CreatedByName { get; set; }
        public List<AttachmentDto> Attachments { get; set; } = [];
    }

    public class AttachmentDto
    {
        public Guid Id { get; set; }
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long Size { get; set; }
        public string? SignedBy { get; set; }
        public DateTime? SignedAt { get; set; }
    }

    public class LeadImportResultDto
    {
        public string FileName { get; set; } = "";
        public int TotalRows { get; set; }
        public int Imported { get; set; }
        public int Skipped { get; set; }
        public int Duplicates { get; set; }
        public int EmptyRows { get; set; }
        /// <summary>Up to 20 short explanations such as "Row 7: no Contact Name".</summary>
        public List<string> Issues { get; set; } = [];
    }

    public class DashboardDto
    {
        public int LeadsTracked { get; set; }
        public int EmailsSent { get; set; }
        public int WaitingInQueue { get; set; }
        public int OnMyBench { get; set; }
        /// <summary>Leads with a reply ÷ leads with an email sent, as a fraction between 0 and 1 (0 when no emails).</summary>
        public double ReplyRate { get; set; }
        public List<StageCountDto> ByStage { get; set; } = [];
        public List<ChannelCountDto> ByChannel { get; set; } = [];
        public List<TemperatureCountDto> ByTemperature { get; set; } = [];
        public List<CampaignCountDto> ByCampaign { get; set; } = [];
        public List<ActivityDto> RecentActivity { get; set; } = [];
    }

    public class StageCountDto { public string Stage { get; set; } = ""; public int Count { get; set; } }
    public class ChannelCountDto { public string Channel { get; set; } = ""; public int Count { get; set; } }
    public class TemperatureCountDto { public string Temperature { get; set; } = ""; public int Count { get; set; } }
    public class CampaignCountDto { public string Campaign { get; set; } = ""; public int Leads { get; set; } public int Emails { get; set; } }
    public class ActivityDto { public string Text { get; set; } = ""; public DateTime At { get; set; } }

    // ---------- requests ----------

    public class StageInput { public string? Stage { get; set; } }
    public class ScoreInput { public int Score { get; set; } }
    public class CloseInput { public string? Reason { get; set; } }

    /// <summary>The queue's filters, already parsed from the query string.</summary>
    public class QueueQuery
    {
        public string? Search { get; set; }
        public string? Channel { get; set; }
        public string? Campaign { get; set; }
        public string? Temperature { get; set; }
        public string? Stage { get; set; }
        public string? Sort { get; set; }
        public string? Dir { get; set; }
    }

    /// <summary>multipart/form-data of POST /leads/{id}/history.</summary>
    public class HistoryForm
    {
        public string? Kind { get; set; }
        public string? OccurredAt { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? Text { get; set; }
        public List<IFormFile>? Files { get; set; }
        /// <summary>JSON array, same order as Files: [{ "fileName": "x-signed.pdf", "signedBy": "Priya Raman", "signedAt": "iso" }]</summary>
        public string? FilesMeta { get; set; }
    }

    /// <summary>The service-side view of an uploaded file, so the service never depends on IFormFile.</summary>
    public sealed record IncomingFile(string FileName, long Length, Func<Stream> Open);

    public class HistoryInput
    {
        public string? Kind { get; set; }
        public string? OccurredAt { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? Text { get; set; }
        public List<IncomingFile> Files { get; set; } = [];
        public string? FilesMeta { get; set; }
    }

    /// <summary>A stored attachment ready to be streamed back.</summary>
    public sealed class AttachmentFile
    {
        public Stream Content { get; init; } = Stream.Null;
        public string ContentType { get; init; } = "";
        public string FileName { get; init; } = "";
    }
}
