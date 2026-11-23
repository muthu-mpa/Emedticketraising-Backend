using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeamTrack.Data;
using TeamTrack.Marketing.Data;
using TeamTrack.Marketing.Dtos;
using TeamTrack.Marketing.Models;
using TeamTrack.Workspace.Services;

namespace TeamTrack.Marketing.Services
{
    public interface IMarketingService
    {
        LookupsDto Lookups();
        Task<WsResult<DashboardDto>> DashboardAsync(CurrentUser user);
        Task<WsResult<List<LeadSummaryDto>>> QueueAsync(CurrentUser user, QueueQuery query);
        Task<WsResult<List<LeadSummaryDto>>> BenchAsync(CurrentUser user, string? status);
        Task<WsResult<LeadDetailDto>> GetAsync(CurrentUser user, Guid id);
        Task<WsResult<LeadImportResultDto>> ImportAsync(CurrentUser user, IncomingFile file);
        Task<WsResult<LeadDetailDto>> PickUpAsync(CurrentUser user, Guid id);
        Task<WsResult<LeadSummaryDto>> ReturnAsync(CurrentUser user, Guid id);
        Task<WsResult<LeadDetailDto>> SetStageAsync(CurrentUser user, Guid id, string? stage);
        Task<WsResult<LeadDetailDto>> SetScoreAsync(CurrentUser user, Guid id, int score);
        Task<WsResult<LeadDetailDto>> CloseAsync(CurrentUser user, Guid id, string? reason);
        Task<WsResult<LeadDetailDto>> AddHistoryAsync(CurrentUser user, Guid id, HistoryInput input);
        Task<WsResult<AttachmentFile>> GetAttachmentAsync(CurrentUser user, Guid attachmentId);
    }

    public class MarketingService : IMarketingService
    {
        public const int MaxFiles = 5;
        public const long MaxFileBytes = 10L * 1024 * 1024;
        public const int MaxSubject = 300;
        public const int MaxBody = 20000;
        public const int MaxText = 4000;
        /// <summary>How far ahead of the server clock a logged time may be, to absorb clock differences with the browser.</summary>
        public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

        /// <summary>Allowed upload types and the content type each is served with (never the client's own claim).</summary>
        public static readonly IReadOnlyDictionary<string, string> AllowedExtensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf",
            [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".ppt"] = "application/vnd.ms-powerpoint",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".txt"] = "text/plain", [".csv"] = "text/csv", [".zip"] = "application/zip",
        };

        private static readonly string[] ContactKinds = ["Email sent", "Reply received", "Call"];
        private static readonly string[] SortKeys = ["score", "name", "created", "lastContact", "emails"];
        private const string NotOnBench = "This lead is not on your work bench.";

        private readonly MarketingDbContext _db;
        private readonly AppDbContext _app;
        private readonly IMarketingFileStore _files;
        private readonly TimeProvider _clock;
        private readonly ILogger<MarketingService> _logger;

        public MarketingService(MarketingDbContext db, AppDbContext app, IMarketingFileStore files, TimeProvider clock, ILogger<MarketingService> logger)
        {
            _db = db;
            _app = app;
            _files = files;
            _clock = clock;
            _logger = logger;
        }

        private DateTime Now => _clock.GetUtcNow().UtcDateTime;

        // ---------- lookups & dashboard ----------

        public LookupsDto Lookups() => new()
        {
            Stages = MarketingValues.Stages, OpenStages = MarketingValues.OpenStages, Channels = MarketingValues.Channels,
            Campaigns = MarketingValues.Campaigns, Interests = MarketingValues.Interests, CloseReasons = MarketingValues.CloseReasons,
            HistoryKinds = MarketingValues.HistoryKinds, LoggableKinds = MarketingValues.LoggableKinds, Temperatures = MarketingValues.Temperatures,
        };

        public async Task<WsResult<DashboardDto>> DashboardAsync(CurrentUser user)
        {
            // team-wide, except onMyBench
            var leads = await _db.Leads.AsNoTracking().Select(l => new { l.Stage, l.Channel, l.Campaign, l.Score, l.OwnerUserId }).ToListAsync();
            var emailsSent = await _db.History.AsNoTracking().CountAsync(h => h.Kind == "Email sent");
            var leadsWithEmail = await _db.History.AsNoTracking().Where(h => h.Kind == "Email sent").Select(h => h.LeadId).Distinct().CountAsync();
            var leadsWithReply = await _db.History.AsNoTracking().Where(h => h.Kind == "Reply received").Select(h => h.LeadId).Distinct().CountAsync();
            var emailsByCampaign = await (from h in _db.History.AsNoTracking()
                                          where h.Kind == "Email sent"
                                          join l in _db.Leads.AsNoTracking() on h.LeadId equals l.Id
                                          group h by l.Campaign into g
                                          select new { Campaign = g.Key, Count = g.Count() }).ToListAsync();
            var activity = await _db.Activity.AsNoTracking().OrderByDescending(a => a.At).Take(10).ToListAsync();

            return WsResult<DashboardDto>.Ok(new DashboardDto
            {
                LeadsTracked = leads.Count,
                EmailsSent = emailsSent,
                WaitingInQueue = leads.Count(l => l.OwnerUserId == null && MarketingValues.IsOpen(l.Stage)),
                OnMyBench = leads.Count(l => l.OwnerUserId == user.Id && MarketingValues.IsOpen(l.Stage)),
                ReplyRate = leadsWithEmail == 0 ? 0 : Math.Round(Math.Min(1.0, (double)leadsWithReply / leadsWithEmail), 4),
                ByStage = MarketingValues.Stages.Select(s => new StageCountDto { Stage = s, Count = leads.Count(l => l.Stage == s) }).ToList(),
                ByChannel = MarketingValues.Channels.Select(c => new ChannelCountDto { Channel = c, Count = leads.Count(l => l.Channel == c) }).ToList(),
                ByTemperature = MarketingValues.Temperatures.Select(t => new TemperatureCountDto { Temperature = t, Count = leads.Count(l => MarketingValues.TemperatureOf(l.Score) == t) }).ToList(),
                ByCampaign = MarketingValues.Campaigns.Select(c => new CampaignCountDto
                {
                    Campaign = c,
                    Leads = leads.Count(l => l.Campaign == c),
                    Emails = emailsByCampaign.FirstOrDefault(e => e.Campaign == c)?.Count ?? 0,
                }).ToList(),
                RecentActivity = activity.Select(a => new ActivityDto { Text = a.Text, At = Utc(a.At) }).ToList(),
            });
        }

        // ---------- lists ----------

        public async Task<WsResult<List<LeadSummaryDto>>> QueueAsync(CurrentUser user, QueueQuery query)
        {
            var sort = string.IsNullOrWhiteSpace(query.Sort) ? "score" : query.Sort.Trim();
            var dir = string.IsNullOrWhiteSpace(query.Dir) ? "desc" : query.Dir.Trim().ToLowerInvariant();
            var bad = Validate(query.Channel, MarketingValues.Channels, "channel")
                ?? Validate(query.Campaign, MarketingValues.Campaigns, "campaign")
                ?? Validate(query.Temperature, MarketingValues.Temperatures, "temperature")
                ?? Validate(query.Stage, MarketingValues.Stages, "stage")
                ?? Validate(sort, SortKeys, "sort")
                ?? Validate(dir, ["asc", "desc"], "dir");
            if (bad != null) return WsResult<List<LeadSummaryDto>>.Fail(400, bad);

            var q = _db.Leads.AsNoTracking().Where(l => l.OwnerUserId == null && l.Stage != MarketingValues.Closed);
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var s = query.Search.Trim().ToLower();
                q = q.Where(l => l.Name.ToLower().Contains(s) || l.Company.ToLower().Contains(s)
                    || l.Email.ToLower().Contains(s) || l.Title.ToLower().Contains(s)
                    || (l.PossibleEmail != null && l.PossibleEmail.ToLower().Contains(s)) || (l.OrgEmail != null && l.OrgEmail.ToLower().Contains(s)));
            }
            if (!string.IsNullOrWhiteSpace(query.Channel)) q = q.Where(l => l.Channel == query.Channel);
            if (!string.IsNullOrWhiteSpace(query.Campaign)) q = q.Where(l => l.Campaign == query.Campaign);
            if (!string.IsNullOrWhiteSpace(query.Stage)) q = q.Where(l => l.Stage == query.Stage);
            if (query.Temperature == "Hot") q = q.Where(l => l.Score >= 70);
            else if (query.Temperature == "Warm") q = q.Where(l => l.Score >= 40 && l.Score < 70);
            else if (query.Temperature == "Cold") q = q.Where(l => l.Score < 40);

            var items = await ToSummariesAsync(await q.ToListAsync(), user);
            var desc = dir == "desc";
            IOrderedEnumerable<LeadSummaryDto> ordered = sort switch
            {
                "name" => Order(items, i => i.Name, desc),
                "created" => Order(items, i => i.CreatedAt, desc),
                "lastContact" => Order(items, i => i.LastContactAt ?? DateTime.MinValue, desc),
                "emails" => Order(items, i => i.EmailsSent, desc),
                _ => Order(items, i => i.Score, desc),
            };
            return WsResult<List<LeadSummaryDto>>.Ok(ordered.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList());
        }

        // ---------- Excel import ----------

        public const long MaxImportBytes = 5L * 1024 * 1024;
        public const int MaxImportRows = 5000;
        public static readonly string[] ImportHeaders =
            ["Organization", "Contact Name", "Title / Role", "ORG PHONE", "EXACT LinkedIn URL", "EMAIL (POSSIBLE)", "ORG EMAIL"];
        private const int ImportScore = 20;

        public async Task<WsResult<LeadImportResultDto>> ImportAsync(CurrentUser user, IncomingFile file)
        {
            var ext = Path.GetExtension(file.FileName);
            if (!ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".xls", StringComparison.OrdinalIgnoreCase))
                return WsResult<LeadImportResultDto>.Fail(400, "Only Excel files (.xlsx or .xls) can be uploaded.");
            if (file.Length <= 0) return WsResult<LeadImportResultDto>.Fail(400, "The file is empty.");
            if (file.Length > MaxImportBytes) return WsResult<LeadImportResultDto>.Fail(400, $"The file is too large (max {MaxImportBytes / 1024 / 1024} MB).");

            List<List<string[]>> sheets;
            try
            {
                await using var stream = file.Open();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;
                sheets = ReadSheets(ms);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the uploaded lead sheet {File}", file.FileName);
                return WsResult<LeadImportResultDto>.Fail(400, "The file could not be read. Make sure it is a valid, unprotected Excel file.");
            }
            if (sheets.All(sh => sh.Count == 0)) return WsResult<LeadImportResultDto>.Fail(400, "The Excel file has no data.");

            // find the header row: across all sheets, the one of the first 30 rows that matches most of the required headers
            var wanted = ImportHeaders.Select(HeaderKey).ToArray();
            List<string[]> sheet = sheets.First(sh => sh.Count > 0);
            var headerAt = 0;
            var best = -1;
            foreach (var sh in sheets)
                for (var ri = 0; ri < Math.Min(sh.Count, 30); ri++)
                {
                    var hits = sh[ri].Select(HeaderKey).Count(k => k.Length > 0 && wanted.Contains(k));
                    if (hits > best) { best = hits; sheet = sh; headerAt = ri; }
                }
            var header = sheet[headerAt].Select(HeaderKey).ToArray();
            var col = new Dictionary<string, int>();
            foreach (var h in ImportHeaders)
            {
                var i = Array.IndexOf(header, HeaderKey(h));
                if (i >= 0) col[h] = i;
            }
            var missing = ImportHeaders.Where(h => !col.ContainsKey(h)).ToList();
            if (missing.Count > 0)
            {
                var found = sheet[headerAt].Select(c => (c ?? "").Trim()).Where(c => c.Length > 0).Take(10).ToList();
                return WsResult<LeadImportResultDto>.Fail(400,
                    $"Missing required column{(missing.Count > 1 ? "s" : "")}: {string.Join(", ", missing)}."
                    + (found.Count > 0 ? $" Columns found: {string.Join(", ", found)}." : ""));
            }

            var rows = sheet.Skip(headerAt + 1).ToList();
            if (rows.Count > MaxImportRows) return WsResult<LeadImportResultDto>.Fail(400, $"The file has too many rows (max {MaxImportRows:N0}).");

            // existing leads, so the same person is never created twice
            var existing = await _db.Leads.AsNoTracking()
                .Select(l => new { l.Name, l.Company, l.Email, l.PossibleEmail, l.LinkedInUrl }).ToListAsync();
            var seen = new HashSet<string>();
            foreach (var e in existing)
                foreach (var k in ImportKeys(e.Name, e.Company, e.PossibleEmail ?? e.Email, e.LinkedInUrl)) seen.Add(k);

            var result = new LeadImportResultDto { FileName = DisplayName(file.FileName) };
            var now = Now;
            var newLeads = new List<MkLead>();
            string Cell(string[] r, string h) => col[h] < r.Length ? CleanCell(r[col[h]]) : "";
            void Issue(int row, string why) { if (result.Issues.Count < 20) result.Issues.Add($"Row {row}: {why}"); }

            for (var n = 0; n < rows.Count; n++)
            {
                var r = rows[n];
                var rowNo = n + 2; // approximately the Excel row number
                if (r.All(string.IsNullOrWhiteSpace)) { result.EmptyRows++; continue; }
                result.TotalRows++;

                var name = Cell(r, "Contact Name");
                var org = Cell(r, "Organization");
                if (name.Length == 0) { result.Skipped++; Issue(rowNo, "no Contact Name."); continue; }

                var possible = Cell(r, "EMAIL (POSSIBLE)");
                var orgEmail = Cell(r, "ORG EMAIL");
                var linkedIn = Cell(r, "EXACT LinkedIn URL");
                var keys = ImportKeys(name, org, possible, linkedIn).ToList();
                if (keys.Any(seen.Contains)) { result.Skipped++; result.Duplicates++; Issue(rowNo, $"{name} is already in the system or earlier in this file."); continue; }
                foreach (var k in keys) seen.Add(k);

                var lead = new MkLead
                {
                    Id = Guid.NewGuid(), Name = Trunc(name, 200), Title = Trunc(Cell(r, "Title / Role"), 200), Company = Trunc(org, 200),
                    Email = Trunc(possible.Length > 0 ? possible : orgEmail, 320),
                    OrgPhone = NullIfEmpty(Trunc(Cell(r, "ORG PHONE"), 60)), LinkedInUrl = NullIfEmpty(Trunc(linkedIn, 500)),
                    PossibleEmail = NullIfEmpty(Trunc(possible, 320)), OrgEmail = NullIfEmpty(Trunc(orgEmail, 320)),
                    Channel = "Excel import", Campaign = "Excel import", Interest = "Not sure yet",
                    Score = ImportScore, Stage = "New", Consent = false, CreatedAt = now, UpdatedAt = now,
                };
                lead.History.Add(new MkLeadHistoryEntry
                {
                    Id = Guid.NewGuid(), Kind = "Captured", OccurredAt = now, CreatedAt = now, CreatedByUserId = user.Id,
                    Text = $"Imported from Excel file {result.FileName} (row {rowNo}).",
                });
                newLeads.Add(lead);
            }

            result.Imported = newLeads.Count;
            if (newLeads.Count > 0)
            {
                _db.Leads.AddRange(newLeads);
                AddActivity($"{user.Name} imported {newLeads.Count} lead{(newLeads.Count == 1 ? "" : "s")} from {result.FileName}", now, user);
                await _db.SaveChangesAsync();
            }
            return WsResult<LeadImportResultDto>.Ok(result);
        }

        private static List<List<string[]>> ReadSheets(Stream stream)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            using var reader = ExcelDataReader.ExcelReaderFactory.CreateReader(stream);
            var sheets = new List<List<string[]>>();
            do
            {
                var all = new List<string[]>();
                while (reader.Read())
                {
                    var row = new string[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++)
                        row[i] = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                    all.Add(row);
                }
                sheets.Add(all);
            } while (reader.NextResult());
            return sheets;
        }

        /// <summary>Header comparison key: lower case letters and digits only, so spacing, slashes and brackets do not matter.</summary>
        private static string HeaderKey(string? h) => new string((h ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        private static string CleanCell(string? v) => (v ?? "").Replace(' ', ' ').Trim();
        private static string Trunc(string v, int max) => v.Length <= max ? v : v[..max];
        private static string? NullIfEmpty(string v) => v.Length == 0 ? null : v;

        /// <summary>Keys that identify "the same person": email, LinkedIn profile, or name + organization.</summary>
        private static IEnumerable<string> ImportKeys(string name, string company, string? email, string? linkedIn)
        {
            if (!string.IsNullOrWhiteSpace(email)) yield return "e:" + email.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(linkedIn)) yield return "l:" + linkedIn.Trim().TrimEnd('/').ToLowerInvariant();
            yield return "n:" + Squash(name) + "|" + Squash(company);
        }

        private static string Squash(string? v) => string.Join(' ', (v ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

        public async Task<WsResult<List<LeadSummaryDto>>> BenchAsync(CurrentUser user, string? status)
        {
            var mode = string.IsNullOrWhiteSpace(status) ? "open" : status.Trim().ToLowerInvariant();
            if (mode is not ("open" or "closed" or "all"))
                return WsResult<List<LeadSummaryDto>>.Fail(400, "status must be open, closed or all.");

            var q = _db.Leads.AsNoTracking().Where(l => l.OwnerUserId == user.Id);
            if (mode == "open") q = q.Where(l => l.Stage != MarketingValues.Closed);
            else if (mode == "closed") q = q.Where(l => l.Stage == MarketingValues.Closed);

            var items = await ToSummariesAsync(await q.ToListAsync(), user);
            return WsResult<List<LeadSummaryDto>>.Ok(items.OrderByDescending(i => i.PickedUpAt ?? i.CreatedAt).ToList());
        }

        public async Task<WsResult<LeadDetailDto>> GetAsync(CurrentUser user, Guid id)
        {
            var lead = await _db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id);
            if (lead == null) return NotFound<LeadDetailDto>();
            if (!CanSee(lead, user)) return WsResult<LeadDetailDto>.Fail(403, "You do not have access to this lead.");
            return WsResult<LeadDetailDto>.Ok(await ToDetailAsync(lead, user));
        }

        // ---------- commands ----------

        public async Task<WsResult<LeadDetailDto>> PickUpAsync(CurrentUser user, Guid id)
        {
            var lead = await _db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id);
            if (lead == null) return NotFound<LeadDetailDto>();
            if (lead.OwnerUserId == user.Id) return WsResult<LeadDetailDto>.Fail(409, "This lead is already on your work bench.");
            if (lead.OwnerUserId != null || !MarketingValues.IsOpen(lead.Stage)) return AlreadyTaken();

            var now = Now;
            await using var tx = await _db.Database.BeginTransactionAsync();
            // Claim atomically (UPDATE ... WHERE owner IS NULL) so two people can never both pick the same lead up.
            var claimed = await _db.Leads
                .Where(l => l.Id == id && l.OwnerUserId == null && l.Stage != MarketingValues.Closed)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.OwnerUserId, (int?)user.Id)
                    .SetProperty(l => l.PickedUpAt, (DateTime?)now)
                    .SetProperty(l => l.UpdatedAt, now));
            if (claimed != 1) return AlreadyTaken();

            AddEntry(id, "Picked up", now, user, text: "Moved to your work bench.");
            AddActivity($"{user.Name} picked up {lead.Name}", now, user);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            var fresh = await _db.Leads.AsNoTracking().FirstAsync(l => l.Id == id);
            return WsResult<LeadDetailDto>.Ok(await ToDetailAsync(fresh, user), "Moved to your work bench.");
        }

        public async Task<WsResult<LeadSummaryDto>> ReturnAsync(CurrentUser user, Guid id)
        {
            var (lead, error) = await LoadOwnedAsync<LeadSummaryDto>(user, id);
            if (error != null) return error;
            if (!MarketingValues.IsOpen(lead!.Stage)) return WsResult<LeadSummaryDto>.Fail(409, "A closed lead cannot be returned. Reopen it first.");

            var now = Now;
            lead.OwnerUserId = null;
            lead.PickedUpAt = null;
            lead.UpdatedAt = now;
            AddEntry(lead.Id, "Returned", now, user, text: "Returned to the work queue.");
            AddActivity($"{user.Name} returned {lead.Name} to the queue", now, user);
            await _db.SaveChangesAsync();

            var summary = (await ToSummariesAsync([lead], user))[0];
            return WsResult<LeadSummaryDto>.Ok(summary, "Returned to the work queue.");
        }

        public async Task<WsResult<LeadDetailDto>> SetStageAsync(CurrentUser user, Guid id, string? stage)
        {
            var (lead, error) = await LoadOwnedAsync<LeadDetailDto>(user, id);
            if (error != null) return error;
            if (string.IsNullOrWhiteSpace(stage) || !MarketingValues.Stages.Contains(stage))
                return WsResult<LeadDetailDto>.Fail(400, $"Stage must be one of: {string.Join(", ", MarketingValues.Stages)}.");
            if (stage == MarketingValues.Closed)
                return WsResult<LeadDetailDto>.Fail(400, "Use the close action to close a lead.");

            if (stage != lead!.Stage)
            {
                var now = Now;
                var old = lead.Stage;
                lead.Stage = stage;
                lead.CloseReason = null; // reopening a closed lead clears the reason
                lead.UpdatedAt = now;
                AddEntry(lead.Id, "Status", now, user, text: $"{old} → {stage}");
                AddActivity($"{lead.Name} moved {old} → {stage}", now, user);
                await _db.SaveChangesAsync();
            }
            return WsResult<LeadDetailDto>.Ok(await ToDetailAsync(lead, user));
        }

        public async Task<WsResult<LeadDetailDto>> SetScoreAsync(CurrentUser user, Guid id, int score)
        {
            var (lead, error) = await LoadOwnedAsync<LeadDetailDto>(user, id);
            if (error != null) return error;

            var clamped = Math.Clamp(score, 0, 100);
            if (clamped != lead!.Score)
            {
                var now = Now;
                var old = lead.Score;
                lead.Score = clamped;
                lead.UpdatedAt = now;
                AddEntry(lead.Id, "Score", now, user, text: $"{old} → {clamped}");
                AddActivity($"{lead.Name} score {old} → {clamped}", now, user);
                await _db.SaveChangesAsync();
            }
            return WsResult<LeadDetailDto>.Ok(await ToDetailAsync(lead, user));
        }

        public async Task<WsResult<LeadDetailDto>> CloseAsync(CurrentUser user, Guid id, string? reason)
        {
            var (lead, error) = await LoadOwnedAsync<LeadDetailDto>(user, id);
            if (error != null) return error;
            if (string.IsNullOrWhiteSpace(reason) || !MarketingValues.CloseReasons.Contains(reason))
                return WsResult<LeadDetailDto>.Fail(400, $"Reason must be one of: {string.Join(", ", MarketingValues.CloseReasons)}.");
            if (!MarketingValues.IsOpen(lead!.Stage)) return WsResult<LeadDetailDto>.Fail(409, "This lead is already closed.");

            var now = Now;
            lead.Stage = MarketingValues.Closed;
            lead.CloseReason = reason;
            lead.UpdatedAt = now;
            AddEntry(lead.Id, "Closed", now, user, text: reason);
            AddActivity($"{lead.Name} closed: {reason}", now, user);
            await _db.SaveChangesAsync();
            return WsResult<LeadDetailDto>.Ok(await ToDetailAsync(lead, user), "Lead closed.");
        }

        public async Task<WsResult<LeadDetailDto>> AddHistoryAsync(CurrentUser user, Guid id, HistoryInput input)
        {
            var (lead, error) = await LoadOwnedAsync<LeadDetailDto>(user, id);
            if (error != null) return error;

            var now = Now;
            var kind = input.Kind?.Trim();
            if (kind == null || !MarketingValues.LoggableKinds.Contains(kind))
                return Bad($"Kind must be one of: {string.Join(", ", MarketingValues.LoggableKinds)}.");

            var occurredAt = now;
            if (!string.IsNullOrWhiteSpace(input.OccurredAt))
            {
                if (!TryParseUtc(input.OccurredAt, out occurredAt)) return Bad("occurredAt is not a valid date.");
                if (occurredAt > now + FutureTolerance) return Bad("occurredAt cannot be in the future."); // both in UTC
            }

            var subject = Clean(input.Subject);
            var body = Clean(input.Body);
            var text = Clean(input.Text);
            if (subject != null && (subject.Contains('\r') || subject.Contains('\n'))) return Bad("Subject must be a single line.");
            if (subject != null && subject.Length > MaxSubject) return Bad($"Subject is too long (max {MaxSubject} characters).");
            if (body != null && body.Length > MaxBody) return Bad($"Body is too long (max {MaxBody} characters).");
            if (text != null && text.Length > MaxText) return Bad($"Text is too long (max {MaxText} characters).");
            if (kind == "Email sent" && subject == null) return Bad("A subject is required for an email.");
            if (kind != "Email sent" && text == null && input.Files.Count == 0) return Bad("Please write some text or attach a file.");

            var filesError = ValidateFiles(input, out var meta);
            if (filesError != null) return Bad(filesError);

            // Files first (nothing references them yet); they are removed again if the database save fails.
            var stored = new List<string>();
            var entry = AddEntry(lead!.Id, kind, occurredAt, user, subject, body, text);
            try
            {
                for (var i = 0; i < input.Files.Count; i++)
                {
                    var file = input.Files[i];
                    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                    await using (var content = file.Open())
                        stored.Add(await _files.SaveAsync(content, ext));
                    _db.Attachments.Add(new MkLeadAttachment
                    {
                        Id = Guid.NewGuid(), HistoryEntryId = entry.Id, FileName = DisplayName(file.FileName), ContentType = AllowedExtensions[ext],
                        SizeBytes = file.Length, StoragePath = stored[i], SignedBy = meta[i].SignedBy, SignedAt = meta[i].SignedAt, CreatedAt = now,
                    });
                }

                // automatic stage changes
                string? newStage = null;
                if (kind == "Email sent" && lead.Stage == "New") newStage = "Contacted";
                else if (kind == "Reply received" && lead.Stage is "New" or "Contacted") newStage = "Replied";
                if (newStage != null)
                {
                    var status = AddEntry(lead.Id, "Status", occurredAt, user, text: $"{lead.Stage} → {newStage}");
                    status.CreatedAt = entry.CreatedAt.AddMilliseconds(1); // sorts above the entry that caused it
                    lead.Stage = newStage;
                }

                lead.UpdatedAt = now;
                AddActivity(kind switch
                {
                    "Email sent" => $"Email to {lead.Name}: {subject}",
                    "Reply received" => $"Reply from {lead.Name}",
                    "Call" => $"Call with {lead.Name}",
                    _ => $"Note on {lead.Name}",
                }, now, user);
                await _db.SaveChangesAsync();
            }
            catch
            {
                foreach (var name in stored) _files.Delete(name);
                throw;
            }
            return WsResult<LeadDetailDto>.Ok(await ToDetailAsync(lead, user), "Saved.");
        }

        public async Task<WsResult<AttachmentFile>> GetAttachmentAsync(CurrentUser user, Guid attachmentId)
        {
            var row = await (from a in _db.Attachments.AsNoTracking()
                             join h in _db.History.AsNoTracking() on a.HistoryEntryId equals h.Id
                             join l in _db.Leads.AsNoTracking() on h.LeadId equals l.Id
                             where a.Id == attachmentId
                             select new { Attachment = a, Lead = l }).FirstOrDefaultAsync();
            if (row == null) return WsResult<AttachmentFile>.Fail(404, "Attachment not found.");
            if (!CanSee(row.Lead, user)) return WsResult<AttachmentFile>.Fail(403, "You do not have access to this attachment.");

            var stream = _files.Open(row.Attachment.StoragePath);
            if (stream == null)
            {
                _logger.LogError("Attachment {AttachmentId} is in the database but its file {Name} is missing.", row.Attachment.Id, row.Attachment.StoragePath);
                return WsResult<AttachmentFile>.Fail(404, "The file is no longer available.");
            }
            return WsResult<AttachmentFile>.Ok(new AttachmentFile
            {
                Content = stream, ContentType = row.Attachment.ContentType, FileName = row.Attachment.FileName,
            });
        }

        // ---------- helpers ----------

        private static bool CanSee(MkLead lead, CurrentUser user)
            => lead.OwnerUserId == user.Id || (lead.OwnerUserId == null && MarketingValues.IsOpen(lead.Stage));

        private static WsResult<T> NotFound<T>() => WsResult<T>.Fail(404, "Lead not found.");
        private static WsResult<LeadDetailDto> AlreadyTaken() => WsResult<LeadDetailDto>.Fail(409, "This lead has already been picked up by someone else.");
        private static WsResult<LeadDetailDto> Bad(string message) => WsResult<LeadDetailDto>.Fail(400, message);

        private static string? Validate(string? value, string[] allowed, string name)
            => string.IsNullOrWhiteSpace(value) || allowed.Contains(value) ? null : $"Invalid {name}: '{value}'.";

        private static IOrderedEnumerable<LeadSummaryDto> Order<TKey>(List<LeadSummaryDto> items, Func<LeadSummaryDto, TKey> key, bool desc)
            => desc ? items.OrderByDescending(key) : items.OrderBy(key);

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
        private static DateTime? Utc(DateTime? value) => value == null ? null : Utc(value.Value);

        private static bool TryParseUtc(string? text, out DateTime utc)
        {
            utc = default;
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return false;
            utc = parsed.UtcDateTime;
            return true;
        }

        /// <summary>The file name as shown to users: no path, no control characters, bounded length. Never used on disk.</summary>
        private static string DisplayName(string fileName)
        {
            var name = new string(Path.GetFileName(fileName.Replace('\\', '/')).Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (name.Length == 0) name = "file";
            return name.Length > 200 ? name[^200..] : name;
        }

        private async Task<(MkLead? Lead, WsResult<T>? Error)> LoadOwnedAsync<T>(CurrentUser user, Guid id)
        {
            var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
            if (lead == null) return (null, NotFound<T>());
            if (lead.OwnerUserId != user.Id) return (null, WsResult<T>.Fail(403, NotOnBench));
            return (lead, null);
        }

        private MkLeadHistoryEntry AddEntry(Guid leadId, string kind, DateTime occurredAt, CurrentUser user,
            string? subject = null, string? body = null, string? text = null)
        {
            var entry = new MkLeadHistoryEntry
            {
                Id = Guid.NewGuid(), LeadId = leadId, Kind = kind, OccurredAt = occurredAt, Subject = subject, Body = body, Text = text,
                CreatedByUserId = user.Id, CreatedAt = Now,
            };
            _db.History.Add(entry);
            return entry;
        }

        private void AddActivity(string text, DateTime at, CurrentUser user)
            => _db.Activity.Add(new MkActivity { Id = Guid.NewGuid(), Text = text, At = at, UserId = user.Id });

        private record FileMeta(string? SignedBy, DateTime? SignedAt);
        private record LeadStats(Guid LeadId, DateTime Last, int Emails);

        private static string? ValidateFiles(HistoryInput input, out List<FileMeta> meta)
        {
            meta = [];
            var files = input.Files;
            if (files.Count > MaxFiles) return $"At most {MaxFiles} files at a time.";
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file.FileName);
                if (!AllowedExtensions.ContainsKey(ext))
                    return $"'{DisplayName(file.FileName)}' is not an allowed file type (PDF, images, Office documents, text, CSV or zip).";
                if (file.Length <= 0) return $"'{DisplayName(file.FileName)}' is empty.";
                if (file.Length > MaxFileBytes) return $"'{DisplayName(file.FileName)}' is too large (max {MaxFileBytes / 1024 / 1024} MB per file).";
            }

            var entries = new List<JsonElement>();
            if (!string.IsNullOrWhiteSpace(input.FilesMeta))
            {
                try
                {
                    var doc = JsonDocument.Parse(input.FilesMeta);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return "filesMeta must be a JSON array.";
                    entries = doc.RootElement.EnumerateArray().ToList();
                }
                catch (JsonException) { return "filesMeta is not valid JSON."; }
                if (entries.Count != files.Count) return "filesMeta must have one entry per file, in the same order.";
            }

            for (var i = 0; i < files.Count; i++)
            {
                string? signedBy = null;
                DateTime? signedAt = null;
                if (entries.Count > 0 && entries[i].ValueKind == JsonValueKind.Object)
                {
                    if (entries[i].TryGetProperty("signedBy", out var by) && by.ValueKind == JsonValueKind.String)
                        signedBy = Clean(by.GetString());
                    if (signedBy is { Length: > 200 }) return "signedBy is too long (max 200 characters).";
                    if (entries[i].TryGetProperty("signedAt", out var at) && at.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(at.GetString()))
                    {
                        if (!TryParseUtc(at.GetString(), out var parsed)) return "signedAt is not a valid date.";
                        signedAt = parsed;
                    }
                }
                meta.Add(new FileMeta(signedBy, signedAt));
            }
            return null;
        }

        private async Task<Dictionary<int, string>> NamesAsync(IEnumerable<int> ids)
        {
            var distinct = ids.Distinct().ToList();
            return await _app.Users.AsNoTracking().Where(u => distinct.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name);
        }

        private async Task<List<LeadSummaryDto>> ToSummariesAsync(List<MkLead> leads, CurrentUser user)
        {
            var ids = leads.Select(l => l.Id).ToList();
            var statRows = ids.Count == 0
                ? []
                : await _db.History.AsNoTracking()
                    .Where(h => ids.Contains(h.LeadId) && ContactKinds.Contains(h.Kind))
                    .GroupBy(h => h.LeadId)
                    .Select(g => new LeadStats(g.Key, g.Max(x => x.OccurredAt), g.Count(x => x.Kind == "Email sent")))
                    .ToListAsync();
            var stats = statRows.ToDictionary(x => x.LeadId);
            var names = await NamesAsync(leads.Where(l => l.OwnerUserId != null).Select(l => l.OwnerUserId!.Value));

            return leads.Select(l =>
            {
                stats.TryGetValue(l.Id, out var s);
                return Fill(new LeadSummaryDto(), l, user, names, s?.Last, s?.Emails ?? 0);
            }).ToList();
        }

        private static T Fill<T>(T dto, MkLead l, CurrentUser user, Dictionary<int, string> names, DateTime? lastContact, int emails) where T : LeadSummaryDto
        {
            dto.Id = l.Id; dto.Name = l.Name; dto.Title = l.Title; dto.Company = l.Company; dto.Email = l.Email; dto.Phone = l.Phone;
            dto.OrgPhone = l.OrgPhone; dto.LinkedInUrl = l.LinkedInUrl; dto.PossibleEmail = l.PossibleEmail; dto.OrgEmail = l.OrgEmail;
            dto.Channel = l.Channel; dto.Campaign = l.Campaign; dto.Interest = l.Interest; dto.Score = l.Score;
            dto.Temperature = MarketingValues.TemperatureOf(l.Score); dto.Stage = l.Stage; dto.CloseReason = l.CloseReason; dto.Consent = l.Consent;
            dto.OwnerUserId = l.OwnerUserId?.ToString();
            dto.OwnerName = l.OwnerUserId != null && names.TryGetValue(l.OwnerUserId.Value, out var n) ? n : null;
            dto.IsMine = l.OwnerUserId == user.Id;
            dto.CreatedAt = Utc(l.CreatedAt); dto.PickedUpAt = Utc(l.PickedUpAt); dto.LastContactAt = Utc(lastContact); dto.EmailsSent = emails;
            return dto;
        }

        private async Task<LeadDetailDto> ToDetailAsync(MkLead lead, CurrentUser user)
        {
            var entries = await _db.History.AsNoTracking().Include(h => h.Attachments)
                .Where(h => h.LeadId == lead.Id).ToListAsync();
            entries = entries.OrderByDescending(h => h.OccurredAt).ThenByDescending(h => h.CreatedAt).ToList();

            var contact = entries.Where(e => ContactKinds.Contains(e.Kind)).ToList();
            var names = await NamesAsync(entries.Where(e => e.CreatedByUserId != null).Select(e => e.CreatedByUserId!.Value)
                .Concat(lead.OwnerUserId is int owner ? [owner] : []));

            var dto = Fill(new LeadDetailDto(), lead, user, names, contact.Count == 0 ? null : contact.Max(e => e.OccurredAt), entries.Count(e => e.Kind == "Email sent"));
            dto.History = entries.Select(e => new HistoryEntryDto
            {
                Id = e.Id, Kind = e.Kind, OccurredAt = Utc(e.OccurredAt), Subject = e.Subject, Body = e.Body, Text = e.Text,
                CreatedByName = e.CreatedByUserId != null && names.TryGetValue(e.CreatedByUserId.Value, out var n) ? n : null,
                Attachments = e.Attachments.OrderBy(a => a.CreatedAt).ThenBy(a => a.FileName).Select(a => new AttachmentDto
                {
                    Id = a.Id, FileName = a.FileName, ContentType = a.ContentType, Size = a.SizeBytes,
                    SignedBy = a.SignedBy, SignedAt = Utc(a.SignedAt),
                }).ToList(),
            }).ToList();
            return dto;
        }
    }
}
