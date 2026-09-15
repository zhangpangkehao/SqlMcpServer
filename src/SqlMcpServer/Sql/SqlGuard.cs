using System.Text;

namespace SqlMcpServer.Sql;

/// <summary>
/// Enforces the read-only contract for every statement that leaves this server.
///
/// The check is deliberately lexical rather than regex based. A regex over raw
/// text can be defeated by hiding a keyword inside a string literal, a comment
/// or a bracket identifier. Here we first neutralise those regions, then scan
/// only the remaining "code" characters, so a write verb can neither be masked
/// by quoting nor smuggled in through a comment.
/// </summary>
public static class SqlGuard
{
    /// <summary>Outcome of validating a candidate statement.</summary>
    public readonly record struct Verdict(bool Allowed, string Sql, string? Reason)
    {
        public static Verdict Permit(string sql) => new(true, sql, null);
        public static Verdict Reject(string reason) => new(false, string.Empty, reason);
    }

    /// <summary>
    /// Statement classes that must never reach SQL Server. Matched on word
    /// boundaries against the de-quoted, de-commented body.
    /// </summary>
    private static readonly string[] ForbiddenKeywords =
    [
        // Data manipulation
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "UPSERT",
        // Schema definition
        "CREATE", "ALTER", "DROP",
        // Permissions
        "GRANT", "REVOKE", "DENY",
        // Code execution
        "EXEC", "EXECUTE", "CALL",
        // Server / database administration
        "BACKUP", "RESTORE", "SHUTDOWN", "RECONFIGURE", "CHECKPOINT",
        "KILL", "DBCC", "BULK",
        // External and file-system access
        "OPENROWSET", "OPENQUERY", "OPENDATASOURCE", "OPENXML",
        // Scheduling, persistence, locking hints
        "WAITFOR", "INTO",
    ];

    private const int MaxSqlLength = 100_000;

    public static Verdict Validate(string? rawSql)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
            return Verdict.Reject("SQL 语句为空。");

        if (rawSql.Length > MaxSqlLength)
            return Verdict.Reject($"SQL 语句过长（{rawSql.Length} 字符，上限 {MaxSqlLength} 字符）。");

        var code = StripLiteralsCommentsAndIdentifiers(rawSql, out string? lexError);
        if (lexError is not null)
            return Verdict.Reject(lexError);

        // Statement stacking: a semicolon may only terminate the whole batch.
        if (code.Contains(';'))
        {
            var trimmedBody = code.TrimEnd();
            bool singleTrailingSemicolon =
                trimmedBody.EndsWith(';') && !trimmedBody[..^1].Contains(';');

            if (!singleTrailingSemicolon)
                return Verdict.Reject("检测到多条语句（分号分隔）。本接口只允许执行单条只读查询。");

            code = trimmedBody[..^1];
        }

        code = code.Trim();
        if (code.Length == 0)
            return Verdict.Reject("SQL 语句为空。");

        // Name the concrete violation first. Telling the model "you used DELETE"
        // is far more actionable than a generic "must start with SELECT".
        string? forbidden = FindForbiddenKeyword(code);
        if (forbidden is not null)
            return Verdict.Reject($"检测到被禁止的关键字 {forbidden}。本接口禁止任何新增、删除、修改及结构变更操作。");

        if (!BeginsWithKeyword(code, "SELECT") && !BeginsWithKeyword(code, "WITH"))
            return Verdict.Reject("语句必须以 SELECT 或 WITH 开头。本接口只提供只读查询能力。");

        return Verdict.Permit(rawSql.Trim());
    }

    /// <summary>
    /// Replaces every string literal, bracket identifier, quoted identifier and
    /// comment with a neutral placeholder, leaving only structural SQL code.
    /// Semicolons inside neutralised regions cannot survive, which is what
    /// makes the statement-stacking check trustworthy.
    /// </summary>
    private static string StripLiteralsCommentsAndIdentifiers(string sql, out string? error)
    {
        error = null;
        var sb = new StringBuilder(sql.Length);
        int i = 0;
        int n = sql.Length;

        while (i < n)
        {
            char c = sql[i];

            // -- single line comment
            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                int nl = sql.IndexOf('\n', i);
                i = nl < 0 ? n : nl + 1;
                sb.Append(' ');
                continue;
            }

            // /* block comment */
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                int close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    error = "存在未闭合的块注释 '/*'，已拒绝执行。";
                    return sb.ToString();
                }
                i = close + 2;
                sb.Append(' ');
                continue;
            }

            // 'string literal'  ('' is an escaped quote)
            if (c == '\'')
            {
                int j = i + 1;
                while (true)
                {
                    if (j >= n)
                    {
                        error = "存在未闭合的字符串字面量，已拒绝执行。";
                        return sb.ToString();
                    }
                    if (sql[j] == '\'')
                    {
                        if (j + 1 < n && sql[j + 1] == '\'') { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                i = j + 1;
                sb.Append(" '' ");
                continue;
            }

            // [bracket identifier]
            if (c == '[')
            {
                int close = sql.IndexOf(']', i + 1);
                if (close < 0)
                {
                    error = "存在未闭合的方括号标识符 '['，已拒绝执行。";
                    return sb.ToString();
                }
                i = close + 1;
                sb.Append(" [] ");
                continue;
            }

            // "quoted identifier"
            if (c == '"')
            {
                int j = i + 1;
                while (true)
                {
                    if (j >= n)
                    {
                        error = "存在未闭合的双引号标识符，已拒绝执行。";
                        return sb.ToString();
                    }
                    if (sql[j] == '"')
                    {
                        if (j + 1 < n && sql[j + 1] == '"') { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                i = j + 1;
                sb.Append(" \"\" ");
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool BeginsWithKeyword(string code, string keyword)
    {
        if (code.Length < keyword.Length)
            return false;

        if (!code.AsSpan(0, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return false;

        return code.Length == keyword.Length || !IsIdentifierChar(code[keyword.Length]);
    }

    private static string? FindForbiddenKeyword(string code)
    {
        foreach (string keyword in ForbiddenKeywords)
        {
            if (IndexOfWord(code, keyword) >= 0)
                return keyword;
        }
        return null;
    }

    private static int IndexOfWord(string haystack, string word)
    {
        int start = 0;
        while (start <= haystack.Length - word.Length)
        {
            int idx = haystack.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return -1;

            bool leftBoundary = idx == 0 || !IsIdentifierChar(haystack[idx - 1]);
            int end = idx + word.Length;
            bool rightBoundary = end >= haystack.Length || !IsIdentifierChar(haystack[end]);

            if (leftBoundary && rightBoundary)
                return idx;

            start = idx + 1;
        }
        return -1;
    }

    private static bool IsIdentifierChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';
}
