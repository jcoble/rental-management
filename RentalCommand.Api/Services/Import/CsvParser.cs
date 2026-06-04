namespace RentalCommand.Api.Services.Import;

/// <summary>
/// A small, dependency-free RFC 4180 CSV reader. Handles quoted fields, embedded commas,
/// embedded newlines (inside quotes), and escaped quotes (<c>""</c>). The first non-empty
/// line is treated as the header row; header names are matched case-insensitively and trimmed
/// of surrounding whitespace by callers. Values are returned verbatim (callers trim per field).
/// </summary>
internal static class CsvParser
{
    /// <summary>
    /// Parses the entire stream as UTF-8 CSV into a header + the data rows that follow it.
    /// Each data row is a string array aligned positionally to the header columns. Rows with
    /// fewer cells than the header are padded with empty strings; rows with more cells keep the
    /// extras (callers only read the columns they know). A trailing newline does not produce a
    /// spurious empty row, and fully-blank lines are skipped.
    /// </summary>
    /// <exception cref="CsvFormatException">The stream contains no header row.</exception>
    public static CsvTable Parse(string text)
    {
        var records = ParseRecords(text);
        if (records.Count == 0)
        {
            throw new CsvFormatException("The CSV file is empty — a header row is required.");
        }

        var header = records[0]
            .Select(h => h.Trim())
            .ToArray();

        var rows = new List<string[]>(records.Count - 1);
        for (var i = 1; i < records.Count; i++)
        {
            rows.Add(records[i]);
        }

        return new CsvTable(header, rows);
    }

    /// <summary>
    /// Low-level tokenizer: splits the text into records (rows) of raw fields per RFC 4180.
    /// Fully empty records (a blank line) are dropped so trailing newlines and stray blank lines
    /// don't become rows.
    /// </summary>
    private static List<string[]> ParseRecords(string text)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;
        var sawAnyField = false;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
            sawAnyField = true;
        }

        void EndRecord()
        {
            EndField();
            // Drop a record that is a single empty field (i.e. a blank line).
            if (!(fields.Count == 1 && fields[0].Length == 0))
            {
                records.Add(fields.ToArray());
            }
            fields.Clear();
            sawAnyField = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // Escaped quote ("") inside a quoted field → a single literal quote.
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    EndField();
                    break;
                case '\r':
                    // Treat CRLF as a single line terminator; a lone CR also ends the record.
                    EndRecord();
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }
                    break;
                case '\n':
                    EndRecord();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        // Flush the final record if the file does not end with a newline.
        if (field.Length > 0 || fields.Count > 0 || sawAnyField)
        {
            EndRecord();
        }

        return records;
    }
}

/// <summary>A parsed CSV: the trimmed header row plus the raw data rows that follow it.</summary>
internal sealed record CsvTable(string[] Header, IReadOnlyList<string[]> Rows);

/// <summary>Thrown when the CSV stream is structurally unusable (e.g. no header row).</summary>
public sealed class CsvFormatException(string message) : Exception(message);
