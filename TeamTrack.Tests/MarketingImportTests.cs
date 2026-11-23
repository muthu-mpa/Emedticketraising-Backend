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

            var bad = Headers.Where(h => h != "ORG PHONE" && h != "ORG EMAIL").ToArray();
            var f = Fails(await k.Svc().ImportAsync(k.Mara, Sheet("b.xlsx", bad, ["a", "b", "c", "d", "e"])), 400, "ORG PHONE");
            Assert.Contains("ORG EMAIL", f.Message);
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
            Fails(await k.Svc().ImportAsync(k.Mara, Sheet("leads.xlsx", Headers.Take(5).ToArray(), ["a", "b", "c", "d", "e"])), 400, "ORG EMAIL");
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
    }
}
