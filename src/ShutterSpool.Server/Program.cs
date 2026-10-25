using ShutterSpool.Server.Composition;
using ShutterSpool.Server.Hosting;

if (args is ["--health-check"])
    return await HealthCheckCommand.RunAsync();

var builder = WebApplication.CreateBuilder(args);
RuntimeConfiguration.LoadFileSecrets(builder.Configuration);
builder.AddShutterSpoolServer();
var app = builder.Build();
await app.InitializeShutterSpoolServerAsync();
app.MapShutterSpoolServer();
app.Run();
return 0;
