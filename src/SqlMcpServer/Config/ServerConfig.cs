using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace SqlMcpServer.Config;

/// <summary>
/// Resolved SQL Server connection settings.
///
/// Precedence, lowest to highest:
///   1. built-in defaults
///   2. appsettings.json next to the executable  (<c>mssql</c> section)
///   3. environment variables (MSSQL_*)
///   4. command line switches (--server, --database, ...)
/// </summary>
public sealed class ServerConfig
{
    public const string FileName = "appsettings.json";

    [JsonPropertyName("server")] public string Server { get; set; } = "localhost";
    [JsonPropertyName("port")] public int? Port { get; set; }
    [JsonPropertyName("database")] public string? Database { get; set; }
    [JsonPropertyName("user")] public string? User { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("integratedSecurity")] public bool IntegratedSecurity { get; set; }
    [JsonPropertyName("encrypt")] public bool Encrypt { get; set; } = true;
    [JsonPropertyName("trustServerCertificate")] public bool TrustServerCertificate { get; set; } = true;
    [JsonPropertyName("connectionString")] public string? ConnectionString { get; set; }

    /// <summary>Hard cap on rows returned to the model per query.</summary>
    [JsonPropertyName("maxRows")] public int MaxRows { get; set; } = 200;

    [JsonPropertyName("commandTimeoutSeconds")] public int CommandTimeoutSeconds { get; set; } = 30;
    [JsonPropertyName("connectTimeoutSeconds")] public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>True when the server came from a file, an env var or the CLI.</summary>
    [JsonIgnore] public bool WasExplicitlyConfigured { get; private set; }

    public static string DefaultFilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static ServerConfig Load(string[] args)
    {
        bool hasFile = File.Exists(DefaultFilePath);
        ServerConfig cfg = (hasFile ? LoadFromFile(DefaultFilePath) : null) ?? new ServerConfig();
        cfg.WasExplicitlyConfigured = hasFile;

        cfg.ApplyEnvironment();
        cfg.ApplyArguments(args);
        return cfg;
    }

    private static ServerConfig? LoadFromFile(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            JsonElement node = doc.RootElement;

            if (node.ValueKind == JsonValueKind.Object &&
                node.TryGetProperty("mssql", out JsonElement section) &&
                section.ValueKind == JsonValueKind.Object)
            {
                node = section;
            }

            if (node.ValueKind != JsonValueKind.Object)
                return null;

            return JsonSerializer.Deserialize<ServerConfig>(node.GetRawText());
        }
        catch
        {
            // A malformed file must not take the server down; fall back to defaults
            // and let the interactive mode surface the problem to the user.
            return null;
        }
    }

    private void ApplyEnvironment()
    {
        string? s;
        if (!string.IsNullOrWhiteSpace(s = Get("MSSQL_CONNECTION_STRING"))) { ConnectionString = s; WasExplicitlyConfigured = true; }
        if (!string.IsNullOrWhiteSpace(s = Get("MSSQL_SERVER"))) { Server = s; WasExplicitlyConfigured = true; }
        if (int.TryParse(Get("MSSQL_PORT"), out int port)) { Port = port; WasExplicitlyConfigured = true; }
        if (!string.IsNullOrWhiteSpace(s = Get("MSSQL_DATABASE"))) { Database = s; WasExplicitlyConfigured = true; }
        if (!string.IsNullOrWhiteSpace(s = Get("MSSQL_USER"))) { User = s; WasExplicitlyConfigured = true; }
        if (Get("MSSQL_PASSWORD") is { } pwd) { Password = pwd; }
        if (TryBool(Get("MSSQL_INTEGRATED_SECURITY"), out bool integrated)) IntegratedSecurity = integrated;
        if (TryBool(Get("MSSQL_ENCRYPT"), out bool encrypt)) Encrypt = encrypt;
        if (TryBool(Get("MSSQL_TRUST_SERVER_CERTIFICATE"), out bool trust)) TrustServerCertificate = trust;
        if (int.TryParse(Get("MSSQL_MAX_ROWS"), out int maxRows) && maxRows > 0) MaxRows = maxRows;
        if (int.TryParse(Get("MSSQL_COMMAND_TIMEOUT"), out int cmdTimeout) && cmdTimeout > 0) CommandTimeoutSeconds = cmdTimeout;
        if (int.TryParse(Get("MSSQL_CONNECT_TIMEOUT"), out int connTimeout) && connTimeout > 0) ConnectTimeoutSeconds = connTimeout;
    }

    private void ApplyArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string? Value()
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    return args[++i];
                return null;
            }

            switch (key.ToLowerInvariant())
            {
                case "--server": case "-s":
                    if (Value() is { } sv) { Server = sv; WasExplicitlyConfigured = true; }
                    break;
                case "--port":
                    if (int.TryParse(Value(), out int p)) Port = p;
                    break;
                case "--database": case "--db": case "-d":
                    if (Value() is { } db) { Database = db; WasExplicitlyConfigured = true; }
                    break;
                case "--user": case "-u":
                    if (Value() is { } u) { User = u; WasExplicitlyConfigured = true; }
                    break;
                case "--password": case "-p":
                    if (Value() is { } pw) { Password = pw; WasExplicitlyConfigured = true; }
                    break;
                case "--integrated":
                    IntegratedSecurity = true;
                    WasExplicitlyConfigured = true;
                    break;
                case "--connection-string":
                    if (Value() is { } cs) { ConnectionString = cs; WasExplicitlyConfigured = true; }
                    break;
                case "--max-rows":
                    if (int.TryParse(Value(), out int mr) && mr > 0) MaxRows = mr;
                    break;
            }
        }
    }

    /// <summary>Builds the ADO.NET connection string actually handed to the driver.</summary>
    public string BuildConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            var provided = new SqlConnectionStringBuilder(ConnectionString)
            {
                ConnectTimeout = ConnectTimeoutSeconds,
                ApplicationName = "SqlMcpServer",
            };
            return provided.ConnectionString;
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Port is > 0 ? $"{Server},{Port}" : Server,
            ConnectTimeout = ConnectTimeoutSeconds,
            ApplicationName = "SqlMcpServer",
            Pooling = true,
            TrustServerCertificate = TrustServerCertificate,
            Encrypt = Encrypt
                ? SqlConnectionEncryptOption.Mandatory
                : SqlConnectionEncryptOption.Optional,
        };

        if (!string.IsNullOrWhiteSpace(Database))
            builder.InitialCatalog = Database;

        if (IntegratedSecurity)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = User ?? string.Empty;
            builder.Password = Password ?? string.Empty;
        }

        return builder.ConnectionString;
    }

    /// <summary>Human readable target, without secrets. Used by the interactive shell.</summary>
    public string DescribeTarget()
    {
        string dataSource = string.IsNullOrWhiteSpace(ConnectionString)
            ? (Port is > 0 ? $"{Server},{Port}" : Server)
            : new SqlConnectionStringBuilder(ConnectionString).DataSource;

        string auth = IntegratedSecurity
            ? "Windows 集成认证"
            : $"SQL 登录（{User ?? "(未设置)"}）";

        string database = string.IsNullOrWhiteSpace(ConnectionString)
            ? (string.IsNullOrWhiteSpace(Database) ? "(默认数据库)" : Database!)
            : (new SqlConnectionStringBuilder(ConnectionString).InitialCatalog is { Length: > 0 } c ? c : "(默认数据库)");

        return $"{dataSource}  数据库={database}  认证={auth}";
    }

    public void Save(string path)
    {
        var payload = new Dictionary<string, ServerConfig> { ["mssql"] = this };
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string? Get(string name) => Environment.GetEnvironmentVariable(name);

    private static bool TryBool(string? value, out bool result)
    {
        result = false;
        if (string.IsNullOrWhiteSpace(value)) return false;
        switch (value.Trim().ToLowerInvariant())
        {
            case "1": case "true": case "yes": case "on": result = true; return true;
            case "0": case "false": case "no": case "off": result = false; return true;
            default: return false;
        }
    }
}
