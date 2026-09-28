using System.Text;
using Farm.Azure;
using Farm.Copilot;
using Farm.Core.Chickens;
using Farm.Core.Cows;
using Farm.Sandbox.Cows;
using Farm.State.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var cowOptions = CowOptions.FromEnvironment();
var azurePat = CowOptions.Required("FARM_AZURE_DEVOPS_PAT");
var azureOrganizationUrl = new Uri(CowOptions.Required("FARM_AZURE_DEVOPS_ORGANIZATION_URL"));
var miroAccessToken = CowOptions.Optional("FARM_COWS_MIRO_ACCESS_TOKEN");
var azureBasicCredential = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{azurePat}"));
// Opt-in: the runner snapshot already supplies tracker data.
var azureMcpAuthorization = CowOptions.OptionalBool("FARM_COWS_ADO_MCP_USE_PAT", false)
    ? $"Basic {azureBasicCredential}"
    : null;
var secrets = new[] { azurePat, azureBasicCredential, miroAccessToken, azureMcpAuthorization };
var redactor = new SensitiveDataRedactor(secrets);
var contentScanner = new SensitiveContentScanner(secrets);
using var processLock = new ProcessLock(cowOptions.LockPath);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(cowOptions);
builder.Services.AddSingleton<ISensitiveDataRedactor>(redactor);
builder.Services.AddSingleton<ISensitiveContentScanner>(contentScanner);
builder.Services.AddSingleton(new CowStatusWriter(cowOptions.StatusPath, redactor));
builder.Services.AddSingleton(new CowNoteStore(cowOptions.NotesPath));
builder.Services.AddSingleton(new AzureDevOpsSettings
{
    OrganizationUrl = azureOrganizationUrl,
    Project = CowOptions.Required("FARM_AZURE_DEVOPS_PROJECT"),
    TerminalStates = CowOptions.Optional("FARM_AZURE_DEVOPS_TERMINAL_STATES")?
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? AzureDevOpsSettings.DefaultTerminalStates,
    PersonalAccessToken = azurePat
});
builder.Services.AddSingleton<AzureDevOpsClient>();
builder.Services.AddSingleton<ICloudWorkItemSource>(provider => provider.GetRequiredService<AzureDevOpsClient>());
builder.Services.AddSingleton<INoteSyncStore>(new SqliteNoteSyncStore(cowOptions.StatePath));
builder.Services.AddSingleton<IInvestigationAuthor>(new CopilotInvestigationAuthor(new CopilotInvestigationOptions
{
    Model = CowOptions.Optional("FARM_COWS_COPILOT_MODEL") ?? "auto",
    SkillFilePath = cowOptions.SkillFilePath,
    McpConfigPath = cowOptions.McpConfigPath,
    AzureDevOpsAuthorizationHeader = azureMcpAuthorization,
    AzureDevOpsOrganization = McpServerConfigLoader.GetAzureDevOpsOrganization(azureOrganizationUrl),
    MiroAccessToken = miroAccessToken,
    SafeProcessEnvironment = CowOptions.LoadSafeProcessEnvironment()
}));
builder.Services.AddSingleton<CowRunner>();
builder.Services.AddHostedService<CowWorker>();

await builder.Build().RunAsync();
