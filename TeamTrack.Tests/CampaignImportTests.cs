using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using TeamTrack.Workspace.Dtos;
using TeamTrack.Workspace.Services;
using Xunit;
using static TeamTrack.Tests.Check;
using static TeamTrack.Tests.XlsxFixtures;

namespace TeamTrack.Tests
{
    public class CampaignImportTests
    {
        private static async Task<ImportPreviewDto> PreviewAsync(Kit k, byte[] bytes, string name = "import.xlsx")
            => Ok(await k.Imports.PreviewAsync(k.Asha, File(bytes, name)));

        private static ImportFlagDto? Flag(ImportRowDto row, string code) => row.Flags.FirstOrDefault(f => f.Code == code);

        // ---------- format A: prospect workbook ----------

        [Fact]
        public async Task Prospect_workbook_is_mapped_and_joined_by_the_hash_column()
        {
            var k = new Kit();
            var preview = await PreviewAsync(k, ProspectWorkbook(), "prospects.xlsx");

            Assert.Equal("prospect_workbook", preview.Format);
            Assert.Equal("prospects.xlsx", preview.FileName);
            Assert.Equal(4, preview.TotalRows);
            Assert.Equal([2, 3, 4, 5], preview.Rows.Select(r => r.RowNumber)); // Excel row numbers of the drafts sheet

            var acme = preview.Rows[0];
            Assert.Equal("Acme Imaging", acme.ClientName);
            Assert.Equal("Acme Imaging", acme.Title);
            Assert.Equal("Dale Flowers", acme.ContactName); // text before the first comma
            Assert.Equal("dale@acme.example", acme.ClientEmail); // from the prospect sheet, joined by "#" although its rows are in another order
            Assert.Equal("Faster coding for Acme", acme.Subject);
            Assert.StartsWith("Hi Dale,\nWe help with coding.", acme.Body);
            Assert.Equal(("Radiology", "A", "Wave 1 - Wed"), (acme.Segment, acme.Priority, acme.SendWave));

            Assert.Equal("info@delta.example", preview.Rows[3].ClientEmail);
            Assert.Equal(("Urgent care", "B", "Wave 2 - Thu"), (preview.Rows[3].Segment, preview.Rows[3].Priority, preview.Rows[3].SendWave));
        }

        [Fact]
        public async Task Prospect_rows_get_error_and_warning_flags()
        {
            var k = new Kit();
            var preview = await PreviewAsync(k, ProspectWorkbook());
            var (acme, beta, gamma, delta) = (preview.Rows[0], preview.Rows[1], preview.Rows[2], preview.Rows[3]);

            // placeholders left in the text -> ERROR (the email itself is fine)
            Assert.Equal("error", acme.Severity);
            Assert.Contains("[Your name]", Flag(acme, "placeholder")!.Message);
            Assert.Contains("[Title]", Flag(acme, "placeholder")!.Message);
            Assert.Null(Flag(acme, "email_missing"));

            // an email cell that is not an email -> ERROR, left empty; "autonomous" -> WARNING
            Assert.Equal("", beta.ClientEmail);
            Assert.Equal("error", Flag(beta, "email_invalid")!.Level);
            Assert.Equal("warning", Flag(beta, "autonom")!.Level);
            Assert.Equal("error", beta.Severity); // the highest level wins

            // an empty email cell -> ERROR
            Assert.Equal("email_missing", Flag(gamma, "email_missing")!.Code);
            Assert.Equal("error", gamma.Severity);

            Assert.Equal("ok", delta.Severity);
            Assert.Empty(delta.Flags);
            Assert.Equal((1, 0, 3), (preview.OkRows, preview.WarningRows, preview.ErrorRows));
        }

        [Fact]
        public async Task Formula_columns_are_ignored_and_formula_cells_that_are_used_give_their_values()
        {
            var k = new Kit();
            var bytes = ProspectWorkbook(editProspects: p => p.Cell(4, 5).FormulaA1 = "\"Urgent\"&\" care\""); // Segment of Delta is a formula
            var preview = await PreviewAsync(k, bytes);
            Assert.Equal("Urgent care", preview.Rows[3].Segment);
            Assert.DoesNotContain(preview.Rows, r => r.Body.Contains("CHECK")); // the "Language check" formula column is not part of any field
        }

        [Fact]
        public async Task A_duplicate_of_an_existing_campaign_or_of_an_earlier_row_is_a_warning()
        {
            var k = new Kit();
            Ok(await k.Campaigns.CreateAsync(k.Ben, new CampaignInput
            { Title = "Earlier", ClientName = "Delta", ContactName = "Ann", ClientEmail = "INFO@delta.example", Subject = "coding for delta ", Body = "x" }));

            var preview = await PreviewAsync(k, ProspectWorkbook());
            Assert.Equal("warning", Flag(preview.Rows[3], "duplicate")!.Level); // same email + subject (ignoring case) as Ben's campaign
            Assert.Equal("warning", preview.Rows[3].Severity); // nothing else is wrong with Delta, so a duplicate alone is a warning
            Assert.Null(Flag(preview.Rows[0], "duplicate"));

            var twice = await PreviewAsync(k, SimpleTemplate(null,
                ["A", "Org", "Pat", "pat@org.example", "Hello", "Body one"],
                ["B", "Org", "Pat", "pat@org.example", "Hello", "Body two"]));
            Assert.Null(Flag(twice.Rows[0], "duplicate"));
            Assert.NotNull(Flag(twice.Rows[1], "duplicate"));
        }

        [Fact]
        public async Task The_drafts_sheet_is_never_mistaken_for_the_prospect_list_and_a_missing_prospect_sheet_just_means_no_emails()
        {
            // "Email body" starts with "Email" too; with only a drafts sheet there is no prospect list
            var bytes = ProspectWorkbook();
            using var wb = new XLWorkbook(new MemoryStream(bytes));
            wb.Worksheet("Week 1 (4)").Delete();
            var preview = await PreviewAsync(new Kit(), Bytes(wb));
            Assert.Equal(4, preview.Rows.Count);
            Assert.All(preview.Rows, r => { Assert.Equal("", r.ClientEmail); Assert.NotNull(Flag(r, "email_missing")); });
        }

        // ---------- format B: simple template ----------

        [Fact]
        public async Task Simple_template_is_mapped_and_headers_match_case_insensitively_by_starts_with()
        {
            var k = new Kit();
            var bytes = SimpleTemplate(
                ["CAMPAIGN NAME (required)", "client", "Contact Person", "Client Email address", "subject line", "Message body"],
                ["Intro", "Apollo", "Dr. Rao", "rao@apollo.example", "Hello", "Hi Dr. Rao"],
                [null, null, null, null, null, null],  // blank rows are skipped
                ["Follow-up", "Apollo", "Dr. Rao", "bad email", "Again", "[First name], hi"]);
            var preview = await PreviewAsync(k, bytes);

            Assert.Equal("simple_template", preview.Format);
            Assert.Equal(2, preview.TotalRows);
            var first = preview.Rows[0];
            Assert.Equal(("Intro", "Apollo", "Dr. Rao", "rao@apollo.example", "Hello", "Hi Dr. Rao"),
                (first.Title, first.ClientName, first.ContactName, first.ClientEmail, first.Subject, first.Body));
            Assert.Equal("ok", first.Severity);
            Assert.Equal(["email_invalid", "placeholder"], preview.Rows[1].Flags.Select(f => f.Code));
            Assert.Equal(4, preview.Rows[1].RowNumber);
        }

        [Fact]
        public async Task The_downloadable_template_is_importable_once_filled()
        {
            var k = new Kit();
            var template = k.Imports.Template();

            var empty = await k.Imports.PreviewAsync(k.Asha, File(template));
            Fails(empty, 400, "no campaign rows");

            using var wb = new XLWorkbook(new MemoryStream(template));
            var ws = wb.Worksheet("Campaigns");
            Assert.Equal(["Campaign name", "Client", "Contact person", "Client email", "Subject", "Message"],
                Enumerable.Range(1, 6).Select(c => ws.Cell(1, c).GetString()));
            ws.Cell(2, 1).Value = "Intro"; ws.Cell(2, 2).Value = "Apollo"; ws.Cell(2, 3).Value = "Dr. Rao";
            ws.Cell(2, 4).Value = "rao@apollo.example"; ws.Cell(2, 5).Value = "Hello"; ws.Cell(2, 6).Value = "Hi";
            var filled = await PreviewAsync(k, Bytes(wb));
            Assert.Equal("ok", Assert.Single(filled.Rows).Severity);
        }

        // ---------- file rules ----------

        [Fact]
        public async Task Bad_files_are_refused_before_anything_is_saved()
        {
            var k = new Kit();
            Fails(await k.Imports.PreviewAsync(k.Asha, null), 400);
            Fails(await k.Imports.PreviewAsync(k.Asha, File([1, 2, 3], "x.csv")), 400, ".xlsx");
            Fails(await k.Imports.PreviewAsync(k.Asha, File("not a zip at all"u8.ToArray(), "x.xlsx")), 400, "does not look like");
            Fails(await k.Imports.PreviewAsync(k.Asha, File([0x50, 0x4B, 0x03, 0x04, 9, 9, 9, 9], "x.xlsx")), 400, "valid .xlsx");
            Fails(await k.Imports.PreviewAsync(k.Asha, File(new byte[5 * 1024 * 1024 + 1], "big.xlsx")), 400, "too large");
            Fails(await k.Imports.PreviewAsync(k.Asha, File(SimpleTemplate(["Name", "Other"], ["a", "b"]))), 400, "supported layout");
            Assert.Equal(0, await k.Ws.Campaigns.CountAsync());
        }

        [Fact]
        public async Task More_than_500_rows_is_refused_and_exactly_500_is_fine()
        {
            var k = new Kit();
            object?[][] Rows(int n) => Enumerable.Range(1, n).Select(i => (object?[])[$"C{i}", "Org", "Pat", $"p{i}@org.example", $"Subject {i}", "Body"]).ToArray();

            Assert.Equal(500, (await PreviewAsync(k, SimpleTemplate(null, Rows(500)))).TotalRows);
            Fails(await k.Imports.PreviewAsync(k.Asha, File(SimpleTemplate(null, Rows(501)))), 400, "500");
        }

        [Fact]
        public async Task Over_long_text_is_shortened_with_a_warning_and_newlines_in_single_line_fields_become_spaces()
        {
            var k = new Kit();
            var preview = await PreviewAsync(k, SimpleTemplate(null,
                [new string('T', 250), "Org", "Pat", "pat@org.example", "Line one\nline two", "Body"]));
            var row = Assert.Single(preview.Rows);
            Assert.Equal(200, row.Title.Length);
            Assert.Equal("Line one line two", row.Subject);
            Assert.NotNull(Flag(row, "truncated"));
        }

        [Fact]
        public async Task A_pm_cannot_import()
        {
            var k = new Kit();
            Fails(await k.Imports.PreviewAsync(k.Priya, File(ProspectWorkbook())), 403);
            var preview = await PreviewAsync(k, ProspectWorkbook());
            Fails(await k.Imports.CommitAsync(k.Priya, preview.PreviewToken, null, [2]), 403);
        }

        // ---------- preview saves nothing; commit ----------

        [Fact]
        public async Task Preview_saves_nothing()
        {
            var k = new Kit();
            await PreviewAsync(k, ProspectWorkbook());
            Assert.Equal(0, await k.Ws.Campaigns.CountAsync());
            Assert.Equal(0, await k.Ws.CampaignImports.CountAsync());
            Assert.Equal(0, await k.Ws.CampaignHistory.CountAsync());
        }

        [Fact]
        public async Task Commit_creates_drafts_for_the_selected_rows_only_and_records_the_batch()
        {
            var k = new Kit();
            var preview = await PreviewAsync(k, ProspectWorkbook(), "week1.xlsx");

            var result = Ok(await k.Imports.CommitAsync(k.Asha, preview.PreviewToken, null, [5, 2, 2])); // Delta and Acme; a repeated row counts once
            Assert.Equal((2, 2), (result.ImportedRows, result.SkippedRows));
            Assert.Equal([2, 5], result.Campaigns.Select(c => c.RowNumber));

            var delta = Ok(await k.Campaigns.GetAsync(k.Asha, result.Campaigns[1].CampaignId));
            Assert.Equal(("draft", "Delta Clinic", "info@delta.example", "Coding for Delta"), (delta.Status, delta.Title, delta.ClientEmail, delta.Subject));
            Assert.Equal((result.ImportId, 5, "Urgent care", "B", "Wave 2 - Thu"), (delta.ImportId, delta.SourceRow, delta.Segment, delta.Priority, delta.SendWave));
            Assert.Equal("Asha", delta.CreatedByName);
            var created = Assert.Single(delta.History);
            Assert.Equal("Created", created.Action);
            Assert.Contains("week1.xlsx", created.Note);

            var batch = Assert.Single(Ok(await k.Imports.ListBatchesAsync(k.Ben)));
            Assert.Equal((result.ImportId, "week1.xlsx", "Asha", 4, 2), (batch.Id, batch.FileName, batch.ImportedByName, batch.TotalRows, batch.ImportedRows));
            Assert.Equal(2, await k.Ws.Campaigns.CountAsync());
        }

        [Fact]
        public async Task A_preview_token_works_once_only_for_the_user_who_made_it()
        {
            var k = new Kit();
            var preview = await PreviewAsync(k, ProspectWorkbook());

            Fails(await k.Imports.CommitAsync(k.Ben, preview.PreviewToken, null, [2]), 404); // someone else's token
            Ok(await k.Imports.CommitAsync(k.Asha, preview.PreviewToken, null, [2]));
            Fails(await k.Imports.CommitAsync(k.Asha, preview.PreviewToken, null, [3]), 404, "expired"); // used up: no double import
            Fails(await k.Imports.CommitAsync(k.Asha, "nonsense", null, [3]), 404);
            Assert.Equal(1, await k.Ws.Campaigns.CountAsync());
        }

        [Fact]
        public async Task Commit_validates_the_selection_and_can_take_the_file_instead_of_a_token()
        {
            var k = new Kit();
            var preview = await PreviewAsync(k, ProspectWorkbook());
            Fails(await k.Imports.CommitAsync(k.Asha, preview.PreviewToken, null, []), 400, "at least one");
            Fails(await k.Imports.CommitAsync(k.Asha, preview.PreviewToken, null, null), 400, "at least one");
            Fails(await k.Imports.CommitAsync(k.Asha, preview.PreviewToken, null, [2, 99]), 400, "Row 99");
            Fails(await k.Imports.CommitAsync(k.Asha, null, null, [2]), 400, "previewToken");
            Assert.Equal(0, await k.Ws.Campaigns.CountAsync()); // a rejected selection creates nothing, not even the rows before the bad one

            var viaFile = Ok(await k.Imports.CommitAsync(k.Asha, null, File(ProspectWorkbook(), "again.xlsx"), [3]));
            Assert.Equal("again.xlsx", viaFile.FileName);
            Assert.Equal("Beta Cardiology", Ok(await k.Campaigns.GetAsync(k.Asha, viaFile.Campaigns[0].CampaignId)).Title);
        }

        [Fact]
        public async Task The_campaign_list_can_be_filtered_by_import_batch()
        {
            var k = new Kit();
            var first = Ok(await k.Imports.CommitAsync(k.Asha, (await PreviewAsync(k, ProspectWorkbook())).PreviewToken, null, [2, 3]));
            Ok(await k.Imports.CommitAsync(k.Asha, (await PreviewAsync(k, ProspectWorkbook())).PreviewToken, null, [4]));
            Ok(await k.Campaigns.CreateAsync(k.Asha, new CampaignInput { Title = "By hand" }));

            Assert.Equal(4, Ok(await k.Campaigns.ListAsync(k.Asha, null, false, null)).Items.Count);
            var list = Ok(await k.Campaigns.ListAsync(k.Asha, null, false, null, importId: first.ImportId));
            Assert.Equal(2, list.Items.Count);
            Assert.All(list.Items, i => Assert.Equal(first.ImportId, i.ImportId));
            Assert.Equal(2, list.Counts["draft"]);
        }

        // ---------- submit rules (server side) ----------

        [Fact]
        public async Task A_row_with_an_error_imports_as_a_draft_but_the_server_refuses_to_submit_it_until_fixed()
        {
            var k = new Kit();
            var preview = await PreviewAsync(k, ProspectWorkbook());
            var import = Ok(await k.Imports.CommitAsync(k.Asha, preview.PreviewToken, null, [2, 4, 5]));
            var (acmeId, gammaId, deltaId) = (import.Campaigns[0].CampaignId, import.Campaigns[1].CampaignId, import.Campaigns[2].CampaignId);

            var acme = Ok(await k.Campaigns.GetAsync(k.Asha, acmeId));
            Assert.Contains(acme.SubmitBlockers, b => b.Contains("[Your name]"));
            Assert.Contains(Ok(await k.Campaigns.GetAsync(k.Asha, gammaId)).SubmitBlockers, b => b.Contains("missing"));
            Assert.Empty(Ok(await k.Campaigns.GetAsync(k.Asha, deltaId)).SubmitBlockers);

            Fails(await k.Campaigns.SubmitAsync(k.Asha, acmeId), 400, "placeholders");
            Fails(await k.Campaigns.SubmitAsync(k.Asha, gammaId), 400, "email");
            Assert.Equal("draft", Ok(await k.Campaigns.GetAsync(k.Asha, acmeId)).Status);
            Assert.Equal("pending", Ok(await k.Campaigns.SubmitAsync(k.Asha, deltaId)).Status);

            // fix Acme: fill the signature, then it goes through
            var fixedInput = new CampaignInput
            {
                Title = acme.Title, ClientName = acme.ClientName, ContactName = acme.ContactName, ClientEmail = acme.ClientEmail,
                Subject = acme.Subject, Body = "Hi Dale,\nWe help with coding.\n\nBest regards,\nAsha\nSales, Emedlogix\n555-0100",
            };
            Ok(await k.Campaigns.UpdateAsync(k.Asha, acmeId, fixedInput));
            Assert.Empty(Ok(await k.Campaigns.GetAsync(k.Asha, acmeId)).SubmitBlockers);
            Assert.Equal("pending", Ok(await k.Campaigns.SubmitAsync(k.Asha, acmeId)).Status);
        }

        [Theory]
        [InlineData("[Your name]")]
        [InlineData("[title]")]
        [InlineData("[DIRECT PHONE]")]
        [InlineData("[First name]")]
        public async Task Placeholders_block_submit_for_every_draft_not_only_imported_ones(string placeholder)
        {
            var k = new Kit();
            var c = Ok(await k.Campaigns.CreateAsync(k.Asha, new CampaignInput
            { Title = "T", ClientName = "C", ContactName = "N", ClientEmail = "n@c.example", Subject = "S", Body = $"Hi,\n\nRegards,\n{placeholder}" }));
            Fails(await k.Campaigns.SubmitAsync(k.Asha, c.Id), 400, "placeholders");
        }

        // ---------- bulk ----------

        [Fact]
        public async Task Bulk_submit_reports_success_or_error_per_item_and_applies_the_single_action_rules()
        {
            var k = new Kit();
            var import = Ok(await k.Imports.CommitAsync(k.Asha, (await PreviewAsync(k, ProspectWorkbook())).PreviewToken, null, [2, 3, 4, 5]));
            var ids = import.Campaigns.Select(c => c.CampaignId).ToList(); // Acme (placeholders), Beta (bad email), Gamma (no email), Delta (ok)
            var others = Ok(await k.Campaigns.CreateAsync(k.Ben, new CampaignInput
            { Title = "Ben's", ClientName = "C", ContactName = "N", ClientEmail = "n@c.example", Subject = "S", Body = "B" }));

            var result = Ok(await k.Campaigns.BulkSubmitAsync(k.Asha, [.. ids, others.Id, 9999, ids[0]])); // the repeated id is handled once
            Assert.Equal(7 - 1, result.Items.Count);
            Assert.Equal((1, 5), (result.Succeeded, result.Failed));

            var byId = result.Items.ToDictionary(i => i.Id);
            Assert.True(byId[ids[3]].Success);
            Assert.Equal((200, "pending"), (byId[ids[3]].Status, byId[ids[3]].CampaignStatus));
            Assert.Equal((400, false, "draft"), (byId[ids[0]].Status, byId[ids[0]].Success, byId[ids[0]].CampaignStatus));
            Assert.Contains("placeholders", byId[ids[0]].Message);
            Assert.Contains("email", byId[ids[1]].Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(400, byId[ids[2]].Status);
            Assert.Equal(403, byId[others.Id].Status); // someone else's mail: same rule as the single action
            Assert.Equal(404, byId[9999].Status);
            Assert.Null(byId[9999].CampaignStatus);

            Assert.Equal("pending", Ok(await k.Campaigns.GetAsync(k.Asha, ids[3])).Status);
            Assert.Equal("draft", Ok(await k.Campaigns.GetAsync(k.Asha, ids[0])).Status);

            // resubmitting the one that went through is a 409 for that item only
            var again = Ok(await k.Campaigns.BulkSubmitAsync(k.Asha, [ids[3]]));
            Assert.Equal(409, again.Items[0].Status);
        }

        [Fact]
        public async Task Bulk_approve_follows_the_single_approve_rules_per_item()
        {
            var k = new Kit();
            var ids = new List<int>();
            for (var i = 0; i < 3; i++)
            {
                var c = Ok(await k.Campaigns.CreateAsync(k.Asha, new CampaignInput
                { Title = $"T{i}", ClientName = "C", ContactName = "N", ClientEmail = "n@c.example", Subject = $"S{i}", Body = "B" }));
                ids.Add(c.Id);
            }
            Ok(await k.Campaigns.SubmitAsync(k.Asha, ids[0]));
            Ok(await k.Campaigns.SubmitAsync(k.Asha, ids[1])); // ids[2] stays a draft
            var pmOwn = Ok(await k.Campaigns.CreateAsync(k.MeenaAsEmployee, new CampaignInput
            { Title = "Meena's", ClientName = "C", ContactName = "N", ClientEmail = "m@c.example", Subject = "S", Body = "B" }));
            Ok(await k.Campaigns.SubmitAsync(k.MeenaAsEmployee, pmOwn.Id));

            var result = Ok(await k.Campaigns.BulkApproveAsync(k.MeenaAsPm, [ids[0], ids[1], ids[2], pmOwn.Id]));
            var byId = result.Items.ToDictionary(i => i.Id);
            Assert.Equal((2, 2), (result.Succeeded, result.Failed));
            Assert.True(byId[ids[0]].Success && byId[ids[1]].Success);
            Assert.Equal(409, byId[ids[2]].Status); // still a draft
            Assert.Equal(403, byId[pmOwn.Id].Status); // a PM cannot approve their own mail
            Assert.Equal("approved", Ok(await k.Campaigns.GetAsync(k.Priya, ids[0])).Status);
            Assert.Equal(2, (await k.NotificationMessagesAsync(k.Asha.Id)).Count(t => t == "campaign_approved"));

            // an employee gets an error per item, not a blanket success
            var denied = Ok(await k.Campaigns.BulkApproveAsync(k.Ben, [ids[2]]));
            Assert.Equal(403, denied.Items[0].Status);
        }

        [Fact]
        public async Task Bulk_calls_need_ids_and_have_a_ceiling()
        {
            var k = new Kit();
            Fails(await k.Campaigns.BulkSubmitAsync(k.Asha, null), 400);
            Fails(await k.Campaigns.BulkApproveAsync(k.Priya, []), 400);
            Fails(await k.Campaigns.BulkSubmitAsync(k.Asha, Enumerable.Range(1, 501).ToList()), 400, "500");
        }
    }
}
