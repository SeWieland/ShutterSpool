using Microsoft.Extensions.Configuration;
using ShutterSpool.AppHost;

var imagePins = ImagePins.Load();
var builder = DistributedApplication.CreateBuilder(args);
if (!builder.ExecutionContext.IsPublishMode) builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables();

var postgresPassword = builder.AddParameter("postgres-password", secret: true);
var keycloakAdministrator = builder.AddParameter("keycloak-administrator");
var keycloakAdministratorPassword = builder.AddParameter("keycloak-administrator-password", secret: true);
var enrollmentClientSecret = builder.AddParameter("keycloak-enrollment-client-secret", new GenerateParameterDefault
{
    MinLength = 32, Lower = true, Upper = true, Numeric = true, Special = false,
}, secret: true, persist: true);
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
    .WithHostPort(builder.Configuration.GetValue("ShutterSpool:Development:DatabasePort", 54329))
    .WithDataVolume("shutterspool-postgres");
postgres.AddDatabase("keycloak-data", "keycloak");
var valkey = builder.AddValkey("valkey")
    .WithDataVolume("shutterspool-valkey");
var postgresEndpoint = postgres.GetEndpoint("tcp");
int identityPort = builder.Configuration.GetValue("ShutterSpool:Development:IdentityPort", 8082);
var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.6")
    .WithHttpEndpoint(port: identityPort, targetPort: 8080)
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
    .WithEnvironment("SHUTTERSPOOL_ENROLLMENT_CLIENT_SECRET", enrollmentClientSecret)
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
    .WithReference(valkey)
    .WaitFor(postgres)
    .WaitFor(valkey)
    .WaitForCompletion(keycloakReconciler)
    .WithEnvironment("ShutterSpool__Security__Authority", keycloakAuthority)
    .WithEnvironment("ShutterSpool__KeycloakEnrollment__ServerUrl", keycloak.GetEndpoint("http"))
    .WithEnvironment("ShutterSpool__KeycloakEnrollment__Issuer", keycloakAuthority)
    .WithEnvironment("ShutterSpool__KeycloakEnrollment__ClientSecret", enrollmentClientSecret)
    .WithEnvironment("ShutterSpool__Security__Audience", "shutterspool-server")
    .WithEnvironment("ShutterSpool__Security__EncryptionKey", securityEncryptionKey)
    .WithEnvironment("ShutterSpool__CameraCredentials__MinimumPasswordLength",
        builder.Configuration["ShutterSpool:CameraCredentials:MinimumPasswordLength"] ?? "12")
    .WithEnvironment("ShutterSpool__Spool__ReservedBytes", builder.Configuration["ShutterSpool:Spool:ReservedBytes"] ?? "1073741824")
    .WithEnvironment("ShutterSpool__Preview__ImaginaryUrl", imaginary.GetEndpoint("http"))
    .WithEnvironment("ShutterSpool__Preview__MaxConcurrentGenerations", previewGenerationSlots)
    .WithEnvironment("ShutterSpool__Preview__GenerationQueueTimeoutSeconds", previewQueueTimeout)
    .WithEnvironment("ShutterSpool__Preview__GenerationTimeoutSeconds", previewGenerationTimeout)
    .WithEnvironment("ShutterSpool__Preview__PrewarmMode", previewPrewarmMode);
if (!builder.ExecutionContext.IsPublishMode)
{
    string storageRoot = Path.GetFullPath(Path.Combine(
        builder.Configuration["ShutterSpool:Development:RootPath"] ?? "../../.data/workspaces-dev", "studio"));
    Directory.CreateDirectory(storageRoot);
    var controlRuntimePassword = builder.AddParameter("control-runtime-password", new GenerateParameterDefault
    {
        MinLength = 32, Lower = true, Upper = true, Numeric = true, Special = false,
    }, secret: true, persist: true);
    var studioRuntimePassword = builder.AddParameter("studio-runtime-password", new GenerateParameterDefault
    {
        MinLength = 32, Lower = true, Upper = true, Numeric = true, Special = false,
    }, secret: true, persist: true);
    var migrationPassword = builder.AddParameter("workspace-migration-password", new GenerateParameterDefault
    {
        MinLength = 32, Lower = true, Upper = true, Numeric = true, Special = false,
    }, secret: true, persist: true);
    var postgresImage = postgres.Resource.Annotations.OfType<ContainerImageAnnotation>().Single();
    var databases = builder.AddContainer("workspace-databases", postgresImage.Image,
            postgresImage.Tag ?? throw new InvalidOperationException("The PostgreSQL dependency requires an explicit image tag."))
        .WithEntrypoint("psql").WithArgs("-f", "/setup/workspace-databases.sql")
        .WithEnvironment("PGHOST", postgresEndpoint.Property(EndpointProperty.Host))
        .WithEnvironment("PGPORT", postgresEndpoint.Property(EndpointProperty.Port))
        .WithEnvironment("PGUSER", "postgres").WithEnvironment("PGDATABASE", "postgres")
        .WithEnvironment("PGPASSWORD", postgresPassword)
        .WithEnvironment("CONTROL_RUNTIME_PASSWORD", controlRuntimePassword)
        .WithEnvironment("STUDIO_RUNTIME_PASSWORD", studioRuntimePassword).WithEnvironment("MIGRATION_PASSWORD", migrationPassword)
        .WithBindMount("../../deploy/package/workspace-databases.sql", "/setup/workspace-databases.sql", isReadOnly: true)
        .WaitFor(postgres);

    void ConfigureWorkspace(IResourceBuilder<ProjectResource> resource, bool maintenance)
    {
        resource.WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("ShutterSpool__Security__Authority", keycloakAuthority)
            .WithEnvironment("ShutterSpool__Security__EncryptionKey", securityEncryptionKey)
            .WithEnvironment("ShutterSpool__Workspaces__Definitions__studio__StorageRoot", storageRoot)
            .WithEnvironment("ShutterSpool__Workspaces__Definitions__studio__OwnerIssuer", keycloakAuthority)
            .WithEnvironment("ShutterSpool__Workspaces__Definitions__studio__OwnerSubject", shutterspoolAdministratorSubject)
            .WithEnvironment("ShutterSpool__Workspaces__Definitions__studio__SetupOperatorIdentity", Environment.UserName)
            .WithEnvironment("ConnectionStrings__shutterspool-control", ReferenceExpression.Create(
                $"Host={postgresEndpoint.Property(EndpointProperty.Host)};Port={postgresEndpoint.Property(EndpointProperty.Port)};Database=shutterspool_control;Username=shutterspool_control_runtime;Password={controlRuntimePassword}"))
            .WithEnvironment("ConnectionStrings__Workspaces__studio__Runtime", ReferenceExpression.Create(
                $"Host={postgresEndpoint.Property(EndpointProperty.Host)};Port={postgresEndpoint.Property(EndpointProperty.Port)};Database=shutterspool_studio;Username=shutterspool_studio_runtime;Password={studioRuntimePassword}"));
        if (maintenance)
            resource.WithEnvironment("ConnectionStrings__shutterspool-control-migration", ReferenceExpression.Create(
                $"Host={postgresEndpoint.Property(EndpointProperty.Host)};Port={postgresEndpoint.Property(EndpointProperty.Port)};Database=shutterspool_control;Username=shutterspool_migration;Password={migrationPassword}"))
                .WithEnvironment("ConnectionStrings__Workspaces__studio__Migration", ReferenceExpression.Create(
                    $"Host={postgresEndpoint.Property(EndpointProperty.Host)};Port={postgresEndpoint.Property(EndpointProperty.Port)};Database=shutterspool_studio;Username=shutterspool_migration;Password={migrationPassword}"));
    }
    var control = builder.AddProject<Projects.ShutterSpool_Server>("workspace-control-init", options => options.ExcludeLaunchProfile = true)
        .WithArgs("--workspace-control-init").WaitForCompletion(databases).WaitForCompletion(keycloakReconciler);
    var workspace = builder.AddProject<Projects.ShutterSpool_Server>("workspace-provision", options => options.ExcludeLaunchProfile = true)
        .WithArgs("--workspace-provision", "studio").WaitForCompletion(control);
    ConfigureWorkspace(control, maintenance: true);
    ConfigureWorkspace(workspace, maintenance: true);
    ConfigureWorkspace(server, maintenance: false);
    server.WaitForCompletion(workspace);
}
foreach ((string key, string value) in WorkspaceRuntimeConfiguration.ForServer(builder.Configuration))
    server.WithEnvironment(key, value);
if (!builder.ExecutionContext.IsPublishMode) builder.AddJavaScriptApp("web", "../ShutterSpool.Web")
    .WithReference(server)
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints();

ConfigureCameraIngress(builder, server, imagePins);
if (builder.ExecutionContext.IsPublishMode) ComposePackage.Configure(builder);
await builder.Build().RunAsync();

static void ConfigureCameraIngress(IDistributedApplicationBuilder builder, IResourceBuilder<ProjectResource> server,
    IReadOnlyDictionary<string, ImagePin> imagePins)
{
    var profiles = CameraIngressProfile.ReadEnabled(builder.Configuration);
    foreach (string profile in CameraIngressProfile.Names)
        server.WithEnvironment($"ShutterSpool__CameraIngress__Profiles__{profile}__Enabled",
            profiles.Any(enabled => enabled.Name == profile) ? "true" : "false");
    server.WithEnvironment("ShutterSpool__CameraIngress__Enabled", profiles.Length > 0 ? "true" : "false")
        .WithEnvironment("ShutterSpool__CameraIngress__Hostname", builder.Configuration["ShutterSpool:CameraIngress:Hostname"] ?? "ingest.shutterspool.com");
    if (profiles.Length == 0) return;

    var settings = new CameraIngressConfiguration(builder.Configuration, profiles, builder.ExecutionContext.IsPublishMode);
    var callbackKey = builder.AddParameter("camera-callback-key", new GenerateParameterDefault
    {
        MinLength = 64, Lower = true, Upper = true, Numeric = true, Special = false,
    }, secret: true, persist: true);
    server.WithEndpoint(scheme: "http", name: "camera-internal", env: "ShutterSpool__CameraIngress__InternalPort", isExternal: false)
        .WithEnvironment("ShutterSpool__CameraIngress__Enabled", "true")
        .WithEnvironment("ShutterSpool__CameraIngress__CallbackKey", callbackKey)
        .WithEnvironment("ShutterSpool__CameraIngress__CallbackBaseUrl", ReferenceExpression.Create($"http://host.docker.internal:{server.GetEndpoint("camera-internal").Property(EndpointProperty.Port)}/internal/camera"));
    ConfigureCameraIngressLimits(builder, server);

    foreach (var profile in profiles)
    {
        server.WithEnvironment($"ShutterSpool__CameraIngress__Profiles__{profile.Name}__ControlPort", profile.Control.ToString());
        if (profile.RequiresTls)
            server.WithEnvironment($"ShutterSpool__CameraIngress__Profiles__{profile.Name}__CaRootPath", Path.Combine(settings.CertificateDirectory, "root.pem"));
        AddCameraEdge(builder, server, settings, profile, imagePins["camera-edge"], callbackKey);
    }
}

static void ConfigureCameraIngressLimits(IDistributedApplicationBuilder builder, IResourceBuilder<ProjectResource> server)
{
    foreach (string option in new[] { "MaximumUploadBytes", "MaximumConcurrentTransfers", "MaximumSessionsPerSource", "UploadBandwidthKiBPerSecond", "TransferTimeoutSeconds" })
    {
        string? value = builder.Configuration[$"ShutterSpool:CameraIngress:{option}"];
        if (value is not null) server.WithEnvironment($"ShutterSpool__CameraIngress__{option}", value);
    }
}

static void AddCameraEdge(IDistributedApplicationBuilder builder, IResourceBuilder<ProjectResource> server,
    CameraIngressConfiguration settings, CameraIngressProfile profile, ImagePin edgePin, IResourceBuilder<ParameterResource> callbackKey)
{
    string authPath = profile.AuthPath;
    var edge = builder.AddContainer(profile.Edge, edgePin.Image, edgePin.Tag)
        .WithImageSHA256(edgePin.Sha256)
        .WithContainerRuntimeArgs(context =>
        {
            context.Args.Add($"--tmpfs=/tmp:rw,noexec,nosuid,size={settings.EdgeBufferBytes}");
            context.Args.Add($"--label=shutterspool.camera-ingress-profile={profile.Name}");
        })
        .WithArgs("sftpgo", "serve", "--log-file-path", "/dev/null", "--log-level", "error")
        .WithEndpoint(port: profile.Control, targetPort: 2121, scheme: "tcp", name: "ftp", isProxied: false, isExternal: true)
        .WithEndpoint("ftp", endpoint => endpoint.TargetHost = settings.ListenAddress)
        .WithEnvironment("SFTPGO_SFTPD__BINDINGS__0__PORT", "0")
        .WithEnvironment("SFTPGO_HTTPD__BINDINGS__0__PORT", "0")
        .WithEnvironment("SFTPGO_FTPD__BINDINGS__0__PORT", "2121")
        .WithEnvironment("SFTPGO_FTPD__BINDINGS__0__FORCE_PASSIVE_IP", settings.AdvertisedAddress)
        .WithEnvironment("SFTPGO_FTPD__PASSIVE_PORT_RANGE__START", profile.Start.ToString())
        .WithEnvironment("SFTPGO_FTPD__PASSIVE_PORT_RANGE__END", profile.End.ToString())
        .WithEnvironment("SFTPGO_FTPD__DISABLE_ACTIVE_MODE", "true")
        .WithEnvironment("SFTPGO_FTPD__ENABLE_SITE", "false")
        .WithEnvironment("SFTPGO_FTPD__HASH_SUPPORT", "0")
        .WithEnvironment("SFTPGO_FTPD__COMBINE_SUPPORT", "0")
        .WithEnvironment("SFTPGO_COMMON__IDLE_TIMEOUT", settings.IdleMinutes.ToString())
        .WithEnvironment("SFTPGO_COMMON__MAX_TOTAL_CONNECTIONS", settings.MaxConnections.ToString())
        .WithEnvironment("SFTPGO_COMMON__MAX_PER_HOST_CONNECTIONS", settings.MaxPerHost.ToString())
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
        .WithEnvironment("SFTPGO_HTTP__TIMEOUT", settings.HookTimeout.ToString())
        .WithEnvironment("SFTPGO_HTTP__RETRY_MAX", "0")
        .WaitFor(server);
    if (profile.RequiresTls)
    {
        edge.WithBindMount(settings.CertificateDirectory, "/etc/sftpgo/certs", isReadOnly: true)
            .WithEnvironment("SFTPGO_FTPD__BINDINGS__0__TLS_MODE", "1")
            .WithEnvironment("SFTPGO_FTPD__BINDINGS__0__MIN_TLS_VERSION", "12")
            .WithEnvironment("SFTPGO_FTPD__BINDINGS__0__CLIENT_AUTH_TYPE", "0")
            .WithEnvironment("SFTPGO_FTPD__BINDINGS__0__CERTIFICATE_FILE", "/etc/sftpgo/certs/server.pem")
            .WithEnvironment("SFTPGO_FTPD__BINDINGS__0__CERTIFICATE_KEY_FILE", "/etc/sftpgo/certs/server.pem");
        if (profile.AllowsLegacyCiphers)
            edge.WithEnvironment("SFTPGO_FTPD__BINDINGS__0__TLS_CIPHER_SUITES",
                "TLS_RSA_WITH_AES_128_CBC_SHA,TLS_RSA_WITH_AES_256_CBC_SHA");
    }
    else
    {
        edge.WithEnvironment("SFTPGO_FTPD__BINDINGS__0__TLS_MODE", "0");
    }
    for (int port = profile.Start; port <= profile.End; port++)
        edge.WithEndpoint(port: port, targetPort: port, scheme: "tcp", name: $"passive-{port}", isProxied: false, isExternal: true)
            .WithEndpoint($"passive-{port}", endpoint => endpoint.TargetHost = settings.ListenAddress);
}
