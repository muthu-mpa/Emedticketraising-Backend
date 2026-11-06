using System.Text.RegularExpressions;

namespace TeamTrack.Workspace.Services
{
    public enum ImportFormat { ProspectWorkbook, SimpleTemplate }

    /// <summary>One row of the file mapped to campaign fields, before flags are worked out.</summary>
    public sealed record ImportCandidate(
        int RowNumber, string Title, string ClientName, string ContactName,
        string RawEmail, string ClientEmail, string Subject, string Body,
        string? Segment, string? Priority, string? SendWave, bool Truncated);

    public sealed class ParsedImport
    {
        public ImportFormat Format { get; init; }
        public List<ImportCandidate> Rows { get; init; } = [];
    }

    /// <summary>
    /// Turns a <see cref="SheetBook"/> into campaign rows. Two layouts are recognised; headers are matched
    /// case-insensitively by "starts with":
    ///  A) prospect workbook: a drafts sheet (Organization, Subject line, Email body) joined by "#" to a prospect sheet
    ///     (Organization, Email...). Formula columns are never read, only the cached values of the columns used.
    ///  B) simple template: Campaign name | Client | Contact person | Client email | Subject | Message.
    /// </summary>
    public static class CampaignImportParser
    {
        public const int MaxRows = 500;
        private const int HeaderSearchRows = 30;

        public static readonly string[] SimpleHeaders = ["campaign name", "client", "contact person", "client email", "subject", "message"];

        public static ParsedImport Parse(SheetBook book)
        {
            var parsed = TryProspectWorkbook(book) ?? TrySimpleTemplate(book)
                ?? throw new XlsxException("This file is not in a supported layout. Use the simple template "
                    + "(Campaign name | Client | Contact person | Client email | Subject | Message) or the prospect workbook "
                    + "(a drafts sheet with Organization, Subject line and Email body, plus a prospect sheet with Organization and Email).");
            if (parsed.Rows.Count == 0) throw new XlsxException("No campaign rows were found in the file.");
            return parsed;
        }

        // ---------- format A ----------

        private static ParsedImport? TryProspectWorkbook(SheetBook book)
        {
            SheetTab? drafts = null; (int Row, Dictionary<string, int> Cols) d = default;
            foreach (var tab in book.Sheets)
            {
                var found = FindHeader(tab, ["organization", "subject line", "email body"], ["#", "to (buyer)", "segment", "priority", "send wave"]);
                if (found != null) { drafts = tab; d = found.Value; break; }
            }
            if (drafts == null) return null;

            // The drafts sheet also has an "Email body" column, so it must never be taken for the prospect list.
            SheetTab? prospects = null; (int Row, Dictionary<string, int> Cols) p = default;
            foreach (var tab in book.Sheets.Where(t => t != drafts))
            {
                var found = FindHeader(tab, ["organization", "email"], ["#", "segment", "priority", "send wave"]);
                if (found != null) { prospects = tab; p = found.Value; break; }
            }

            var byNumber = new Dictionary<string, int>();
            var byOrganization = new Dictionary<string, int>();
            if (prospects != null)
            {
                for (var r = p.Row + 1; r <= prospects.MaxRow; r++)
                {
                    if (p.Cols.TryGetValue("#", out var nc) && Key(prospects.Text(r, nc)) is { Length: > 0 } number) byNumber.TryAdd(number, r);
                    if (Key(prospects.Text(r, p.Cols["organization"])) is { Length: > 0 } org) byOrganization.TryAdd(org, r);
                }
            }

            var rows = new List<ImportCandidate>();
            for (var r = d.Row + 1; r <= drafts.MaxRow; r++)
            {
                var organization = drafts.Text(r, d.Cols["organization"]);
                var subject = drafts.Text(r, d.Cols["subject line"]);
                var body = drafts.Text(r, d.Cols["email body"]);
                if (organization.Trim().Length == 0 && subject.Trim().Length == 0 && body.Trim().Length == 0) continue;
                CheckRowLimit(rows.Count);

                int? match = null;
                if (d.Cols.TryGetValue("#", out var dn) && byNumber.TryGetValue(Key(drafts.Text(r, dn)), out var byNum)) match = byNum;
                else if (byOrganization.TryGetValue(Key(organization), out var byOrg)) match = byOrg;

                string FromProspect(string header)
                {
                    var own = d.Cols.TryGetValue(header, out var dc) ? drafts.Text(r, dc) : "";
                    if (match == null || prospects == null || !p.Cols.TryGetValue(header, out var pc)) return own;
                    var value = prospects.Text(match.Value, pc);
                    return value.Trim().Length > 0 ? value : own;
                }

                var rawEmail = match != null && prospects != null ? prospects.Text(match.Value, p.Cols["email"]) : "";
                var contact = d.Cols.TryGetValue("to (buyer)", out var tc) ? drafts.Text(r, tc) : "";
                rows.Add(Build(r, title: organization, client: organization, contact: BeforeFirstComma(contact), rawEmail,
                    subject, body, FromProspect("segment"), FromProspect("priority"), FromProspect("send wave")));
            }
            return new ParsedImport { Format = ImportFormat.ProspectWorkbook, Rows = rows };
        }

        // ---------- format B ----------

        private static ParsedImport? TrySimpleTemplate(SheetBook book)
        {
            foreach (var tab in book.Sheets)
            {
                var found = FindHeader(tab, SimpleHeaders, []);
                if (found == null) continue;
                var (headerRow, cols) = found.Value;

                var rows = new List<ImportCandidate>();
                for (var r = headerRow + 1; r <= tab.MaxRow; r++)
                {
                    string Get(string h) => tab.Text(r, cols[h]);
                    if (SimpleHeaders.All(h => Get(h).Trim().Length == 0)) continue;
                    CheckRowLimit(rows.Count);
                    rows.Add(Build(r, Get("campaign name"), Get("client"), Get("contact person"), Get("client email"),
                        Get("subject"), Get("message"), null, null, null));
                }
                return new ParsedImport { Format = ImportFormat.SimpleTemplate, Rows = rows };
            }
            return null;
        }

        // ---------- helpers ----------

        private static void CheckRowLimit(int rowsSoFar)
        {
            if (rowsSoFar >= MaxRows) throw new XlsxException($"The file has more than {MaxRows} rows. Split it into smaller files.");
        }

        private static string BeforeFirstComma(string text)
        {
            var i = text.IndexOf(',');
            return (i < 0 ? text : text[..i]).Trim();
        }

        /// <summary>Row-join key: text, trimmed, case-insensitive; numbers compare as numbers ("1" == "1.0").</summary>
        private static string Key(string text)
        {
            text = text.Trim();
            return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? n.ToString("G15", System.Globalization.CultureInfo.InvariantCulture)
                : text.ToLowerInvariant();
        }

        private static string Norm(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");

        /// <summary>
        /// First row (within the top rows) whose cells start with all the required headers. Longer headers claim their
        /// column first, so "Client email" is not taken for "Client". Optional headers are mapped when present.
        /// </summary>
        private static (int Row, Dictionary<string, int> Cols)? FindHeader(SheetTab tab, string[] required, string[] optional)
        {
            var last = Math.Min(tab.MaxRow, HeaderSearchRows);
            for (var r = 1; r <= last; r++)
            {
                if (!tab.Rows.TryGetValue(r, out var cells)) continue;
                var headers = cells.OrderBy(c => c.Key).Select(c => (Col: c.Key, Text: Norm(c.Value.AsText()))).Where(h => h.Text.Length > 0).ToList();
                var claimed = new HashSet<int>();
                var map = new Dictionary<string, int>();
                var ok = true;
                foreach (var spec in required.Select(s => (Name: s, IsRequired: true)).Concat(optional.Select(s => (Name: s, IsRequired: false)))
                             .OrderByDescending(s => s.Name.Length))
                {
                    var hit = headers.FirstOrDefault(h => !claimed.Contains(h.Col) && h.Text.StartsWith(spec.Name, StringComparison.Ordinal));
                    if (hit.Text != null) { claimed.Add(hit.Col); map[spec.Name] = hit.Col; }
                    else if (spec.IsRequired) { ok = false; break; }
                }
                if (ok) return (r, map);
            }
            return null;
        }

        /// <summary>Normalises a row to what a draft can hold: single-line fields on one line, lengths clipped (and noted).</summary>
        private static ImportCandidate Build(int row, string title, string client, string contact, string rawEmail,
            string subject, string body, string? segment, string? priority, string? sendWave)
        {
            var truncated = false;
            string Line(string s, int max)
            {
                s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
                if (s.Length <= max) return s;
                truncated = true;
                return s[..max].TrimEnd();
            }
            string? Optional(string? s) => string.IsNullOrWhiteSpace(s) ? null : Line(s, 200);

            var email = rawEmail.Trim();
            if (email.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) email = email[7..].Trim();

            var cleanClient = Line(client, 200);
            var cleanSubject = Line(subject, 300);
            var cleanTitle = Line(title, 200);
            if (cleanTitle.Length == 0) cleanTitle = cleanClient.Length > 0 ? cleanClient : cleanSubject.Length > 0 ? cleanSubject : $"Imported row {row}";

            var cleanBody = (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (cleanBody.Length > 20000) { cleanBody = cleanBody[..20000]; truncated = true; }

            return new ImportCandidate(
                row, cleanTitle, cleanClient, Line(contact, 200), email,
                CampaignService.IsValidEmail(email) ? email : "",
                cleanSubject, cleanBody, Optional(segment), Optional(priority), Optional(sendWave), truncated);
        }
    }
}
