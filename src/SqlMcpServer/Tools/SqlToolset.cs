using System.Text.Json.Nodes;
using SqlMcpServer.Config;
using SqlMcpServer.Mcp;
using SqlMcpServer.Sql;

namespace SqlMcpServer.Tools;

/// <summary>Definition and wiring of every tool this server exposes.</summary>
internal static class SqlToolset
{
    public static IReadOnlyList<McpTool> Build(SqlGateway gateway, ServerConfig config)
    {
        return
        [
            BuildQueryTool(gateway, config),
            BuildListDatabasesTool(gateway),
            BuildListTablesTool(gateway),
            BuildDescribeTableTool(gateway),
            BuildServerInfoTool(gateway),
        ];
    }

    // ------------------------------------------------------------- query tool

    private static McpTool BuildQueryTool(SqlGateway gateway, ServerConfig config) => new()
    {
        Name = "execute_readonly_query",
        Title = "执行只读 SQL 查询",
        Description =
            "对 SQL Server 执行一条**只读**查询并返回结果。仅允许单条 SELECT / WITH 语句。" +
            "任何写操作（INSERT/UPDATE/DELETE/MERGE/TRUNCATE）、结构变更（CREATE/ALTER/DROP）、" +
            "权限变更、EXEC 动态执行、分号拼接的多语句、SELECT INTO 以及 OPENROWSET 等外部访问都会被拒绝。" +
            "用于数据探查、统计与取数。",
        InputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["sql"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "要执行的 T-SQL 只读查询，单条语句，例如：SELECT TOP 10 * FROM dbo.Users",
                },
                ["maxRows"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = $"最大返回行数，默认 {config.MaxRows}，上限 10000。",
                    ["minimum"] = 1,
                    ["maximum"] = 10000,
                },
                ["format"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "结果呈现格式：markdown（默认，表格）、json、csv。",
                    ["enum"] = new JsonArray { "markdown", "json", "csv" },
                    ["default"] = "markdown",
                },
            },
            ["required"] = new JsonArray { "sql" },
            ["additionalProperties"] = false,
        },
        Invoke = async (arguments, ct) =>
        {
            string sql = ReadString(arguments, "sql") ?? string.Empty;
            int? maxRows = ReadInt(arguments, "maxRows");
            ResultFormat format = ParseFormat(ReadString(arguments, "format"));

            QueryResult result = await gateway.ExecuteReadOnlyAsync(sql, maxRows, ct).ConfigureAwait(false);
            return Wrap(result, format);
        },
    };

    // -------------------------------------------------------- schema browsing

    private static McpTool BuildListDatabasesTool(SqlGateway gateway) => new()
    {
        Name = "list_databases",
        Title = "列出数据库",
        Description = "列出当前 SQL Server 实例上的全部数据库，包含状态、恢复模式与创建时间。",
        InputSchema = McpTool.Schema(),
        Invoke = async (_, ct) =>
        {
            QueryResult result = await gateway.ListDatabasesAsync(ct).ConfigureAwait(false);
            return Wrap(result, ResultFormat.Markdown);
        },
    };

    private static McpTool BuildListTablesTool(SqlGateway gateway) => new()
    {
        Name = "list_tables",
        Title = "列出数据表",
        Description = "列出指定数据库中的用户表。database 省略时使用当前连接的数据库；schema 可用于过滤，例如 dbo。",
        InputSchema = McpTool.Schema(
            ("database", "string", "数据库名称，省略则使用当前连接的数据库。", false),
            ("schema", "string", "架构名称，例如 dbo，省略则返回全部架构。", false)),
        Invoke = async (arguments, ct) =>
        {
            QueryResult result = await gateway
                .ListTablesAsync(ReadString(arguments, "database"), ReadString(arguments, "schema"), ct)
                .ConfigureAwait(false);

            return Wrap(result, ResultFormat.Markdown);
        },
    };

    private static McpTool BuildDescribeTableTool(SqlGateway gateway) => new()
    {
        Name = "describe_table",
        Title = "查看表结构",
        Description = "返回指定表的列定义：列名、数据类型、长度、精度、是否可空与默认值。写查询前用它确认字段。",
        InputSchema = McpTool.Schema(
            ("table", "string", "表名，例如 Users。", true),
            ("schema", "string", "架构名，例如 dbo。省略则匹配任意架构。", false),
            ("database", "string", "数据库名称，省略则使用当前连接的数据库。", false)),
        Invoke = async (arguments, ct) =>
        {
            string table = ReadString(arguments, "table") ?? string.Empty;
            QueryResult result = await gateway
                .DescribeTableAsync(table, ReadString(arguments, "schema"), ReadString(arguments, "database"), ct)
                .ConfigureAwait(false);

            return Wrap(result, ResultFormat.Markdown);
        },
    };

    private static McpTool BuildServerInfoTool(SqlGateway gateway) => new()
    {
        Name = "get_server_info",
        Title = "查看服务器信息",
        Description = "返回 SQL Server 版本、当前数据库、登录账号与是否具备 sysadmin 角色，用于确认连接目标。",
        InputSchema = McpTool.Schema(),
        Invoke = async (_, ct) =>
        {
            QueryResult result = await gateway.GetServerInfoAsync(ct).ConfigureAwait(false);
            return Wrap(result, ResultFormat.Json);
        },
    };

    // ----------------------------------------------------------- instructions

    public static string BuildInstructions(ServerConfig config) =>
        $"""
        本 MCP 服务器提供对 SQL Server 数据库的**只读**访问，请严格遵守以下约定：

        1. 结构未知时，先探查再查询：list_databases → list_tables → describe_table，然后再写 SQL。
        2. 查询统一通过 execute_readonly_query 执行，只接受单条 SELECT / WITH 语句。
        3. INSERT、UPDATE、DELETE、MERGE、TRUNCATE、CREATE、ALTER、DROP、EXEC 以及分号拼接的多语句
           一律被拒绝。遇到拒绝不要尝试绕过（改注释、拆函数名、嵌套引号都会被识别），请改写为只读查询。
        4. 单次结果默认最多返回 {config.MaxRows} 行，可用 maxRows 上调至 10000。数据量大时请用聚合、
           TOP 或 WHERE 先收敛，避免拉取全表。
        5. 请用中文向用户解释结果；多行结果建议整理成表格，并点明关键结论。
        6. 若查询报错，读懂 SQL Server 的错误编号与消息后自行修正再试，必要时把错误原文反馈给用户。

        连接目标：{config.DescribeTarget()}
        """;

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Renders a result and flags rejected or failed calls as MCP execution
    /// errors, so a client can surface them distinctly from a normal answer.
    /// </summary>
    private static ToolResponse Wrap(QueryResult result, ResultFormat format) =>
        new(ResultFormatter.Render(result, format), IsError: !result.Success);

    private static string? ReadString(JsonObject arguments, string name)
    {
        if (!arguments.TryGetPropertyValue(name, out JsonNode? node) || node is null)
            return null;

        return node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }

    private static int? ReadInt(JsonObject arguments, string name)
    {
        if (!arguments.TryGetPropertyValue(name, out JsonNode? node) || node is null)
            return null;

        if (node is JsonValue value && value.TryGetValue(out int number))
            return number;

        if (node is JsonValue textValue && textValue.TryGetValue(out string? text) && int.TryParse(text, out int parsed))
            return parsed;

        return null;
    }

    private static ResultFormat ParseFormat(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "json" => ResultFormat.Json,
        "csv" => ResultFormat.Csv,
        _ => ResultFormat.Markdown,
    };
}
