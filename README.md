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
| `query` | Query records (OData or FetchXML) | `entitySet`, `filter`, `select`, `top`, `orderby`, `expand`, `skip`, `apply`, `fetchXml`, `pageSize`, `all` |
| `retrieve` | Get one record | `entitySet`, `id`, `select`, `expand` |
| `execute` | Call any action or function | `name`, `kind`, `entitySet`, `id`, `parameters` (object) |
| `audit` | Audit logs | `objectid`, `objecttypecode`, `top` |
| `audit_changedata` | Change details | `objectid`, `auditid`, `top` |
| `create` | Create a record | `entitySet`, `data` (object), `returnRecord` |
| `update` | Update a record | `entitySet`, `id`, `data` (object), `returnRecord` |
| `delete` | Delete a record | `entitySet`, `id` |
| `associate` | Link two records | `entitySet`, `id`, `relationship`, `targetEntitySet`, `targetId` |
| `disassociate` | Unlink records | `entitySet`, `id`, `relationship`, `targetId` |
| `batch` | Atomic create/update/delete changeset | `operations` (array) |
| `metadata` | Raw metadata call — schema authoring | `method`, `path`, `data` (object), `solution` |
| `publish` | Publish customizations | `entities` (optional) |
| `solution_export` | Export a solution to a .zip | `uniqueName`, `path`, `managed` |
| `solution_import` | Import a solution .zip | `path`, `overwrite`, `publishWorkflows` |

Every tool also takes `impersonate` — a `systemuserid` to run the call as (`MSCRMCallerID`). Omit it to run as the app user.

Lookups in `data` use the OData binding syntax: `{"name": "Acme", "primarycontactid@odata.bind": "/contacts(<guid>)"}`.

`returnRecord: "true"` sends `Prefer: return=representation` so the written row comes back.

`disassociate` takes `targetId` only for collection-valued relationships; omit it to clear a lookup.

`batch` runs every operation in one changeset — all commit or none do:

```json
{"operations": [
  {"method": "create", "entitySet": "accounts", "data": {"name": "Acme"}},
  {"method": "update", "entitySet": "contacts", "id": "<guid>", "data": {"jobtitle": "CEO"}},
  {"method": "delete", "entitySet": "tasks", "id": "<guid>"}
]}
```

`query` returns every row it fetched — set `top`, or `all: "true"` to follow paging (capped at 20 pages, which it reports). `pageSize` sets the server page size.

`execute` reaches everything the Web API exposes as a message. `kind` is `action` (POST, default) or `function` (GET); pass `entitySet` + `id` for bound messages:

```json
{"name": "WhoAmI", "kind": "function"}
{"name": "Assign", "entitySet": "accounts", "id": "<guid>",
 "parameters": {"Assignee": {"@odata.id": "systemusers(<guid>)"}}}
{"name": "GrantAccess", "entitySet": "accounts", "id": "<guid>", "parameters": {"...": "..."}}
```

Security admin needs no dedicated tools: roles, teams and business units are ordinary tables (`roles`, `teams`, `businessunits`) for `query`/`create`/`update`; role assignment is `associate` on `systemuserroles_association`; record sharing is `GrantAccess` / `ModifyAccess` / `RevokeAccess` via `execute`.

`metadata` is the escape hatch for schema authoring — create an entity, attribute, option set or relationship by POSTing to the matching definition path:

```json
{"method": "post", "path": "EntityDefinitions(LogicalName='account')/Attributes",
 "data": {"@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "...": "..."},
 "solution": "MySolution"}
```

Then `publish` to make the change live. Metadata `patch` sends `MSCRM.MergeLabels: true`.

Self-check for the batch, query and execute builders: `dotnet run -- --selftest`.

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
