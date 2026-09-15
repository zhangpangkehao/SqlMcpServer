using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlMcpServer.Mcp;

/// <summary>JSON-RPC 2.0 helpers shared by the transport loop.</summary>
internal static class JsonRpc
{
    public const string Version = "2.0";

    // Standard JSON-RPC error codes.
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Server info and result sets contain Chinese text; do not \uXXXX escape it.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonNode? Parse(string line) => JsonNode.Parse(line);

    public static string Serialize(JsonNode node) => node.ToJsonString(SerializerOptions);

    public static JsonObject Result(JsonNode? id, JsonNode payload) => new()
    {
        ["jsonrpc"] = Version,
        ["id"] = id?.DeepClone(),
        ["result"] = payload,
    };

    public static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };
        if (data is not null)
            error["data"] = data;

        return new JsonObject
        {
            ["jsonrpc"] = Version,
            ["id"] = id?.DeepClone(),
            ["error"] = error,
        };
    }

    public static JsonObject CallToolResult(string text, bool isError = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = text,
        }),
        ["isError"] = isError,
    };

    public static JsonObject TextProperty(string text) => new() { ["type"] = "text", ["text"] = text };
}
