using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TeamTrack.Data;
using TeamTrack.Workspace.Data;
using TeamTrack.Workspace.Dtos;
using TeamTrack.Workspace.Models;

namespace TeamTrack.Workspace.Services
{
    public interface ICampaignImportService
    {
        /// <summary>Parses an uploaded .xlsx and returns the rows with flags. Saves nothing.</summary>
        Task<WsResult<ImportPreviewDto>> PreviewAsync(CurrentUser user, IFormFile? file);

        /// <summary>The same preview for data that is already parsed (a shared sheet's current content).</summary>
        Task<WsResult<ImportPreviewDto>> PreviewBookAsync(CurrentUser user, string fileName, SheetBook book);

        /// <summary>Creates drafts for the chosen rows, from a preview token or from the file itself.</summary>
        Task<WsResult<ImportCommitResultDto>> CommitAsync(CurrentUser user, string? previewToken, IFormFile? file, IReadOnlyList<int>? selectedRows);

        Task<WsResult<List<ImportBatchDto>>> ListBatchesAsync(CurrentUser user, int take = 50);
        byte[] Template();
    }

    public class CampaignImportService : ICampaignImportService
    {
        public const long MaxFileBytes = 5L * 1024 * 1024;
        public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(30);
        private const string CachePrefix = "ws-import-preview:";

        private sealed record StoredPreview(int UserId, string FileName, ParsedImport Parsed);

        private readonly WorkspaceDbContext _db;
        private readonly AppDbContext _app;
        private readonly IMemoryCache _cache;

        public CampaignImportService(WorkspaceDbContext db, AppDbContext app, IMemoryCache cache)
        {
            _db = db;
            _app = app;
            _cache = cache;
        }

        public byte[] Template() => XlsxConverter.BuildTemplate();

        // ---------- preview ----------

        public async Task<WsResult<ImportPreviewDto>> PreviewAsync(CurrentUser user, IFormFile? file)
        {
            var denied = CheckCreator<ImportPreviewDto>(user);
            if (denied != null) return denied;
            if (file == null) return WsResult<ImportPreviewDto>.Fail(400, "Upload an .xlsx file.");
            try
            {
                var book = XlsxConverter.Read(await XlsxConverter.ReadUploadAsync(file, MaxFileBytes));
                return await PreviewParsedAsync(user, CleanFileName(file.FileName), CampaignImportParser.Parse(book));
            }
            catch (XlsxException ex) { return WsResult<ImportPreviewDto>.Fail(400, ex.Message); }
        }

        public async Task<WsResult<ImportPreviewDto>> PreviewBookAsync(CurrentUser user, string fileName, SheetBook book)
        {
            var denied = CheckCreator<ImportPreviewDto>(user);
            if (denied != null) return denied;
            try { return await PreviewParsedAsync(user, CleanFileName(fileName), CampaignImportParser.Parse(book)); }
            catch (XlsxException ex) { return WsResult<ImportPreviewDto>.Fail(400, ex.Message); }
        }

        private async Task<WsResult<ImportPreviewDto>> PreviewParsedAsync(CurrentUser user, string fileName, ParsedImport parsed)
        {
            var existing = await ExistingKeysAsync(parsed.Rows);
            var seenInFile = new HashSet<(string, string)>();
            var rows = new List<ImportRowDto>();
            foreach (var c in parsed.Rows)
            {
                var duplicate = false;
                if (c.ClientEmail.Length > 0 && c.Subject.Length > 0)
                {
                    var key = DuplicateKey(c.ClientEmail, c.Subject);
                    duplicate = existing.Contains(key) | !seenInFile.Add(key); // non-short-circuit: always record the key
                }
                rows.Add(ToRowDto(c, duplicate));
            }

            var token = Guid.NewGuid().ToString("N");
            var expires = DateTime.UtcNow.Add(PreviewLifetime);
            _cache.Set(CachePrefix + token, new StoredPreview(user.Id, fileName, parsed), PreviewLifetime);

            return WsResult<ImportPreviewDto>.Ok(new ImportPreviewDto
            {
                PreviewToken = token,
                ExpiresAt = expires,
                FileName = fileName,
                Format = parsed.Format == ImportFormat.ProspectWorkbook ? "prospect_workbook" : "simple_template",
                TotalRows = rows.Count,
                ErrorRows = rows.Count(r => r.Severity == "error"),
                WarningRows = rows.Count(r => r.Severity == "warning"),
                OkRows = rows.Count(r => r.Severity == "ok"),
                Rows = rows,
            });
        }

        internal static (string Email, string Subject) DuplicateKey(string email, string subject)
            => (email.Trim().ToLowerInvariant(), subject.Trim().ToLowerInvariant());

        private async Task<HashSet<(string, string)>> ExistingKeysAsync(List<ImportCandidate> rows)
        {
            var emails = rows.Where(r => r.ClientEmail.Length > 0).Select(r => r.ClientEmail.ToLowerInvariant()).Distinct().ToList();
            if (emails.Count == 0) return [];
            var found = await _db.Campaigns.Where(c => emails.Contains(c.ClientEmail.ToLower()))
                .Select(c => new { c.ClientEmail, c.Subject }).ToListAsync();
            return found.Select(f => DuplicateKey(f.ClientEmail, f.Subject)).ToHashSet();
        }

        private static ImportRowDto ToRowDto(ImportCandidate c, bool duplicate)
        {
            var flags = new List<ImportFlagDto>();
            void Error(string code, string message) => flags.Add(new ImportFlagDto { Level = "error", Code = code, Message = message });
            void Warn(string code, string message) => flags.Add(new ImportFlagDto { Level = "warning", Code = code, Message = message });

            if (string.IsNullOrWhiteSpace(c.RawEmail)) Error("email_missing", "Client email is missing.");
            else if (c.ClientEmail.Length == 0)
                Error("email_invalid", $"\"{(c.RawEmail.Length > 60 ? c.RawEmail[..60] + "…" : c.RawEmail)}\" is not a valid email address; the email was left empty.");

            var placeholders = CampaignRules.PlaceholdersIn(c.Title, c.ClientName, c.ContactName, c.Subject, c.Body);
            if (placeholders.Count > 0) Error("placeholder", $"Placeholders left in the text: {string.Join(", ", placeholders)}.");
            if (c.Subject.Length == 0) Error("subject_missing", "Subject is missing.");
            if (c.Body.Length == 0) Error("body_missing", "Body is missing.");

            if (CampaignRules.MentionsAutonom(c.Title, c.Subject, c.Body)) Warn("autonom", "The text contains \"autonom…\". Check the wording.");
            if (duplicate) Warn("duplicate", "A campaign to this email with the same subject already exists (or appears twice in this file).");
            if (c.Truncated) Warn("truncated", "Some text was too long and was shortened.");

            return new ImportRowDto
            {
                RowNumber = c.RowNumber, Title = c.Title, ClientName = c.ClientName, ContactName = c.ContactName,
                ClientEmail = c.ClientEmail, Subject = c.Subject, Body = c.Body,
                Segment = c.Segment, Priority = c.Priority, SendWave = c.SendWave,
                Flags = flags,
                Severity = flags.Any(f => f.Level == "error") ? "error" : flags.Count > 0 ? "warning" : "ok",
            };
        }

        // ---------- commit ----------

        public async Task<WsResult<ImportCommitResultDto>> CommitAsync(CurrentUser user, string? previewToken, IFormFile? file, IReadOnlyList<int>? selectedRows)
        {
            var denied = CheckCreator<ImportCommitResultDto>(user);
            if (denied != null) return denied;
            if (selectedRows == null || selectedRows.Count == 0)
                return WsResult<ImportCommitResultDto>.Fail(400, "Select at least one row to import.");

            string fileName;
            ParsedImport parsed;
            var tokenKey = CachePrefix + previewToken;
            if (!string.IsNullOrWhiteSpace(previewToken))
            {
                if (!_cache.TryGetValue(tokenKey, out StoredPreview? stored) || stored == null || stored.UserId != user.Id)
                    return WsResult<ImportCommitResultDto>.Fail(404, "This preview has expired. Upload the file again.");
                (fileName, parsed) = (stored.FileName, stored.Parsed);
            }
            else if (file != null)
            {
                try
                {
                    parsed = CampaignImportParser.Parse(XlsxConverter.Read(await XlsxConverter.ReadUploadAsync(file, MaxFileBytes)));
                    fileName = CleanFileName(file.FileName);
                }
                catch (XlsxException ex) { return WsResult<ImportCommitResultDto>.Fail(400, ex.Message); }
            }
            else return WsResult<ImportCommitResultDto>.Fail(400, "Send a previewToken or the .xlsx file.");

            var byRow = parsed.Rows.ToDictionary(r => r.RowNumber);
            var chosen = new List<ImportCandidate>();
            foreach (var number in selectedRows.Distinct())
            {
                if (!byRow.TryGetValue(number, out var row))
                    return WsResult<ImportCommitResultDto>.Fail(400, $"Row {number} is not one of the rows in the file.");
                chosen.Add(row);
            }
            chosen = chosen.OrderBy(r => r.RowNumber).ToList();

            var now = DateTime.UtcNow;
            var batch = new WsCampaignImport
            {
                FileName = fileName, ImportedBy = user.Id, TotalRows = parsed.Rows.Count,
                ImportedRows = chosen.Count, CreatedAt = now,
            };
            var created = new List<(ImportCandidate Row, WsCampaign Campaign)>();
            foreach (var r in chosen)
            {
                var c = new WsCampaign
                {
                    Title = r.Title, ClientName = r.ClientName, ContactName = r.ContactName, ClientEmail = r.ClientEmail,
                    Subject = r.Subject, Body = r.Body, Status = CampaignStatus.Draft, CreatedByUserId = user.Id,
                    CreatedAt = now, UpdatedAt = now, Import = batch, SourceRow = r.RowNumber,
                    Segment = r.Segment, Priority = r.Priority, SendWave = r.SendWave,
                };
                c.History.Add(new WsCampaignHistory { UserId = user.Id, Action = "Created", Note = $"Imported from {fileName}, row {r.RowNumber}", CreatedAt = now });
                _db.Campaigns.Add(c);
                created.Add((r, c));
            }
            await _db.SaveChangesAsync(); // batch + drafts in one transaction

            if (!string.IsNullOrWhiteSpace(previewToken)) _cache.Remove(tokenKey); // a preview imports once

            return WsResult<ImportCommitResultDto>.Ok(new ImportCommitResultDto
            {
                ImportId = batch.Id, FileName = fileName, ImportedRows = chosen.Count,
                SkippedRows = parsed.Rows.Count - chosen.Count,
                Campaigns = created.Select(x => new ImportedCampaignDto { RowNumber = x.Row.RowNumber, CampaignId = x.Campaign.Id }).ToList(),
            }, $"{chosen.Count} draft(s) created.");
        }

        // ---------- batches ----------

        public async Task<WsResult<List<ImportBatchDto>>> ListBatchesAsync(CurrentUser user, int take = 50)
        {
            take = Math.Clamp(take, 1, 200);
            var batches = await _db.CampaignImports.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id).Take(take).ToListAsync();
            var ids = batches.Select(b => b.ImportedBy).Distinct().ToList();
            var names = await _app.Users.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name);
            return WsResult<List<ImportBatchDto>>.Ok(batches.Select(b => new ImportBatchDto
            {
                Id = b.Id, FileName = b.FileName, ImportedBy = b.ImportedBy,
                ImportedByName = names.GetValueOrDefault(b.ImportedBy) ?? "",
                CreatedAt = b.CreatedAt, TotalRows = b.TotalRows, ImportedRows = b.ImportedRows,
            }).ToList());
        }

        // ---------- helpers ----------

        private static WsResult<T>? CheckCreator<T>(CurrentUser user)
            => user.IsPM ? WsResult<T>.Fail(403, "Only employees can import campaign mails.") : null;

        private static string CleanFileName(string? name)
        {
            var clean = Path.GetFileName((name ?? "").Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(clean)) clean = "import.xlsx";
            return clean.Length > 200 ? clean[..200] : clean;
        }
    }
}
