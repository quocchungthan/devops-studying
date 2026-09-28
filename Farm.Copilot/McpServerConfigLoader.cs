using System.Text.Json;
using GitHub.Copilot;

namespace Farm.Copilot;

public sealed record McpServerSelection(
    IDictionary<string, McpServerConfig> Servers,
    IReadOnlyList<string> Unavailable)
{
    // Server name -> the only tool names the permission policy approves for that server.
    public IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedTools { get; init; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
}

// Reads a VS Code style mcp.json ({"servers": {name: {type, url, headers}}}) and injects runner-owned auth headers.
public static class McpServerConfigLoader
{
    public const string AzureDevOpsMcpHost = "mcp.dev.azure.com";
    public const string MiroMcpHost = "mcp.miro.com";
    public const string OrganizationPlaceholder = "{organization}";

    public static McpServerSelection Load(
        string? path,
        string? azureDevOpsAuthorizationHeader,
        string? miroAccessToken,
        string? azureDevOpsOrganization = null)
    {
        var servers = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
        var allowedTools = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        var unavailable = new List<string>();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            unavailable.Add("mcp: no MCP configuration file found");
            return new McpServerSelection(servers, unavailable);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("servers", out var serverElements) || serverElements.ValueKind != JsonValueKind.Object)
        {
            unavailable.Add("mcp: configuration has no servers object");
            return new McpServerSelection(servers, unavailable);
        }

        foreach (var server in serverElements.EnumerateObject())
        {
            var type = GetString(server.Value, "type") ?? "http";
            var url = GetString(server.Value, "url");
            if (url?.Contains(OrganizationPlaceholder, StringComparison.OrdinalIgnoreCase) == true)
            {
                if (string.IsNullOrWhiteSpace(azureDevOpsOrganization))
                {
                    unavailable.Add($"{server.Name}: Azure DevOps organization could not be derived from FARM_AZURE_DEVOPS_ORGANIZATION_URL");
                    continue;
                }

                url = url.Replace(OrganizationPlaceholder, Uri.EscapeDataString(azureDevOpsOrganization.Trim()), StringComparison.OrdinalIgnoreCase);
            }

            if (!string.Equals(type, "http", StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                unavailable.Add($"{server.Name}: only https MCP servers are supported");
                continue;
            }

            var tools = CopilotInvestigationPolicy.GetReadOnlyTools(uri.Host);
            if (tools is null)
            {
                unavailable.Add($"{server.Name}: no read-only tool allowlist for this MCP host");
                continue;
            }

            var headers = ReadHeaders(server.Value);
            if (uri.Host.Equals(MiroMcpHost, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(miroAccessToken))
                {
                    unavailable.Add($"{server.Name}: design source (Miro) has no access token configured");
                    continue;
                }

                headers["Authorization"] = $"Bearer {miroAccessToken.Trim()}";
            }
            else if (uri.Host.Equals(AzureDevOpsMcpHost, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(azureDevOpsAuthorizationHeader))
                {
                    unavailable.Add($"{server.Name}: Azure DevOps MCP auth is disabled; tracker data comes from the runner snapshot");
                    continue;
                }

                headers["Authorization"] = azureDevOpsAuthorizationHeader.Trim();
            }

            servers[server.Name] = new McpHttpServerConfig
            {
                Url = uri.AbsoluteUri,
                Headers = headers.Count == 0 ? null : headers,
                Tools = tools.Order(StringComparer.Ordinal).ToList()
            };
            allowedTools[server.Name] = tools;
        }

        return new McpServerSelection(servers, unavailable) { AllowedTools = allowedTools };
    }

    // https://dev.azure.com/{org}[/...] or https://{org}.visualstudio.com; null for anything else.
    public static string? GetAzureDevOpsOrganization(Uri organizationUrl)
    {
        ArgumentNullException.ThrowIfNull(organizationUrl);
        if (organizationUrl.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var segment = organizationUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(segment) ? null : Uri.UnescapeDataString(segment);
        }

        const string legacySuffix = ".visualstudio.com";
        return organizationUrl.Host.EndsWith(legacySuffix, StringComparison.OrdinalIgnoreCase)
            ? organizationUrl.Host[..^legacySuffix.Length]
            : null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Dictionary<string, string> ReadHeaders(JsonElement server)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (server.TryGetProperty("headers", out var element) && element.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in element.EnumerateObject().Where(header => header.Value.ValueKind == JsonValueKind.String))
            {
                headers[header.Name] = header.Value.GetString()!;
            }
        }

        return headers;
    }
}
