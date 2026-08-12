using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataverseMcpServer;

// Config
record Config(string TenantId, string ClientId, string ClientSecret, string EnvironmentUrl, string ApiVersion = "v9.2");

// Auth token cache
class TokenCache
{
    public string? Token { get; set; }
    public long ExpiresAt { get; set; }
}

// MCP Protocol types
class McpMessage
{
    public object? Id { get; set; }
    public string? Method { get; set; }
    public JsonObject? Params { get; set; }
}

class JsonRpcResponse
{
    public string Jsonrpc { get; set; } = "2.0";
    public required object Id { get; set; }
    public JsonObject? Result { get; set; }
    public JsonObject? Error { get; set; }
}

class Program
{
    // JSON-RPC is camelCase on the wire; C# properties are PascalCase.
    static readonly JsonSerializerOptions Rpc = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    static async Task Main(string[] args)
    {
        if (args.Contains("--selftest")) { SelfTest(); return; }

        // ponytail: speed bump, not security — anyone with the binary can patch this out
        if (Environment.GetEnvironmentVariable("MCP_TOOL_KEY") != "16012003")
        {
            Console.Error.WriteLine("nice try 🙃");
            return;
        }

        var config = LoadConfig();
        var tokenCache = new TokenCache();

        // MCP protocol over stdio
        using var reader = Console.OpenStandardInput();
        using var stream = new StreamReader(reader);

        while (true)
        {
            var line = await stream.ReadLineAsync();
            if (line == null) break;

            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var msg = JsonSerializer.Deserialize<McpMessage>(line, Rpc);
                if (msg?.Method == null || msg.Id == null)
                {
                    // Response or notification (no id) - never answer, it's protocol noise
                    continue;
                }

                var response = await HandleRequest(msg, config, tokenCache);
                var responseJson = JsonSerializer.Serialize(response, Rpc);
                Console.WriteLine(responseJson);
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                var error = new JsonRpcResponse
                {
                    Id = new JsonObject(),
                    Error = new JsonObject
                    {
                        ["code"] = -32603,
                        ["message"] = ex.Message
                    }
                };
                Console.WriteLine(JsonSerializer.Serialize(error, Rpc));
                Console.Out.Flush();
            }
        }
    }

    // dotnet run -- --selftest
    static void SelfTest()
    {
        var ops = JsonNode.Parse("""
            [
              {"method":"create","entitySet":"accounts","data":{"name":"A"}},
              {"method":"update","entitySet":"accounts","id":"{11111111-1111-1111-1111-111111111111}","data":{"name":"B"}},
              {"method":"delete","entitySet":"contacts","id":"22222222-2222-2222-2222-222222222222"}
            ]
            """)!.AsArray();

        var body = DataverseClient.BuildBatchBody(ops, "batch_1", "changeset_1", "https://x.crm.dynamics.com/api/data/v9.2");

        Assert(body.StartsWith("--batch_1\r\nContent-Type: multipart/mixed;boundary=changeset_1"), "batch header");
        Assert(body.EndsWith("--changeset_1--\r\n--batch_1--\r\n"), "closing boundaries");
        Assert(body.Contains("POST https://x.crm.dynamics.com/api/data/v9.2/accounts HTTP/1.1"), "create verb+url");
        Assert(body.Contains("PATCH https://x.crm.dynamics.com/api/data/v9.2/accounts(11111111-1111-1111-1111-111111111111) HTTP/1.1"), "update strips braces");
        Assert(body.Contains("DELETE https://x.crm.dynamics.com/api/data/v9.2/contacts(22222222-2222-2222-2222-222222222222) HTTP/1.1"), "delete verb+url");
        Assert(body.Contains("If-Match: *"), "update guards against upsert");
        Assert(body.Contains("Content-ID: 1") && body.Contains("Content-ID: 3"), "content ids");
        Assert(body.Split("--changeset_1\r\n").Length == 4, "one part per operation");
        Assert(!body.Contains("\n\r\n\r"), "CRLF only");

        // delete carries no body, create/update do
        var deletePart = body[body.IndexOf("DELETE ")..];
        Assert(!deletePart.Contains("{"), "delete has no json body");

        Assert(Throws(() => DataverseClient.BuildBatchBody(
            JsonNode.Parse("""[{"method":"update","entitySet":"accounts","data":{}}]""")!.AsArray(), "b", "c", "u")), "update without id rejected");
        Assert(Throws(() => DataverseClient.BuildBatchBody(
            JsonNode.Parse("""[{"method":"upsert","entitySet":"accounts"}]""")!.AsArray(), "b", "c", "u")), "unknown method rejected");

        // query url
        Assert(DataverseClient.BuildQueryUrl("accounts", null, null, null, null, null, null, null, null) == "accounts", "bare query");
        Assert(DataverseClient.BuildQueryUrl("accounts", "revenue gt 5", "name", "10", "name asc", "primarycontactid($select=fullname)", "20", null, null)
            == "accounts?$filter=revenue%20gt%205&$select=name&$expand=primarycontactid($select=fullname)&$orderby=name asc&$skip=20&$top=10", "full query");
        Assert(DataverseClient.BuildQueryUrl("accounts", "revenue gt 5", "name", "10", null, null, null, null, "<fetch/>")
            == "accounts?fetchXml=%3Cfetch%2F%3E", "fetchXml wins over odata options");

        // execute paths
        Assert(DataverseClient.BuildExecutePath("WhoAmI", null, null, null, true) == "WhoAmI()", "unbound function");
        Assert(DataverseClient.BuildExecutePath("new_DoThing", null, null, null, false) == "new_DoThing", "unbound action keeps params in body");
        Assert(DataverseClient.BuildExecutePath("Assign", "accounts", "{11111111-1111-1111-1111-111111111111}", null, false)
            == "accounts(11111111-1111-1111-1111-111111111111)/Microsoft.Dynamics.CRM.Assign", "bound action");
        Assert(DataverseClient.BuildExecutePath("GetX", null, null,
            JsonNode.Parse("""{"Name":"O'Brien","Count":3}""")!.AsObject(), true)
            == "GetX(Name=@p1,Count=@p2)?@p1=%27O%27%27Brien%27&@p2=3", "function params aliased, strings quoted and escaped");

        Console.WriteLine("selftest ok");
    }

    static void Assert(bool ok, string what)
    {
        if (!ok) throw new Exception($"selftest failed: {what}");
    }

    static bool Throws(Action a)
    {
        try { a(); return false; } catch { return true; }
    }

    static Config CreateConfigInteractively(string targetPath)
    {
        Console.Error.WriteLine("=== Dataverse MCP Server - First Time Setup ===");
        Console.Error.WriteLine();
        Console.Error.WriteLine("You need to create an Azure AD App Registration.");
        Console.Error.WriteLine("Quick guide:");
        Console.Error.WriteLine("  1. Go to https://portal.azure.com");
        Console.Error.WriteLine("  2. Azure AD > App registrations > New registration");
        Console.Error.WriteLine("  3. Name: any (e.g. 'Dataverse MCP')");
        Console.Error.WriteLine("  4. Supported account types: Accounts in this organizational directory only");
        Console.Error.WriteLine("  5. Register");
        Console.Error.WriteLine("  6. Copy: Application (client) ID, Directory (tenant) ID");
        Console.Error.WriteLine("  7. Certificates & secrets > New client secret");
        Console.Error.WriteLine("  8. Copy the secret value (you won't see it again!)");
        Console.Error.WriteLine("  9. In Power Platform admin center:");
        Console.Error.WriteLine("     - Environment > Settings > Users + permissions > Application users");
        Console.Error.WriteLine("     - New app user > pick your app > assign security role");
        Console.Error.WriteLine();

        Console.Error.Write("Tenant ID: ");
        var tenantId = Console.ReadLine()?.Trim();
        Console.Error.Write("Client ID: ");
        var clientId = Console.ReadLine()?.Trim();
        Console.Error.Write("Client Secret: ");
        var clientSecret = Console.ReadLine()?.Trim();
        Console.Error.Write("Environment URL (e.g. https://yourorg.crm.dynamics.com): ");
        var envUrl = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) ||
            string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(envUrl))
        {
            throw new Exception("All fields are required.");
        }

        var config = new Config(tenantId, clientId, clientSecret, envUrl, "v9.2");

        // Create directory and save config
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(targetPath, json);

        Console.Error.WriteLine();
        Console.Error.WriteLine($"Config saved to: {targetPath}");
        Console.Error.WriteLine("You're all set!");
        Console.Error.WriteLine();

        return config;
    }

    static Config LoadConfig()
    {
        // Try current directory, then home directory
        var configPaths = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "dataverse.config.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dataverse", "dataverse.config.json")
        };

        string? configPath = null;
        foreach (var path in configPaths)
        {
            if (File.Exists(path))
            {
                configPath = path;
                break;
            }
        }

        if (configPath == null)
        {
            return CreateConfigInteractively(configPaths[1]);
        }

        var json = File.ReadAllText(configPath);
        var node = JsonNode.Parse(json);
        var root = node?.AsObject();

        return new Config(
            TenantId: root?["tenantId"]?.GetValue<string>() ?? throw new Exception("Missing tenantId"),
            ClientId: root?["clientId"]?.GetValue<string>() ?? throw new Exception("Missing clientId"),
            ClientSecret: root?["clientSecret"]?.GetValue<string>() ?? throw new Exception("Missing clientSecret"),
            EnvironmentUrl: root?["environmentUrl"]?.GetValue<string>() ?? throw new Exception("Missing environmentUrl"),
            ApiVersion: root?["apiVersion"]?.GetValue<string>() ?? "v9.2"
        );
    }

    static async Task<JsonRpcResponse> HandleRequest(McpMessage msg, Config config, TokenCache tokenCache)
    {
        return msg.Method switch
        {
            "initialize" => new JsonRpcResponse
            {
                Id = msg.Id ?? new JsonObject(),
                Result = new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "dataverse-mcp-server",
                        ["version"] = "1.0.0"
                    },
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject
                        {
                            ["listChanged"] = false
                        }
                    }
                }
            },
            "tools/list" => new JsonRpcResponse
            {
                Id = msg.Id ?? new JsonObject(),
                Result = new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        Tool("whoami", "Verify authentication works"),
                        Tool("list_entities", "List entities (pattern optional)", "pattern"),
                        Tool("entity_info", "Get entity definition", "logicalName"),
                        Tool("attributes", "List all attributes for an entity", "logicalName"),
                        Tool("optionset", "Get option set values", "entity", "attribute"),
                        Tool("relationships", "Get 1:N, N:1, M:N relationships", "logicalName"),
                        Tool("query", "Query records with OData. all='true' follows paging. fetchXml overrides the other options",
                            "entitySet", "filter", "select", "top", "orderby", "expand", "skip", "apply", "fetchXml", "pageSize", "all"),
                        Tool("retrieve", "Get one record. id = guid, or an alternate key like \"name='Contoso'\"", "entitySet", "id", "select", "expand"),
                        Tool("execute", "Call any Dataverse action or function (Assign, SetState, WinOpportunity, Merge, GrantAccess, custom API...). kind='action' (POST, default) or 'function' (GET). Pass entitySet+id for bound messages",
                            "name", "kind", "entitySet", "id", "parameters:object"),
                        Tool("audit", "Query audit logs", "objectid", "objecttypecode", "top"),
                        Tool("audit_changedata", "Get audit change details", "objectid", "auditid", "top"),
                        Tool("create", "Create a record. data = object of attribute:value. returnRecord='true' returns the created row", "entitySet", "data:object", "returnRecord"),
                        Tool("update", "Update a record. data = object of attribute:value. returnRecord='true' returns the updated row", "entitySet", "id", "data:object", "returnRecord"),
                        Tool("delete", "Delete a record", "entitySet", "id"),
                        Tool("associate", "Link two records via a relationship", "entitySet", "id", "relationship", "targetEntitySet", "targetId"),
                        Tool("disassociate", "Unlink records; targetId only for collection-valued relationships", "entitySet", "id", "relationship", "targetId"),
                        Tool("batch", "Run create/update/delete ops in one atomic changeset. operations = [{method,entitySet,id,data}]", "operations:array"),
                        Tool("metadata", "Raw metadata Web API call - the escape hatch for schema authoring. method=get|post|patch|delete, path e.g. EntityDefinitions, EntityDefinitions(LogicalName='account')/Attributes, GlobalOptionSetDefinitions, RelationshipDefinitions. solution = unique name to add the change to",
                            "method", "path", "data:object", "solution"),
                        Tool("publish", "Publish customizations. entities = comma-separated logical names; omit to publish everything", "entities"),
                        Tool("solution_export", "Export a solution to a .zip on disk", "uniqueName", "path", "managed"),
                        Tool("solution_import", "Import a solution .zip from disk. Returns importJobId - poll the importjobs table for progress", "path", "overwrite", "publishWorkflows")
                    }
                }
            },
            "tools/call" => await CallTool(msg, config, tokenCache),
            _ => new JsonRpcResponse
            {
                Id = msg.Id ?? new JsonObject(),
                Error = new JsonObject
                {
                    ["code"] = -32601,
                    ["message"] = $"Method not found: {msg.Method}"
                }
            }
        };
    }

    static JsonObject Tool(string name, string description, params string[] paramNames)
    {
        var properties = new JsonObject();
        foreach (var p in paramNames)
        {
            // "name:object" declares a JSON object param; everything else is a string
            var parts = p.Split(':');
            properties[parts[0]] = new JsonObject { ["type"] = parts.Length > 1 ? parts[1] : "string" };
        }

        // every call can run as another user (systemuserid) instead of the app user
        properties["impersonate"] = new JsonObject { ["type"] = "string" };

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            // ponytail: every param is an optional string; tools validate required ones at call time
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties
            }
        };
    }

    static async Task<JsonRpcResponse> CallTool(McpMessage msg, Config config, TokenCache tokenCache)
    {
        var args = msg.Params;
        var name = args?["name"]?.GetValue<string>();
        var arguments = args?["arguments"]?.AsObject();

        if (name == null || arguments == null)
        {
            return new JsonRpcResponse
            {
                Id = msg.Id ?? new JsonObject(),
                Error = new JsonObject
                {
                    ["code"] = -32602,
                    ["message"] = "Invalid tool call"
                }
            };
        }

        try
        {
            var result = await ExecuteTool(name, arguments, config, tokenCache);
            return new JsonRpcResponse
            {
                Id = msg.Id ?? new JsonObject(),
                Result = new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                        }
                    }
                }
            };
        }
        catch (Exception ex)
        {
            return new JsonRpcResponse
            {
                Id = msg.Id ?? new JsonObject(),
                Error = new JsonObject
                {
                    ["code"] = -32603,
                    ["message"] = ex.Message
                }
            };
        }
    }

    static async Task<object> ExecuteTool(string name, JsonObject args, Config config, TokenCache tokenCache)
    {
        var api = new DataverseClient(config, tokenCache, GetArg(args, "impersonate"));

        return name switch
        {
            "whoami" => await api.WhoAmI(),
            "list_entities" => await api.ListEntities(GetArg(args, "pattern")),
            "entity_info" => await api.EntityInfo(GetArgRequired(args, "logicalName")),
            "attributes" => await api.Attributes(GetArgRequired(args, "logicalName")),
            "optionset" => await api.OptionSet(GetArgRequired(args, "entity"), GetArgRequired(args, "attribute")),
            "relationships" => await api.Relationships(GetArgRequired(args, "logicalName")),
            "query" => await api.Query(GetArgRequired(args, "entitySet"), GetArg(args, "filter"), GetArg(args, "select"),
                GetArg(args, "top"), GetArg(args, "orderby"), GetArg(args, "expand"), GetArg(args, "skip"),
                GetArg(args, "apply"), GetArg(args, "fetchXml"), GetArg(args, "pageSize"), GetArg(args, "all") == "true"),
            "retrieve" => await api.Retrieve(GetArgRequired(args, "entitySet"), GetArgRequired(args, "id"), GetArg(args, "select"), GetArg(args, "expand")),
            "execute" => await api.Execute(GetArgRequired(args, "name"), GetArg(args, "kind"), GetArg(args, "entitySet"),
                GetArg(args, "id"), GetObject(args, "parameters")),
            "audit" => await api.Audit(GetArg(args, "objectid"), GetArg(args, "objecttypecode"), GetArg(args, "top")),
            "audit_changedata" => await api.AuditChangeData(GetArgRequired(args, "objectid"), GetArg(args, "auditid"), GetArg(args, "top")),
            "create" => await api.Create(GetArgRequired(args, "entitySet"), GetData(args), GetArg(args, "returnRecord") == "true"),
            "update" => await api.Update(GetArgRequired(args, "entitySet"), GetArgRequired(args, "id"), GetData(args), GetArg(args, "returnRecord") == "true"),
            "delete" => await api.Delete(GetArgRequired(args, "entitySet"), GetArgRequired(args, "id")),
            "associate" => await api.Associate(GetArgRequired(args, "entitySet"), GetArgRequired(args, "id"), GetArgRequired(args, "relationship"), GetArgRequired(args, "targetEntitySet"), GetArgRequired(args, "targetId")),
            "disassociate" => await api.Disassociate(GetArgRequired(args, "entitySet"), GetArgRequired(args, "id"), GetArgRequired(args, "relationship"), GetArg(args, "targetId")),
            "batch" => await api.Batch(GetOperations(args)),
            "metadata" => await api.Metadata(GetArgRequired(args, "method"), GetArgRequired(args, "path"), GetObject(args, "data"), GetArg(args, "solution")),
            "publish" => await api.Publish(GetArg(args, "entities")),
            "solution_export" => await api.SolutionExport(GetArgRequired(args, "uniqueName"), GetArgRequired(args, "path"), GetArg(args, "managed") == "true"),
            "solution_import" => await api.SolutionImport(GetArgRequired(args, "path"), GetArg(args, "overwrite") == "true", GetArg(args, "publishWorkflows") == "true"),
            _ => throw new Exception($"Unknown tool: {name}")
        };
    }

    static string? GetArg(JsonObject args, string name) =>
        args.ContainsKey(name) && args[name] != null ? args[name]!.GetValue<string>() : null;

    // "data" arrives as an object, or as a JSON string from clients that stringify it
    static JsonObject GetData(JsonObject args)
    {
        var node = args["data"] ?? throw new Exception("Missing required parameter: data");
        var obj = node as JsonObject ?? JsonNode.Parse(node.GetValue<string>()) as JsonObject;
        if (obj == null || obj.Count == 0) throw new Exception("data must be a non-empty object");
        return obj;
    }

    // optional object param; same string-or-object tolerance as GetData
    static JsonObject? GetObject(JsonObject args, string name)
    {
        var node = args[name];
        if (node == null) return null;
        return node as JsonObject ?? JsonNode.Parse(node.GetValue<string>()) as JsonObject
            ?? throw new Exception($"{name} must be an object");
    }

    static JsonArray GetOperations(JsonObject args)
    {
        var node = args["operations"] ?? throw new Exception("Missing required parameter: operations");
        return node as JsonArray ?? JsonNode.Parse(node.GetValue<string>()) as JsonArray
            ?? throw new Exception("operations must be an array");
    }

    static string GetArgRequired(JsonObject args, string name) =>
        GetArg(args, name) ?? throw new Exception($"Missing required parameter: {name}");
}

// Dataverse API Client
class DataverseClient
{
    private readonly Config _config;
    private readonly TokenCache _tokenCache;
    private readonly string? _impersonate;
    // solution import runs synchronously and can take minutes; the 100s default kills it
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public DataverseClient(Config config, TokenCache tokenCache, string? impersonate = null)
    {
        _config = config;
        _tokenCache = tokenCache;
        _impersonate = impersonate;
    }

    private async Task<string> GetToken()
    {
        if (_tokenCache.Token != null && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < _tokenCache.ExpiresAt - 60000)
        {
            return _tokenCache.Token;
        }

        var tokenUrl = $"https://login.microsoftonline.com/{_config.TenantId}/oauth2/v2.0/token";
        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _config.ClientId,
            ["client_secret"] = _config.ClientSecret,
            ["scope"] = $"{_config.EnvironmentUrl}/.default"
        };

        var response = await _http.PostAsync(tokenUrl, new FormUrlEncodedContent(body));
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Token request failed: {response.StatusCode} {content}");
        }

        var json = JsonNode.Parse(content);
        _tokenCache.Token = json?["access_token"]?.GetValue<string>();
        _tokenCache.ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (json?["expires_in"]?.GetValue<int>() ?? 3600) * 1000;

        return _tokenCache.Token!;
    }

    private async Task<JsonNode> Fetch(string path) =>
        (await Send(HttpMethod.Get, path, null)).Body!;

    private async Task<(JsonNode? Body, string Raw, HttpResponseMessage Response)> Send(
        HttpMethod method, string path, JsonObject? body, bool mustExist = false,
        bool returnRecord = false, HttpContent? rawContent = null,
        string? extraPrefer = null, string? solution = null, bool mergeLabels = false)
    {
        var token = await GetToken();
        var url = path.StartsWith("http") ? path : $"{_config.EnvironmentUrl}/api/data/{_config.ApiVersion}/{path}";
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Authorization", $"Bearer {token}");
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Add("Accept", "application/json");
        var prefer = "odata.include-annotations=\"*\"";
        if (returnRecord) prefer += ",return=representation";
        if (extraPrefer != null) prefer += "," + extraPrefer;
        request.Headers.Add("Prefer", prefer);
        // without If-Match, a PATCH to a missing id silently upserts a new record
        if (mustExist) request.Headers.Add("If-Match", "*");
        if (_impersonate != null) request.Headers.Add("MSCRMCallerID", _impersonate);
        if (solution != null) request.Headers.Add("MSCRM.SolutionUniqueName", solution);
        // metadata PATCH fails on existing labels unless it is told to merge them
        if (mergeLabels) request.Headers.Add("MSCRM.MergeLabels", "true");
        if (rawContent != null)
            request.Content = rawContent;
        else if (body != null)
            request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Dataverse HTTP {response.StatusCode} for {url}: {content}");
        }

        // $batch answers multipart/mixed, not JSON - callers read Raw for that
        var isJson = content.TrimStart().StartsWith('{') || content.TrimStart().StartsWith('[');
        return (isJson ? JsonNode.Parse(content) : null, content, response);
    }

    public async Task<object> WhoAmI()
    {
        var data = await Fetch("WhoAmI");
        return new
        {
            userId = data["UserId"]?.GetValue<string>(),
            businessUnitId = data["BusinessUnitId"]?.GetValue<string>(),
            organizationId = data["OrganizationId"]?.GetValue<string>()
        };
    }

    public async Task<object> ListEntities(string? pattern)
    {
        var data = await Fetch($"EntityDefinitions?$select=LogicalName,SchemaName,DisplayName,EntitySetName,ObjectTypeCode,IsAuditEnabled,IsCustomEntity");
        var lower = pattern?.ToLowerInvariant();

        var entities = (data["value"]?.AsArray().Where(e =>
        {
            var name = e["LogicalName"]?.GetValue<string>();
            return name != null && (lower == null || name.ToLowerInvariant().Contains(lower));
        }).Select(e => new
        {
            logicalName = e["LogicalName"]?.GetValue<string>(),
            entitySetName = e["EntitySetName"]?.GetValue<string>(),
            schemaName = e["SchemaName"]?.GetValue<string>(),
            displayName = e["DisplayName"]?["UserLocalizedLabel"]?["Label"]?.GetValue<string>(),
            objectTypeCode = e["ObjectTypeCode"]?.GetValue<int>(),
            isAuditEnabled = e["IsAuditEnabled"]?["Value"]?.GetValue<bool>(),
            isCustom = e["IsCustomEntity"]?.GetValue<bool>()
        }).OrderBy(e => e.logicalName).ToList())!;

        return new { pattern, count = entities.Count, entities };
    }

    public async Task<object> EntityInfo(string logicalName)
    {
        var data = await Fetch($"EntityDefinitions(LogicalName='{logicalName}')?$select=LogicalName,SchemaName,DisplayName,ObjectTypeCode,EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute,IsAuditEnabled,IsCustomEntity,IsActivity,OwnershipType");

        return new
        {
            logicalName = data["LogicalName"]?.GetValue<string>(),
            schemaName = data["SchemaName"]?.GetValue<string>(),
            displayName = data["DisplayName"]?["UserLocalizedLabel"]?["Label"]?.GetValue<string>(),
            entitySetName = data["EntitySetName"]?.GetValue<string>(),
            objectTypeCode = data["ObjectTypeCode"]?.GetValue<int>(),
            primaryIdAttribute = data["PrimaryIdAttribute"]?.GetValue<string>(),
            primaryNameAttribute = data["PrimaryNameAttribute"]?.GetValue<string>(),
            isAuditEnabled = data["IsAuditEnabled"]?["Value"]?.GetValue<bool>(),
            isCustomEntity = data["IsCustomEntity"]?.GetValue<bool>(),
            isActivity = data["IsActivity"]?.GetValue<bool>(),
            ownershipType = data["OwnershipType"]?.GetValue<string>()
        };
    }

    public async Task<object> Attributes(string logicalName)
    {
        var data = await Fetch($"EntityDefinitions(LogicalName='{logicalName}')/Attributes?$select=LogicalName,SchemaName,AttributeType,DisplayName,IsAuditEnabled,IsCustomAttribute,IsValidForCreate,IsValidForUpdate,RequiredLevel");

        var attrs = data["value"]?.AsArray().Select(a => new
        {
            logicalName = a["LogicalName"]?.GetValue<string>(),
            schemaName = a["SchemaName"]?.GetValue<string>(),
            attributeType = a["AttributeType"]?.GetValue<string>(),
            displayName = a["DisplayName"]?["UserLocalizedLabel"]?["Label"]?.GetValue<string>(),
            isAuditEnabled = a["IsAuditEnabled"]?["Value"]?.GetValue<bool>(),
            isCustom = a["IsCustomAttribute"]?.GetValue<bool>(),
            requiredLevel = a["RequiredLevel"]?["Value"]?.GetValue<string>()
        }).OrderBy(a => a.logicalName).ToList();

        return new { entity = logicalName, count = attrs.Count, attributes = attrs };
    }

    public async Task<object> OptionSet(string entity, string attribute)
    {
        var types = new[] { "PicklistAttributeMetadata", "StateAttributeMetadata", "StatusAttributeMetadata" };
        JsonNode? data = null;
        string? foundType = null;

        foreach (var t in types)
        {
            try
            {
                data = await Fetch($"EntityDefinitions(LogicalName='{entity}')/Attributes(LogicalName='{attribute}')/Microsoft.Dynamics.CRM.{t}?$expand=OptionSet,GlobalOptionSet");
                foundType = t;
                break;
            }
            catch { }
        }

        if (data == null)
        {
            throw new Exception($"Could not retrieve option set for {entity}.{attribute}");
        }

        var optionSet = data["OptionSet"] ?? data["GlobalOptionSet"];
        var options = optionSet?["Options"]?.AsArray().Select(o => new
        {
            value = o["Value"]?.GetValue<int>(),
            label = o["Label"]?["UserLocalizedLabel"]?["Label"]?.GetValue<string>(),
            description = o["Description"]?["UserLocalizedLabel"]?["Label"]?.GetValue<string>()
        }).ToList();

        return new { entity, attribute, attributeType = foundType, options };
    }

    public async Task<object> Relationships(string logicalName)
    {
        var oneToMany = await Fetch($"EntityDefinitions(LogicalName='{logicalName}')/OneToManyRelationships?$select=SchemaName,ReferencingEntity,ReferencingAttribute,ReferencedAttribute,ReferencedEntity,IsCustomRelationship");
        var manyToOne = await Fetch($"EntityDefinitions(LogicalName='{logicalName}')/ManyToOneRelationships?$select=SchemaName,ReferencingEntity,ReferencingAttribute,ReferencedAttribute,ReferencedEntity,IsCustomRelationship");
        var manyToMany = await Fetch($"EntityDefinitions(LogicalName='{logicalName}')/ManyToManyRelationships?$select=SchemaName,Entity1LogicalName,Entity2LogicalName,IntersectEntityName,IsCustomRelationship");

        return new
        {
            entity = logicalName,
            oneToMany = oneToMany["value"]?.AsArray().Select(r => new
            {
                schemaName = r["SchemaName"]?.GetValue<string>(),
                referencingEntity = r["ReferencingEntity"]?.GetValue<string>(),
                referencingAttribute = r["ReferencingAttribute"]?.GetValue<string>(),
                referencedEntity = r["ReferencedEntity"]?.GetValue<string>(),
                referencedAttribute = r["ReferencedAttribute"]?.GetValue<string>(),
                isCustom = r["IsCustomRelationship"]?.GetValue<bool>()
            }).ToList(),
            manyToOne = manyToOne["value"]?.AsArray().Select(r => new
            {
                schemaName = r["SchemaName"]?.GetValue<string>(),
                referencingEntity = r["ReferencingEntity"]?.GetValue<string>(),
                referencingAttribute = r["ReferencingAttribute"]?.GetValue<string>(),
                referencedEntity = r["ReferencedEntity"]?.GetValue<string>(),
                referencedAttribute = r["ReferencedAttribute"]?.GetValue<string>(),
                isCustom = r["IsCustomRelationship"]?.GetValue<bool>()
            }).ToList(),
            manyToMany = manyToMany["value"]?.AsArray().Select(r => new
            {
                schemaName = r["SchemaName"]?.GetValue<string>(),
                entity1 = r["Entity1LogicalName"]?.GetValue<string>(),
                entity2 = r["Entity2LogicalName"]?.GetValue<string>(),
                intersectEntity = r["IntersectEntityName"]?.GetValue<string>(),
                isCustom = r["IsCustomRelationship"]?.GetValue<bool>()
            }).ToList()
        };
    }

    // ponytail: hard stop on all='true' so a runaway table cannot page forever
    const int MaxPages = 20;

    public static string BuildQueryUrl(string entitySet, string? filter, string? select, string? top,
        string? orderby, string? expand, string? skip, string? apply, string? fetchXml)
    {
        // FetchXML carries its own filter/select/order, so it replaces the OData options
        if (!string.IsNullOrEmpty(fetchXml))
            return $"{entitySet}?fetchXml={Uri.EscapeDataString(fetchXml)}";

        var p = new List<string>();
        if (!string.IsNullOrEmpty(apply)) p.Add($"$apply={apply}");
        if (!string.IsNullOrEmpty(filter)) p.Add($"$filter={Uri.EscapeDataString(filter)}");
        if (!string.IsNullOrEmpty(select)) p.Add($"$select={select}");
        if (!string.IsNullOrEmpty(expand)) p.Add($"$expand={expand}");
        if (!string.IsNullOrEmpty(orderby)) p.Add($"$orderby={orderby}");
        if (!string.IsNullOrEmpty(skip)) p.Add($"$skip={skip}");
        if (!string.IsNullOrEmpty(top)) p.Add($"$top={top}");

        return p.Count > 0 ? $"{entitySet}?{string.Join("&", p)}" : entitySet;
    }

    public async Task<object> Query(string entitySet, string? filter, string? select, string? top,
        string? orderby, string? expand, string? skip, string? apply, string? fetchXml, string? pageSize, bool all)
    {
        var prefer = string.IsNullOrEmpty(pageSize) ? null : $"odata.maxpagesize={pageSize}";
        var records = new JsonArray();
        var pages = 0;
        var truncated = false;
        string? next = BuildQueryUrl(entitySet, filter, select, top, orderby, expand, skip, apply, fetchXml);

        while (next != null)
        {
            var (body, _, _) = await Send(HttpMethod.Get, next, null, extraPrefer: prefer);
            foreach (var r in body?["value"]?.AsArray() ?? new JsonArray())
            {
                // a JsonNode has one parent, so it has to be detached before it moves lists
                if (r != null) records.Add(JsonNode.Parse(r.ToJsonString()));
            }

            next = body?["@odata.nextLink"]?.GetValue<string>();
            if (!all) break;
            if (next != null && ++pages >= MaxPages) { truncated = true; break; }
        }

        return new
        {
            entitySet,
            filter,
            select,
            top,
            count = records.Count,
            records,
            hasMore = next != null,
            truncated,
            note = truncated ? $"stopped after {MaxPages} pages - narrow the filter or use skip" : null
        };
    }

    public async Task<object> Retrieve(string entitySet, string id, string? select, string? expand)
    {
        var p = new List<string>();
        if (!string.IsNullOrEmpty(select)) p.Add($"$select={select}");
        if (!string.IsNullOrEmpty(expand)) p.Add($"$expand={expand}");
        var query = p.Count > 0 ? "?" + string.Join("&", p) : "";

        var record = await Fetch($"{entitySet}({Key(id)}){query}");
        return new { entitySet, id, record };
    }

    // A bound message hangs off a record and needs its full type name; unbound ones (incl. custom APIs) do not.
    public static string BuildExecutePath(string name, string? entitySet, string? id, JsonObject? parameters, bool isFunction)
    {
        var bound = !string.IsNullOrEmpty(entitySet) && !string.IsNullOrEmpty(id);
        var path = bound ? $"{entitySet}({Key(id!)})/Microsoft.Dynamics.CRM.{name}" : name;

        // actions take their parameters in the POST body; functions take them in the URL
        if (!isFunction) return path;
        if (parameters == null || parameters.Count == 0) return path + "()";

        var names = new List<string>();
        var aliases = new List<string>();
        var i = 0;
        foreach (var kv in parameters)
        {
            var alias = $"@p{++i}";
            names.Add($"{kv.Key}={alias}");
            aliases.Add($"{alias}={Uri.EscapeDataString(FunctionLiteral(kv.Value))}");
        }

        return $"{path}({string.Join(",", names)})?{string.Join("&", aliases)}";
    }

    // URL parameter values are OData literals: strings quoted, everything else raw JSON
    static string FunctionLiteral(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
            return $"'{s.Replace("'", "''")}'";
        return node?.ToJsonString() ?? "null";
    }

    public async Task<object> Execute(string name, string? kind, string? entitySet, string? id, JsonObject? parameters)
    {
        var isFunction = string.Equals(kind, "function", StringComparison.OrdinalIgnoreCase);
        var path = BuildExecutePath(name, entitySet, id, parameters, isFunction);

        var (body, raw, response) = isFunction
            ? await Send(HttpMethod.Get, path, null)
            : await Send(HttpMethod.Post, path, parameters ?? new JsonObject());

        return new
        {
            message = name,
            kind = isFunction ? "function" : "action",
            path,
            status = (int)response.StatusCode,
            // 204 No Content is the normal answer for an action that returns nothing
            result = body ?? (string.IsNullOrWhiteSpace(raw) ? null : (object)raw)
        };
    }

    public async Task<object> Metadata(string method, string path, JsonObject? data, string? solution)
    {
        var verb = method.ToLowerInvariant() switch
        {
            "get" => HttpMethod.Get,
            "post" => HttpMethod.Post,
            "patch" => HttpMethod.Patch,
            "delete" => HttpMethod.Delete,
            _ => throw new Exception($"method must be get|post|patch|delete, got: {method}")
        };
        if ((verb == HttpMethod.Post || verb == HttpMethod.Patch) && data == null)
            throw new Exception($"{method} needs data");

        var (body, _, response) = await Send(verb, path, data,
            solution: solution, mergeLabels: verb == HttpMethod.Patch);

        var entityId = response.Headers.TryGetValues("OData-EntityId", out var v) ? v.FirstOrDefault() : null;
        return new { method, path, solution, status = (int)response.StatusCode, uri = entityId, result = body };
    }

    public async Task<object> Publish(string? entities)
    {
        if (string.IsNullOrWhiteSpace(entities))
        {
            await Send(HttpMethod.Post, "PublishAllXml", new JsonObject());
            return new { published = "all" };
        }

        var names = entities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var xml = "<importexportxml><entities>"
            + string.Join("", names.Select(n => $"<entity>{n}</entity>"))
            + "</entities></importexportxml>";

        await Send(HttpMethod.Post, "PublishXml", new JsonObject { ["ParameterXml"] = xml });
        return new { published = names };
    }

    public async Task<object> SolutionExport(string uniqueName, string path, bool managed)
    {
        var (body, _, _) = await Send(HttpMethod.Post, "ExportSolution", new JsonObject
        {
            ["SolutionName"] = uniqueName,
            ["Managed"] = managed
        });

        var base64 = body?["ExportSolutionFile"]?.GetValue<string>()
            ?? throw new Exception("ExportSolution returned no file");
        var bytes = Convert.FromBase64String(base64);
        await File.WriteAllBytesAsync(path, bytes);

        return new { uniqueName, managed, path, bytes = bytes.Length };
    }

    public async Task<object> SolutionImport(string path, bool overwrite, bool publishWorkflows)
    {
        if (!File.Exists(path)) throw new Exception($"File not found: {path}");
        var importJobId = Guid.NewGuid();

        await Send(HttpMethod.Post, "ImportSolution", new JsonObject
        {
            ["OverwriteUnmanagedCustomizations"] = overwrite,
            ["PublishWorkflows"] = publishWorkflows,
            ["CustomizationFile"] = Convert.ToBase64String(await File.ReadAllBytesAsync(path)),
            ["ImportJobId"] = importJobId.ToString()
        });

        return new { path, imported = true, importJobId, poll = $"importjobs({importJobId})" };
    }

    public async Task<object> Audit(string? objectid, string? objecttypecode, string? top)
    {
        var filters = new List<string>();
        if (!string.IsNullOrEmpty(objectid)) filters.Add($"_objectid_value eq {objectid}");
        if (!string.IsNullOrEmpty(objecttypecode)) filters.Add($"objecttypecode eq '{objecttypecode}'");

        var filterPart = filters.Count > 0 ? $"&$filter={Uri.EscapeDataString(string.Join(" and ", filters))}" : "";
        var topValue = top ?? "20";

        var data = await Fetch($"audits?$select=auditid,objecttypecode,_objectid_value,_userid_value,createdon,operation,action,changedata&$orderby=createdon desc&$top={topValue}{filterPart}");

        var records = data["value"]?.AsArray().Select(a => new
        {
            auditid = a["auditid"]?.GetValue<string>(),
            objecttypecode = a["objecttypecode"]?.GetValue<string>(),
            objectId = a["_objectid_value"]?.GetValue<string>(),
            user = a["_userid_value@OData.Community.Display.V1.FormattedValue"]?.GetValue<string>() ?? a["_userid_value"]?.GetValue<string>(),
            createdOn = a["createdon"]?.GetValue<DateTime>(),
            operation = a["operation@OData.Community.Display.V1.FormattedValue"]?.GetValue<string>() ?? a["operation"]?.GetValue<string>(),
            action = a["action@OData.Community.Display.V1.FormattedValue"]?.GetValue<string>() ?? a["action"]?.GetValue<string>()
        }).ToList();

        return new { filter = new { objectid, objecttypecode, top = topValue }, count = records.Count, records };
    }

    public async Task<object> AuditChangeData(string objectid, string? auditid, string? top)
    {
        var url = !string.IsNullOrEmpty(auditid)
            ? $"audits({auditid})?$select=auditid,objecttypecode,_objectid_value,_userid_value,createdon,operation,action,changedata"
            : $"audits?$select=auditid,objecttypecode,_objectid_value,_userid_value,createdon,operation,action,changedata&$filter=_objectid_value eq {objectid}&$orderby=createdon desc&$top={top ?? "1"}";

        var data = await Fetch(url);
        var rows = !string.IsNullOrEmpty(auditid) ? new[] { data } : data["value"]?.AsArray().ToArray() ?? Array.Empty<JsonNode>();

        var results = rows.Select(row => new
        {
            auditid = row["auditid"]?.GetValue<string>(),
            objecttypecode = row["objecttypecode"]?.GetValue<string>(),
            createdon = row["createdon"]?.GetValue<DateTime>(),
            user = row["_userid_value@OData.Community.Display.V1.FormattedValue"]?.GetValue<string>() ?? row["_userid_value"]?.GetValue<string>(),
            operation = row["operation@OData.Community.Display.V1.FormattedValue"]?.GetValue<string>() ?? row["operation"]?.GetValue<string>(),
            action = row["action@OData.Community.Display.V1.FormattedValue"]?.GetValue<string>() ?? row["action"]?.GetValue<string>(),
            changedata = row["changedata"]?.GetValue<string>()
        }).ToList();

        return new { objectid, auditid, count = results.Count, records = results };
    }

    public async Task<object> Create(string entitySet, JsonObject data, bool returnRecord)
    {
        var (record, _, response) = await Send(HttpMethod.Post, entitySet, data, returnRecord: returnRecord);

        // Dataverse returns the new record's URI in OData-EntityId, e.g. ...accounts(guid)
        var entityId = response.Headers.TryGetValues("OData-EntityId", out var v) ? v.FirstOrDefault() : null;
        var id = entityId != null && entityId.EndsWith(")")
            ? entityId[(entityId.LastIndexOf('(') + 1)..^1]
            : entityId;

        return new { entitySet, id, uri = entityId, created = true, record };
    }

    public async Task<object> Update(string entitySet, string id, JsonObject data, bool returnRecord)
    {
        var (record, _, _) = await Send(HttpMethod.Patch, $"{entitySet}({CleanId(id)})", data,
            mustExist: true, returnRecord: returnRecord);
        return new { entitySet, id, updated = data.Select(kv => kv.Key).ToList(), record };
    }

    public async Task<object> Delete(string entitySet, string id)
    {
        await Send(HttpMethod.Delete, $"{entitySet}({CleanId(id)})", null);
        return new { entitySet, id, deleted = true };
    }

    public async Task<object> Associate(string entitySet, string id, string relationship, string targetEntitySet, string targetId)
    {
        var body = new JsonObject
        {
            ["@odata.id"] = $"{_config.EnvironmentUrl}/api/data/{_config.ApiVersion}/{targetEntitySet}({CleanId(targetId)})"
        };
        await Send(HttpMethod.Post, $"{entitySet}({CleanId(id)})/{relationship}/$ref", body);
        return new { entitySet, id, relationship, targetEntitySet, targetId, associated = true };
    }

    public async Task<object> Disassociate(string entitySet, string id, string relationship, string? targetId)
    {
        // collection-valued needs the target in the path; single-valued (lookup) does not
        var path = string.IsNullOrEmpty(targetId)
            ? $"{entitySet}({CleanId(id)})/{relationship}/$ref"
            : $"{entitySet}({CleanId(id)})/{relationship}({CleanId(targetId)})/$ref";
        await Send(HttpMethod.Delete, path, null);
        return new { entitySet, id, relationship, targetId, disassociated = true };
    }

    // One atomic changeset: all operations commit, or none do.
    public async Task<object> Batch(JsonArray operations)
    {
        if (operations.Count == 0) throw new Exception("operations must not be empty");

        var batchId = $"batch_{Guid.NewGuid():N}";
        var changesetId = $"changeset_{Guid.NewGuid():N}";
        var baseUrl = $"{_config.EnvironmentUrl}/api/data/{_config.ApiVersion}";
        var content = new StringContent(BuildBatchBody(operations, batchId, changesetId, baseUrl));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("multipart/mixed");
        content.Headers.ContentType.Parameters.Add(
            new System.Net.Http.Headers.NameValueHeaderValue("boundary", batchId));

        var (_, raw, _) = await Send(HttpMethod.Post, "$batch", null, rawContent: content);

        // The batch call itself succeeds even when an inner operation fails; read the parts.
        var statuses = raw.Split('\n')
            .Where(l => l.StartsWith("HTTP/1.1 "))
            .Select(l => l.Trim())
            .ToList();
        var ids = raw.Split('\n')
            .Where(l => l.StartsWith("OData-EntityId:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Split(':', 2)[1].Trim())
            .ToList();
        var failed = statuses.Any(s => !s.StartsWith("HTTP/1.1 2"));

        return new
        {
            operations = operations.Count,
            committed = !failed,
            statuses,
            createdIds = ids,
            raw = failed ? raw : null
        };
    }

    public static string BuildBatchBody(JsonArray operations, string batchId, string changesetId, string baseUrl)
    {
        var sb = new System.Text.StringBuilder();

        sb.Append($"--{batchId}\r\n");
        sb.Append($"Content-Type: multipart/mixed;boundary={changesetId}\r\n\r\n");

        var contentId = 1;
        foreach (var opNode in operations)
        {
            var op = opNode?.AsObject() ?? throw new Exception("each operation must be an object");
            var kind = op["method"]?.GetValue<string>()?.ToLowerInvariant()
                ?? throw new Exception("operation missing method (create|update|delete)");
            var entitySet = op["entitySet"]?.GetValue<string>()
                ?? throw new Exception("operation missing entitySet");
            var id = op["id"]?.GetValue<string>();
            var data = op["data"]?.AsObject();

            var (verb, target) = kind switch
            {
                "create" => ("POST", entitySet),
                "update" => ("PATCH", $"{entitySet}({CleanId(id ?? throw new Exception("update needs id"))})"),
                "delete" => ("DELETE", $"{entitySet}({CleanId(id ?? throw new Exception("delete needs id"))})"),
                _ => throw new Exception($"Unknown batch method: {kind}")
            };
            if (kind != "delete" && data == null) throw new Exception($"{kind} needs data");

            sb.Append($"--{changesetId}\r\n");
            sb.Append("Content-Type: application/http\r\n");
            sb.Append("Content-Transfer-Encoding: binary\r\n");
            sb.Append($"Content-ID: {contentId++}\r\n\r\n");
            sb.Append($"{verb} {baseUrl}/{target} HTTP/1.1\r\n");
            sb.Append("Content-Type: application/json;type=entry\r\n");
            if (kind == "update") sb.Append("If-Match: *\r\n");
            sb.Append("\r\n");
            if (data != null) sb.Append(data.ToJsonString() + "\r\n");
        }

        sb.Append($"--{changesetId}--\r\n");
        sb.Append($"--{batchId}--\r\n");
        return sb.ToString();
    }

    private static string CleanId(string id) => id.Trim().Trim('{', '}');

    // an alternate key ("name='Contoso'") goes in the path verbatim; a guid gets its braces stripped
    private static string Key(string id) => id.Contains('=') ? id.Trim() : CleanId(id);
}
