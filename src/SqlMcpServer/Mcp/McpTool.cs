using System.Text.Json.Nodes;

namespace SqlMcpServer.Mcp;

/// <summary>
/// Payload returned by a tool: the text shown to the model, plus whether the
/// call should be flagged as an execution error (per MCP <c>isError</c>).
/// </summary>
internal readonly record struct ToolResponse(string Text, bool IsError = false);

/// <summary>
/// A single callable MCP tool. The <see cref="Invoke"/> delegate receives the
/// raw <c>arguments</c> object from <c>tools/call</c> and returns the text that
/// is handed back to the model.
/// </summary>
internal sealed class McpTool
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required JsonObject InputSchema { get; init; }
    public required Func<JsonObject, CancellationToken, Task<ToolResponse>> Invoke { get; init; }

    /// <summary>Serialised form used by <c>tools/list</c>.</summary>
    public JsonObject ToDefinition() => new()
    {
        ["name"] = Name,
        ["title"] = Title,
        ["description"] = Description,
        ["inputSchema"] = InputSchema.DeepClone(),
        ["annotations"] = new JsonObject
        {
            // Every tool behind this server is read-only; telling the client lets
            // it skip destructive-operation confirmations.
            ["readOnlyHint"] = true,
            ["destructiveHint"] = false,
            ["idempotentHint"] = true,
            ["openWorldHint"] = false,
        },
    };

    // ------------------------------------------------------------ schema sugar

    public static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();

        foreach (var (name, type, description, isRequired) in properties)
        {
            props[name] = new JsonObject
            {
                ["type"] = type,
                ["description"] = description,
            };
            if (isRequired)
                required.Add(name);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false,
        };

        if (required.Count > 0)
            schema["required"] = required;

        return schema;
    }
}
