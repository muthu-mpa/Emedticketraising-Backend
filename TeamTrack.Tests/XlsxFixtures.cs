using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;

namespace TeamTrack.Tests
{
    /// <summary>Small fake workbooks built in code. They mimic the headers of the real prospect workbook but contain no real data.</summary>
    public static class XlsxFixtures
    {
        public static byte[] Bytes(XLWorkbook wb)
        {
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        public static IFormFile File(byte[] bytes, string name = "import.xlsx")
            => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);

        private static void Row(IXLWorksheet ws, int row, params object?[] values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                switch (values[i])
                {
                    case null: break;
                    case string s: ws.Cell(row, i + 1).Value = s; break;
                    case int n: ws.Cell(row, i + 1).Value = n; break;
                    case double d: ws.Cell(row, i + 1).Value = d; break;
                    default: throw new ArgumentException("unsupported fixture value");
                }
            }
        }

        public const string Footer = "\n\nBest regards,\n\n[Your name]\n[Title], Emedlogix\n[Direct phone]";

        /// <summary>
        /// Format A. Prospect sheet first (rows deliberately in a different order than the drafts, to test the "#" join),
        /// then the drafts sheet with formula columns, then a sheet that must be ignored.
        /// </summary>
        public static byte[] ProspectWorkbook(Action<IXLWorksheet>? editDrafts = null, Action<IXLWorksheet>? editProspects = null)
        {
            using var wb = new XLWorkbook();

            var p = wb.AddWorksheet("Week 1 (4)");
            Row(p, 1, "#", "Send wave", "Priority (30-Oct list)", "Organization", "Segment", "City", "Email (published / leads file)", "Email status");
            Row(p, 2, 3, "Wave 1 - Wed", "A", "Gamma Health", "Health system", "Dallas", "", "to find");
            Row(p, 3, 1, "Wave 1 - Wed", "A", "Acme Imaging", "Radiology", "Plano", "dale@acme.example", "verified");
            Row(p, 4, 4, "Wave 2 - Thu", "B", "Delta Clinic", "Urgent care", "Irving", "info@delta.example", "verified");
            Row(p, 5, 2, "Wave 2 - Thu", "C", "Beta Cardiology", "Cardiology", "Austin", "Use the contact form", "none");
            editProspects?.Invoke(p);

            var d = wb.AddWorksheet("Email Drafts");
            Row(d, 1, "#", "Send date", "Organization", "To (buyer)", "Subject line", "Alt subject (A/B test)", "Preview text",
                "Email body (paste-ready, with CAN-SPAM footer)", "Word count (incl. signature)", "Language check (no 'autonomous')");
            Row(d, 2, 1, "Wed 4-Nov", "Acme Imaging", "Dale Flowers, CIO, Acme Imaging", "Faster coding for Acme", "alt", "preview", "Hi Dale,\nWe help with coding." + Footer);
            Row(d, 3, 2, "Wed 4-Nov", "Beta Cardiology", "Sam Rao, Director", "Coding for Beta", "alt", "preview", "Hi Sam,\nOur autonomous coding engine is fast.\n\nRegards,\nPriya");
            Row(d, 4, 3, "Thu 5-Nov", "Gamma Health", "Lee Park", "Coding for Gamma", "alt", "preview", "Hi Lee,\nClean text.\n\nRegards,\nPriya");
            Row(d, 5, 4, "Thu 5-Nov", "Delta Clinic", "Ann Cole, Owner", "Coding for Delta", "alt", "preview", "Hi Ann,\nAll good.\n\nRegards,\nPriya");
            for (var r = 2; r <= 5; r++)
            {
                d.Cell(r, 9).FormulaA1 = $"LEN(H{r})";
                d.Cell(r, 10).FormulaA1 = $"IF(ISNUMBER(SEARCH(\"autonom\",H{r})),\"CHECK\",\"OK\")";
            }
            editDrafts?.Invoke(d);

            var other = wb.AddWorksheet("Summary");
            Row(other, 1, "Pipeline", "Total");
            Row(other, 2, "Organization", 4);
            return Bytes(wb);
        }

        /// <summary>Format B: Campaign name | Client | Contact person | Client email | Subject | Message.</summary>
        public static byte[] SimpleTemplate(string[]? headers = null, params object?[][] rows)
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Campaigns");
            Row(ws, 1, (headers ?? ["Campaign name", "Client", "Contact person", "Client email", "Subject", "Message"]).Cast<object?>().ToArray());
            for (var i = 0; i < rows.Length; i++) Row(ws, i + 2, rows[i]);
            return Bytes(wb);
        }
    }
}
