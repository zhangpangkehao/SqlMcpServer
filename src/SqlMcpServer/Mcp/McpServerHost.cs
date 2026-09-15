using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlMcpServer.Mcp;

/// <summary>
/// Minimal MCP server speaking JSON-RPC 2.0 over stdio.
///
/// Wire rules taken from the MCP specification (transports / stdio):
///   * messages are newline delimited and MUST NOT contain embedded newlines;
///   * stdout carries protocol traffic only - all diagnostics go to stderr;
///   * the client launches this process and closes stdin to shut it down.
/// </summary>
internal sealed class McpServerHost
{
    public const string ServerName = "sqlserver-mcp";
    public const string ServerVersion = "1.0.0";
    public const string DefaultProtocolVersion = "2025-06-18";

    private static readonly string[] SupportedProtocolVersions =
        ["2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly IReadOnlyList<McpTool> _tools;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Func<string> _instructions;
    private readonly Action<string> _log;

    public McpServerHost(
        IReadOnlyList<McpTool> tools,
        TextReader input,
        TextWriter output,
        Func<string> instructions,
        Action<string>? log = null)
    {
        _tools = tools;
        _input = input;
        _output = output;
        _instructions = instructions;
        _log = log ?? (_ => { });
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        _log("stdio transport ready, waiting for JSON-RPC on stdin");

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                _log($"stdin read faulted: {ex.Message}");
                break;
            }

            if (line is null)
                break; // client closed the pipe -> graceful shutdown

            if (line.Length == 0)
                continue;

            await DispatchAsync(line, cancellationToken).ConfigureAwait(false);
        }

        _log("stdin reached EOF, shutting down");
        return 0;
    }

    private async Task DispatchAsync(string line, CancellationToken cancellationToken)
    {
        JsonNode? root;
        try
        {
            root = JsonRpc.Parse(line);
        }
        catch (JsonException ex)
        {
            _log($"malformed JSON payload: {ex.Message}");
            await WriteAsync(JsonRpc.Error(null, JsonRpc.ParseError, "请求不是合法的 JSON。")).ConfigureAwait(false);
            return;
        }

        if (root is not JsonObject request)
        {
            await WriteAsync(JsonRpc.Error(null, JsonRpc.InvalidRequest, "JSON-RPC 请求必须是对象。")).ConfigureAwait(false);
            return;
        }

        bool hasId = request.ContainsKey("id");
        JsonNode? id = hasId ? request["id"] : null;
        string? method = (request["method"] as JsonValue)?.GetValue<string>();
        var parameters = request["params"] as JsonObject;

        if (string.IsNullOrEmpty(method))
        {
            if (hasId)
                await WriteAsync(JsonRpc.Error(id, JsonRpc.InvalidRequest, "请求缺少 method 字段。")).ConfigureAwait(false);
            return;
        }

        // Notifications never get a reply, per JSON-RPC 2.0.
        if (!hasId || method.StartsWith("notifications/", StringComparison.Ordinal))
        {
            _log($"notification received: {method}");
            return;
        }

        JsonNode response = method switch
        {
            "initialize" => JsonRpc.Result(id, HandleInitialize(parameters)),
            "ping" => JsonRpc.Result(id, new JsonObject()),
            "tools/list" => JsonRpc.Result(id, HandleToolsList()),
            "tools/call" => await HandleToolsCallAsync(id, parameters, cancellationToken).ConfigureAwait(false),
            // Declared capabilities do not include resources or prompts, but some
            // clients probe them anyway; answer with an empty set instead of an error.
            "resources/list" => JsonRpc.Result(id, new JsonObject { ["resources"] = new JsonArray() }),
            "prompts/list" => JsonRpc.Result(id, new JsonObject { ["prompts"] = new JsonArray() }),
            _ => JsonRpc.Error(id, JsonRpc.MethodNotFound, $"不支持的方法：{method}"),
        };

        await WriteAsync(response).ConfigureAwait(false);
    }

    private JsonObject HandleInitialize(JsonObject? parameters)
    {
        string requested = (parameters?["protocolVersion"] as JsonValue)?.GetValue<string>()
                           ?? DefaultProtocolVersion;

        // Spec: echo the client version when supported, otherwise answer with ours.
        string negotiated = Array.IndexOf(SupportedProtocolVersions, requested) >= 0
            ? requested
            : DefaultProtocolVersion;

        var clientInfo = parameters?["clientInfo"] as JsonObject;
        string clientName = (clientInfo?["name"] as JsonValue)?.GetValue<string>() ?? "unknown";
        string clientVersion = (clientInfo?["version"] as JsonValue)?.GetValue<string>() ?? "unknown";

        _log($"initialize from {clientName} {clientVersion} (requested {requested} -> {negotiated})");

        return new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["title"] = "SQL Server 只读查询",
                ["version"] = ServerVersion,
            },
            ["instructions"] = _instructions(),
        };
    }

    private JsonObject HandleToolsList()
    {
        var array = new JsonArray();
        foreach (McpTool tool in _tools)
            array.Add(tool.ToDefinition());

        return new JsonObject { ["tools"] = array };
    }

    private async Task<JsonNode> HandleToolsCallAsync(JsonNode? id, JsonObject? parameters, CancellationToken cancellationToken)
    {
        string? name = (parameters?["name"] as JsonValue)?.GetValue<string>();
        if (string.IsNullOrEmpty(name))
            return JsonRpc.Error(id, JsonRpc.InvalidParams, "tools/call 缺少 name 参数。");

        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();

        McpTool? tool = _tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (tool is null)
            return JsonRpc.Error(id, JsonRpc.InvalidParams, $"未知的工具：{name}");

        _log($"tools/call {name}");

        try
        {
            ToolResponse response = await tool.Invoke(arguments, cancellationToken).ConfigureAwait(false);
            return JsonRpc.Result(id, JsonRpc.CallToolResult(response.Text, response.IsError));
        }
        catch (OperationCanceledException)
        {
            return JsonRpc.Result(id, JsonRpc.CallToolResult("工具执行已取消。", isError: true));
        }
        catch (Exception ex)
        {
            _log($"tool {name} failed: {ex}");
            return JsonRpc.Result(id, JsonRpc.CallToolResult($"工具执行失败：{ex.Message}", isError: true));
        }
    }

    private async Task WriteAsync(JsonNode response)
    {
        await _output.WriteLineAsync(JsonRpc.Serialize(response)).ConfigureAwait(false);
        await _output.FlushAsync().ConfigureAwait(false);
    }
}
