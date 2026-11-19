using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;

namespace ShutterSpool.Server.Security;

public static class SecurityExtensions
{
    public static IServiceCollection AddShutterSpoolSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        SecurityOptions options = configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();
        options.Validate();
        services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.SectionName));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt => ConfigureJwtBearer(jwt, options));
        services.AddScoped<IAuthorizationHandler, LocalGrantAuthorizationHandler>();
        services.AddHttpContextAccessor();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, WorkspaceAuthorizationResultHandler>();
        var authorization = services.AddAuthorizationBuilder();
        foreach (string permission in new[] { WorkspacePolicies.Member, WorkspacePolicies.Curate,
            WorkspacePolicies.Administer, WorkspacePolicies.ManageOwners, WorkspacePolicies.Unclassified })
        {
            authorization.AddPolicy(permission, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new LocalGrantRequirement(permission));
            });
        }
        services.AddCors(cors => cors.AddPolicy("live-hub", policy =>
        {
            if (options.AllowedOrigins.Length > 0)
                policy.WithOrigins(options.AllowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        }));
        services.AddSignalR(hubs => hubs.EnableDetailedErrors = false);
        return services;
    }

    private static void ConfigureJwtBearer(JwtBearerOptions jwt, SecurityOptions options)
    {
        jwt.Events = new JwtBearerEvents
        {
            OnMessageReceived = ReceiveLiveHubTokenAsync,
            OnTokenValidated = TokenValidatedAsync,
            OnAuthenticationFailed = AuthenticationFailedAsync,
        };
        if (Uri.TryCreate(options.Authority, UriKind.Absolute, out Uri? authority) && !string.IsNullOrWhiteSpace(options.Audience))
        {
            string publicIssuer = authority.AbsoluteUri.TrimEnd('/');
            jwt.Authority = publicIssuer;
            if (!string.IsNullOrWhiteSpace(options.MetadataAddress))
                jwt.MetadataAddress = new Uri(options.MetadataAddress, UriKind.Absolute).AbsoluteUri;
            jwt.Audience = options.Audience;
            jwt.RequireHttpsMetadata = string.IsNullOrWhiteSpace(options.MetadataAddress)
                ? !authority.IsLoopback
                : new Uri(options.MetadataAddress, UriKind.Absolute).Scheme == Uri.UriSchemeHttps;
            jwt.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
            jwt.TokenValidationParameters.ValidateIssuer = true;
            jwt.TokenValidationParameters.ValidIssuer = publicIssuer;
            jwt.TokenValidationParameters.IssuerValidator = (issuer, _, _) =>
                string.Equals(issuer, publicIssuer, StringComparison.Ordinal)
                    ? issuer
                    : throw new SecurityTokenInvalidIssuerException("The token issuer does not match the configured public OIDC authority.");
        }
        else
        {
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.Zero,
            };
        }
    }

    private static Task ReceiveLiveHubTokenAsync(MessageReceivedContext context)
    {
        if (IsLiveHubPath(context.Request.Path)
            && context.Request.Headers.Authorization.Count == 0
            && context.Request.Query.TryGetValue("access_token", out var token)
            && !string.IsNullOrWhiteSpace(token))
            context.Token = token;
        return Task.CompletedTask;
    }

    private static Task TokenValidatedAsync(TokenValidatedContext context)
    {
        if (context.Principal?.Identity is ClaimsIdentity identity && context.SecurityToken is not null)
            identity.AddClaim(new Claim("iss", context.SecurityToken.Issuer));

        if (context.Request.Path.StartsWithSegments("/management"))
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SecurityAudit").ManagementAuthenticationSucceeded();
        return Task.CompletedTask;
    }

    private static Task AuthenticationFailedAsync(AuthenticationFailedContext context)
    {
        if (context.Request.Path.StartsWithSegments("/management"))
        {
            context.HttpContext.Items["security.authentication-failed"] = true;
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SecurityAudit").ManagementAuthenticationFailed();
        }
        return Task.CompletedTask;
    }

    private static bool IsLiveHubPath(PathString path) => path == "/live/changes"
        || path == "/live/changes/negotiate";
}
