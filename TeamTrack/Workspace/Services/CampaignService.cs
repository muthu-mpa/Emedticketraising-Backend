using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TeamTrack.Data;
using TeamTrack.Services;
using TeamTrack.Workspace.Data;
using TeamTrack.Workspace.Dtos;
using TeamTrack.Workspace.Models;

namespace TeamTrack.Workspace.Services
{
    public interface ICampaignService
    {
        Task<WsResult<CampaignListDto>> ListAsync(CurrentUser user, string? status, bool onlyMine, string? search);
        Task<WsResult<CampaignDetailDto>> GetAsync(CurrentUser user, int id);
        Task<WsResult<CampaignDetailDto>> CreateAsync(CurrentUser user, CampaignInput input);
        Task<WsResult<CampaignDetailDto>> UpdateAsync(CurrentUser user, int id, CampaignInput input);
        Task<WsResult<CampaignDetailDto>> SendAsync(CurrentUser user, int id, bool confirm);
    }

    public class CampaignService : ICampaignService
    {
        private static readonly Regex EmailPattern = new(@"^[^@\s,;<>]+@[^@\s,;<>]+\.[^@\s,;<>]+$", RegexOptions.Compiled);
        private const string UnsubscribeLine = "Reply 'unsubscribe' to stop these emails";

        private readonly WorkspaceDbContext _db;
        private readonly AppDbContext _app;
        private readonly INotificationService _notifications;
        private readonly IEmailService _email;
        private readonly IConfiguration _config;
        private readonly ILogger<CampaignService> _logger;

        public CampaignService(WorkspaceDbContext db, AppDbContext app, INotificationService notifications,
            IEmailService email, IConfiguration config, ILogger<CampaignService> logger)
        {
            _db = db;
            _app = app;
            _notifications = notifications;
            _email = email;
            _config = config;
            _logger = logger;
        }

        // ---------- queries ----------

        public async Task<WsResult<CampaignListDto>> ListAsync(CurrentUser user, string? status, bool onlyMine, string? search)
        {
            var query = _db.Campaigns.AsQueryable();
            if (onlyMine) query = query.Where(c => c.CreatedByUserId == user.Id);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim().ToLower();
                var creatorIds = await _app.Users.Where(u => u.Name.ToLower().Contains(s)).Select(u => u.Id).ToListAsync();
                query = query.Where(c => c.Title.ToLower().Contains(s) || c.ClientName.ToLower().Contains(s)
                    || c.Subject.ToLower().Contains(s) || creatorIds.Contains(c.CreatedByUserId));
            }

            var counts = new Dictionary<string, int>
            {
                [CampaignStatus.Draft] = 0, [CampaignStatus.Sending] = 0, [CampaignStatus.Sent] = 0,
            };
            foreach (var g in await query.GroupBy(c => c.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync())
                counts[g.Key] = g.N;

            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!counts.ContainsKey(status))
                    return WsResult<CampaignListDto>.Fail(400, "Status must be draft, sending or sent.");
                query = query.Where(c => c.Status == status);
            }

            var rows = await query.OrderByDescending(c => c.UpdatedAt).ThenByDescending(c => c.Id).ToListAsync();
            var names = await NamesAsync(rows.Select(r => r.CreatedByUserId));
            return WsResult<CampaignListDto>.Ok(new CampaignListDto
            {
                Counts = counts,
                Items = rows.Select(r => ToDto(r, user, names)).ToList(),
            });
        }

        public async Task<WsResult<CampaignDetailDto>> GetAsync(CurrentUser user, int id)
        {
            var c = await _db.Campaigns.Include(x => x.History).FirstOrDefaultAsync(x => x.Id == id);
            return c == null ? NotFound() : WsResult<CampaignDetailDto>.Ok(await ToDetailAsync(c, user));
        }

        // ---------- commands ----------

        public async Task<WsResult<CampaignDetailDto>> CreateAsync(CurrentUser user, CampaignInput input)
        {
            if (user.IsPM) return WsResult<CampaignDetailDto>.Fail(403, "Only employees can create campaign mails.");
            if (string.IsNullOrWhiteSpace(input.Title)) return WsResult<CampaignDetailDto>.Fail(400, "A title is required.");
            var error = ValidateFields(input, requireAll: false);
            if (error != null) return WsResult<CampaignDetailDto>.Fail(400, error);

            var now = DateTime.UtcNow;
            var c = new WsCampaign { CreatedByUserId = user.Id, CreatedAt = now, UpdatedAt = now, Status = CampaignStatus.Draft };
            Apply(c, input);
            AddHistory(c, user.Id, "Created", null);
            _db.Campaigns.Add(c);
            await _db.SaveChangesAsync();
            return WsResult<CampaignDetailDto>.Ok(await ToDetailAsync(c, user), "Draft created.");
        }

        public async Task<WsResult<CampaignDetailDto>> UpdateAsync(CurrentUser user, int id, CampaignInput input)
        {
            var c = await LoadAsync(id);
            if (c == null) return NotFound();
            if (c.CreatedByUserId != user.Id) return WsResult<CampaignDetailDto>.Fail(403, "Only the creator can edit this mail.");
            if (c.Status != CampaignStatus.Draft) return WsResult<CampaignDetailDto>.Fail(409, "A mail can only be edited while it is a draft.");
            if (string.IsNullOrWhiteSpace(input.Title)) return WsResult<CampaignDetailDto>.Fail(400, "A title is required.");
            var error = ValidateFields(input, requireAll: false);
            if (error != null) return WsResult<CampaignDetailDto>.Fail(400, error);

            Apply(c, input);
            c.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return WsResult<CampaignDetailDto>.Ok(await ToDetailAsync(c, user), "Draft saved.");
        }

        /// <summary>The ERROR rules: a missing or invalid email, or placeholders left in the text. Enforced on every send.</summary>
        private static List<string> SendBlockers(WsCampaign c)
            => CampaignRules.SendBlockers(c.ClientEmail, c.Title, c.ClientName, c.ContactName, c.Subject, c.Body);

        public async Task<WsResult<CampaignDetailDto>> SendAsync(CurrentUser user, int id, bool confirm)
        {
            var c = await LoadAsync(id);
            if (c == null) return NotFound();
            if (c.CreatedByUserId != user.Id) return WsResult<CampaignDetailDto>.Fail(403, "Only the creator can send this mail.");
            if (c.Status == CampaignStatus.Sending) return WsResult<CampaignDetailDto>.Fail(409, AlreadyBeingSent);
            if (c.Status != CampaignStatus.Draft) return WsResult<CampaignDetailDto>.Fail(409, "Only a draft can be sent.");

            var error = ValidateFields(new CampaignInput
            {
                Title = c.Title, ClientName = c.ClientName, ContactName = c.ContactName,
                ClientEmail = c.ClientEmail, Subject = c.Subject, Body = c.Body,
            }, requireAll: true);
            if (error != null) return WsResult<CampaignDetailDto>.Fail(400, error);
            var blockers = SendBlockers(c);
            if (blockers.Count > 0) return WsResult<CampaignDetailDto>.Fail(400, string.Join(" ", blockers));
            if (!confirm) return WsResult<CampaignDetailDto>.Fail(400, $"Please confirm: send to {c.ContactName} at {c.ClientEmail}.");

            // Claim the mail atomically (UPDATE ... WHERE status = 'draft') so two requests can never both send it.
            if (!await TryClaimAsync(c.Id)) return WsResult<CampaignDetailDto>.Fail(409, AlreadyBeingSent);

            try
            {
                await _email.SendEmailAsync(c.ClientEmail, c.Subject, ComposeHtml(c.Body, FooterLines()));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sending campaign {CampaignId} to {Email} failed.", c.Id, c.ClientEmail);
                await ReleaseClaimAsync(c, user, ex.Message);
                return WsResult<CampaignDetailDto>.Fail(502, $"The mail could not be sent: {ex.Message}");
            }

            try
            {
                c.Status = CampaignStatus.Sent;
                c.SentAt = DateTime.UtcNow;
                c.UpdatedAt = c.SentAt.Value;
                AddHistory(c, user.Id, "Sent the mail", null);
                _notifications.Queue(await _notifications.PmIdsAsync(), "campaign_sent",
                    $"{user.Name} sent \"{c.Title}\" to {c.ContactName} ({c.ClientName}).", Link(c));
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // The mail is out but the row still says 'sending', so it cannot be sent a second time.
                _logger.LogCritical(ex, "Campaign {CampaignId} WAS EMAILED to {Email} but saving status 'sent' failed; it stays 'sending' on purpose. Do not resend.", c.Id, c.ClientEmail);
                throw;
            }
            return WsResult<CampaignDetailDto>.Ok(await ToDetailAsync(c, user), "Mail sent.");
        }

        private const string AlreadyBeingSent = "This mail is already being sent.";

        /// <summary>draft -> sending in one conditional UPDATE. False when another request got there first.</summary>
        public async Task<bool> TryClaimAsync(int id)
        {
            var now = DateTime.UtcNow;
            var rows = await _db.Campaigns
                .Where(x => x.Id == id && x.Status == CampaignStatus.Draft)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, CampaignStatus.Sending)
                    .SetProperty(x => x.UpdatedAt, now));
            return rows == 1;
        }

        /// <summary>After a failed send: sending -> draft again, and the error is kept in the history.</summary>
        private async Task ReleaseClaimAsync(WsCampaign c, CurrentUser user, string error)
        {
            try
            {
                var now = DateTime.UtcNow;
                await _db.Campaigns
                    .Where(x => x.Id == c.Id && x.Status == CampaignStatus.Sending)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Status, CampaignStatus.Draft)
                        .SetProperty(x => x.UpdatedAt, now));
                c.Status = CampaignStatus.Draft;
                AddHistory(c, user.Id, "Send failed", error.Length > 500 ? error[..500] : error);
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Campaign {CampaignId}: sending failed AND releasing the 'sending' status failed. It needs a manual reset to 'draft'.", c.Id);
            }
        }

        // ---------- helpers ----------

        private Task<WsCampaign?> LoadAsync(int id) => _db.Campaigns.Include(c => c.History).FirstOrDefaultAsync(c => c.Id == id);

        private static WsResult<CampaignDetailDto> NotFound() => WsResult<CampaignDetailDto>.Fail(404, "Campaign not found.");

        private static string Link(WsCampaign c) => $"/workspace/campaigns/{c.Id}";

        private static void AddHistory(WsCampaign c, int userId, string action, string? note)
            => c.History.Add(new WsCampaignHistory { UserId = userId, Action = action, Note = note, CreatedAt = DateTime.UtcNow });

        private static void Apply(WsCampaign c, CampaignInput i)
        {
            c.Title = (i.Title ?? "").Trim();
            c.ClientName = (i.ClientName ?? "").Trim();
            c.ContactName = (i.ContactName ?? "").Trim();
            c.ClientEmail = (i.ClientEmail ?? "").Trim();
            c.Subject = (i.Subject ?? "").Trim();
            c.Body = (i.Body ?? "").Trim();
        }

        internal static bool IsValidEmail(string? email) => !string.IsNullOrWhiteSpace(email) && email.Length <= 254 && EmailPattern.IsMatch(email.Trim());

        internal static string? ValidateFields(CampaignInput i, bool requireAll)
        {
            var singleLine = new (string Label, string? Value, int Max)[]
            {
                ("Title", i.Title, 200), ("Client name", i.ClientName, 200), ("Contact name", i.ContactName, 200),
                ("Subject", i.Subject, 300),
            };
            foreach (var (label, value, max) in singleLine)
            {
                if (requireAll && string.IsNullOrWhiteSpace(value)) return $"{label} is required.";
                if (value != null && (value.Contains('\r') || value.Contains('\n'))) return $"{label} must be a single line.";
                if (value != null && value.Trim().Length > max) return $"{label} is too long (max {max} characters).";
            }

            if (requireAll && string.IsNullOrWhiteSpace(i.ClientEmail)) return "Client email is required.";
            if (!string.IsNullOrWhiteSpace(i.ClientEmail) && !IsValidEmail(i.ClientEmail)) return "Client email is not a valid email address.";

            if (requireAll && string.IsNullOrWhiteSpace(i.Body)) return "Body is required.";
            if (i.Body != null && i.Body.Length > 20000) return "Body is too long (max 20000 characters).";
            return null;
        }

        private string[] FooterLines()
        {
            var address = _config["Workspace:CompanyAddress"];
            if (string.IsNullOrWhiteSpace(address)) address = "Emedlogix Solutions";
            return [address.Trim(), UnsubscribeLine];
        }

        /// <summary>The body (plain text, HTML-encoded) followed by the footer.</summary>
        internal static string ComposeHtml(string body, IEnumerable<string> footerLines)
        {
            var html = WebUtility.HtmlEncode(body).Replace("\r\n", "\n").Replace("\n", "<br/>");
            var footer = string.Join("<br/>", footerLines.Select(WebUtility.HtmlEncode));
            return $"<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;line-height:1.5\">{html}</div>"
                 + $"<hr style=\"border:none;border-top:1px solid #e2e8f0;margin:24px 0 12px\"/>"
                 + $"<p style=\"font-family:Segoe UI,Arial,sans-serif;font-size:12px;color:#64748b\">{footer}</p>";
        }

        private async Task<Dictionary<int, (string Name, string Email)>> NamesAsync(IEnumerable<int> ids)
        {
            var distinct = ids.Distinct().ToList();
            return await _app.Users.Where(u => distinct.Contains(u.Id))
                .Select(u => new { u.Id, u.Name, u.Email })
                .ToDictionaryAsync(u => u.Id, u => (u.Name, u.Email));
        }

        private static CampaignDto ToDto(WsCampaign c, CurrentUser v, Dictionary<int, (string Name, string Email)> names)
            => Fill(new CampaignDto(), c, v, names);

        private static T Fill<T>(T d, WsCampaign c, CurrentUser v, Dictionary<int, (string Name, string Email)> names) where T : CampaignDto
        {
            var isCreator = c.CreatedByUserId == v.Id;
            d.Id = c.Id; d.Title = c.Title; d.ClientName = c.ClientName; d.ContactName = c.ContactName;
            d.ClientEmail = c.ClientEmail; d.Subject = c.Subject; d.Body = c.Body; d.Status = c.Status;
            d.CreatedByUserId = c.CreatedByUserId;
            d.SendBlockers = c.Status == CampaignStatus.Draft ? SendBlockers(c) : [];
            d.CreatedByName = names.TryGetValue(c.CreatedByUserId, out var n) ? n.Name : "";
            d.CreatedAt = c.CreatedAt; d.UpdatedAt = c.UpdatedAt; d.SentAt = c.SentAt;
            d.CanEdit = isCreator && c.Status == CampaignStatus.Draft;
            d.CanSend = d.CanEdit;
            return d;
        }

        private async Task<CampaignDetailDto> ToDetailAsync(WsCampaign c, CurrentUser v)
        {
            var ids = c.History.Select(h => h.UserId).Append(c.CreatedByUserId);
            var names = await NamesAsync(ids);
            var d = Fill(new CampaignDetailDto(), c, v, names);
            d.FromName = d.CreatedByName;
            d.FromEmail = names.TryGetValue(c.CreatedByUserId, out var n) ? n.Email : "";
            d.Footer = string.Join("\n", FooterLines());
            d.History = c.History.OrderBy(h => h.CreatedAt).ThenBy(h => h.Id).Select(h => new CampaignHistoryDto
            {
                Id = h.Id, UserId = h.UserId, UserName = names.TryGetValue(h.UserId, out var hn) ? hn.Name : "",
                Action = h.Action, Note = h.Note, CreatedAt = h.CreatedAt,
            }).ToList();
            return d;
        }
    }
}
