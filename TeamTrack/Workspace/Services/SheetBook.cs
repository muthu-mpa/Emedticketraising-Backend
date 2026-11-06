using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamTrack.Workspace.Services
{
    /// <summary>
    /// The JSON model of a workbook: sheets -> rows -> cells. Sparse: only cells that hold something exist.
    /// Rows and columns are 1-based Excel numbers, so a cell is addressed by (sheet name, row, col).
    /// Used for shared sheets (stored as jsonb) and as the common input of the campaign import parser.
    /// </summary>
    public sealed class SheetBook
    {
        public List<SheetTab> Sheets { get; set; } = [];

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        public static SheetBook FromJson(string? json)
        {
            var book = string.IsNullOrWhiteSpace(json) ? new SheetBook() : JsonSerializer.Deserialize<SheetBook>(json, Json) ?? new SheetBook();
            foreach (var cell in book.Sheets.SelectMany(s => s.Rows.Values).SelectMany(r => r.Values))
                cell.Value = SheetCell.Normalize(cell.Value);
            return book;
        }

        public SheetTab? Find(string? name)
            => Sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        [JsonIgnore] public int CellCount => Sheets.Sum(s => s.Rows.Values.Sum(r => r.Count));
    }

    public sealed class SheetTab
    {
        public string Name { get; set; } = "";
        /// <summary>row number -> (column number -> cell)</summary>
        public Dictionary<int, Dictionary<int, SheetCell>> Rows { get; set; } = new();

        public SheetCell? Get(int row, int col)
            => Rows.TryGetValue(row, out var r) && r.TryGetValue(col, out var c) ? c : null;

        public void Set(int row, int col, SheetCell? cell)
        {
            if (cell == null || cell.IsEmpty)
            {
                if (Rows.TryGetValue(row, out var existing))
                {
                    existing.Remove(col);
                    if (existing.Count == 0) Rows.Remove(row);
                }
                return;
            }
            if (!Rows.TryGetValue(row, out var r)) Rows[row] = r = new Dictionary<int, SheetCell>();
            r[col] = cell;
        }

        [JsonIgnore] public int MaxRow => Rows.Count == 0 ? 0 : Rows.Keys.Max();
        [JsonIgnore] public int MaxCol => Rows.Count == 0 ? 0 : Rows.Values.Max(r => r.Count == 0 ? 0 : r.Keys.Max());

        /// <summary>The cell's value as plain text (numbers without trailing zeros, booleans as TRUE/FALSE); "" for empty.</summary>
        public string Text(int row, int col) => Get(row, col)?.AsText() ?? "";
    }

    public sealed class SheetCell
    {
        /// <summary>string, double or bool (null for a formula whose result is unknown).</summary>
        [JsonPropertyName("v")] public object? Value { get; set; }
        /// <summary>The formula including the leading "=", if the cell has one.</summary>
        [JsonPropertyName("f")] public string? Formula { get; set; }
        /// <summary>"d" when <see cref="Value"/> is an ISO date/time string that should be written back as a date.</summary>
        [JsonPropertyName("t")] public string? Type { get; set; }

        [JsonIgnore] public bool IsEmpty => Value == null && Formula == null;

        public string AsText() => Value switch
        {
            null => "",
            string s => s,
            double d => d.ToString("G15", CultureInfo.InvariantCulture),
            bool b => b ? "TRUE" : "FALSE",
            _ => Value.ToString() ?? "",
        };

        /// <summary>JSON deserialises "v" as a JsonElement; turn it into string / double / bool.</summary>
        internal static object? Normalize(object? v)
        {
            switch (v)
            {
                case int i: return (double)i;
                case long l: return (double)l;
                case float f: return (double)f;
                case decimal m: return (double)m;
            }
            if (v is not JsonElement e) return v;
            return e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Number => e.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        /// <summary>A cell typed into the grid: text starting with "=" is a formula, anything else is a plain value.</summary>
        public static SheetCell? FromInput(object? input)
        {
            input = Normalize(input);
            if (input is string s)
            {
                if (s.Length == 0) return null;
                if (s.Length > 1 && s[0] == '=') return new SheetCell { Formula = s };
                return new SheetCell { Value = s };
            }
            return input == null ? null : new SheetCell { Value = input };
        }

        /// <summary>The value a client would send to write this cell back (the formula when there is one).</summary>
        public object? ToInput() => Formula ?? Value;

        public bool SameAs(SheetCell? other)
            => Formula == other?.Formula && Equals(Value, other?.Value) && Type == other?.Type;
    }
}
