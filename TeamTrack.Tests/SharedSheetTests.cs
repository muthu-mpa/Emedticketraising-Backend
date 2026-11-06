using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using TeamTrack.Workspace.Dtos;
using TeamTrack.Workspace.Models;
using TeamTrack.Workspace.Services;
using Xunit;
using static TeamTrack.Tests.Check;
using static TeamTrack.Tests.XlsxFixtures;

namespace TeamTrack.Tests
{
    public class SharedSheetTests
    {
        private static SheetCellChangeInput Cell(int row, int col, object? value, string sheet = "Sheet1")
            => new() { Sheet = sheet, Row = row, Col = col, Value = value };

        private static SheetPatchInput Patch(int baseVersion, params SheetCellChangeInput[] changes)
            => new() { BaseVersion = baseVersion, Changes = [.. changes] };

        private static async Task<SheetDetailDto> BlankAsync(Kit k, CurrentUser? owner = null, string title = "Pipeline")
            => Ok(await k.Sheets.CreateAsync(owner ?? k.Asha, title, null));

        /// <summary>Asha's blank sheet, shared with Ben (can edit) and Meena (view only).</summary>
        private static async Task<SheetDetailDto> SharedAsync(Kit k)
        {
            var sheet = await BlankAsync(k);
            Ok(await k.Sheets.SetAccessAsync(k.Asha, sheet.Id, [new() { UserId = k.Ben.Id, CanEdit = true }, new() { UserId = k.MeenaAsEmployee.Id, CanEdit = false }]));
            return sheet;
        }

        private static byte[] ExampleWorkbook()
        {
            using var wb = new XLWorkbook();
            var a = wb.AddWorksheet("Leads");
            a.Cell(1, 1).Value = "Name"; a.Cell(1, 2).Value = "Qty"; a.Cell(1, 3).Value = "Total";
            a.Cell(2, 1).Value = "Acme"; a.Cell(2, 2).Value = 4.5; a.Cell(2, 3).FormulaA1 = "B2*2";
            a.Cell(2, 4).Value = true; a.Cell(2, 5).Value = new DateTime(2026, 11, 4);
            a.Cell(1, 1).Style.Fill.BackgroundColor = XLColor.Yellow;
            a.Range("A5:B6").Merge();
            wb.AddWorksheet("Notes").Cell(3, 2).Value = "second tab";
            return Bytes(wb);
        }

        // ---------- create / read ----------

        [Fact]
        public async Task A_blank_sheet_starts_at_version_1_and_is_owned_by_its_creator()
        {
            var k = new Kit();
            var sheet = await BlankAsync(k);
            Assert.Equal((1, "Asha", true, true, false), (sheet.Version, sheet.OwnerName, sheet.IsOwner, sheet.CanEdit, sheet.FormattingLost));
            Assert.Equal("Sheet1", Assert.Single(sheet.Data.Sheets).Name);
            Assert.Empty(k.SheetFiles.Stored);

            Fails(await k.Sheets.CreateAsync(k.Asha, " ", null), 400, "title");
            Fails(await k.Sheets.CreateAsync(k.Asha, new string('x', 201), null), 400, "too long");
        }

        [Fact]
        public async Task An_uploaded_workbook_is_parsed_into_the_json_model_and_the_original_is_kept()
        {
            var k = new Kit();
            var sheet = Ok(await k.Sheets.CreateAsync(k.Asha, null, File(ExampleWorkbook(), "leads.xlsx")));

            Assert.Equal("leads", sheet.Title); // defaults to the file name
            Assert.True(sheet.FormattingLost); // colours and merged cells were dropped: the UI should warn
            Assert.Equal(["Leads", "Notes"], sheet.Data.Sheets.Select(s => s.Name));
            var leads = sheet.Data.Sheets[0];
            Assert.Equal("Acme", leads.Get(2, 1)!.Value);
            Assert.Equal(4.5, leads.Get(2, 2)!.Value);
            Assert.Equal("=B2*2", leads.Get(2, 3)!.Formula);
            Assert.Equal(9.0, leads.Get(2, 3)!.Value); // the formula's value is kept next to it
            Assert.Equal(true, leads.Get(2, 4)!.Value);
            Assert.Equal(("2026-11-04", "d"), (leads.Get(2, 5)!.Value, leads.Get(2, 5)!.Type));
            Assert.Null(leads.Get(5, 1)); // merged cells are not carried over
            Assert.Equal("second tab", sheet.Data.Sheets[1].Get(3, 2)!.Value);

            var stored = Assert.Single(k.SheetFiles.Stored);
            Assert.StartsWith("/uploads/sheets/", stored.Key);
            Assert.Equal((await k.Ws.SharedSheets.SingleAsync()).OriginalFilePath, stored.Key);
        }

        [Fact]
        public async Task Uploads_are_checked_for_type_content_and_size()
        {
            var k = new Kit();
            Fails(await k.Sheets.CreateAsync(k.Asha, "x", File([1, 2, 3], "x.xls")), 400, ".xlsx");
            Fails(await k.Sheets.CreateAsync(k.Asha, "x", File("plain text"u8.ToArray(), "x.xlsx")), 400, "does not look like");
            Fails(await k.Sheets.CreateAsync(k.Asha, "x", File(new byte[10 * 1024 * 1024 + 1], "big.xlsx")), 400, "too large");
            Assert.Empty(k.SheetFiles.Stored);
            Assert.Equal(0, await k.Ws.SharedSheets.CountAsync());
        }

        [Fact]
        public async Task The_list_shows_only_my_sheets_and_the_ones_shared_with_me_without_loading_their_data()
        {
            var k = new Kit();
            var shared = await SharedAsync(k);
            var bens = await BlankAsync(k, k.Ben, "Ben's own");
            var lonely = await BlankAsync(k, k.Asha, "Asha only");

            Assert.Equal(["Asha only", "Pipeline"], Ok(await k.Sheets.ListAsync(k.Asha)).Select(s => s.Title).Order());
            var forBen = Ok(await k.Sheets.ListAsync(k.Ben));
            Assert.Equal(["Ben's own", "Pipeline"], forBen.Select(s => s.Title).Order());
            Assert.True(forBen.Single(s => s.Id == shared.Id).CanEdit);
            Assert.False(forBen.Single(s => s.Id == shared.Id).IsOwner);
            Assert.False(Ok(await k.Sheets.ListAsync(k.MeenaAsEmployee)).Single().CanEdit); // view only
            Assert.Empty(Ok(await k.Sheets.ListAsync(k.Priya))); // a PM can open any sheet by id but it is not listed unless mine or shared
            Assert.DoesNotContain(Ok(await k.Sheets.ListAsync(k.Asha)), s => s.Id == bens.Id);
            Assert.Contains(Ok(await k.Sheets.ListAsync(k.Asha)), s => s.Id == lonely.Id);
        }

        // ---------- access rules ----------

        [Fact]
        public async Task Strangers_cannot_see_or_touch_a_sheet()
        {
            var k = new Kit();
            var sheet = await BlankAsync(k);
            var stranger = k.Ben;

            Fails(await k.Sheets.GetAsync(stranger, sheet.Id), 403);
            Fails(await k.Sheets.VersionAsync(stranger, sheet.Id), 403);
            Fails(await k.Sheets.PatchAsync(stranger, sheet.Id, Patch(1, Cell(1, 1, "x"))), 403);
            Fails(await k.Sheets.DownloadAsync(stranger, sheet.Id), 403);
            Fails(await k.Sheets.HistoryAsync(stranger, sheet.Id), 403);
            Fails(await k.Sheets.ImportPreviewAsync(stranger, sheet.Id), 403);
            Fails(await k.Sheets.SetAccessAsync(stranger, sheet.Id, []), 403);
            Fails(await k.Sheets.DeleteAsync(stranger, sheet.Id), 403);
            Fails(await k.Sheets.GetAsync(stranger, 999), 404);
        }

        [Fact]
        public async Task Shared_users_view_and_only_can_edit_users_save()
        {
            var k = new Kit();
            var sheet = await SharedAsync(k); // Ben can edit, Meena can only view

            var asViewer = Ok(await k.Sheets.GetAsync(k.MeenaAsEmployee, sheet.Id));
            Assert.False(asViewer.CanEdit);
            Assert.Null(asViewer.SharedWith); // only the owner sees who it is shared with
            Fails(await k.Sheets.PatchAsync(k.MeenaAsEmployee, sheet.Id, Patch(1, Cell(1, 1, "x"))), 403, "not edit");

            Ok(await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(1, Cell(1, 1, "from Ben"))));
            Assert.Equal("from Ben", Ok(await k.Sheets.GetAsync(k.Asha, sheet.Id)).Data.Sheets[0].Get(1, 1)!.Value);
            Assert.Equal(2, Ok(await k.Sheets.GetAsync(k.Asha, sheet.Id)).SharedWith!.Count);

            Fails(await k.Sheets.SetAccessAsync(k.Ben, sheet.Id, []), 403, "owner"); // an editor cannot change sharing
            Fails(await k.Sheets.DeleteAsync(k.Ben, sheet.Id), 403, "owner");
        }

        [Fact]
        public async Task A_product_manager_views_any_sheet_but_edits_only_when_shared_with_can_edit()
        {
            var k = new Kit();
            var sheet = await BlankAsync(k);

            Assert.False(Ok(await k.Sheets.GetAsync(k.Priya, sheet.Id)).CanEdit);
            Ok(await k.Sheets.VersionAsync(k.Priya, sheet.Id));
            Ok(await k.Sheets.DownloadAsync(k.Priya, sheet.Id));
            Ok(await k.Sheets.HistoryAsync(k.Priya, sheet.Id));
            Fails(await k.Sheets.PatchAsync(k.Priya, sheet.Id, Patch(1, Cell(1, 1, "x"))), 403, "not edit");

            Ok(await k.Sheets.SetAccessAsync(k.Asha, sheet.Id, [new() { UserId = k.Priya.Id, CanEdit = false }]));
            Fails(await k.Sheets.PatchAsync(k.Priya, sheet.Id, Patch(1, Cell(1, 1, "x"))), 403); // shared as viewer: still no edit

            Ok(await k.Sheets.SetAccessAsync(k.Asha, sheet.Id, [new() { UserId = k.Priya.Id, CanEdit = true }]));
            Ok(await k.Sheets.PatchAsync(k.Priya, sheet.Id, Patch(1, Cell(1, 1, "from the PM"))));

            // the same person acting as an Employee has no PM view access
            Fails(await k.Sheets.GetAsync(k.MeenaAsEmployee, sheet.Id), 403);
            Ok(await k.Sheets.GetAsync(k.MeenaAsPm, sheet.Id));
            Fails(await k.Sheets.DeleteAsync(k.Priya, sheet.Id), 403);
        }

        [Fact]
        public async Task Setting_access_replaces_the_list_and_validates_the_people()
        {
            var k = new Kit();
            var sheet = await SharedAsync(k);

            var updated = Ok(await k.Sheets.SetAccessAsync(k.Asha, sheet.Id, [new() { UserId = k.Ben.Id, CanEdit = false }]));
            Assert.Equal((k.Ben.Id, false), (Assert.Single(updated).UserId, updated[0].CanEdit)); // Meena removed, Ben downgraded
            Fails(await k.Sheets.GetAsync(k.MeenaAsEmployee, sheet.Id), 403);
            Fails(await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(1, Cell(1, 1, "x"))), 403);

            Fails(await k.Sheets.SetAccessAsync(k.Asha, sheet.Id, [new() { UserId = 999 }]), 400, "999");
            Fails(await k.Sheets.SetAccessAsync(k.Asha, sheet.Id, [new() { UserId = k.Asha.Id }]), 400, "owner");
            Fails(await k.Sheets.SetAccessAsync(k.Asha, sheet.Id, null), 400);
            (await k.App.Users.FindAsync(k.Ben.Id))!.IsActive = false;
            await k.App.SaveChangesAsync();
            Fails(await k.Sheets.SetAccessAsync(k.Asha, sheet.Id, [new() { UserId = k.Ben.Id }]), 400);

            Assert.Empty(Ok(await k.Sheets.SetAccessAsync(k.Asha, sheet.Id, [])));
            Assert.Equal(0, await k.Ws.SharedSheetAccess.CountAsync());
        }

        // ---------- saving cells ----------

        [Fact]
        public async Task A_save_applies_every_cell_bumps_the_version_and_is_logged()
        {
            var k = new Kit();
            var sheet = await SharedAsync(k);

            var saved = Ok(await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(1, Cell(1, 1, "Name"), Cell(1, 2, 12.5), Cell(2, 1, true), Cell(3, 3, "=A1&\"!\""))));
            Assert.Equal((2, 4), (saved.Version, saved.AppliedCells));

            var read = Ok(await k.Sheets.GetAsync(k.Asha, sheet.Id));
            var tab = read.Data.Sheets[0];
            Assert.Equal((2, "Ben"), (read.Version, read.UpdatedByName));
            Assert.Equal(("Name", 12.5, true), (tab.Get(1, 1)!.Value, tab.Get(1, 2)!.Value, tab.Get(2, 1)!.Value));
            Assert.Equal("=A1&\"!\"", tab.Get(3, 3)!.Formula); // "=" makes it a formula

            var history = Ok(await k.Sheets.HistoryAsync(k.Asha, sheet.Id));
            Assert.Equal((2, "Ben", 4), (Assert.Single(history).Version, history[0].UserName, history[0].CellCount));

            // clearing: empty string or null removes the cell
            Ok(await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(2, Cell(1, 1, ""), Cell(1, 2, null))));
            var cleared = Ok(await k.Sheets.GetAsync(k.Asha, sheet.Id)).Data.Sheets[0];
            Assert.Null(cleared.Get(1, 1));
            Assert.Null(cleared.Get(1, 2));
            Assert.Equal([3, 2], Ok(await k.Sheets.HistoryAsync(k.Asha, sheet.Id)).Select(h => h.Version)); // newest first
        }

        [Fact]
        public async Task A_changed_cell_that_someone_else_saved_after_my_base_version_gives_409_and_nothing_is_applied()
        {
            var k = new Kit();
            var sheet = await SharedAsync(k);
            Ok(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(1, Cell(1, 1, "start"), Cell(2, 2, "keep"))));  // v2
            Ok(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(2, Cell(1, 1, "Asha's edit"))));                 // v3: Asha changes A1

            // Ben still has version 2 on screen and edits A1 (clash) and C3 (free)
            var result = await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(2, Cell(1, 1, "Ben's edit"), Cell(3, 3, "Ben's other cell")));
            Fails(result, 409, "nothing was saved");
            var conflict = Assert.Single(result.Data!.Conflicts);
            Assert.Equal(("Sheet1", 1, 1, "Asha's edit", 3, "Asha"), (conflict.Sheet, conflict.Row, conflict.Col, conflict.CurrentValue, conflict.ChangedInVersion, conflict.ChangedByName));
            Assert.Equal(3, result.Data.Version); // so the client can reload and retry

            var after = Ok(await k.Sheets.GetAsync(k.Asha, sheet.Id));
            Assert.Equal(3, after.Version); // not bumped
            Assert.Equal("Asha's edit", after.Data.Sheets[0].Get(1, 1)!.Value);
            Assert.Null(after.Data.Sheets[0].Get(3, 3)); // all or nothing: the free cell was not applied either
            Assert.Equal(2, await k.Ws.SharedSheetChanges.CountAsync()); // and nothing was logged

            // after reloading (version 3) the same edit goes through
            Ok(await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(3, Cell(1, 1, "Ben's edit"), Cell(3, 3, "Ben's other cell"))));
        }

        [Fact]
        public async Task An_old_base_version_is_fine_when_the_cells_are_not_the_ones_others_changed()
        {
            var k = new Kit();
            var sheet = await SharedAsync(k);
            Ok(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(1, Cell(1, 1, "Asha's"))));                           // v2
            var merged = Ok(await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(1, Cell(5, 5, "Ben's far-away cell")))); // base v1, different cell
            Assert.Equal(3, merged.Version);
            var data = Ok(await k.Sheets.GetAsync(k.Asha, sheet.Id)).Data.Sheets[0];
            Assert.Equal(("Asha's", "Ben's far-away cell"), (data.Get(1, 1)!.Value, data.Get(5, 5)!.Value)); // both edits survive
        }

        [Fact]
        public async Task It_is_not_a_conflict_when_the_other_person_saved_the_same_value_or_when_the_earlier_change_was_my_own()
        {
            var k = new Kit();
            var sheet = await SharedAsync(k);
            Ok(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(1, Cell(1, 1, "same"))));        // v2 by Asha
            Ok(await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(1, Cell(1, 1, "same"))));         // v1 base, but the cell already says "same"
            Ok(await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(2, Cell(2, 2, "Ben v3"))));       // v3 by Ben
            Ok(await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(2, Cell(2, 2, "Ben again"))));    // stale base, but the earlier change was Ben's own
            Assert.Equal(4, Ok(await k.Sheets.VersionAsync(k.Ben, sheet.Id)).Version);
        }

        [Fact]
        public async Task Saving_what_is_already_there_changes_nothing_and_does_not_bump_the_version()
        {
            var k = new Kit();
            var sheet = await BlankAsync(k);
            Ok(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(1, Cell(1, 1, "x"))));
            var again = Ok(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(2, Cell(1, 1, "x"), Cell(9, 9, ""))));
            Assert.Equal((2, 0), (again.Version, again.AppliedCells));
            Assert.Single(Ok(await k.Sheets.HistoryAsync(k.Asha, sheet.Id)));
        }

        [Fact]
        public async Task The_version_column_is_an_optimistic_concurrency_token()
        {
            // two saves racing from the same version: the second UPDATE ... WHERE version = old matches no row
            var k = new Kit();
            var sheet = await BlankAsync(k);
            Assert.True(k.Ws.Model.FindEntityType(typeof(WsSharedSheet))!.FindProperty(nameof(WsSharedSheet.Version))!.IsConcurrencyToken);

            await using var first = k.NewWorkspaceContext();
            await using var second = k.NewWorkspaceContext();
            var a = await first.SharedSheets.SingleAsync(s => s.Id == sheet.Id);
            var b = await second.SharedSheets.SingleAsync(s => s.Id == sheet.Id);
            a.Version++; await first.SaveChangesAsync();
            b.Version++;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        }

        [Fact]
        public async Task Bad_saves_are_rejected_without_changing_anything()
        {
            var k = new Kit();
            var sheet = await BlankAsync(k);

            Fails(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(1)), 400, "at least one");
            Fails(await k.Sheets.PatchAsync(k.Asha, sheet.Id, new SheetPatchInput { BaseVersion = 1, Changes = null }), 400);
            Fails(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(0, Cell(1, 1, "x"))), 400, "baseVersion");
            Fails(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(7, Cell(1, 1, "x"))), 400, "newer");
            Fails(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(1, Cell(0, 1, "x"))), 400, "Row");
            Fails(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(1, Cell(1, 16385, "x"))), 400, "Column");
            Fails(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(1, Cell(1, 1, "x", sheet: "Nope"))), 400, "Nope");
            Fails(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(1, Cell(1, 1, new string('x', 32768)))), 400, "32767");
            Fails(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(1, Cell(1, 1, "x"), Cell(1, 2, "y", sheet: " "))), 400, "sheet name");
            Fails(await k.Sheets.PatchAsync(k.Asha, 999, Patch(1, Cell(1, 1, "x"))), 404);
            Assert.Equal(1, Ok(await k.Sheets.VersionAsync(k.Asha, sheet.Id)).Version);
        }

        [Fact]
        public async Task The_version_endpoint_is_tiny_and_tracks_the_last_saver()
        {
            var k = new Kit();
            var sheet = await SharedAsync(k);
            var before = Ok(await k.Sheets.VersionAsync(k.MeenaAsEmployee, sheet.Id));
            Assert.Equal((1, "Asha"), (before.Version, before.UpdatedByName));
            Ok(await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(1, Cell(1, 1, "x"))));
            var after = Ok(await k.Sheets.VersionAsync(k.MeenaAsEmployee, sheet.Id));
            Assert.Equal((2, k.Ben.Id, "Ben"), (after.Version, after.UpdatedBy, after.UpdatedByName));
            Assert.True(after.UpdatedAt >= before.UpdatedAt);
        }

        // ---------- notifications ----------

        [Fact]
        public async Task Sharing_notifies_the_new_person_and_only_them()
        {
            var k = new Kit();
            var sheet = await BlankAsync(k);
            Ok(await k.Sheets.SetAccessAsync(k.Asha, sheet.Id, [new() { UserId = k.Ben.Id, CanEdit = true }]));
            Assert.Equal(["sheet_shared"], await k.NotificationMessagesAsync(k.Ben.Id));
            Assert.Equal($"/workspace/sheets/{sheet.Id}", (await k.Ws.Notifications.SingleAsync(n => n.UserId == k.Ben.Id)).Link);

            // saving the list again with Ben unchanged and Meena added tells only Meena
            Ok(await k.Sheets.SetAccessAsync(k.Asha, sheet.Id, [new() { UserId = k.Ben.Id, CanEdit = false }, new() { UserId = k.MeenaAsEmployee.Id, CanEdit = false }]));
            Assert.Single(await k.NotificationMessagesAsync(k.Ben.Id));
            Assert.Equal(["sheet_shared"], await k.NotificationMessagesAsync(k.MeenaAsEmployee.Id));
            Assert.Empty(await k.NotificationMessagesAsync(k.Asha.Id));
        }

        [Fact]
        public async Task Saving_notifies_the_others_with_access_at_most_once_per_sheet_every_10_minutes()
        {
            var k = new Kit();
            var sheet = await SharedAsync(k); // Asha owns; Ben edits; Meena views
            await k.Ws.Notifications.ExecuteDeleteAsync(); // forget the "shared with you" ones

            Ok(await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(1, Cell(1, 1, "a"))));
            Assert.Equal(["sheet_saved"], await k.NotificationMessagesAsync(k.Asha.Id));
            Assert.Equal(["sheet_saved"], await k.NotificationMessagesAsync(k.MeenaAsEmployee.Id));
            Assert.Empty(await k.NotificationMessagesAsync(k.Ben.Id)); // not the person who saved

            Ok(await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(2, Cell(1, 1, "b"))));
            Ok(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(3, Cell(1, 1, "c"))));
            Assert.Single(await k.NotificationMessagesAsync(k.Asha.Id)); // Asha's own save does not notify her; Ben's second save was inside the window
            Assert.Single(await k.NotificationMessagesAsync(k.MeenaAsEmployee.Id));
            Assert.Single(await k.NotificationMessagesAsync(k.Ben.Id)); // Ben had none yet, so Asha's save tells him once

            // 11 minutes later the next save notifies again
            foreach (var n in await k.Ws.Notifications.ToListAsync()) n.CreatedAt = DateTime.UtcNow.AddMinutes(-11);
            await k.Ws.SaveChangesAsync();
            Ok(await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(4, Cell(1, 1, "d"))));
            Assert.Equal(2, (await k.NotificationMessagesAsync(k.Asha.Id)).Count);
            Assert.Equal(2, (await k.NotificationMessagesAsync(k.MeenaAsEmployee.Id)).Count);
        }

        [Fact]
        public async Task A_save_that_conflicts_or_changes_nothing_sends_no_notification()
        {
            var k = new Kit();
            var sheet = await SharedAsync(k);
            await k.Ws.Notifications.ExecuteDeleteAsync();
            Ok(await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(1, Cell(1, 1, "x"))));
            await k.Ws.Notifications.ExecuteDeleteAsync();

            Fails(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(1, Cell(1, 1, "mine"))), 409);
            Ok(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(2, Cell(1, 1, "x"))));
            Assert.Equal(0, await k.Ws.Notifications.CountAsync());
        }

        // ---------- export ----------

        [Fact]
        public async Task Download_exports_the_current_values_and_formulas_and_it_reads_back_the_same()
        {
            var k = new Kit();
            var sheet = await BlankAsync(k, title: "Q4 plan / draft");
            Ok(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(1,
                Cell(1, 1, "Name"), Cell(1, 2, 12.5), Cell(2, 1, true), Cell(2, 2, "=B1*2"), Cell(3, 1, "multi\nline"))));

            var file = Ok(await k.Sheets.DownloadAsync(k.Asha, sheet.Id));
            Assert.Equal("Q4 plan _ draft.xlsx", file.FileName);

            var back = XlsxConverter.Read(file.Bytes).Sheets[0];
            Assert.Equal(("Name", 12.5, true), (back.Get(1, 1)!.Value, back.Get(1, 2)!.Value, back.Get(2, 1)!.Value));
            Assert.Equal("=B1*2", back.Get(2, 2)!.Formula);
            Assert.Equal(25.0, back.Get(2, 2)!.Value); // Excel/ClosedXML can compute it once the file is read
            Assert.Equal("multi\nline", back.Get(3, 1)!.Value);
        }

        [Fact]
        public async Task An_uploaded_workbook_survives_upload_edit_and_download()
        {
            var k = new Kit();
            var sheet = Ok(await k.Sheets.CreateAsync(k.Asha, "Leads", File(ExampleWorkbook())));
            Ok(await k.Sheets.PatchAsync(k.Asha, sheet.Id, Patch(1, Cell(3, 1, "Beta", "Leads"), Cell(4, 2, "note", "Notes"))));

            var back = XlsxConverter.Read(Ok(await k.Sheets.DownloadAsync(k.Asha, sheet.Id)).Bytes);
            Assert.Equal(["Leads", "Notes"], back.Sheets.Select(s => s.Name));
            var leads = back.Sheets[0];
            Assert.Equal(("Acme", 4.5, "Beta"), (leads.Get(2, 1)!.Value, leads.Get(2, 2)!.Value, leads.Get(3, 1)!.Value));
            Assert.Equal("=B2*2", leads.Get(2, 3)!.Formula);
            Assert.Equal(("2026-11-04", "d"), (leads.Get(2, 5)!.Value, leads.Get(2, 5)!.Type)); // dates stay dates
            Assert.Equal("note", back.Sheets[1].Get(4, 2)!.Value);
        }

        [Fact]
        public void The_json_model_round_trips_and_unusual_sheet_names_are_made_legal_for_excel()
        {
            var book = new SheetBook { Sheets = [new SheetTab { Name = "A/B:C?" }, new SheetTab { Name = "a/b:c?" }, new SheetTab { Name = "" }, new SheetTab { Name = new string('n', 40) }] };
            book.Sheets[0].Set(1, 1, new SheetCell { Value = "x" });
            book.Sheets[0].Set(2, 3, new SheetCell { Formula = "=A1" });

            Assert.DoesNotContain("maxRow", book.ToJson()); // helper properties are not part of the stored/served model
            Assert.DoesNotContain("cellCount", book.ToJson());
            var again = SheetBook.FromJson(book.ToJson());
            Assert.Equal("x", again.Sheets[0].Get(1, 1)!.Value);
            Assert.Equal("=A1", again.Sheets[0].Get(2, 3)!.Formula);
            Assert.Equal(2, again.CellCount);

            var names = XlsxConverter.Read(XlsxConverter.Write(book)).Sheets.Select(s => s.Name).ToList();
            Assert.Equal(4, names.Count);
            Assert.Equal(4, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(names, n => Assert.True(n.Length is > 0 and <= 31 && n.IndexOfAny("[]:*?/\\".ToCharArray()) < 0));
            Assert.Contains("A_B_C_", names);
        }

        // ---------- history, delete ----------

        [Fact]
        public async Task Deleting_removes_the_sheet_its_access_its_history_and_the_stored_file()
        {
            var k = new Kit();
            var sheet = Ok(await k.Sheets.CreateAsync(k.Asha, "Leads", File(ExampleWorkbook())));
            Ok(await k.Sheets.SetAccessAsync(k.Asha, sheet.Id, [new() { UserId = k.Ben.Id, CanEdit = true }]));
            Ok(await k.Sheets.PatchAsync(k.Ben, sheet.Id, Patch(1, Cell(1, 1, "x", "Leads"))));
            var stored = Assert.Single(k.SheetFiles.Stored).Key;

            Ok(await k.Sheets.DeleteAsync(k.Asha, sheet.Id));
            Assert.Equal((0, 0, 0), (await k.Ws.SharedSheets.CountAsync(), await k.Ws.SharedSheetAccess.CountAsync(), await k.Ws.SharedSheetChanges.CountAsync()));
            Assert.Equal([stored], k.SheetFiles.Deleted);
            Fails(await k.Sheets.GetAsync(k.Asha, sheet.Id), 404);
            Fails(await k.Sheets.DeleteAsync(k.Asha, sheet.Id), 404);
        }

        // ---------- bonus: shared sheet -> campaign drafts ----------

        [Fact]
        public async Task A_shared_sheet_can_be_previewed_and_imported_as_campaign_drafts()
        {
            var k = new Kit();
            var sheet = await BlankAsync(k, title: "Outreach list");
            Ok(await k.Sheets.SetAccessAsync(k.Asha, sheet.Id, [new() { UserId = k.Ben.Id, CanEdit = true }]));
            string[] headers = ["Campaign name", "Client", "Contact person", "Client email", "Subject", "Message"];
            var changes = headers.Select((h, i) => Cell(1, i + 1, h)).ToList();
            changes.AddRange(new[] { "Intro", "Apollo", "Dr. Rao", "rao@apollo.example", "Hello", "Hi doctor" }.Select((v, i) => Cell(2, i + 1, v)));
            changes.AddRange(new[] { "Second", "Apollo", "Dr. Rao", "", "Again", "Hi [First name]" }.Select((v, i) => Cell(3, i + 1, v)));
            Ok(await k.Sheets.PatchAsync(k.Ben, sheet.Id, new SheetPatchInput { BaseVersion = 1, Changes = changes }));

            var preview = Ok(await k.Sheets.ImportPreviewAsync(k.Ben, sheet.Id)); // a collaborator (Ben) can import it as his own drafts
            Assert.Equal("simple_template", preview.Format);
            Assert.Equal(["ok", "error"], preview.Rows.Select(r => r.Severity));
            Assert.Contains("Outreach list", preview.FileName);
            Assert.Equal(0, await k.Ws.Campaigns.CountAsync()); // still saves nothing

            var committed = Ok(await k.Imports.CommitAsync(k.Ben, preview.PreviewToken, null, [2, 3]));
            Assert.Equal(2, committed.ImportedRows);
            Assert.All(await k.Ws.Campaigns.ToListAsync(), c => Assert.Equal(k.Ben.Id, c.CreatedByUserId));

            Fails(await k.Sheets.ImportPreviewAsync(k.Priya, sheet.Id), 403); // PMs view but do not create campaigns
            var blank = await BlankAsync(k, title: "Empty");
            Fails(await k.Sheets.ImportPreviewAsync(k.Asha, blank.Id), 400, "supported layout");
        }
    }
}
