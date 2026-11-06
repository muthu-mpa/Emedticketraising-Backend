using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TeamTrack.Data;
using TeamTrack.Workspace.Data;
using TeamTrack.Workspace.Dtos;
using TeamTrack.Workspace.Models;

namespace TeamTrack.Workspace.Services
{
    public interface ISharedSheetService
    {
        /// <summary>An uploaded .xlsx (max 10 MB) or, with no file, a blank sheet with the given title.</summary>
        Task<WsResult<SheetDetailDto>> CreateAsync(CurrentUser user, string? title, IFormFile? file);
        Task<WsResult<List<SheetListItemDto>>> ListAsync(CurrentUser user);
        Task<WsResult<SheetDetailDto>> GetAsync(CurrentUser user, int id);
        /// <summary>Tiny response for polling; does not load the sheet data.</summary>
        Task<WsResult<SheetVersionDto>> VersionAsync(CurrentUser user, int id);
        /// <summary>All-or-nothing: a 409 carries the conflicting cells and nothing is applied.</summary>
        Task<WsResult<SheetPatchResultDto>> PatchAsync(CurrentUser user, int id, SheetPatchInput input);
        Task<WsResult<List<SheetAccessDto>>> SetAccessAsync(CurrentUser user, int id, IReadOnlyList<SheetAccessInput>? input);
        Task<WsResult<SheetFileDto>> DownloadAsync(CurrentUser user, int id);
        Task<WsResult<List<SheetHistoryDto>>> HistoryAsync(CurrentUser user, int id);
        Task<WsResult<bool>> DeleteAsync(CurrentUser user, int id);
        /// <summary>Runs the campaign import preview on the sheet's current content.</summary>
        Task<WsResult<ImportPreviewDto>> ImportPreviewAsync(CurrentUser user, int id);
    }

    public class SharedSheetService : ISharedSheetService
    {
        public const long MaxFileBytes = 10L * 1024 * 1024;
        public const int MaxChangesPerSave = 5000;
        public static readonly TimeSpan NotifyEvery = TimeSpan.FromMinutes(10);
        private const int MaxSaveAttempts = 3;
        private const string SavedType = "sheet_saved";
        private const string SharedType = "sheet_shared";

        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly WorkspaceDbContext _db;
        private readonly AppDbContext _app;
        private readonly INotificationService _notifications;
        private readonly ISheetFileStore _files;
        private readonly ICampaignImportService _imports;
        private readonly ILogger<SharedSheetService> _logger;

        public SharedSheetService(WorkspaceDbContext db, AppDbContext app, INotificationService notifications,
            ISheetFileStore files, ICampaignImportService imports, ILogger<SharedSheetService> logger)
        {
            _db = db;
            _app = app;
            _notifications = notifications;
            _files = files;
            _imports = imports;
            _logger = logger;
        }

        // ---------- create / read ----------

        public async Task<WsResult<SheetDetailDto>> CreateAsync(CurrentUser user, string? title, IFormFile? file)
        {
            title = string.IsNullOrWhiteSpace(title) && file != null ? Path.GetFileNameWithoutExtension(file.FileName) : title;
            var error = ValidateTitle(title);
            if (error != null) return WsResult<SheetDetailDto>.Fail(400, error);

            SheetBook book;
            string? storedPath = null;
            if (file != null)
            {
                try
                {
                    var bytes = await XlsxConverter.ReadUploadAsync(file, MaxFileBytes);
                    book = XlsxConverter.Read(bytes);
                    storedPath = await _files.SaveAsync(bytes);
                }
                catch (XlsxException ex) { return WsResult<SheetDetailDto>.Fail(400, ex.Message); }
            }
            else book = new SheetBook { Sheets = [new SheetTab { Name = "Sheet1" }] };

            var now = DateTime.UtcNow;
            var sheet = new WsSharedSheet
            {
                Title = title!.Trim(), OwnerId = user.Id, OriginalFilePath = storedPath, Data = book.ToJson(),
                Version = 1, UpdatedBy = user.Id, UpdatedAt = now, CreatedAt = now,
            };
            try
            {
                _db.SharedSheets.Add(sheet);
                await _db.SaveChangesAsync();
            }
            catch
            {
                _files.Delete(storedPath); // no orphan file when the row could not be saved
                throw;
            }
            return WsResult<SheetDetailDto>.Ok(await ToDetailAsync(sheet, book, user, new Access(true, true, true)), "Sheet created.");
        }

        public async Task<WsResult<List<SheetListItemDto>>> ListAsync(CurrentUser user)
        {
            var rows = await (
                from s in _db.SharedSheets
                join a in _db.SharedSheetAccess.Where(x => x.UserId == user.Id) on s.Id equals a.SheetId into mine
                from a in mine.DefaultIfEmpty()
                where s.OwnerId == user.Id || a != null
                orderby s.UpdatedAt descending, s.Id descending
                select new { s.Id, s.Title, s.OwnerId, s.Version, s.UpdatedBy, s.UpdatedAt, s.CreatedAt, SharedEdit = a != null && a.CanEdit }
            ).ToListAsync();

            var names = await NamesAsync(rows.Select(r => r.OwnerId).Concat(rows.Where(r => r.UpdatedBy != null).Select(r => r.UpdatedBy!.Value)));
            return WsResult<List<SheetListItemDto>>.Ok(rows.Select(r => new SheetListItemDto
            {
                Id = r.Id, Title = r.Title, OwnerId = r.OwnerId, OwnerName = names.GetValueOrDefault(r.OwnerId) ?? "",
                IsOwner = r.OwnerId == user.Id, CanEdit = r.OwnerId == user.Id || r.SharedEdit,
                Version = r.Version, UpdatedBy = r.UpdatedBy, UpdatedByName = r.UpdatedBy == null ? null : names.GetValueOrDefault(r.UpdatedBy.Value),
                UpdatedAt = r.UpdatedAt, CreatedAt = r.CreatedAt,
            }).ToList());
        }

        public async Task<WsResult<SheetDetailDto>> GetAsync(CurrentUser user, int id)
        {
            var sheet = await _db.SharedSheets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
            if (sheet == null) return NotFound<SheetDetailDto>();
            var access = await AccessAsync(user, sheet.Id, sheet.OwnerId);
            if (!access.CanView) return NoAccess<SheetDetailDto>();
            return WsResult<SheetDetailDto>.Ok(await ToDetailAsync(sheet, SheetBook.FromJson(sheet.Data), user, access));
        }

        public async Task<WsResult<SheetVersionDto>> VersionAsync(CurrentUser user, int id)
        {
            var meta = await _db.SharedSheets.Where(s => s.Id == id)
                .Select(s => new { s.OwnerId, s.Version, s.UpdatedBy, s.UpdatedAt }).FirstOrDefaultAsync();
            if (meta == null) return NotFound<SheetVersionDto>();
            if (!(await AccessAsync(user, id, meta.OwnerId)).CanView) return NoAccess<SheetVersionDto>();
            var names = await NamesAsync(meta.UpdatedBy == null ? [] : [meta.UpdatedBy.Value]);
            return WsResult<SheetVersionDto>.Ok(new SheetVersionDto
            {
                Version = meta.Version, UpdatedBy = meta.UpdatedBy,
                UpdatedByName = meta.UpdatedBy == null ? null : names.GetValueOrDefault(meta.UpdatedBy.Value), UpdatedAt = meta.UpdatedAt,
            });
        }

        // ---------- save ----------

        public async Task<WsResult<SheetPatchResultDto>> PatchAsync(CurrentUser user, int id, SheetPatchInput input)
        {
            var error = ValidatePatch(input);
            if (error != null) return WsResult<SheetPatchResultDto>.Fail(400, error);

            for (var attempt = 1; attempt <= MaxSaveAttempts; attempt++)
            {
                _db.ChangeTracker.Clear(); // a retry must see the other save, not a cached row
                var sheet = await _db.SharedSheets.FirstOrDefaultAsync(s => s.Id == id);
                if (sheet == null) return NotFound<SheetPatchResultDto>();
                var access = await AccessAsync(user, sheet.Id, sheet.OwnerId);
                if (!access.CanView) return NoAccess<SheetPatchResultDto>();
                if (!access.CanEdit) return WsResult<SheetPatchResultDto>.Fail(403, "You can view this sheet but not edit it.");
                if (input.BaseVersion > sheet.Version)
                    return WsResult<SheetPatchResultDto>.Fail(400, $"baseVersion {input.BaseVersion} is newer than the sheet (version {sheet.Version}).");

                var book = SheetBook.FromJson(sheet.Data);
                var wanted = new Dictionary<CellKey, SheetCell?>(); // later entries for the same cell win
                foreach (var ch in input.Changes!)
                {
                    var tab = book.Find(ch.Sheet);
                    if (tab == null) return WsResult<SheetPatchResultDto>.Fail(400, $"Sheet \"{ch.Sheet}\" does not exist.");
                    wanted[new CellKey(tab.Name, ch.Row, ch.Col)] = SheetCell.FromInput(ch.Value);
                }

                var conflicts = await FindConflictsAsync(sheet, user, input.BaseVersion, book, wanted);
                if (conflicts.Count > 0)
                {
                    return new WsResult<SheetPatchResultDto>(false,
                        new SheetPatchResultDto { Version = sheet.Version, UpdatedAt = sheet.UpdatedAt, Conflicts = conflicts },
                        $"{conflicts.Count} cell(s) were changed by someone else since version {input.BaseVersion}. Nothing was saved.", 409);
                }

                var applied = new List<object>();
                foreach (var (key, cell) in wanted)
                {
                    var tab = book.Find(key.Sheet)!;
                    if (SameCell(tab.Get(key.Row, key.Col), cell)) continue; // typing the value that is already there changes nothing
                    tab.Set(key.Row, key.Col, cell);
                    applied.Add(new { sheet = key.Sheet, row = key.Row, col = key.Col, value = cell?.ToInput() });
                }
                if (applied.Count == 0)
                    return WsResult<SheetPatchResultDto>.Ok(new SheetPatchResultDto { Version = sheet.Version, UpdatedAt = sheet.UpdatedAt }, "No cell changed.");

                var now = DateTime.UtcNow;
                sheet.Data = book.ToJson();
                sheet.Version += 1;
                sheet.UpdatedBy = user.Id;
                sheet.UpdatedAt = now;
                _db.SharedSheetChanges.Add(new WsSharedSheetChange
                {
                    SheetId = sheet.Id, UserId = user.Id, Version = sheet.Version,
                    ChangedCells = JsonSerializer.Serialize(applied, Json), CreatedAt = now,
                });
                await QueueSavedNotificationsAsync(sheet, user, now);

                try
                {
                    await _db.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    continue; // someone saved between our read and write: look again (and re-check for conflicts)
                }
                return WsResult<SheetPatchResultDto>.Ok(new SheetPatchResultDto { Version = sheet.Version, AppliedCells = applied.Count, UpdatedAt = now }, "Saved.");
            }
            _db.ChangeTracker.Clear();
            return WsResult<SheetPatchResultDto>.Fail(409, "The sheet is being edited by others right now. Reload it and try again.");
        }

        private readonly record struct CellKey(string Sheet, int Row, int Col)
        {
            public bool Equals(CellKey other) => Row == other.Row && Col == other.Col && string.Equals(Sheet, other.Sheet, StringComparison.OrdinalIgnoreCase);
            public override int GetHashCode() => HashCode.Combine(Sheet.ToLowerInvariant(), Row, Col);
        }

        private static bool SameCell(SheetCell? a, SheetCell? b)
            => a == null || a.IsEmpty ? b == null || b.IsEmpty : b != null && a.SameAs(b);

        /// <summary>
        /// A cell conflicts when somebody else changed it in a version after the client's baseVersion and it now holds
        /// something other than what the client wants to write.
        /// </summary>
        private async Task<List<SheetCellConflictDto>> FindConflictsAsync(WsSharedSheet sheet, CurrentUser user, int baseVersion,
            SheetBook book, Dictionary<CellKey, SheetCell?> wanted)
        {
            var conflicts = new List<SheetCellConflictDto>();
            if (baseVersion >= sheet.Version) return conflicts;

            var newer = await _db.SharedSheetChanges
                .Where(c => c.SheetId == sheet.Id && c.Version > baseVersion && c.UserId != user.Id)
                .OrderBy(c => c.Version).ToListAsync();
            if (newer.Count == 0) return conflicts;

            var lastTouched = new Dictionary<CellKey, (int Version, int UserId)>();
            foreach (var change in newer)
            {
                using var doc = JsonDocument.Parse(change.ChangedCells);
                foreach (var e in doc.RootElement.EnumerateArray())
                    lastTouched[new CellKey(e.GetProperty("sheet").GetString() ?? "", e.GetProperty("row").GetInt32(), e.GetProperty("col").GetInt32())] = (change.Version, change.UserId);
            }

            var names = await NamesAsync(lastTouched.Values.Select(v => v.UserId));
            foreach (var (key, cell) in wanted)
            {
                if (!lastTouched.TryGetValue(key, out var touched)) continue;
                var current = book.Find(key.Sheet)?.Get(key.Row, key.Col);
                if (SameCell(current, cell)) continue;
                conflicts.Add(new SheetCellConflictDto
                {
                    Sheet = key.Sheet, Row = key.Row, Col = key.Col, CurrentValue = current?.ToInput(),
                    ChangedInVersion = touched.Version, ChangedByName = names.GetValueOrDefault(touched.UserId) ?? "",
                });
            }
            return conflicts.OrderBy(c => c.Sheet).ThenBy(c => c.Row).ThenBy(c => c.Col).ToList();
        }

        private static string? ValidatePatch(SheetPatchInput? input)
        {
            if (input == null) return "A request body is required.";
            if (input.BaseVersion < 1) return "baseVersion is required (the version you loaded).";
            if (input.Changes == null || input.Changes.Count == 0) return "changes must contain at least one cell.";
            if (input.Changes.Count > MaxChangesPerSave) return $"At most {MaxChangesPerSave} cells per save.";
            foreach (var ch in input.Changes)
            {
                if (ch == null || string.IsNullOrWhiteSpace(ch.Sheet)) return "Every change needs a sheet name.";
                if (ch.Row < 1 || ch.Row > 1_048_576) return $"Row {ch.Row} is outside 1 to 1048576.";
                if (ch.Col < 1 || ch.Col > 16_384) return $"Column {ch.Col} is outside 1 to 16384.";
                var value = SheetCell.Normalize(ch.Value);
                if (value is string s && s.Length > 32_767) return "A cell can hold at most 32767 characters.";
                if (value == null && ch.Value is JsonElement { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
                    return "A cell value must be text, a number, true/false or null.";
            }
            return null;
        }

        private async Task QueueSavedNotificationsAsync(WsSharedSheet sheet, CurrentUser saver, DateTime now)
        {
            var recipients = await _db.SharedSheetAccess.Where(a => a.SheetId == sheet.Id).Select(a => a.UserId).ToListAsync();
            recipients.Add(sheet.OwnerId);
            recipients = recipients.Where(r => r != saver.Id).Distinct().ToList();
            if (recipients.Count == 0) return;

            // at most one per sheet and person every 10 minutes: skip anyone already told within the window
            var link = SheetLink(sheet.Id);
            var since = now - NotifyEvery;
            var recentlyTold = await _db.Notifications
                .Where(n => n.Type == SavedType && n.Link == link && n.CreatedAt >= since && recipients.Contains(n.UserId))
                .Select(n => n.UserId).Distinct().ToListAsync();
            _notifications.Queue(recipients.Except(recentlyTold), SavedType, $"{saver.Name} edited the sheet \"{sheet.Title}\".", link);
        }

        // ---------- sharing ----------

        public async Task<WsResult<List<SheetAccessDto>>> SetAccessAsync(CurrentUser user, int id, IReadOnlyList<SheetAccessInput>? input)
        {
            var sheet = await _db.SharedSheets.Where(s => s.Id == id).Select(s => new { s.Id, s.OwnerId, s.Title }).FirstOrDefaultAsync();
            if (sheet == null) return NotFound<List<SheetAccessDto>>();
            if (sheet.OwnerId != user.Id) return WsResult<List<SheetAccessDto>>.Fail(403, "Only the owner can change who the sheet is shared with.");
            if (input == null) return WsResult<List<SheetAccessDto>>.Fail(400, "Send the full list of people (an empty list stops sharing).");

            var wanted = input.GroupBy(i => i.UserId).ToDictionary(g => g.Key, g => g.Last().CanEdit); // the last entry for a person wins
            if (wanted.ContainsKey(sheet.OwnerId)) return WsResult<List<SheetAccessDto>>.Fail(400, "The owner already has full access.");
            var wantedIds = wanted.Keys.ToList();
            var known = await _app.Users.Where(u => u.IsActive && wantedIds.Contains(u.Id)).Select(u => u.Id).ToListAsync();
            var unknown = wanted.Keys.Except(known).ToList();
            if (unknown.Count > 0) return WsResult<List<SheetAccessDto>>.Fail(400, $"User {string.Join(", ", unknown)} not found.");

            var current = await _db.SharedSheetAccess.Where(a => a.SheetId == id).ToListAsync();
            foreach (var gone in current.Where(a => !wanted.ContainsKey(a.UserId))) _db.SharedSheetAccess.Remove(gone);
            foreach (var (userId, canEdit) in wanted)
            {
                var row = current.FirstOrDefault(a => a.UserId == userId);
                if (row != null) { row.CanEdit = canEdit; continue; }
                _db.SharedSheetAccess.Add(new WsSharedSheetAccess { SheetId = id, UserId = userId, CanEdit = canEdit });
                _notifications.Queue([userId], SharedType,
                    $"{user.Name} shared the sheet \"{sheet.Title}\" with you ({(canEdit ? "can edit" : "view only")}).", SheetLink(id));
            }
            await _db.SaveChangesAsync();
            return WsResult<List<SheetAccessDto>>.Ok(await AccessListAsync(id), "Sharing updated.");
        }

        private async Task<List<SheetAccessDto>> AccessListAsync(int sheetId)
        {
            var rows = await _db.SharedSheetAccess.Where(a => a.SheetId == sheetId).ToListAsync();
            var names = await NamesAsync(rows.Select(r => r.UserId));
            return rows.Select(r => new SheetAccessDto { UserId = r.UserId, UserName = names.GetValueOrDefault(r.UserId) ?? "", CanEdit = r.CanEdit })
                .OrderBy(r => r.UserName).ToList();
        }

        // ---------- export, history, delete, bonus ----------

        public async Task<WsResult<SheetFileDto>> DownloadAsync(CurrentUser user, int id)
        {
            var sheet = await _db.SharedSheets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
            if (sheet == null) return NotFound<SheetFileDto>();
            if (!(await AccessAsync(user, sheet.Id, sheet.OwnerId)).CanView) return NoAccess<SheetFileDto>();
            return WsResult<SheetFileDto>.Ok(new SheetFileDto { Bytes = XlsxConverter.Write(SheetBook.FromJson(sheet.Data)), FileName = SafeFileName(sheet.Title) + ".xlsx" });
        }

        public async Task<WsResult<List<SheetHistoryDto>>> HistoryAsync(CurrentUser user, int id)
        {
            var meta = await _db.SharedSheets.Where(s => s.Id == id).Select(s => new { s.OwnerId }).FirstOrDefaultAsync();
            if (meta == null) return NotFound<List<SheetHistoryDto>>();
            if (!(await AccessAsync(user, id, meta.OwnerId)).CanView) return NoAccess<List<SheetHistoryDto>>();

            var changes = await _db.SharedSheetChanges.Where(c => c.SheetId == id)
                .OrderByDescending(c => c.Version).Take(200).ToListAsync();
            var names = await NamesAsync(changes.Select(c => c.UserId));
            return WsResult<List<SheetHistoryDto>>.Ok(changes.Select(c => new SheetHistoryDto
            {
                Version = c.Version, UserId = c.UserId, UserName = names.GetValueOrDefault(c.UserId) ?? "",
                CellCount = CountCells(c.ChangedCells), CreatedAt = c.CreatedAt,
            }).ToList());
        }

        private static int CountCells(string json)
        {
            try { using var doc = JsonDocument.Parse(json); return doc.RootElement.GetArrayLength(); }
            catch (JsonException) { return 0; }
        }

        public async Task<WsResult<bool>> DeleteAsync(CurrentUser user, int id)
        {
            var sheet = await _db.SharedSheets.FirstOrDefaultAsync(s => s.Id == id);
            if (sheet == null) return NotFound<bool>();
            if (sheet.OwnerId != user.Id) return WsResult<bool>.Fail(403, "Only the owner can delete this sheet.");

            await using var tx = await _db.Database.BeginTransactionAsync();
            await _db.SharedSheetChanges.Where(c => c.SheetId == id).ExecuteDeleteAsync();
            await _db.SharedSheetAccess.Where(a => a.SheetId == id).ExecuteDeleteAsync();
            _db.SharedSheets.Remove(sheet);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            _files.Delete(sheet.OriginalFilePath);
            return WsResult<bool>.Ok(true, "Sheet deleted.");
        }

        public async Task<WsResult<ImportPreviewDto>> ImportPreviewAsync(CurrentUser user, int id)
        {
            var sheet = await _db.SharedSheets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
            if (sheet == null) return NotFound<ImportPreviewDto>();
            if (!(await AccessAsync(user, sheet.Id, sheet.OwnerId)).CanView) return NoAccess<ImportPreviewDto>();
            return await _imports.PreviewBookAsync(user, $"{sheet.Title} (shared sheet #{sheet.Id})", SheetBook.FromJson(sheet.Data));
        }

        // ---------- helpers ----------

        private sealed record Access(bool IsOwner, bool CanView, bool CanEdit);

        /// <summary>
        /// Owner: view + edit. Shared user: view, and edit only with can_edit. A ProductManager (role in the JWT) can
        /// view any sheet, but edits only when it is shared with can_edit.
        /// </summary>
        private async Task<Access> AccessAsync(CurrentUser user, int sheetId, int ownerId)
        {
            if (ownerId == user.Id) return new Access(true, true, true);
            var shared = await _db.SharedSheetAccess.Where(a => a.SheetId == sheetId && a.UserId == user.Id).Select(a => (bool?)a.CanEdit).FirstOrDefaultAsync();
            return new Access(false, shared != null || user.IsPM, shared == true);
        }

        private async Task<SheetDetailDto> ToDetailAsync(WsSharedSheet sheet, SheetBook book, CurrentUser user, Access access)
        {
            var people = new List<int> { sheet.OwnerId };
            if (sheet.UpdatedBy != null) people.Add(sheet.UpdatedBy.Value);
            var names = await NamesAsync(people);
            return new SheetDetailDto
            {
                Id = sheet.Id, Title = sheet.Title, OwnerId = sheet.OwnerId, OwnerName = names.GetValueOrDefault(sheet.OwnerId) ?? "",
                IsOwner = access.IsOwner, CanEdit = access.CanEdit, Version = sheet.Version,
                UpdatedBy = sheet.UpdatedBy, UpdatedByName = sheet.UpdatedBy == null ? null : names.GetValueOrDefault(sheet.UpdatedBy.Value),
                UpdatedAt = sheet.UpdatedAt, CreatedAt = sheet.CreatedAt,
                Data = book,
                FormattingLost = sheet.OriginalFilePath != null,
                SharedWith = access.IsOwner ? await AccessListAsync(sheet.Id) : null,
            };
        }

        private async Task<Dictionary<int, string>> NamesAsync(IEnumerable<int> ids)
        {
            var distinct = ids.Distinct().ToList();
            return await _app.Users.Where(u => distinct.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name);
        }

        private static string? ValidateTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "A title is required.";
            if (title.Contains('\r') || title.Contains('\n')) return "The title must be a single line.";
            if (title.Trim().Length > 200) return "The title is too long (max 200 characters).";
            return null;
        }

        private static string SafeFileName(string title)
        {
            var clean = string.Concat(title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
            return clean.Length == 0 ? "sheet" : clean;
        }

        private static string SheetLink(int id) => $"/workspace/sheets/{id}";
        private static WsResult<T> NotFound<T>() => WsResult<T>.Fail(404, "Sheet not found.");
        private static WsResult<T> NoAccess<T>() => WsResult<T>.Fail(403, "You do not have access to this sheet.");
    }
}
