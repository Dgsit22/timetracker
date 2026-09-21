using System.Text;

namespace TimeTracker.Server;

/// <summary>
/// CSV field encoding for the export handlers. Exported activity carries window titles and page
/// titles, which routinely contain commas, quotes and newlines - pasting those into a line
/// directly produces a file that silently loads with shifted columns.
/// </summary>
public static class Csv
{
    /// <summary>
    /// Excel reads a UTF-8 file without a BOM as the system codepage, which mangles every
    /// non-ASCII character in a window title.
    /// </summary>
    public static byte[] ToUtf8WithBom(StringBuilder csv) =>
        Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();

    public static string Line(params object?[] fields) =>
        string.Join(",", fields.Select(Field));

    public static string Field(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss"),
            double d => d.ToString("F0"),
            _ => value.ToString() ?? string.Empty,
        };

        if (text.Length == 0)
        {
            return string.Empty;
        }

        // A field opening with one of these is treated as a formula by Excel and Sheets, so a
        // window title someone chose could run on the machine of whoever opens the export. The
        // leading apostrophe makes the spreadsheet treat it as text; it is not part of the value.
        if (text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        if (text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
        {
            return '"' + text.Replace("\"", "\"\"") + '"';
        }

        return text;
    }
}
