using Farm.Azure;
using Farm.Copilot;
using Farm.Core.Chickens;
using Farm.Git;
using Farm.Sandbox.Chickens;
using Farm.State.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text;

var chickenOptions = ChickenOptions.FromEnvironment();
var statePath = chickenOptions.StatePath;
var repositoryPath = ChickenOptions.Required("FARM_CHICKENS_REPOSITORY_PATH");
var cachePath = ChickenOptions.Required("FARM_CHICKENS_CACHE_PATH");
var worktreesPath = ChickenOptions.Required("FARM_CHICKENS_WORKTREES_PATH");
var safeProcessEnvironment = ChickenOptions.LoadSafeProcessEnvironment();
var azurePat = ChickenOptions.Required("FARM_AZURE_DEVOPS_PAT");
var gitAuthToken = Environment.GetEnvironmentVariable("FARM_CHICKENS_GIT_AUTH_TOKEN")?.Trim();
var gitAuthUser = Environment.GetEnvironmentVariable("FARM_CHICKENS_GIT_AUTH_USER")?.Trim() ?? "x-access-token";
var gitCredential = string.IsNullOrEmpty(gitAuthToken) ? $":{azurePat}" : $"{gitAuthUser}:{gitAuthToken}";
var httpExtraHeader = $"AUTHORIZATION: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(gitCredential))}";
var redactor = new SensitiveDataRedactor([azurePat, gitAuthToken, gitCredential, httpExtraHeader]);
var contentScanner = new SensitiveContentScanner([azurePat, gitAuthToken, gitCredential, httpExtraHeader]);
using var processLock = new ProcessLock(chickenOptions.LockPath);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(chickenOptions);
builder.Services.AddSingleton<ISensitiveDataRedactor>(redactor);
builder.Services.AddSingleton<ISensitiveContentScanner>(contentScanner);
builder.Services.AddSingleton(new ChickenStatusWriter(chickenOptions.StatusPath, redactor));
builder.Services.AddSingleton(new AzureDevOpsSettings
{
    OrganizationUrl = new Uri(ChickenOptions.Required("FARM_AZURE_DEVOPS_ORGANIZATION_URL")),
    Project = ChickenOptions.Required("FARM_AZURE_DEVOPS_PROJECT"),
    PersonalAccessToken = azurePat,
    ReviewContextMaxDepth = chickenOptions.WorkItemMaxDepth,
    ReviewContextMaxWorkItems = chickenOptions.WorkItemMaxCount
});
builder.Services.AddSingleton<AzureDevOpsClient>();
builder.Services.AddSingleton<IReviewContextSource>(provider => provider.GetRequiredService<AzureDevOpsClient>());
builder.Services.AddSingleton<IRepositoryWorkspaceManager>(new GitWorkspaceManager(new GitWorkspaceOptions
{
    RepositoryPath = repositoryPath,
    CachePath = cachePath,
    WorktreesPath = worktreesPath,
    BaseBranch = ChickenOptions.Required("FARM_CHICKENS_BASE_BRANCH"),
    EnablePush = chickenOptions.EnablePush,
    HttpExtraHeader = httpExtraHeader,
    UserName = ChickenOptions.Required("FARM_CHICKENS_GIT_USER_NAME"),
    UserEmail = ChickenOptions.Required("FARM_CHICKENS_GIT_USER_EMAIL"),
    ValidationCommands = ChickenOptions.LoadValidationCommands(),
    SafeProcessEnvironment = safeProcessEnvironment,
    Redactor = redactor,
    ContentScanner = contentScanner
}));
builder.Services.AddSingleton<IReviewBrain>(new CopilotReviewBrain(new CopilotReviewOptions
{
    Model = Environment.GetEnvironmentVariable("FARM_CHICKENS_COPILOT_MODEL") ?? "auto",
    ResourcesRootPath = Environment.GetEnvironmentVariable("FARM_CHICKENS_COPILOT_RESOURCES_PATH"),
    PromptFilePath = Environment.GetEnvironmentVariable("FARM_CHICKENS_COPILOT_PROMPT_PATH"),
    AgentName = Environment.GetEnvironmentVariable("FARM_CHICKENS_COPILOT_AGENT"),
    SafeProcessEnvironment = safeProcessEnvironment
}));
builder.Services.AddSingleton<IAttemptStore>(new SqliteAttemptStore(statePath));
builder.Services.AddSingleton<ChickenRunner>();
builder.Services.AddHostedService<ChickenWorker>();

await builder.Build().RunAsync();