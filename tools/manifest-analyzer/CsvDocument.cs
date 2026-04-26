using System.Text;

namespace ManifestAnalyzer;

internal sealed class CsvDocument
{
    private readonly bool _hasUtf8Bom;
    private readonly string _newLine;
    private readonly bool _hasTrailingNewLine;

    private CsvDocument(List<CsvRow> rows, bool hasUtf8Bom, string newLine, bool hasTrailingNewLine)
    {
        Rows = rows;
        _hasUtf8Bom = hasUtf8Bom;
        _newLine = newLine;
        _hasTrailingNewLine = hasTrailingNewLine;
    }

    public List<CsvRow> Rows { get; }

    public static CsvDocument Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"CSV file not found: {path}", path);

        var bytes = File.ReadAllBytes(path);
        var hasUtf8Bom = bytes.Length >= 3 &&
                         bytes[0] == 0xEF &&
                         bytes[1] == 0xBB &&
                         bytes[2] == 0xBF;

        var bomLength = hasUtf8Bom ? 3 : 0;
        var text = Encoding.UTF8.GetString(bytes, bomLength, bytes.Length - bomLength);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var hasTrailingNewLine = text.EndsWith("\r\n", StringComparison.Ordinal) || text.EndsWith('\n');

        return new CsvDocument(Parse(text), hasUtf8Bom, newLine, hasTrailingNewLine);
    }

    public int GetColumnIndex(string columnName)
    {
        if (Rows.Count == 0)
            throw new InvalidOperationException("CSV is empty.");

        if (TryGetColumnIndex(columnName, out var columnIndex))
            return columnIndex;

        throw new InvalidOperationException($"CSV is missing required column '{columnName}'.");
    }

    public bool TryGetColumnIndex(string columnName, out int columnIndex)
    {
        if (Rows.Count == 0)
            throw new InvalidOperationException("CSV is empty.");

        var header = Rows[0];
        for (var i = 0; i < header.Fields.Count; i++)
        {
            if (string.Equals(header.Fields[i].Value, columnName, StringComparison.OrdinalIgnoreCase))
            {
                columnIndex = i;
                return true;
            }
        }

        columnIndex = -1;
        return false;
    }

    public int GetOrAddColumn(string columnName)
    {
        if (TryGetColumnIndex(columnName, out var existingColumnIndex))
            return existingColumnIndex;

        if (Rows.Count == 0)
            throw new InvalidOperationException("CSV is empty.");

        Rows[0].Fields.Add(new CsvField(columnName, wasQuoted: false));
        for (var rowIndex = 1; rowIndex < Rows.Count; rowIndex++)
            Rows[rowIndex].Fields.Add(new CsvField(string.Empty, wasQuoted: false));

        return Rows[0].Fields.Count - 1;
    }

    public void Save(string path)
    {
        var builder = new StringBuilder();

        for (var rowIndex = 0; rowIndex < Rows.Count; rowIndex++)
        {
            var row = Rows[rowIndex];
            for (var fieldIndex = 0; fieldIndex < row.Fields.Count; fieldIndex++)
            {
                if (fieldIndex > 0)
                    builder.Append(',');

                builder.Append(SerializeField(row.Fields[fieldIndex]));
            }

            if (rowIndex < Rows.Count - 1 || _hasTrailingNewLine)
                builder.Append(_newLine);
        }

        var tempPath = path + ".tmp";
        var encoding = new UTF8Encoding(_hasUtf8Bom);
        File.WriteAllText(tempPath, builder.ToString(), encoding);
        File.Move(tempPath, path, overwrite: true);
    }

    private static List<CsvRow> Parse(string text)
    {
        var rows = new List<CsvRow>();
        var fields = new List<CsvField>();
        var valueBuilder = new StringBuilder();
        var inQuotes = false;
        var fieldWasQuoted = false;
        var atFieldStart = true;

        void CommitField()
        {
            fields.Add(new CsvField(valueBuilder.ToString(), fieldWasQuoted));
            valueBuilder.Clear();
            fieldWasQuoted = false;
            atFieldStart = true;
        }

        void CommitRow()
        {
            rows.Add(new CsvRow(new List<CsvField>(fields)));
            fields.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        valueBuilder.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    valueBuilder.Append(ch);
                }

                atFieldStart = false;
                continue;
            }

            if (atFieldStart && ch == '"')
            {
                inQuotes = true;
                fieldWasQuoted = true;
                atFieldStart = false;
                continue;
            }

            if (ch == ',')
            {
                CommitField();
                continue;
            }

            if (ch == '\r' || ch == '\n')
            {
                CommitField();
                CommitRow();

                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;

                continue;
            }

            valueBuilder.Append(ch);
            atFieldStart = false;
        }

        if (inQuotes)
            throw new InvalidOperationException("CSV ended while still inside a quoted field.");

        if (valueBuilder.Length > 0 || fieldWasQuoted || atFieldStart == false || fields.Count > 0 || rows.Count == 0)
        {
            CommitField();
            CommitRow();
        }

        return rows;
    }

    private static string SerializeField(CsvField field)
    {
        var requiresQuotes = field.WasQuoted ||
                             field.Value.IndexOfAny([',', '"', '\r', '\n']) >= 0;

        if (!requiresQuotes)
            return field.Value;

        return '"' + field.Value.Replace("\"", "\"\"") + '"';
    }
}

internal sealed class CsvRow(List<CsvField> fields)
{
    public List<CsvField> Fields { get; } = fields;
}

internal sealed class CsvField(string value, bool wasQuoted)
{
    public string Value { get; set; } = value;

    public bool WasQuoted { get; set; } = wasQuoted;
}
