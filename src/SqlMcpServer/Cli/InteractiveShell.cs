using System.Text;
using System.Text.Json;
using SqlMcpServer.Config;
using SqlMcpServer.Mcp;
using SqlMcpServer.Sql;
using SqlMcpServer.Tools;

namespace SqlMcpServer.Cli;

/// <summary>
/// Friendly console shown when the executable is started directly (double click)
/// instead of being launched by an MCP client through a pipe.
/// </summary>
internal sealed class InteractiveShell
{
    private ServerConfig _config;
    private SqlGateway _gateway;
    private readonly IReadOnlyList<McpTool> _toolPreview;

    public InteractiveShell(ServerConfig config)
    {
        _config = config;
        _gateway = new SqlGateway(config);
        _toolPreview = SqlToolset.Build(_gateway, config);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        PrintBanner();

        while (true)
        {
            PrintMenu();
            Console.Write("请选择 > ");
            string? choice = Console.ReadLine()?.Trim();

            switch (choice)
            {
                case "1":
                    await TestConnectionAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "2":
                    Configure();
                    break;
                case "3":
                    await RunQueryAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "4":
                    PrintClientConfiguration();
                    break;
                case "5":
                    PrintTools();
                    break;
                case "0":
                case "q":
                case "quit":
                case null:
                    Console.WriteLine();
                    Console.WriteLine("已退出。把本程序配置进 AI 客户端的 MCP 设置即可长期使用。");
                    return 0;
                default:
                    Console.WriteLine("无效选项，请输入 0-5。");
                    break;
            }
        }
    }

    private void PrintBanner()
    {
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine($"  SqlMcpServer v{McpServerHost.ServerVersion}  -  SQL Server 只读 MCP 服务器");
        Console.WriteLine("============================================================");
        Console.WriteLine("  本程序以 stdio(标准输入输出) 方式为 AI 客户端提供只读");
        Console.WriteLine("  SQL 查询能力。被 AI 客户端拉起时会自动进入服务模式；");
        Console.WriteLine("  手动双击运行时显示本控制台，用于配置与连通性自检。");
        Console.WriteLine();
        Console.WriteLine($"  当前连接：{_config.DescribeTarget()}");
        Console.WriteLine($"  配置文件：{ServerConfig.DefaultFilePath}（{(_config.WasExplicitlyConfigured ? "已存在" : "尚未创建")}）");
        Console.WriteLine($"  协议版本：{McpServerHost.DefaultProtocolVersion}");
        Console.WriteLine("  安全边界：仅允许单条 SELECT / WITH，禁写、禁 DDL、禁多语句。");
        Console.WriteLine("============================================================");
    }

    private void PrintMenu()
    {
        Console.WriteLine();
        Console.WriteLine("  [1] 测试数据库连接");
        Console.WriteLine("  [2] 配置数据库连接（写入 appsettings.json）");
        Console.WriteLine("  [3] 执行只读查询（交互式自测）");
        Console.WriteLine("  [4] 生成 MCP 客户端配置片段");
        Console.WriteLine("  [5] 查看可用工具");
        Console.WriteLine("  [0] 退出");
    }

    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("正在连接……");
        var (ok, message, elapsed) = await _gateway.TestConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (ok)
        {
            WriteColor(ConsoleColor.Green, $"[成功] {message}  耗时 {elapsed} ms");
        }
        else
        {
            WriteColor(ConsoleColor.Red, $"[失败] {message}");
            Console.WriteLine();
            Console.WriteLine("排查建议：");
            Console.WriteLine("  * 确认 SQL Server 已启动，且启用了 TCP/IP 协议（SQL Server 配置管理器）。");
            Console.WriteLine("  * 确认实例端口（默认 1433）与 Windows 防火墙已放行。");
            Console.WriteLine("  * 若使用 SQL 登录，确认已启用混合身份验证，且账号具备登录权限。");
            Console.WriteLine("  * 使用自签名证书时，请保持 trustServerCertificate = true。");
        }
    }

    private void Configure()
    {
        Console.WriteLine();
        Console.WriteLine("按回车保留方括号中的当前值。");

        string server = Ask("服务器地址", _config.Server);
        int? port = AskInt("端口", _config.Port ?? 1433);

        Console.Write($"认证方式  1=SQL Server 登录  2=Windows 集成认证  [{( _config.IntegratedSecurity ? 2 : 1)}]: ");
        string auth = Console.ReadLine()?.Trim() ?? string.Empty;
        bool integrated = auth == "2" || (auth.Length == 0 && _config.IntegratedSecurity);

        string? user = _config.User;
        string? password = _config.Password;
        if (!integrated)
        {
            user = Ask("用户名", _config.User ?? "sa");
            password = AskPassword("密码");
        }

        string database = Ask("数据库名", _config.Database ?? "master");

        var updated = new ServerConfig
        {
            Server = server,
            Port = port,
            Database = string.IsNullOrWhiteSpace(database) ? null : database,
            IntegratedSecurity = integrated,
            User = integrated ? null : user,
            Password = integrated ? null : password,
            Encrypt = _config.Encrypt,
            TrustServerCertificate = _config.TrustServerCertificate,
            MaxRows = _config.MaxRows,
            CommandTimeoutSeconds = _config.CommandTimeoutSeconds,
            ConnectTimeoutSeconds = _config.ConnectTimeoutSeconds,
        };

        string path = ServerConfig.DefaultFilePath;
        try
        {
            updated.Save(path);
            _config = ServerConfig.Load(Array.Empty<string>());
            _gateway = new SqlGateway(_config);
            WriteColor(ConsoleColor.Green, $"配置已保存到：{path}");
            Console.WriteLine("提示：该文件可能包含明文密码，请注意文件访问权限，不要提交到版本库。");
            Console.WriteLine($"新的连接目标：{_config.DescribeTarget()}");
        }
        catch (Exception ex)
        {
            WriteColor(ConsoleColor.Red, $"保存失败：{ex.Message}");
        }
    }

    private async Task RunQueryAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("请输入单条 SELECT / WITH 查询（输入空行取消）：");
        Console.Write("SQL > ");
        string? sql = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(sql))
        {
            Console.WriteLine("已取消。");
            return;
        }

        QueryResult result = await _gateway.ExecuteReadOnlyAsync(sql, null, cancellationToken).ConfigureAwait(false);

        if (result.Success)
            WriteColor(ConsoleColor.Green, $"执行成功，{result.Rows.Count} 行，{result.ElapsedMs} ms。");
        else
            WriteColor(ConsoleColor.Yellow, $"未执行：{result.Error}");

        Console.WriteLine();
        Console.WriteLine(ResultFormatter.Render(result, ResultFormat.Markdown));
    }

    private void PrintClientConfiguration()
    {
        string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SqlMcpServer.exe");

        var env = new Dictionary<string, string>
        {
            ["MSSQL_SERVER"] = _config.Server,
        };

        if (_config.Port is > 0) env["MSSQL_PORT"] = _config.Port.Value.ToString();
        if (!string.IsNullOrWhiteSpace(_config.Database)) env["MSSQL_DATABASE"] = _config.Database!;
        if (_config.IntegratedSecurity)
        {
            env["MSSQL_INTEGRATED_SECURITY"] = "true";
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(_config.User)) env["MSSQL_USER"] = _config.User!;
            if (!string.IsNullOrWhiteSpace(_config.Password)) env["MSSQL_PASSWORD"] = _config.Password!;
        }
        env["MSSQL_TRUST_SERVER_CERTIFICATE"] = _config.TrustServerCertificate ? "true" : "false";

        var payload = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["sqlserver"] = new Dictionary<string, object>
                {
                    ["command"] = exePath,
                    ["args"] = Array.Empty<string>(),
                    ["env"] = env,
                },
            },
        };

        Console.WriteLine();
        Console.WriteLine("把下面这段 JSON 合并进 AI 客户端的 MCP 配置即可：");
        Console.WriteLine();
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        Console.WriteLine();
        Console.WriteLine("常见位置：");
        Console.WriteLine("  WorkBuddy / CodeBuddy : ~/.workbuddy/mcp.json");
        Console.WriteLine("  Claude Desktop (Win)  : %APPDATA%\\Claude\\claude_desktop_config.json");
        Console.WriteLine("  Cursor                : %USERPROFILE%\\.cursor\\mcp.json");
        Console.WriteLine("  VS Code               : .vscode/mcp.json（工作区级）");
    }

    private void PrintTools()
    {
        Console.WriteLine();
        Console.WriteLine("本服务器注册的工具：");
        Console.WriteLine();
        foreach (McpTool tool in _toolPreview)
        {
            Console.WriteLine($"  - {tool.Name}");
            Console.WriteLine($"      {tool.Title}");
            Console.WriteLine($"      {tool.Description}");
            Console.WriteLine();
        }
    }

    // ------------------------------------------------------------- primitives

    private static string Ask(string label, string fallback)
    {
        Console.Write($"{label} [{fallback}]: ");
        string? input = Console.ReadLine()?.Trim();
        return string.IsNullOrWhiteSpace(input) ? fallback : input;
    }

    private static int? AskInt(string label, int? fallback)
    {
        Console.Write($"{label} [{fallback?.ToString() ?? "无"}]: ");
        string? input = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(input)) return fallback;
        return int.TryParse(input, out int value) ? value : fallback;
    }

    private static string? AskPassword(string label)
    {
        Console.Write($"{label}: ");
        var buffer = new StringBuilder();

        try
        {
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0) buffer.Length--;
                    continue;
                }
                if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
            }
            Console.WriteLine();
        }
        catch
        {
            // Terminals without ReadKey support (redirected input) fall back to a plain read.
            string? plain = Console.ReadLine();
            return plain;
        }

        return buffer.ToString();
    }

    private static void WriteColor(ConsoleColor color, string text)
    {
        try
        {
            ConsoleColor previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = previous;
        }
        catch
        {
            Console.WriteLine(text);
        }
    }
}
