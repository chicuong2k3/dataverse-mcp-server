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
    public JsonRpcBase? Jsonrpc { get; set; }
    public object? Id { get; set; }
    public string? Method { get; set; }
    public JsonObject? Params { get; set; }
    public JsonObject? Result { get; set; }
    public JsonObject? Error { get; set; }
}

abstract class JsonRpcBase { }

class JsonRpcRequest : JsonRpcBase
{
    public object? Id { get; set; }
    public required string Method { get; set; }
    public JsonObject? Params { get; set; }
}

class JsonRpcResponse : JsonRpcBase
{
    public required object Id { get; set; }
    public JsonObject? Result { get; set; }
    public JsonObject? Error { get; set; }
}

class Program
{
    static async Task Main(string[] args)
    {
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
                var msg = JsonSerializer.Deserialize<McpMessage>(line);
                if (msg?.Method == null)
                {
                    // Response or notification, ignore
                    continue;
                }

                var response = await HandleRequest(msg, config, tokenCache);
                var responseJson = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = false });
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
                Console.WriteLine(JsonSerializer.Serialize(error));
                Console.Out.Flush();
            }
        }
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
                        Tool("query", "Query entity records with OData", "entitySet", "filter", "select", "top"),
                        Tool("audit", "Query audit logs", "objectid", "objecttypecode", "top"),
                        Tool("audit_changedata", "Get audit change details", "objectid", "auditid", "top")
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
        var input = new JsonObject();
        var props = new JsonObject();
        var required = new JsonArray();

        if (paramNames.Length > 0)
        {
            props["type"] = "object";
            var properties = new JsonObject();
            foreach (var p in paramNames)
            {
                properties[p] = new JsonObject
                {
                    ["type"] = "string"
                };
                required.Add(p);
            }
            props["properties"] = properties;
            props["required"] = required;
        }

        input["description"] = description;
        if (paramNames.Length > 0)
            input["inputSchema"] = props;

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = props
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
        var api = new DataverseClient(config, tokenCache);

        return name switch
        {
            "whoami" => await api.WhoAmI(),
            "list_entities" => await api.ListEntities(GetArg(args, "pattern")),
            "entity_info" => await api.EntityInfo(GetArgRequired(args, "logicalName")),
            "attributes" => await api.Attributes(GetArgRequired(args, "logicalName")),
            "optionset" => await api.OptionSet(GetArgRequired(args, "entity"), GetArgRequired(args, "attribute")),
            "relationships" => await api.Relationships(GetArgRequired(args, "logicalName")),
            "query" => await api.Query(GetArgRequired(args, "entitySet"), GetArg(args, "filter"), GetArg(args, "select"), GetArg(args, "top")),
            "audit" => await api.Audit(GetArg(args, "objectid"), GetArg(args, "objecttypecode"), GetArg(args, "top")),
            "audit_changedata" => await api.AuditChangeData(GetArgRequired(args, "objectid"), GetArg(args, "auditid"), GetArg(args, "top")),
            _ => throw new Exception($"Unknown tool: {name}")
        };
    }

    static string? GetArg(JsonObject args, string name) =>
        args.ContainsKey(name) && args[name] != null ? args[name]!.GetValue<string>() : null;

    static string GetArgRequired(JsonObject args, string name) =>
        GetArg(args, name) ?? throw new Exception($"Missing required parameter: {name}");
}

// Dataverse API Client
class DataverseClient
{
    private readonly Config _config;
    private readonly TokenCache _tokenCache;
    private readonly HttpClient _http = new();

    public DataverseClient(Config config, TokenCache tokenCache)
    {
        _config = config;
        _tokenCache = tokenCache;
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

    private async Task<JsonNode> Fetch(string path)
    {
        var token = await GetToken();
        var url = path.StartsWith("http") ? path : $"{_config.EnvironmentUrl}/api/data/{_config.ApiVersion}/{path}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {token}");
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("Prefer", "odata.include-annotations=\"*\"");

        var response = await _http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Dataverse HTTP {response.StatusCode} for {url}: {content}");
        }

        return JsonNode.Parse(content)!;
    }

    public async Task<object> WhoAmI()
    {
        var data = await Fetch("WhoAmI");
        return new
        {
            userId = data["SystemUserId"]?.GetValue<string>(),
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

    public async Task<object> Query(string entitySet, string? filter, string? select, string? top)
    {
        var paramsList = new List<string>();
        if (!string.IsNullOrEmpty(filter)) paramsList.Add($"$filter={Uri.EscapeDataString(filter)}");
        if (!string.IsNullOrEmpty(select)) paramsList.Add($"$select={select}");
        if (!string.IsNullOrEmpty(top)) paramsList.Add($"$top={top}");

        var url = entitySet;
        if (paramsList.Count > 0)
            url += "?" + string.Join("&", paramsList);

        var data = await Fetch(url);
        var records = data["value"]?.AsArray().ToList() ?? new List<JsonNode>();

        return new
        {
            entitySet,
            filter,
            select,
            top,
            count = records.Count,
            records = records.Take(5).ToList(),
            hasMore = data["@odata.nextLink"] != null
        };
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
}
