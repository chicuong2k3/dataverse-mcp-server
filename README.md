# Dataverse MCP Server

C# MCP server for Microsoft Dataverse / Dynamics CE.

## Install

```bash
dotnet tool install -g DataverseMcpServer
```

Requires .NET 8.0 SDK/runtime.

## First Run Setup

On first run, you'll be prompted interactively:

```
=== Dataverse MCP Server - First Time Setup ===

You need to create an Azure AD App Registration.
Quick guide:
  1. Go to https://portal.azure.com
  2. Azure AD > App registrations > New registration
  3. Name: any (e.g. 'Dataverse MCP')
  4. Supported account types: Accounts in this organizational directory only
  5. Register
  6. Copy: Application (client) ID, Directory (tenant) ID
  7. Certificates & secrets > New client secret
  8. Copy the secret value (you won't see it again!)
  9. In Power Platform admin center:
     - Environment > Settings > Users + permissions > Application users
     - New app user > pick your app > assign security role

Tenant ID: YOUR_TENANT_ID
Client ID: YOUR_CLIENT_ID
Client Secret: YOUR_CLIENT_SECRET
Environment URL: https://yourorg.crm.dynamics.com

Config saved to: C:\Users\YOU\.dataverse\dataverse.config.json
You're all set!
```

## Configure Claude Desktop

Edit `%APPDATA%\Claude\settings.json`:

```json
{
  "mcpServers": {
    "dataverse": {
      "command": "dataverse-mcp-server"
    }
  }
}
```

## MCP Tools

| Tool | Description | Parameters |
|------|-------------|------------|
| `whoami` | Verify authentication | - |
| `list_entities` | List entities | `pattern` (optional) |
| `entity_info` | Entity definition + IsAuditEnabled | `logicalName` |
| `attributes` | All fields with type + audit | `logicalName` |
| `optionset` | Option set values | `entity`, `attribute` |
| `relationships` | 1:N, N:1, M:N relationships | `logicalName` |
| `query` | Query records (OData) | `entitySet`, `filter`, `select`, `top` |
| `audit` | Audit logs | `objectid`, `objecttypecode`, `top` |
| `audit_changedata` | Change details | `objectid`, `auditid`, `top` |

## Publish

```bash
# Tag for release
git tag v1.0.0
git push origin v1.0.0

# Or manual publish
dotnet pack
dotnet nuget push DataverseMcpServer.1.0.0.nupkg --source nuget.org --api-key YOUR_KEY
```

## Requirements

- .NET 8.0 SDK/runtime
- Azure AD App Registration with client credentials
- Dataverse environment URL

## Config Location

- `%USERPROFILE%\.dataverse\dataverse.config.json` (auto-created)
- Or `dataverse.config.json` in current directory

## Ported from

[fetch_dataverse.js](fetch_dataverse.js) - Node.js CLI tool
