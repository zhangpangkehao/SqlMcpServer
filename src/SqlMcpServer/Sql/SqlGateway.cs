using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace SqlMcpServer.Sql;

/// <summary>
/// The single data-access entry point. Every statement that reaches the database
/// is built here, and every model supplied statement passes through
/// <see cref="SqlGuard"/> first.
/// </summary>
public sealed class SqlGateway
{
    private const int MaxCellChars = 4000;

    private readonly Config.ServerConfig _config;

    public SqlGateway(Config.ServerConfig config) => _config = config;

    // ---------------------------------------------------------------- queries

    /// <summary>Validates and runs a model supplied statement. Read-only only.</summary>
    public async Task<QueryResult> ExecuteReadOnlyAsync(string sql, int? maxRows, CancellationToken ct)
    {
        SqlGuard.Verdict verdict = SqlGuard.Validate(sql);
        if (!verdict.Allowed)
            return QueryResult.Fail(verdict.Reason!);

        int limit = maxRows is > 0 ? Math.Min(maxRows.Value, 10_000) : _config.MaxRows;
        return await RunAsync(verdict.Sql, limit, ct).ConfigureAwait(false);
    }

    public async Task<QueryResult> ListDatabasesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT
                d.name            AS database_name,
                d.database_id     AS database_id,
                d.state_desc      AS state,
                d.recovery_model_desc AS recovery_model,
                d.create_date     AS create_date,
                CASE WHEN d.database_id <= 4 THEN 1 ELSE 0 END AS is_system_database
            FROM sys.databases AS d
            ORDER BY d.name;
            """;

        return await RunAsync(sql, _config.MaxRows, ct).ConfigureAwait(false);
    }

    public async Task<QueryResult> ListTablesAsync(string? database, string? schema, CancellationToken ct)
    {
        string prefix = BuildDatabasePrefix(database);

        string filter = string.IsNullOrWhiteSpace(schema)
            ? string.Empty
            : "WHERE s.name = @schema";

        string sql = $"""
            SELECT
                s.name AS schema_name,
                t.name AS table_name,
                'BASE TABLE' AS table_type,
                t.create_date,
                t.modify_date
            FROM {prefix}sys.tables AS t
            INNER JOIN {prefix}sys.schemas AS s ON s.schema_id = t.schema_id
            {filter}
            ORDER BY s.name, t.name;
            """;

        return await RunAsync(sql, _config.MaxRows, ct, cmd =>
        {
            if (!string.IsNullOrWhiteSpace(schema))
                cmd.Parameters.AddWithValue("@schema", schema);
        }).ConfigureAwait(false);
    }

    public async Task<QueryResult> DescribeTableAsync(string table, string? schema, string? database, CancellationToken ct)
    {
        string prefix = BuildDatabasePrefix(database);

        string sql = $"""
            SELECT
                c.ORDINAL_POSITION     AS ordinal,
                c.COLUMN_NAME          AS column_name,
                c.DATA_TYPE            AS data_type,
                c.CHARACTER_MAXIMUM_LENGTH AS max_length,
                c.NUMERIC_PRECISION    AS numeric_precision,
                c.NUMERIC_SCALE        AS numeric_scale,
                c.IS_NULLABLE          AS is_nullable,
                c.COLUMN_DEFAULT       AS default_value
            FROM {prefix}INFORMATION_SCHEMA.COLUMNS AS c
            WHERE c.TABLE_NAME = @table
              AND (@schema IS NULL OR c.TABLE_SCHEMA = @schema)
            ORDER BY c.ORDINAL_POSITION;
            """;

        return await RunAsync(sql, _config.MaxRows, ct, cmd =>
        {
            cmd.Parameters.AddWithValue("@table", table);
            cmd.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
        }).ConfigureAwait(false);
    }

    public async Task<QueryResult> GetServerInfoAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT
                CAST(SERVERPROPERTY('ServerName')     AS nvarchar(256)) AS server_name,
                CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS product_version,
                CAST(SERVERPROPERTY('ProductLevel')   AS nvarchar(128)) AS product_level,
                CAST(SERVERPROPERTY('Edition')        AS nvarchar(256)) AS edition,
                DB_NAME()                                              AS current_database,
                SUSER_SNAME()                                          AS login_name,
                IS_SRVROLEMEMBER('sysadmin')                           AS is_sysadmin,
                @@VERSION                                              AS version_string;
            """;

        return await RunAsync(sql, 1, ct).ConfigureAwait(false);
    }

    /// <summary>Lightweight connectivity probe used by the interactive shell.</summary>
    public async Task<(bool Ok, string Message, long ElapsedMs)> TestConnectionAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new SqlConnection(_config.BuildConnectionString());
            await conn.OpenAsync(ct).ConfigureAwait(false);
            sw.Stop();
            return (true, $"连接成功：{conn.ServerVersion}（{conn.DataSource}/{conn.Database}）", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    // --------------------------------------------------------------- plumbing

    private async Task<QueryResult> RunAsync(
        string sql,
        int rowLimit,
        CancellationToken ct,
        Action<SqlCommand>? configure = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new SqlConnection(_config.BuildConnectionString());
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = new SqlCommand(sql, conn)
            {
                CommandTimeout = _config.CommandTimeoutSeconds,
                CommandType = System.Data.CommandType.Text,
            };
            configure?.Invoke(cmd);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            var columns = new List<string>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));

            var rows = new List<object?[]>(Math.Min(rowLimit, 256));
            bool truncated = false;

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (rows.Count >= rowLimit)
                {
                    truncated = true;
                    break;
                }

                var values = new object?[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    values[i] = await reader.IsDBNullAsync(i, ct).ConfigureAwait(false)
                        ? null
                        : TruncateCell(reader.GetValue(i));
                }
                rows.Add(values);
            }

            sw.Stop();
            string? note = truncated ? $"已达行数上限 {rowLimit}，请收紧 WHERE 条件或调大 maxRows" : null;
            return QueryResult.Ok(columns, rows, truncated, sw.ElapsedMilliseconds, note);
        }
        catch (OperationCanceledException)
        {
            return QueryResult.Fail("查询被取消或超时。");
        }
        catch (SqlException ex)
        {
            sw.Stop();
            string detail = string.Join(" | ", ex.Errors.Cast<SqlError>().Select(e => $"#{e.Number} {e.Message}"));
            string? hint = HintFor(ex);
            return QueryResult.Fail(hint is null
                ? $"SQL Server 返回错误：{detail}"
                : $"SQL Server 返回错误：{detail}\n排查建议：{hint}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return QueryResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Maps well known SQL Server error numbers to an actionable hint.</summary>
    private static string? HintFor(SqlException ex)
    {
        foreach (SqlError error in ex.Errors)
        {
            switch (error.Number)
            {
                case 233 or 232 or 10054 or 10061:
                    return "无法与 SQL Server 建立会话。请确认实例正在运行、已在配置管理器中启用 TCP/IP 协议，且端口（默认 1433）已放行。";
                case 53 or 40 or -2:
                    return "找不到服务器或实例。请检查 server 地址；命名实例需写成 主机名\\实例名，并确认 SQL Server Browser 服务已启动。";
                case 18456:
                    return "登录失败。请检查用户名与密码，并确认实例已启用 SQL Server 身份验证（混合模式）。";
                case 4060:
                    return "登录成功但无法打开目标数据库。请确认数据库名拼写正确，且该账号具备访问权限。";
                case 229 or 230:
                    return "权限不足。建议为该账号授予相应对象的 SELECT 权限，或加入 db_datareader 角色。";
                case 208:
                    return "对象不存在。请先用 list_tables / describe_table 确认数据库名、架构名与表名。";
                case 102 or 105 or 156:
                    return "语法错误。注意本接口只接受单条 SELECT / WITH 查询。";
            }
        }
        return null;
    }

    private static object? TruncateCell(object? value)
    {
        if (value is string s && s.Length > MaxCellChars)
            return string.Concat(s.AsSpan(0, MaxCellChars), $"…(已截断，原始长度 {s.Length})");
        if (value is byte[] b && b.Length > 256)
            return $"0x{Convert.ToHexString(b.AsSpan(0, 256))}…(已截断，原始长度 {b.Length} 字节)";
        return value;
    }

    private static string BuildDatabasePrefix(string? database)
    {
        if (string.IsNullOrWhiteSpace(database))
            return string.Empty;

        return QuoteIdentifier(database) + ".";
    }

    /// <summary>Wraps an identifier in brackets and neutralises any embedded bracket.</summary>
    private static string QuoteIdentifier(string identifier)
    {
        if (identifier.Length == 0 || identifier.Length > 128)
            throw new ArgumentException("数据库名称长度非法。", nameof(identifier));

        if (identifier.Any(char.IsControl))
            throw new ArgumentException("数据库名称包含控制字符。", nameof(identifier));

        return "[" + identifier.Replace("]", "]]") + "]";
    }

    /// <summary>Formats a table name for display purposes.</summary>
    public static string DisplayName(string schema, string table) =>
        string.IsNullOrWhiteSpace(schema) ? table : $"{schema}.{table}";
}
