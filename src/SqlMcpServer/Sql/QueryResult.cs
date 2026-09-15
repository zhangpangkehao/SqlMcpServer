using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SqlMcpServer.Sql;

/// <summary>Immutable outcome of one read-only statement.</summary>
public sealed class QueryResult
{
    public bool Success { get; private init; }
    public string? Error { get; private init; }
    public List<string> Columns { get; private init; } = [];
    public List<object?[]> Rows { get; private init; } = [];
    public bool Truncated { get; private init; }
    public long ElapsedMs { get; private init; }

    /// <summary>Optional note such as the number of rows available in total.</summary>
    public string? Note { get; private init; }

    public static QueryResult Ok(List<string> columns, List<object?[]> rows, bool truncated, long elapsedMs, string? note = null) =>
        new() { Success = true, Columns = columns, Rows = rows, Truncated = truncated, ElapsedMs = elapsedMs, Note = note };

    public static QueryResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public enum ResultFormat
{
    Markdown,
    Json,
    Csv,
}

/// <summary>Renders a <see cref="QueryResult"/> into a model friendly payload.</summary>
public static class ResultFormatter
{
    public static string Render(QueryResult result, ResultFormat format)
    {
        if (!result.Success)
            return $"查询被拒绝或执行失败：{result.Error}";

        var sb = new StringBuilder();

        sb.Append("[只读查询] 返回 ")
          .Append(result.Rows.Count.ToString(CultureInfo.InvariantCulture))
          .Append(" 行，耗时 ")
          .Append(result.ElapsedMs.ToString(CultureInfo.InvariantCulture))
          .Append(" ms");
        if (result.Truncated)
            sb.Append("（结果已按行数上限截断）");
        if (!string.IsNullOrEmpty(result.Note))
            sb.Append("。").Append(result.Note);
        sb.AppendLine("。").AppendLine();

        if (result.Columns.Count == 0 || result.Rows.Count == 0)
        {
            sb.AppendLine(result.Rows.Count == 0 ? "（结果集为空）" : "（无列）");
            return sb.ToString();
        }

        switch (format)
        {
            case ResultFormat.Json:
                sb.AppendLine(RenderJson(result));
                break;
            case ResultFormat.Csv:
                sb.AppendLine(RenderCsv(result));
                break;
            default:
                sb.AppendLine(RenderMarkdown(result));
                break;
        }

        return sb.ToString();
    }

    private static string RenderMarkdown(QueryResult result)
    {
        var sb = new StringBuilder();

        sb.Append('|');
        foreach (string c in result.Columns)
            sb.Append(' ').Append(EscapeMarkdown(c)).Append(" |");
        sb.AppendLine();

        sb.Append('|');
        foreach (string _ in result.Columns)
            sb.Append(" --- |");
        sb.AppendLine();

        foreach (object?[] row in result.Rows)
        {
            sb.Append('|');
            for (int i = 0; i < result.Columns.Count; i++)
            {
                sb.Append(' ').Append(EscapeMarkdown(Cell(row, i))).Append(" |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string RenderJson(QueryResult result)
    {
        var records = new List<Dictionary<string, object?>>(result.Rows.Count);
        foreach (object?[] row in result.Rows)
        {
            var record = new Dictionary<string, object?>(result.Columns.Count, StringComparer.Ordinal);
            for (int i = 0; i < result.Columns.Count; i++)
                record[result.Columns[i]] = Normalize(row, i);
            records.Add(record);
        }

        return JsonSerializer.Serialize(records, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static string RenderCsv(QueryResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", result.Columns.Select(Csv)));

        foreach (object?[] row in result.Rows)
        {
            var cells = new string[result.Columns.Count];
            for (int i = 0; i < result.Columns.Count; i++)
                cells[i] = Csv(Cell(row, i));
            sb.AppendLine(string.Join(",", cells));
        }

        return sb.ToString();
    }

    private static string Cell(object?[] row, int index) =>
        index < row.Length ? FormatCell(row[index]) : "NULL";

    private static object? Normalize(object?[] row, int index)
    {
        object? value = index < row.Length ? row[index] : null;
        if (value is null or DBNull) return null;
        if (value is byte[] bytes) return Convert.ToBase64String(bytes);
        if (value is DateTime dt) return dt.ToString("O", CultureInfo.InvariantCulture);
        if (value is DateTimeOffset dto) return dto.ToString("O", CultureInfo.InvariantCulture);
        return value;
    }

    private static string FormatCell(object? value)
    {
        return value switch
        {
            null or DBNull => "NULL",
            byte[] bytes => bytes.Length == 0 ? "0x" : $"0x{Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 32)))}" + (bytes.Length > 32 ? "…" : string.Empty),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "NULL",
        };
    }

    private static string EscapeMarkdown(string text) =>
        text.Replace("\\", "\\\\")
            .Replace("|", "\\|")
            .Replace("\r\n", "<br>")
            .Replace("\n", "<br>")
            .Replace("\r", "<br>");

    private static string Csv(string text)
    {
        if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        return text;
    }
}
