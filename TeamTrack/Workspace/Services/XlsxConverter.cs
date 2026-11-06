using System.Globalization;
using System.IO.Compression;
using ClosedXML.Excel;

namespace TeamTrack.Workspace.Services
{
    /// <summary>The file could not be used (not an .xlsx, corrupt, or over a limit). The message is safe to show the user.</summary>
    public sealed class XlsxException : Exception
    {
        public XlsxException(string message) : base(message) { }
    }

    /// <summary>
    /// .xlsx &lt;-&gt; <see cref="SheetBook"/> using ClosedXML (MIT). Values and formulas only: colours, merged cells and
    /// other formatting are not carried over.
    /// </summary>
    public static class XlsxConverter
    {
        public const int MaxCells = 300_000;
        private const long MaxUncompressedBytes = 200L * 1024 * 1024; // guards against zip bombs
        private const int MaxCellTextLength = 32_767;                  // Excel's own limit

        private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

        /// <summary>Reads an uploaded file into memory after checking name, size and the zip signature.</summary>
        public static async Task<byte[]> ReadUploadAsync(Microsoft.AspNetCore.Http.IFormFile file, long maxBytes)
        {
            if (file.Length == 0) throw new XlsxException("The file is empty.");
            if (file.Length > maxBytes) throw new XlsxException($"The file is too large (max {maxBytes / (1024 * 1024)} MB).");
            if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new XlsxException("Only .xlsx files are allowed.");

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (bytes.Length < ZipMagic.Length || !bytes.AsSpan(0, ZipMagic.Length).SequenceEqual(ZipMagic))
                throw new XlsxException("The file content does not look like an .xlsx file.");
            return bytes;
        }

        public static SheetBook Read(byte[] bytes)
        {
            CheckZip(bytes);
            try
            {
                using var wb = new XLWorkbook(new MemoryStream(bytes));
                var book = new SheetBook();
                var total = 0;
                foreach (var ws in wb.Worksheets)
                {
                    var tab = new SheetTab { Name = ws.Name };
                    foreach (var xl in ws.CellsUsed(XLCellsUsedOptions.AllContents))
                    {
                        var cell = ToCell(xl);
                        if (cell == null) continue;
                        if (++total > MaxCells) throw new XlsxException($"The workbook has too many filled cells (max {MaxCells:N0}).");
                        tab.Set(xl.Address.RowNumber, xl.Address.ColumnNumber, cell);
                    }
                    book.Sheets.Add(tab);
                }
                return book;
            }
            catch (XlsxException) { throw; }
            catch (Exception ex)
            {
                throw new XlsxException($"The Excel file could not be read ({ex.GetType().Name}). Is it a valid .xlsx?");
            }
        }

        private static void CheckZip(byte[] bytes)
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                if (zip.Entries.Sum(e => e.Length) > MaxUncompressedBytes)
                    throw new XlsxException("The workbook is too large when unpacked.");
            }
            catch (InvalidDataException)
            {
                throw new XlsxException("The file is not a valid .xlsx.");
            }
        }

        private static SheetCell? ToCell(IXLCell xl)
        {
            XLCellValue value;
            // For formula cells ClosedXML returns the value cached in the file; if there is none it evaluates, which can throw.
            try { value = xl.Value; } catch { value = Blank.Value; }

            var cell = new SheetCell();
            if (value.IsBoolean) cell.Value = value.GetBoolean();
            else if (value.IsNumber) cell.Value = value.GetNumber();
            else if (value.IsDateTime)
            {
                var dt = value.GetDateTime();
                cell.Value = dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                cell.Type = "d";
            }
            else if (value.IsTimeSpan) cell.Value = value.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture);
            else if (value.IsText) cell.Value = Clip(value.GetText());
            else if (value.IsError) cell.Value = value.ToString();

            if (xl.HasFormula)
            {
                var f = xl.FormulaA1;
                if (!string.IsNullOrWhiteSpace(f)) cell.Formula = "=" + f.TrimStart('=');
            }
            return cell.IsEmpty ? null : cell;
        }

        private static string Clip(string s) => s.Length > MaxCellTextLength ? s[..MaxCellTextLength] : s;

        // ---------- export ----------

        public static byte[] Write(SheetBook book)
        {
            using var wb = new XLWorkbook();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tab in book.Sheets)
            {
                var ws = wb.AddWorksheet(UniqueName(tab.Name, used));
                foreach (var (row, cols) in tab.Rows)
                {
                    foreach (var (col, cell) in cols)
                    {
                        if (row < 1 || row > XLHelper.MaxRowNumber || col < 1 || col > XLHelper.MaxColumnNumber) continue;
                        Put(ws.Cell(row, col), cell);
                    }
                }
            }
            if (wb.Worksheets.Count == 0) wb.AddWorksheet("Sheet1");
            wb.ForceFullCalculation = true; // formulas written without a cached result are calculated when the file is opened
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        private static void Put(IXLCell target, SheetCell cell)
        {
            if (cell.Formula != null) { target.FormulaA1 = cell.Formula.TrimStart('='); return; }
            switch (cell.Value)
            {
                case string s when cell.Type == "d" && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt):
                    target.Value = dt;
                    target.Style.DateFormat.Format = dt.TimeOfDay == TimeSpan.Zero ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm:ss";
                    break;
                case string s: target.Value = s; break;
                case double d: target.Value = d; break;
                case bool b: target.Value = b; break;
            }
        }

        /// <summary>Excel sheet names: max 31 characters, none of [ ] : * ? / \, not empty, unique ignoring case.</summary>
        internal static string UniqueName(string? name, HashSet<string> used)
        {
            var clean = new string((name ?? "").Select(ch => "[]:*?/\\".Contains(ch) ? '_' : ch).ToArray()).Trim().Trim('\'');
            if (clean.Length == 0) clean = "Sheet";
            if (clean.Length > 31) clean = clean[..31];
            var candidate = clean;
            for (var i = 2; !used.Add(candidate); i++)
            {
                var suffix = $" ({i})";
                candidate = clean[..Math.Min(clean.Length, 31 - suffix.Length)] + suffix;
            }
            return candidate;
        }

        // ---------- template ----------

        public static readonly string[] TemplateHeaders = ["Campaign name", "Client", "Contact person", "Client email", "Subject", "Message"];

        public static byte[] BuildTemplate()
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Campaigns");
            for (var i = 0; i < TemplateHeaders.Length; i++)
            {
                var c = ws.Cell(1, i + 1);
                c.Value = TemplateHeaders[i];
                c.Style.Font.Bold = true;
                c.Style.Fill.BackgroundColor = XLColor.LightGray;
            }
            ws.Column(1).Width = 28; ws.Column(2).Width = 28; ws.Column(3).Width = 24;
            ws.Column(4).Width = 30; ws.Column(5).Width = 40; ws.Column(6).Width = 70;
            ws.SheetView.FreezeRows(1);

            var help = wb.AddWorksheet("How to use");
            help.Cell(1, 1).Value = "Fill one campaign mail per row on the 'Campaigns' sheet, starting at row 2. Keep the header row.";
            help.Cell(2, 1).Value = "Each row becomes a draft. Rows with a missing or invalid client email, or with placeholders such as [Your name] left in the text, can be imported but cannot be sent for approval until fixed.";
            help.Cell(3, 1).Value = "Limits: 500 rows and 5 MB per file.";
            help.Column(1).Width = 120;

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }
    }
}
