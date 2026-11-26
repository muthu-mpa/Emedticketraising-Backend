using ClosedXML.Excel;
using TeamTrack.Marketing.Dtos;
using TeamTrack.Marketing.Services;
using Xunit;
using static TeamTrack.Tests.Check;

namespace TeamTrack.Tests
{
    public class MarketingImportTests
    {
        private static readonly string[] Headers = ["Organization", "Contact Name", "Title / Role", "ORG PHONE", "EXACT LinkedIn URL", "EMAIL (POSSIBLE)", "ORG EMAIL"];

        private static IncomingFile Sheet(string fileName, string[] headers, params string[][] rows)
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Leads");
            for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++)
                    if (rows[r][c].Length > 0) ws.Cell(r + 2, c + 1).Value = rows[r][c];
            var ms = new MemoryStream();
            wb.SaveAs(ms);
            var bytes = ms.ToArray();
            return new IncomingFile(fileName, bytes.Length, () => new MemoryStream(bytes));
        }

        [Fact]
        public async Task Rows_are_imported_into_the_queue_with_every_column_kept()
        {
            var k = new MarketingKit();
            var r = Ok(await k.Svc().ImportAsync(k.Mara, Sheet("leads.xlsx", Headers,
                ["Acme Corp", "Jane Roe", "CTO", "+1 555 0100", "https://linkedin.com/in/jane", "jane@acme.com", "info@acme.com"])));
            Assert.Equal(1, r.Imported);
            Assert.Equal(0, r.Skipped);

            var queue = Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery()));
            var l = Assert.Single(queue);
            Assert.Equal("Jane Roe", l.Name);
            Assert.Equal("Acme Corp", l.Company);
            Assert.Equal("CTO", l.Title);
            Assert.Equal("+1 555 0100", l.OrgPhone);
            Assert.Equal("https://linkedin.com/in/jane", l.LinkedInUrl);
            Assert.Equal("jane@acme.com", l.PossibleEmail);
            Assert.Equal("info@acme.com", l.OrgEmail);
            Assert.Equal("New", l.Stage);

            // behaves like any lead: searchable by org email, can be picked up
            Assert.Single(Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery { Search = "info@acme" })));
            Ok(await k.Svc().PickUpAsync(k.Mara, l.Id));
        }

        [Fact]
        public async Task Upload_history_lists_files_and_undo_removes_only_untouched_leads()
        {
            var k = new MarketingKit();
            var r = Ok(await k.Svc().ImportAsync(k.Mara, Sheet("march.xlsx", Headers,
                ["Acme", "Jane Roe", "CTO", "", "", "jane@acme.com", ""],
                ["Beta", "Sam Poe", "CEO", "", "", "sam@beta.com", ""])));
            var h = Assert.Single(Ok(await k.Svc().ImportsAsync(k.Mara)));
            Assert.Equal(r.ImportId, h.Id);
            Assert.Equal("march.xlsx", h.FileName);
            Assert.Equal(2, h.Imported);
            Assert.True(h.CanUndo);

            // one lead is picked up: undo must leave it alone
            var queue = Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery()));
            Ok(await k.Svc().PickUpAsync(k.Mara, queue.First(l => l.Name == "Jane Roe").Id));

            var undo = Ok(await k.Svc().UndoImportAsync(k.Mara, h.Id));
            Assert.Equal(1, undo.Removed);
            Assert.Equal(1, undo.Kept);
            Assert.Empty(Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery())));
            Assert.Single(Ok(await k.Svc().BenchAsync(k.Mara, null)));

            var after = Assert.Single(Ok(await k.Svc().ImportsAsync(k.Mara)));
            Assert.True(after.Undone);
            Assert.False(after.CanUndo);
            Fails(await k.Svc().UndoImportAsync(k.Mara, h.Id), 409, "already");

            // the removed person can be uploaded again
            Assert.Equal(1, Ok(await k.Svc().ImportAsync(k.Mara, Sheet("march.xlsx", Headers,
                ["Beta", "Sam Poe", "CEO", "", "", "sam@beta.com", ""]))).Imported);
        }

        [Fact]
        public async Task A_waiting_lead_can_be_removed_but_not_one_on_a_bench()
        {
            var k = new MarketingKit();
            Ok(await k.Svc().ImportAsync(k.Mara, Sheet("a.xlsx", Headers,
                ["Acme", "Jane Roe", "CTO", "", "", "jane@acme.com", ""],
                ["Beta", "Sam Poe", "CEO", "", "", "sam@beta.com", ""])));
            var queue = Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery()));
            var jane = queue.First(l => l.Name == "Jane Roe");
            var sam = queue.First(l => l.Name == "Sam Poe");
            Ok(await k.Svc().PickUpAsync(k.Mara, sam.Id));

            Fails(await k.Svc().RemoveFromQueueAsync(k.Mara, sam.Id), 409, "work queue");
            Ok(await k.Svc().RemoveFromQueueAsync(k.Mara, jane.Id));
            Assert.Empty(Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery())));
            Fails(await k.Svc().RemoveFromQueueAsync(k.Mara, jane.Id), 404, "not found");
        }

        [Fact]
        public async Task Removing_a_queue_lead_also_deletes_its_history_attachments_and_stored_files()
        {
            var k = new MarketingKit();
            var imp = Ok(await k.Svc().ImportAsync(k.Mara, Sheet("a.xlsx", Headers,
                ["Acme", "Jane Roe", "CTO", "", "", "jane@acme.com", ""],
                ["Beta", "Sam Poe", "CEO", "", "", "sam@beta.com", ""])));
            var jane = Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery())).First(l => l.Name == "Jane Roe");

            // Jane was worked on (note + file) and then returned to the queue, so she carries history and a stored file
            Ok(await k.Svc().PickUpAsync(k.Mara, jane.Id));
            Ok(await k.Svc().AddHistoryAsync(k.Mara, jane.Id, new HistoryInput
            { Kind = "Note", Text = "Called", Files = [MarketingKit.File("brief.pdf", [1, 2, 3])] }));
            Ok(await k.Svc().ReturnAsync(k.Mara, jane.Id));
            Assert.Single(k.Files.Stored);

            Assert.Equal(2, Assert.Single(Ok(await k.Svc().ImportsAsync(k.Mara))).Remaining);
            var removed = Ok(await k.Svc().RemoveFromQueueAsync(k.Noah, jane.Id));
            Assert.Equal("Jane Roe", removed.Name);

            Assert.Empty(k.Files.Stored);
            Assert.DoesNotContain(Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery())), l => l.Id == jane.Id);
            Assert.Equal(1, Assert.Single(Ok(await k.Svc().ImportsAsync(k.Mara))).Remaining);
            await using var db = k.NewContext();
            Assert.Empty(db.History.Where(h => h.LeadId == jane.Id));
            Assert.Empty(db.Attachments);
        }

        [Fact]
        public async Task Removing_is_refused_for_owned_closed_and_unknown_leads()
        {
            var k = new MarketingKit();
            var owned = await k.AddLeadAsync("Owned One", owner: 1);
            var closed = await k.AddLeadAsync("Closed One", stage: "Closed");
            Fails(await k.Svc().RemoveFromQueueAsync(k.Noah, owned), 409, "work queue");
            Fails(await k.Svc().RemoveFromQueueAsync(k.Mara, closed), 409, "work queue");
            Fails(await k.Svc().RemoveFromQueueAsync(k.Mara, Guid.NewGuid()), 404, "not found");
            await using var db = k.NewContext();
            Assert.Equal(2, db.Leads.Count()); // nothing was deleted
        }

        [Fact]
        public async Task Empty_rows_missing_names_and_duplicates_are_skipped_and_counted()
        {
            var k = new MarketingKit();
            var file = Sheet("leads.xlsx", Headers,
                ["Acme", "Jane Roe", "CTO", "", "", "jane@acme.com", ""],
                ["", "", "", "", "", "", ""],
                ["Acme", "", "CFO", "", "", "", ""],
                ["Acme", "Jane Roe", "CTO", "", "", "JANE@acme.com", ""],
                ["Beta", "Bob", "", "", "", "", "hello@beta.com"]);
            var r = Ok(await k.Svc().ImportAsync(k.Mara, file));
            Assert.Equal(2, r.Imported);
            Assert.Equal(2, r.Skipped);
            Assert.Equal(1, r.Duplicates);
            Assert.Equal(1, r.EmptyRows);

            // uploading the same file again creates nothing new
            var again = Ok(await k.Svc().ImportAsync(k.Mara, file));
            Assert.Equal(0, again.Imported);
            Assert.Equal(2, Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery())).Count);
        }

        private static async Task<MarketingKit> SeededAsync()
        {
            var k = new MarketingKit();
            Ok(await k.Svc().ImportAsync(k.Mara, Sheet("a.xlsx", Headers,
                ["Acme", "Jane Roe", "CTO", "", "https://linkedin.com/in/jane", "jane@acme.com", ""])));
            return k;
        }

        [Fact]
        public async Task Defaults_main_email_fallback_and_captured_history()
        {
            var k = new MarketingKit();
            Ok(await k.Svc().ImportAsync(k.Mara, Sheet("a.xlsx", Headers,
                ["Acme", "Only Org", "", "", "", "", "info@acme.com"],
                ["Acme", "Has Both", "", "", "", "p@acme.com", "info@acme.com"])));
            var q = Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery()));
            Assert.Equal("info@acme.com", q.Single(l => l.Name == "Only Org").Email);
            var both = q.Single(l => l.Name == "Has Both");
            Assert.Equal("p@acme.com", both.Email);
            Assert.Equal(20, both.Score);
            Assert.Equal("Cold", both.Temperature);
            Assert.Equal("Excel import", both.Channel);
            Assert.Equal("Excel import", both.Campaign);
            var detail = Ok(await k.Svc().GetAsync(k.Mara, both.Id));
            Assert.Contains(detail.History, h => h.Kind == "Captured");
        }

        [Fact]
        public async Task Duplicate_by_email_linkedin_or_name_and_organization_is_skipped()
        {
            var k = await SeededAsync();
            var r = Ok(await k.Svc().ImportAsync(k.Mara, Sheet("b.xlsx", Headers,
                ["Other", "Someone A", "", "", "", "  JANE@ACME.COM ", ""],
                ["Other", "Someone B", "", "", "HTTPS://linkedin.com/in/jane/", "", ""],
                ["  acme ", " jane   roe", "", "", "", "", ""],
                ["Other", "Fresh", "", "", "", "fresh@other.com", ""])));
            Assert.Equal(1, r.Imported);
            Assert.Equal(3, r.Duplicates);
            Assert.Equal(3, r.Skipped);
            Assert.Equal(3, r.Issues.Count);
        }

        [Fact]
        public async Task Duplicates_inside_one_file_are_skipped()
        {
            var k = new MarketingKit();
            var r = Ok(await k.Svc().ImportAsync(k.Mara, Sheet("a.xlsx", Headers,
                ["Acme", "Jane", "", "", "", "j@acme.com", ""],
                ["Zed", "Janet", "", "", "", "j@acme.com", ""],
                ["Acme", "Jane", "", "", "", "other@acme.com", ""])));
            Assert.Equal(1, r.Imported);
            Assert.Equal(2, r.Duplicates);
        }

        [Fact]
        public async Task Headers_match_ignoring_case_and_extra_spaces_and_missing_ones_are_named()
        {
            var k = new MarketingKit();
            var messy = new[] { "  organization ", "CONTACT   NAME", "title / role", "org phone", "exact linkedin url", "email (possible)", " org email" };
            Assert.Equal(1, Ok(await k.Svc().ImportAsync(k.Mara, Sheet("a.xlsx", messy, ["Acme", "Jane", "", "", "", "", ""]))).Imported);

            var bad = Headers.Where(h => h != "Organization" && h != "Contact Name").ToArray();
            var f = Fails(await k.Svc().ImportAsync(k.Mara, Sheet("b.xlsx", bad, ["a", "b", "c", "d", "e"])), 400, "Organization");
            Assert.Contains("Contact Name", f.Message);

            // the newer sheet layout: renamed columns, no org phone / org email
            var newer = new[] { "ORGANIZATION NAME", "COMPANY LINKEDIN URL", "CONTACT NAME", "DESIGNATION / JOB TITLE", "PERSONAL LINKEDIN URL", "WORK EMAIL", "CONTACT NUMBER" };
            Assert.Equal(1, Ok(await k.Svc().ImportAsync(k.Mara, Sheet("c.xlsx", newer, ["Beta", "https://co", "Sam", "CEO", "https://in/sam", "sam@beta.com", "555"]))).Imported);
        }

        [Fact]
        public async Task Oversized_file_is_rejected()
        {
            var k = new MarketingKit();
            Fails(await k.Svc().ImportAsync(k.Mara, new IncomingFile("big.xlsx", MarketingService.MaxImportBytes + 1, () => new MemoryStream())), 400, "too large");
        }

        [Fact]
        public void Schema_script_adds_the_four_columns_idempotently_without_dropping_anything()
        {
            var sql = TeamTrack.Marketing.Data.MarketingSchemaInitializer.Sql;
            foreach (var c in new[] { "org_phone", "linked_in_url", "possible_email", "org_email" })
                Assert.Contains($"ALTER TABLE mk_leads ADD COLUMN IF NOT EXISTS {c} text;", sql);
            Assert.DoesNotContain("DROP ", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{", sql); // the script runs through ExecuteSqlRaw
        }

        [Fact]
        public async Task A_missing_column_or_wrong_file_is_rejected()
        {
            var k = new MarketingKit();
            Fails(await k.Svc().ImportAsync(k.Mara, Sheet("leads.xlsx", Headers.Skip(2).ToArray(), ["a", "b", "c", "d", "e"])), 400, "Organization");
            Fails(await k.Svc().ImportAsync(k.Mara, new IncomingFile("leads.csv", 3, () => new MemoryStream([1, 2, 3]))), 400, "Excel");
            Fails(await k.Svc().ImportAsync(k.Mara, new IncomingFile("leads.xlsx", 3, () => new MemoryStream([1, 2, 3]))), 400, "could not be read");
            Assert.Empty(Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery())));
        }

        [Fact]
        public async Task The_header_row_is_found_below_a_title_row_and_on_a_later_sheet()
        {
            var k = new MarketingKit();
            using var wb = new XLWorkbook();
            wb.AddWorksheet("Cover").Cell(1, 1).Value = "Notes";
            var ws = wb.AddWorksheet("Leads");
            ws.Cell(1, 1).Value = "Prospect list Q4";
            for (var c = 0; c < Headers.Length; c++) ws.Cell(3, c + 1).Value = Headers[c].ToLowerInvariant().Replace(" / ", "/");
            ws.Cell(4, 2).Value = "Jane Roe"; ws.Cell(4, 1).Value = "Acme"; ws.Cell(4, 7).Value = "info@acme.com";
            var ms = new MemoryStream();
            wb.SaveAs(ms);
            var bytes = ms.ToArray();
            var r = Ok(await k.Svc().ImportAsync(k.Mara, new IncomingFile("leads.xlsx", bytes.Length, () => new MemoryStream(bytes))));
            Assert.Equal(1, r.Imported);
        }

        private static readonly string[] Template23 =
        [
            "UNIQUE ID", "ORGANIZATION", "BUSINESS CATEGORY", "LOCATION", "OFFICIAL WEBSITE", "ORG LINKEDIN URL", "LEAD ID", "LEAD NAME",
            "DESIGNATION / JOB TITLE", "DECISION-MAKER LEVEL", "LEAD LINKEDIN URL", "LEAD EMAIL", "CONTACT NUMBER", "CONTACT TYPE",
            "EMEDLOGIX SOLUTION", "POTENTIAL BUSINESS OPPORTUNITY", "EMEDLOGIX FIT / REASON FOR TARGETING", "PROSPECT PRIORITY", "STATUS",
            "LAST CONTACTED", "NEXT ACTION", "NEXT ACTION DATE", "REMARKS", "Custom Extra",
        ];

        [Fact]
        public async Task The_23_column_template_maps_standard_fields_and_keeps_every_column()
        {
            var k = new MarketingKit();
            string[] row = ["U1", "Acme", "Health", "NYC", "https://acme.com", "https://linkedin.com/company/acme", "L1", "Jane Roe", "CTO", "C-level",
                "https://linkedin.com/in/jane", "jane@acme.com", "555", "Direct", "Sol", "Big", "Fit", "High", "New", "2026-01-05", "Call", "2026-02-01", "", "extra!"];
            Assert.Equal(1, Ok(await k.Svc().ImportAsync(k.Mara, Sheet("n.xlsx", Template23, row))).Imported);

            var l = Assert.Single(Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery())));
            Assert.Equal("Jane Roe", l.Name);
            Assert.Equal("CTO", l.Title);
            Assert.Equal("jane@acme.com", l.PossibleEmail);
            Assert.Equal("555", l.Phone);
            Assert.Null(l.OrgPhone);
            Assert.Equal("https://linkedin.com/in/jane", l.LinkedInUrl);

            // the extra data survives pick up and a stage change
            Ok(await k.Svc().PickUpAsync(k.Mara, l.Id));
            var d = Ok(await k.Svc().SetStageAsync(k.Mara, l.Id, "Contacted"));
            Assert.Equal(24, d.ImportedFields.Count);
            Assert.Equal(Template23, d.ImportedFields.Select(f => f.Name).ToArray());
            Assert.Equal("https://linkedin.com/company/acme", d.ImportedFields[5].Value);
            Assert.Equal("", d.ImportedFields[22].Value);
            Assert.Equal("extra!", d.ImportedFields[23].Value);
        }

        [Fact]
        public async Task Legacy_sheets_keep_their_columns_and_a_company_linkedin_is_not_the_leads()
        {
            var k = new MarketingKit();
            Ok(await k.Svc().ImportAsync(k.Mara, Sheet("a.xlsx", Headers, ["Acme", "Jane", "CTO", "1", "https://www.linkedin.com/company/acme", "", "info@acme.com"])));
            var l = Assert.Single(Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery())));
            Assert.Null(l.LinkedInUrl);
            Ok(await k.Svc().PickUpAsync(k.Mara, l.Id));
            var d = Ok(await k.Svc().GetAsync(k.Mara, l.Id));
            Assert.Equal(Headers, d.ImportedFields.Select(f => f.Name).ToArray());
            Assert.Equal("https://www.linkedin.com/company/acme", d.ImportedFields[4].Value);
        }
    }
}
