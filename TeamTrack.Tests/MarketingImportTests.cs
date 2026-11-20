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

        [Fact]
        public async Task A_missing_column_or_wrong_file_is_rejected()
        {
            var k = new MarketingKit();
            Fails(await k.Svc().ImportAsync(k.Mara, Sheet("leads.xlsx", Headers.Take(5).ToArray(), ["a", "b", "c", "d", "e"])), 400, "ORG EMAIL");
            Fails(await k.Svc().ImportAsync(k.Mara, new IncomingFile("leads.csv", 3, () => new MemoryStream([1, 2, 3]))), 400, "Excel");
            Fails(await k.Svc().ImportAsync(k.Mara, new IncomingFile("leads.xlsx", 3, () => new MemoryStream([1, 2, 3]))), 400, "could not be read");
            Assert.Empty(Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery())));
        }
    }
}
