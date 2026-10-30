using ShutterSpool.Server.Composition;
using ShutterSpool.Server.Hosting;

if (args is ["--health-check"])
    return await HealthCheckCommand.RunAsync();

bool workspaceOperation = args.Length > 0 && args[0].StartsWith("--workspace-", StringComparison.Ordinal);
var builder = WebApplication.CreateBuilder(workspaceOperation ? [] : args);
RuntimeConfiguration.LoadFileSecrets(builder.Configuration);
if (workspaceOperation) return await ServerComposition.RunWorkspaceOperatorAsync(args, builder.Configuration);
builder.AddShutterSpoolServer();
var app = builder.Build();
await app.InitializeShutterSpoolServerAsync();
app.MapShutterSpoolServer();
app.Run();
return 0;
