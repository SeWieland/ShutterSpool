using ShutterSpool.Server.Composition;
using ShutterSpool.Server.Hosting;

if (args is ["--health-check"])
    return await HealthCheckCommand.RunAsync();

bool workspaceOperation = args.Length > 0 && args[0].StartsWith("--workspace-", StringComparison.Ordinal);
var builder = WebApplication.CreateBuilder(workspaceOperation ? [] : args);
if (workspaceOperation) return await ServerComposition.RunWorkspaceOperatorAsync(args, builder.Configuration);
builder.AddShutterSpoolServer();
await using var app = builder.Build();
// Build-time OpenAPI discovery executes the entrypoint without operational resources.
if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name != "GetDocument.Insider")
    await app.InitializeShutterSpoolServerAsync();
app.MapShutterSpoolServer();
app.Run();
return 0;
