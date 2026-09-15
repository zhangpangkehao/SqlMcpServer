using System.Text;
using SqlMcpServer.Cli;
using SqlMcpServer.Config;
using SqlMcpServer.Mcp;
using SqlMcpServer.Sql;
using SqlMcpServer.Tools;

namespace SqlMcpServer;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Any(IsHelp)) { PrintHelp(); return 0; }
        if (args.Any(IsVersion)) { Console.WriteLine($"SqlMcpServer {McpServerHost.ServerVersion} (MCP {McpServerHost.DefaultProtocolVersion})"); return 0; }

        ServerConfig config = ServerConfig.Load(args);
        bool verbose = args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase))
                       || IsTruthy(Environment.GetEnvironmentVariable("MSSQL_DEBUG"));

        // ---- mode selection -------------------------------------------------
        //
        // An MCP client launches us as a child process with both stdin and
        // stdout connected to pipes. A user double clicking the exe has a
        // console on both. That difference is what picks the mode; --stdio and
        // --interactive exist as explicit overrides.

        bool forceStdio = args.Any(a => a.Equals("--stdio", StringComparison.OrdinalIgnoreCase));
        bool forceInteractive = args.Any(a => a.Equals("--interactive", StringComparison.OrdinalIgnoreCase));

        // Explicit one-shot commands win over transport auto-detection, so that
        // --test and --init keep working from a script whose stdout is a pipe.
        if (!forceStdio)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { /* legacy console */ }

            if (args.Any(a => a.Equals("--test", StringComparison.OrdinalIgnoreCase)))
                return await RunConnectionTestAsync(config).ConfigureAwait(false);

            if (args.Any(a => a.Equals("--init", StringComparison.OrdinalIgnoreCase)))
            {
                config.Save(ServerConfig.DefaultFilePath);
                Console.WriteLine($"已写入配置模板：{ServerConfig.DefaultFilePath}");
                return 0;
            }
        }

        // An MCP client launches us with both stdin and stdout on pipes, whereas
        // a user double clicking the exe still owns a console on both. That
        // difference is what selects the mode.
        bool stdioMode = forceInteractive
            ? false
            : forceStdio || (Console.IsInputRedirected && Console.IsOutputRedirected);

        if (stdioMode)
            return await RunStdioAsync(config, verbose).ConfigureAwait(false);

        var shell = new InteractiveShell(config);
        return await shell.RunAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<int> RunStdioAsync(ServerConfig config, bool verbose)
    {
        // stdout is reserved for JSON-RPC. Every diagnostic goes to stderr.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var input = new StreamReader(Console.OpenStandardInput(), utf8, detectEncodingFromByteOrderMarks: false);
        var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n", AutoFlush = false };
        var error = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };

        void Log(string message)
        {
            try { error.WriteLine($"[SqlMcpServer] {message}"); } catch { /* stderr gone */ }
        }

        if (!config.WasExplicitlyConfigured)
        {
            Log("warning: no explicit configuration detected; defaulting to localhost with Windows integrated security.");
            Log($"         set MSSQL_SERVER / MSSQL_DATABASE / MSSQL_USER / MSSQL_PASSWORD via the MCP client env block.");
        }

        using var cts = new CancellationTokenSource();

        try
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { output.Flush(); } catch { } };
        }
        catch { /* no console attached */ }

        var gateway = new SqlGateway(config);
        IReadOnlyList<McpTool> tools = SqlToolset.Build(gateway, config);

        var host = new McpServerHost(
            tools,
            input,
            output,
            () => SqlToolset.BuildInstructions(config),
            verbose ? Log : null);

        Log($"starting (pid {Environment.ProcessId}), {tools.Count} read-only tools registered, target: {config.DescribeTarget()}");

        try
        {
            return await host.RunAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"fatal: {ex}");
            return 1;
        }
    }

    private static async Task<int> RunConnectionTestAsync(ServerConfig config)
    {
        Console.WriteLine($"正在连接：{config.DescribeTarget()}");
        var gateway = new SqlGateway(config);
        var (ok, message, elapsed) = await gateway.TestConnectionAsync(CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine(ok
            ? $"[成功] {message}  耗时 {elapsed} ms"
            : $"[失败] {message}");

        return ok ? 0 : 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            SqlMcpServer v{McpServerHost.ServerVersion} — SQL Server 只读 MCP 服务器
            协议：Model Context Protocol {McpServerHost.DefaultProtocolVersion}（stdio 传输，JSON-RPC 2.0）

            用法：
              SqlMcpServer.exe                     启动交互式控制台（双击运行时自动进入）
              SqlMcpServer.exe --stdio             以 stdio 服务模式运行，供 AI 客户端调用
              SqlMcpServer.exe --interactive       强制进入交互式控制台
              SqlMcpServer.exe --test              测试数据库连接后退出
              SqlMcpServer.exe --init              在程序目录生成 appsettings.json 模板
              SqlMcpServer.exe --help              显示本帮助
              SqlMcpServer.exe --version           显示版本

            连接参数（优先级：命令行 > 环境变量 > appsettings.json > 默认值）：
              --server <host> --port <n> --database <db>
              --user <name>   --password <pwd>   --integrated
              --connection-string "<完整连接串>"  --max-rows <n>

            环境变量：
              MSSQL_SERVER  MSSQL_PORT  MSSQL_DATABASE  MSSQL_USER  MSSQL_PASSWORD
              MSSQL_INTEGRATED_SECURITY  MSSQL_ENCRYPT  MSSQL_TRUST_SERVER_CERTIFICATE
              MSSQL_MAX_ROWS  MSSQL_COMMAND_TIMEOUT  MSSQL_CONNECT_TIMEOUT
              MSSQL_CONNECTION_STRING  MSSQL_DEBUG=1

            安全说明：
              仅允许单条 SELECT / WITH 查询。INSERT/UPDATE/DELETE/DDL/EXEC/多语句/
              SELECT INTO/OPENROWSET 等一律在内核侧被拒绝，不依赖模型自觉。

            建议使用只读数据库账号（db_datareader）以形成第二道防线。
            """);
    }

    private static bool IsHelp(string arg) =>
        arg is "--help" or "-h" or "-?" or "/?" or "/help";

    private static bool IsVersion(string arg) =>
        arg is "--version" or "-v" or "/version";

    private static bool IsTruthy(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
