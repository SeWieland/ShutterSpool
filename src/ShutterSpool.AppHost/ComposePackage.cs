using Aspire.Hosting.Docker.Resources;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;

namespace ShutterSpool.AppHost;

internal static class ComposePackage
{
    public static void Configure(IDistributedApplicationBuilder builder)
    {
        builder.AddDockerComposeEnvironment("compose")
            .WithProperties(environment => environment.DashboardEnabled = false)
            .ConfigureComposeFile(ConfigureFile)
            .ConfigureEnvFile(environment => environment.Clear());
    }

    private static void ConfigureFile(ComposeFile file)
    {
        // Customize the generated graph; never ship development paths, ports or resolved parameters.
        file.Name = "${COMPOSE_PROJECT_NAME:?Set a unique installation name}";
        file.Networks.Clear();
        file.Networks["backend"] = new Network
        {
            Name = "${COMPOSE_PROJECT_NAME:?Set a unique installation name}-backend",
            Ipam = new Ipam { Config = [new() { ["subnet"] = "${BACKEND_SUBNET:?Set a free private /24 subnet}" }] },
        };
        file.Networks["proxy"] = new Network
        {
            Name = "${COMPOSE_PROJECT_NAME:?Set a unique installation name}-proxy",
            Ipam = new Ipam { Config = [new() { ["subnet"] = "${PROXY_SUBNET:?Set a free private /28 subnet for the proxy-facing network}" }] },
        };
        file.Networks["identity-proxy"] = new Network
        {
            Name = "${COMPOSE_PROJECT_NAME:?Set a unique installation name}-identity-proxy",
            Ipam = new Ipam { Config = [new() { ["subnet"] = "${IDENTITY_PROXY_SUBNET:?Set a free private /28 subnet for the identity proxy}" }] },
        };
        file.Volumes.Clear();
        foreach (string name in new[] { "spool", "previews" })
            file.Volumes[name] = new Volume { Name = "${COMPOSE_PROJECT_NAME:?Set a unique installation name}-" + name };
        foreach (var service in file.Services.Values)
        {
            service.Ports.Clear();
            service.Expose.Clear();
            service.Networks = ["backend"];
            service.Volumes.Clear();
            service.Environment.Keys.Where(key => key.StartsWith("OTEL_", StringComparison.Ordinal)
                || key.StartsWith("ASPIRE_", StringComparison.Ordinal)).ToList().ForEach(key => service.Environment.Remove(key));
            service.Restart = "unless-stopped";
            service.StopGracePeriod = "150s";
            service.Logging = new Logging { Driver = "local", Options = new() { ["max-size"] = "10m", ["max-file"] = "3" } };
        }

        file.Services["imaginary"].Command = ["-p", "9000", "-concurrency", "20", "-cpus", "2", "-http-read-timeout", "120", "-http-write-timeout", "120", "-log-level", "error"];

        var server = file.Services["server"];
        var ingressPorts = server.Environment.Where(item => item.Key.EndsWith("__ControlPort", StringComparison.Ordinal))
            .ToDictionary(item => item.Key, item => item.Value);
        server.Image = "${SERVER_IMAGE:?Select the qualified Server image}";
        server.Build = null;
        server.Environment = new()
        {
            ["ASPNETCORE_URLS"] = "http://0.0.0.0:8080",
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["ShutterSpool__Deployment__Packaged"] = "true",
            ["ShutterSpool__Security__EncryptionKeyFile"] = "/run/secrets/encryption-key",
            ["ConnectionStrings__shutterspoolFile"] = "/run/secrets/database-connection",
            ["ShutterSpool__Spool__RootPath"] = "/var/lib/shutterspool/spool",
            ["ShutterSpool__Preview__CacheRootPath"] = "/var/cache/shutterspool",
            ["ShutterSpool__Preview__ImaginaryUrl"] = "http://imaginary:9000",
            ["ShutterSpool__ForwardedHeaders__KnownNetworks__0"] = "${PROXY_SUBNET:?Set the proxy-facing subnet trusted for forwarded headers}",
        };
        server.DependsOn.Clear();
        server.Networks = ["backend", "proxy"];
        server.Volumes.Add(Mount("volume", "spool", "/var/lib/shutterspool/spool"));
        server.Volumes.Add(Mount("volume", "previews", "/var/cache/shutterspool"));
        server.Volumes.Add(Mount("bind", "./config/server.json", "/app/appsettings.Production.json", true));
        server.Volumes.Add(Mount("bind", "./restore-evidence", "/run/restore-evidence", true));
        Secret(file, server, "encryption-key");
        Secret(file, server, "database-connection");
        server.Healthcheck = Health(["CMD", "dotnet", "ShutterSpool.Server.dll", "--health-check"]);

        foreach (var (name, edge) in file.Services.Where(item => item.Key.StartsWith("camera-edge", StringComparison.Ordinal)))
        {
            edge.Entrypoint = ["/bin/sh", "/opt/package/edge-entrypoint.sh"];
            edge.Command.Clear();
            edge.Volumes.Add(Mount("bind", "./package/edge-entrypoint.sh", "/opt/package/edge-entrypoint.sh", true));
            edge.Environment["SFTPGO_DATA_PROVIDER__EXTERNAL_AUTH_HOOK"] = "http://server:8081/internal/camera/"
                + (name.EndsWith("legacy", StringComparison.Ordinal) ? "legacy-auth" : name.EndsWith("plain", StringComparison.Ordinal) ? "plain-auth" : "auth");
            edge.Environment["SFTPGO_COMMON__ACTIONS__HOOK"] = "http://server:8081/internal/camera/hook";
            edge.Environment.Remove("SFTPGO_HTTP__HEADERS__0__VALUE");
            edge.Environment["SFTPGO_FTPD__BINDINGS__0__FORCE_PASSIVE_IP"] = "${INGEST_ADVERTISED_ADDRESS:?Set the camera-reachable passive IPv4 address}";
            edge.Environment["CAMERA_HOSTNAME"] = "${INGEST_HOSTNAME:?Set the certificate and Source ingest hostname}";
            edge.Tmpfs = ["/tmp:rw,noexec,nosuid,size=1073741824"];
            edge.Environment["SFTPGO_GRACE_TIME"] = "300";
            edge.StopSignal = "SIGINT";
            edge.StopGracePeriod = "330s";
            Secret(file, edge, "camera-callback-key");
            string profile = name.EndsWith("legacy", StringComparison.Ordinal) ? "LegacyFtps" : name.EndsWith("plain", StringComparison.Ordinal) ? "PlainFtpLan" : "ModernFtps";
            int control = int.Parse(ingressPorts[$"ShutterSpool__CameraIngress__Profiles__{profile}__ControlPort"]);
            edge.Ports.Add($"${{INGEST_LISTEN_ADDRESS:?Set the local ingress IPv4 binding}}:{control}:2121");
            int start = int.Parse(edge.Environment["SFTPGO_FTPD__PASSIVE_PORT_RANGE__START"]);
            int end = int.Parse(edge.Environment["SFTPGO_FTPD__PASSIVE_PORT_RANGE__END"]);
            edge.Ports.Add($"${{INGEST_LISTEN_ADDRESS}}:{start}-{end}:{start}-{end}");
            server.Environment[$"ShutterSpool__CameraIngress__Profiles__{profile}__Enabled"] = "true";
            server.Environment[$"ShutterSpool__CameraIngress__Profiles__{profile}__ControlPort"] = control.ToString();
            if (profile != "PlainFtpLan")
            {
                edge.Volumes.Add(Mount("bind", "./certificates", "/etc/sftpgo/certs", true));
                server.Environment[$"ShutterSpool__CameraIngress__Profiles__{profile}__CaRootPath"] = "/run/certificates/root.pem";
            }
            edge.DependsOn["server"] = new() { Condition = "service_healthy" };
        }
        if (file.Services.Keys.Any(name => name.StartsWith("camera-edge", StringComparison.Ordinal)))
        {
            Secret(file, server, "camera-callback-key");
            server.Environment["ShutterSpool__CameraIngress__Enabled"] = "true";
            server.Environment["ShutterSpool__CameraIngress__Hostname"] = "${INGEST_HOSTNAME:?Set the certificate and Source ingest hostname}";
            server.Environment["ShutterSpool__CameraIngress__InternalPort"] = "8081";
            server.Environment["ShutterSpool__CameraIngress__CallbackBaseUrl"] = "http://server:8081/internal/camera";
            server.Environment["ShutterSpool__CameraIngress__CallbackKeyFile"] = "/run/secrets/camera-callback-key";
            server.Volumes.Add(Mount("bind", "./certificates/root.pem", "/run/certificates/root.pem", true));
        }

        file.Services.Remove("postgres");
        file.Services.Remove("valkey");
        file.Services.Remove("keycloak");
        file.Services.Remove("keycloak-reconciler");
        foreach (string name in file.Secrets.Keys.Where(name => !file.Services.Values.Any(service => service.Secrets.Any(secret => secret.Source == name))).ToArray())
            file.Secrets.Remove(name);
        file.Secrets["database-connection"] = new Secret { File = "./secrets/database-connection" };
        file.Secrets["encryption-key"] = new Secret { File = "./secrets/encryption-key" };
    }

    private static Volume Mount(string type, string source, string target, bool readOnly = false) =>
        new() { Name = source, Type = type, Source = source, Target = target, ReadOnly = readOnly };

    private static Healthcheck Health(List<string> test) =>
        new() { Test = test, Interval = "5s", Timeout = "3s", Retries = 24, StartPeriod = "30s" };

    private static void Secret(ComposeFile file, Service service, string name)
    {
        file.Secrets[name] = new Secret { File = $"./secrets/{name}" };
        service.Secrets.Add(new SecretReference { Source = name, Target = name });
    }
}
