using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;

namespace ShutterSpool.Server.Security;

public static class SecurityExtensions
{
    public const string ManagementAccessPolicy = "management-access";

    public static IServiceCollection AddShutterSpoolSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        SecurityOptions options = configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();
        options.Validate();
        services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.SectionName));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (IsLiveHubPath(context.HttpContext.Request.Path)
                            && !context.HttpContext.Request.Headers.Authorization.Any()
                            && context.Request.Query.TryGetValue("access_token", out var token)
                            && !string.IsNullOrWhiteSpace(token))
                            context.Token = token;
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        if (context.Principal?.Identity is ClaimsIdentity identity && context.SecurityToken is not null)
                        {
                            identity.AddClaim(new Claim("iss", context.SecurityToken.Issuer));
                        }

                        if (context.HttpContext.Request.Path.StartsWithSegments("/management"))
                        {
                            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SecurityAudit").ManagementAuthenticationSucceeded();
                        }

                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        if (context.HttpContext.Request.Path.StartsWithSegments("/management"))
                        {
                            context.HttpContext.Items["security.authentication-failed"] = true;
                            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SecurityAudit").ManagementAuthenticationFailed();
                        }

                        return Task.CompletedTask;
                    },
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
                    jwt.TokenValidationParameters.RoleClaimType = options.AdministratorRoleClaimType;
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
            });
        services.AddScoped<IAuthorizationHandler, LocalGrantAuthorizationHandler>();
        services.AddAuthorizationBuilder().AddPolicy(ManagementAccessPolicy, policy => policy.RequireRole(options.AdministratorRole).AddRequirements(new LocalGrantRequirement()));
        services.AddCors(cors => cors.AddPolicy("live-hub", policy =>
        {
            if (options.AllowedOrigins.Length > 0)
                policy.WithOrigins(options.AllowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        }));
        services.AddSignalR(hubs => hubs.EnableDetailedErrors = false);
        return services;
    }

    private static bool IsLiveHubPath(PathString path) => path == "/live/changes"
        || path == "/live/changes/negotiate";
}
