using Microsoft.Extensions.Configuration;
using ShutterSpool.AppHost;

var imagePins = ImagePins.Load();
var builder = DistributedApplication.CreateBuilder(args);
if (!builder.ExecutionContext.IsPublishMode) builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables();

var postgresPassword = builder.AddParameter("postgres-password", secret: true);
var keycloakAdministrator = builder.AddParameter("keycloak-administrator");
var keycloakAdministratorPassword = builder.AddParameter("keycloak-administrator-password", secret: true);
var shutterspoolAdministrator = builder.AddParameter("shutterspool-administrator");
var shutterspoolAdministratorPassword = builder.AddParameter("shutterspool-administrator-password", secret: true);
var shutterspoolAdministratorSubject = builder.AddParameter("shutterspool-administrator-subject");
var securityEncryptionKey = builder.AddParameter("security-encryption-key", secret: true);
var previewGenerationSlots = builder.AddParameter("preview-max-concurrent-generations", "32");
var previewQueueTimeout = builder.AddParameter("preview-queue-timeout-seconds", "60");
var previewGenerationTimeout = builder.AddParameter("preview-generation-timeout-seconds", "90");
var previewPrewarmMode = builder.AddParameter("preview-prewarm-mode", "GridOnly");
var imaginaryConcurrency = builder.AddParameter("imaginary-concurrency", "20");
var imaginaryCpus = builder.AddParameter("imaginary-cpus", "2");
var imaginaryHttpTimeout = builder.AddParameter("imaginary-http-timeout-seconds", "120");
var postgres = builder.AddPostgres("postgres", password: postgresPassword)
    .WithDataVolume("shutterspool-postgres");
var shutterspoolDatabase = postgres.AddDatabase("shutterspool", builder.Configuration["ShutterSpool:DatabaseName"] ?? "shutterspool");
var keycloakDatabase = postgres.AddDatabase("keycloak-data", "keycloak");
var valkey = builder.AddValkey("valkey")
    .WithDataVolume("shutterspool-valkey");
var postgresEndpoint = postgres.GetEndpoint("tcp");
var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.6")
    .WithHttpEndpoint(targetPort: 8080)
    .WithArgs("start", "--import-realm", "--http-enabled=true", "--hostname-strict=false")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", keycloakAdministrator)
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", keycloakAdministratorPassword)
    .WithEnvironment("KC_DB", "postgres")
    .WithEnvironment("KC_DB_URL_HOST", postgresEndpoint.Property(EndpointProperty.Host))
    .WithEnvironment("KC_DB_URL_PORT", postgresEndpoint.Property(EndpointProperty.Port))
    .WithEnvironment("KC_DB_URL_DATABASE", "keycloak")
    .WithEnvironment("KC_DB_USERNAME", "postgres")
    .WithEnvironment("KC_DB_PASSWORD", postgresPassword)
    .WithBindMount("./Realms", "/opt/shutterspool", isReadOnly: true)
    .WithBindMount("./Themes/shutterspool", "/opt/keycloak/themes/shutterspool", isReadOnly: true)
    .WaitFor(postgres);
var keycloakReconciler = builder.AddContainer("keycloak-reconciler", "quay.io/keycloak/keycloak", "26.6")
    .WithEntrypoint("/bin/sh")
    .WithArgs("/opt/shutterspool/reconcile.sh")
    .WithEnvironment("KEYCLOAK_ADMIN", keycloakAdministrator)
    .WithEnvironment("KEYCLOAK_ADMIN_PASSWORD", keycloakAdministratorPassword)
    .WithEnvironment("SHUTTERSPOOL_ADMIN_USERNAME", shutterspoolAdministrator)
    .WithEnvironment("SHUTTERSPOOL_ADMIN_PASSWORD", shutterspoolAdministratorPassword)
    .WithEnvironment("SHUTTERSPOOL_ADMIN_SUBJECT", shutterspoolAdministratorSubject)
    .WithEnvironment("SHUTTERSPOOL_LOGIN_THEME", builder.Configuration["ShutterSpool:Keycloak:LoginTheme"] ?? "shutterspool")
    .WithBindMount("./Realms", "/opt/shutterspool", isReadOnly: true)
    .WithBindMount("./Themes/shutterspool", "/opt/keycloak/themes/shutterspool", isReadOnly: true)
    .WaitFor(keycloak);
var keycloakAuthority = ReferenceExpression.Create($"{keycloak.GetEndpoint("http").Property(EndpointProperty.Url)}/realms/shutterspool");
var imaginaryPin = imagePins["imaginary"];
var imaginary = builder.AddContainer("imaginary", imaginaryPin.Image, imaginaryPin.Tag)
    .WithImageSHA256(imaginaryPin.Sha256)
    .WithHttpEndpoint(targetPort: 9000)
    .WithArgs("-p", "9000", "-concurrency", "20", "-cpus", "2",
        "-http-read-timeout", "120", "-http-write-timeout", "120", "-log-level", "error")
    .WithContainerRuntimeArgs(async context =>
    {
        string? cpus = await imaginaryCpus.Resource.GetValueAsync(context.CancellationToken);
        if (!int.TryParse(cpus, out int count) || count is < 1 or > 128)
            throw new InvalidOperationException("Imaginary CPUs must be between 1 and 128.");
        context.Args.Add($"--cpus={count}");
    });
if (!builder.ExecutionContext.IsPublishMode)
    imaginary.WithArgs(context =>
    {
        context.Args.Clear();
        foreach (object argument in new object[] { "-p", "9000", "-concurrency", imaginaryConcurrency, "-cpus", imaginaryCpus,
            "-http-read-timeout", imaginaryHttpTimeout, "-http-write-timeout", imaginaryHttpTimeout, "-log-level", "error" })
            context.Args.Add(argument);
    });
var server = builder.AddProject<Projects.ShutterSpool_Server>("server")
    .WithReference(shutterspoolDatabase)
    .WithReference(valkey)
    .WaitFor(postgres)
    .WaitFor(valkey)
    .WaitForCompletion(keycloakReconciler)
    .WithEnvironment("ShutterSpool__Security__Authority", keycloakAuthority)
    .WithEnvironment("ShutterSpool__Security__Audience", "shutterspool-server")
    .WithEnvironment("ShutterSpool__Security__BootstrapIssuer", keycloakAuthority)
    .WithEnvironment("ShutterSpool__Security__BootstrapSubject", shutterspoolAdministratorSubject)
    .WithEnvironment("ShutterSpool__Security__EncryptionKey", securityEncryptionKey)
    .WithEnvironment("ShutterSpool__CameraCredentials__MinimumPasswordLength",
        builder.Configuration["ShutterSpool:CameraCredentials:MinimumPasswordLength"] ?? "12")
    .WithEnvironment("ShutterSpool__Spool__RootPath", Path.GetFullPath(builder.Configuration["ShutterSpool:Spool:RootPath"] ?? "../../.data/spool"))
    .WithEnvironment("ShutterSpool__Spool__ReservedBytes", builder.Configuration["ShutterSpool:Spool:ReservedBytes"] ?? "1073741824")
    .WithEnvironment("ShutterSpool__Preview__ImaginaryUrl", imaginary.GetEndpoint("http"))
    .WithEnvironment("ShutterSpool__Preview__MaxConcurrentGenerations", previewGenerationSlots)
    .WithEnvironment("ShutterSpool__Preview__GenerationQueueTimeoutSeconds", previewQueueTimeout)
    .WithEnvironment("ShutterSpool__Preview__GenerationTimeoutSeconds", previewGenerationTimeout)
    .WithEnvironment("ShutterSpool__Preview__PrewarmMode", previewPrewarmMode)
    .WithEnvironment("ShutterSpool__Preview__CacheRootPath", Path.GetFullPath("../../.data/previews"));
if (!builder.ExecutionContext.IsPublishMode) builder.AddJavaScriptApp("web", "../ShutterSpool.Web")
    .WithReference(server)
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints();

var profiles = new[]
{
    (Name: "ModernFtps", Edge: "camera-edge", Control: 2121, Start: 2122, End: 2137, Auth: "/internal/camera/auth"),
    (Name: "LegacyFtps", Edge: "camera-edge-legacy", Control: 2138, Start: 2139, End: 2154, Auth: "/internal/camera/legacy-auth"),
    (Name: "PlainFtpLan", Edge: "camera-edge-plain", Control: 2155, Start: 2156, End: 2171, Auth: "/internal/camera/plain-auth"),
}.Where(profile => builder.Configuration.GetValue<bool>($"ShutterSpool:CameraIngress:Profiles:{profile.Name}:Enabled"))
    .Select(profile => (
        profile.Name, profile.Edge, profile.Auth,
        Control: builder.Configuration.GetValue($"ShutterSpool:CameraIngress:Profiles:{profile.Name}:ControlPort", profile.Control),
        Start: builder.Configuration.GetValue($"ShutterSpool:CameraIngress:Profiles:{profile.Name}:PassiveStart", profile.Start),
        End: builder.Configuration.GetValue($"ShutterSpool:CameraIngress:Profiles:{profile.Name}:PassiveEnd", profile.End)))
    .ToArray();
foreach (string profile in new[] { "ModernFtps", "LegacyFtps", "PlainFtpLan" })
    server.WithEnvironment($"ShutterSpool__CameraIngress__Profiles__{profile}__Enabled",
        profiles.Any(enabled => enabled.Name == profile) ? "true" : "false");
server.WithEnvironment("ShutterSpool__CameraIngress__Enabled", profiles.Length > 0 ? "true" : "false")
    .WithEnvironment("ShutterSpool__CameraIngress__Hostname", builder.Configuration["ShutterSpool:CameraIngress:Hostname"] ?? "ingest.shutterspool.com");

if (profiles.Length > 0)
{
    int maxConnections = builder.Configuration.GetValue("ShutterSpool:CameraIngress:MaxConnections", 16);
    int maxPerHost = builder.Configuration.GetValue("ShutterSpool:CameraIngress:MaxPerHostConnections", 8);
    int idleMinutes = builder.Configuration.GetValue("ShutterSpool:CameraIngress:IdleMinutes", 5);
    int hookTimeout = builder.Configuration.GetValue("ShutterSpool:CameraIngress:HookTimeoutSeconds", 60);
    long edgeBufferBytes = builder.Configuration.GetValue("ShutterSpool:CameraIngress:EdgeBufferBytes", 1073741824L);
    string listenAddress = builder.Configuration["ShutterSpool:CameraIngress:ListenAddress"]
        ?? builder.Configuration["ShutterSpool:CameraIngress:AdvertisedAddress"] ?? "127.0.0.1";
    string advertisedAddress = builder.Configuration["ShutterSpool:CameraIngress:AdvertisedAddress"] ?? listenAddress;
    if (maxConnections is < 1 or > 128 || maxPerHost < 1 || maxPerHost > maxConnections
        || idleMinutes is < 1 or > 60 || hookTimeout is < 1 or > 300 || edgeBufferBytes is < 1048576 or > 17179869184
        || !System.Net.IPAddress.TryParse(listenAddress, out var listenIp) || listenIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
        || !System.Net.IPAddress.TryParse(advertisedAddress, out var advertisedIp) || advertisedIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        throw new InvalidOperationException("Camera ingress needs a valid IPv4 address, control port and separate passive range of 2–64 ports.");
    var occupiedPorts = new HashSet<int>();
    foreach (var profile in profiles)
    {
        if (profile.Control is < 1024 or > 65535 || profile.Start is < 1024 or > 65534 || profile.End <= profile.Start
            || profile.End > 65535 || profile.End - profile.Start >= 64
            || !occupiedPorts.Add(profile.Control))
            throw new InvalidOperationException("Camera profiles need valid, disjoint control/passive ports with 2–64 passive ports each.");
        for (int port = profile.Start; port <= profile.End; port++)
            if (!occupiedPorts.Add(port))
                throw new InvalidOperationException("Camera profile control/passive ports must not overlap.");
    }

    string certificateDirectory = builder.Configuration["ShutterSpool:CameraIngress:CertificateDirectory"] ?? "";
    string hostname = builder.Configuration["ShutterSpool:CameraIngress:Hostname"] ?? "ingest.shutterspool.com";
    if (profiles.Any(profile => profile.Name != "PlainFtpLan"))
    {
        if (string.IsNullOrWhiteSpace(certificateDirectory))
            throw new InvalidOperationException("Explicit FTPS requires ShutterSpool:CameraIngress:CertificateDirectory containing server.pem and root.pem.");
        certificateDirectory = Path.GetFullPath(certificateDirectory);
        if (!builder.ExecutionContext.IsPublishMode)
            CameraIngressCertificate.Validate(Path.Combine(certificateDirectory, "server.pem"),
                Path.Combine(certificateDirectory, "root.pem"), hostname);
    }

    var callbackKey = builder.AddParameter("camera-callback-key", new GenerateParameterDefault
    {
        MinLength = 64, Lower = true, Upper = true, Numeric = true, Special = false,
    }, secret: true, persist: true);
    server.WithEndpoint(scheme: "http", name: "camera-internal", env: "ShutterSpool__CameraIngress__InternalPort", isExternal: false)
        .WithEnvironment("ShutterSpool__CameraIngress__Enabled", "true")
        .WithEnvironment("ShutterSpool__CameraIngress__CallbackKey", callbackKey)
        .WithEnvironment("ShutterSpool__CameraIngress__CallbackBaseUrl", ReferenceExpression.Create($"http://host.docker.internal:{server.GetEndpoint("camera-internal").Property(EndpointProperty.Port)}/internal/camera"));
    foreach (string option in new[] { "MaximumUploadBytes", "MaximumConcurrentTransfers", "MaximumSessionsPerSource", "UploadBandwidthKiBPerSecond", "TransferTimeoutSeconds" })
    {
        string? value = builder.Configuration[$"ShutterSpool:CameraIngress:{option}"];
        if (value is not null) server.WithEnvironment($"ShutterSpool__CameraIngress__{option}", value);
    }

    void AddCameraEdge(string name, string profile, int edgeControlPort, int edgePassiveStart, int edgePassiveEnd,
        string authPath, bool secure, bool allowLegacyCiphers)
    {
        var edgePin = imagePins["camera-edge"];
        var edge = builder.AddContainer(name, edgePin.Image, edgePin.Tag)
            .WithImageSHA256(edgePin.Sha256)
            .WithContainerRuntimeArgs(context =>
            {
                context.Args.Add($"--tmpfs=/tmp:rw,noexec,nosuid,size={edgeBufferBytes}");
                context.Args.Add($"--label=shutterspool.camera-ingress-profile={profile}");
            })
            .WithArgs("sftpgo", "serve", "--log-file-path", "/dev/null", "--log-level", "error")
            .WithEndpoint(port: edgeControlPort, targetPort: 2121, scheme: "tcp", name: "ftp", isProxied: false, isExternal: true)
            .WithEndpoint("ftp", endpoint => endpoint.TargetHost = listenAddress)
            .WithEnvironment("SFTPGO_SFTPD__BINDINGS__0__PORT", "0")
            .WithEnvironment("SFTPGO_HTTPD__BINDINGS__0__PORT", "0")
            .WithEnvironment("SFTPGO_FTPD__BINDINGS__0__PORT", "2121")
            .WithEnvironment("SFTPGO_FTPD__BINDINGS__0__FORCE_PASSIVE_IP", advertisedAddress)
            .WithEnvironment("SFTPGO_FTPD__PASSIVE_PORT_RANGE__START", edgePassiveStart.ToString())
            .WithEnvironment("SFTPGO_FTPD__PASSIVE_PORT_RANGE__END", edgePassiveEnd.ToString())
            .WithEnvironment("SFTPGO_FTPD__DISABLE_ACTIVE_MODE", "true")
            .WithEnvironment("SFTPGO_FTPD__ENABLE_SITE", "false")
            .WithEnvironment("SFTPGO_FTPD__HASH_SUPPORT", "0")
            .WithEnvironment("SFTPGO_FTPD__COMBINE_SUPPORT", "0")
            .WithEnvironment("SFTPGO_COMMON__IDLE_TIMEOUT", idleMinutes.ToString())
            .WithEnvironment("SFTPGO_COMMON__MAX_TOTAL_CONNECTIONS", maxConnections.ToString())
            .WithEnvironment("SFTPGO_COMMON__MAX_PER_HOST_CONNECTIONS", maxPerHost.ToString())
            .WithEnvironment("SFTPGO_COMMON__TEMP_PATH", "/tmp")
            .WithEnvironment("SFTPGO_COMMON__DEFENDER__ENABLED", "true")
            .WithEnvironment("SFTPGO_DATA_PROVIDER__DRIVER", "memory")
            .WithEnvironment("SFTPGO_DATA_PROVIDER__EXTERNAL_AUTH_SCOPE", "1")
            .WithEnvironment("SFTPGO_DATA_PROVIDER__EXTERNAL_AUTH_HOOK", ReferenceExpression.Create(
                $"http://host.docker.internal:{server.GetEndpoint("camera-internal").Property(EndpointProperty.Port)}{authPath}"))
            .WithEnvironment("SFTPGO_COMMON__ACTIONS__HOOK", ReferenceExpression.Create($"http://host.docker.internal:{server.GetEndpoint("camera-internal").Property(EndpointProperty.Port)}/internal/camera/hook"))
            .WithEnvironment("SFTPGO_COMMON__ACTIONS__EXECUTE_ON", "pre-upload,upload")
            .WithEnvironment("SFTPGO_COMMON__ACTIONS__EXECUTE_SYNC", "upload")
            .WithEnvironment("SFTPGO_HTTP__HEADERS__0__KEY", "X-API-KEY")
            .WithEnvironment("SFTPGO_HTTP__HEADERS__0__VALUE", callbackKey)
            .WithEnvironment("SFTPGO_HTTP__TIMEOUT", hookTimeout.ToString())
            .WithEnvironment("SFTPGO_HTTP__RETRY_MAX", "0")
            .WaitFor(server);
        if (secure)
        {
            edge.WithBindMount(certificateDirectory, "/etc/sftpgo/certs", isReadOnly: true)
                .WithEnvironment("SFTPGO_FTPD__BINDINGS__0__TLS_MODE", "1")
                .WithEnvironment("SFTPGO_FTPD__BINDINGS__0__MIN_TLS_VERSION", "12")
                .WithEnvironment("SFTPGO_FTPD__BINDINGS__0__CLIENT_AUTH_TYPE", "0")
                .WithEnvironment("SFTPGO_FTPD__BINDINGS__0__CERTIFICATE_FILE", "/etc/sftpgo/certs/server.pem")
                .WithEnvironment("SFTPGO_FTPD__BINDINGS__0__CERTIFICATE_KEY_FILE", "/etc/sftpgo/certs/server.pem");
            if (allowLegacyCiphers)
                edge.WithEnvironment("SFTPGO_FTPD__BINDINGS__0__TLS_CIPHER_SUITES",
                    "TLS_RSA_WITH_AES_128_CBC_SHA,TLS_RSA_WITH_AES_256_CBC_SHA");
        }
        else
        {
            edge.WithEnvironment("SFTPGO_FTPD__BINDINGS__0__TLS_MODE", "0");
        }
        for (int port = edgePassiveStart; port <= edgePassiveEnd; port++)
            edge.WithEndpoint(port: port, targetPort: port, scheme: "tcp", name: $"passive-{port}", isProxied: false, isExternal: true)
                .WithEndpoint($"passive-{port}", endpoint => endpoint.TargetHost = listenAddress);
    }

    foreach (var profile in profiles)
    {
        server.WithEnvironment($"ShutterSpool__CameraIngress__Profiles__{profile.Name}__ControlPort", profile.Control.ToString());
        if (profile.Name != "PlainFtpLan")
            server.WithEnvironment($"ShutterSpool__CameraIngress__Profiles__{profile.Name}__CaRootPath", Path.Combine(certificateDirectory, "root.pem"));
        AddCameraEdge(profile.Edge, profile.Name, profile.Control, profile.Start, profile.End,
            profile.Auth, profile.Name != "PlainFtpLan", profile.Name == "LegacyFtps");
    }
}

if (builder.ExecutionContext.IsPublishMode) ComposePackage.Configure(builder);
builder.Build().Run();
