using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShutterSpool.Server.Security;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace ShutterSpool.IntegrationTests;

public sealed class SecurityConfigurationTests
{
    [Fact]
    public void Private_metadata_keeps_public_authority_for_tokens_and_uses_configured_role_claim()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ShutterSpool:Security:Authority"] = "https://identity.example.invalid/realms/shutterspool",
            ["ShutterSpool:Security:Audience"] = "shutterspool-server",
            ["ShutterSpool:Security:MetadataAddress"] = "http://keycloak:8080/realms/shutterspool/.well-known/openid-configuration",
            ["ShutterSpool:Security:AllowInsecureMetadataAddress"] = "true",
            ["ShutterSpool:Security:AdministratorRoleClaimType"] = "groups",
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddShutterSpoolSecurity(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        JwtBearerOptions options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal("http://keycloak:8080/realms/shutterspool/.well-known/openid-configuration", options.MetadataAddress);
        Assert.Equal("https://identity.example.invalid/realms/shutterspool", options.TokenValidationParameters.ValidIssuer);
        Assert.Equal("groups", options.TokenValidationParameters.RoleClaimType);
        Assert.False(options.RequireHttpsMetadata);
    }

    [Fact]
    public void Default_role_claim_type_remains_framework_compatible()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ShutterSpool:Security:Authority"] = "https://identity.example.invalid/realms/shutterspool",
            ["ShutterSpool:Security:Audience"] = "shutterspool-server",
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddShutterSpoolSecurity(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        JwtBearerOptions options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal(System.Security.Claims.ClaimsIdentity.DefaultRoleClaimType, options.TokenValidationParameters.RoleClaimType);
        Assert.Equal("https://identity.example.invalid/realms/shutterspool", options.TokenValidationParameters.ValidIssuer);
        Assert.True(options.RequireHttpsMetadata);
    }

    [Fact]
    public void Signed_tokens_accept_only_public_authority_and_use_configured_role_claim()
    {
        const string publicIssuer = "https://identity.example.invalid/realms/shutterspool";
        const string metadataIssuer = "http://keycloak:8080/realms/shutterspool";
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ShutterSpool:Security:Authority"] = publicIssuer,
            ["ShutterSpool:Security:Audience"] = "shutterspool-server",
            ["ShutterSpool:Security:MetadataAddress"] = "http://keycloak:8080/realms/shutterspool/.well-known/openid-configuration",
            ["ShutterSpool:Security:AllowInsecureMetadataAddress"] = "true",
            ["ShutterSpool:Security:AdministratorRoleClaimType"] = "groups",
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddShutterSpoolSecurity(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        TokenValidationParameters parameters = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters.Clone();
        byte[] signingKey = Encoding.UTF8.GetBytes("synthetic signing key for issuer and role validation tests");
        parameters.IssuerSigningKey = new SymmetricSecurityKey(signingKey);
        parameters.ValidateIssuerSigningKey = true;
        parameters.ValidateLifetime = true;
        parameters.ValidAudience = "shutterspool-server";
        parameters.ValidateAudience = true;
        // JwtBearerHandler adds the discovery document issuer to ValidIssuers.
        parameters.ValidIssuers = [publicIssuer, metadataIssuer];

        ClaimsPrincipal principal = new JwtSecurityTokenHandler().ValidateToken(
            CreateToken(publicIssuer, "shutterspool-server", signingKey), parameters, out _);

        Assert.True(principal.IsInRole("shutterspool-admin"));
        Assert.Throws<SecurityTokenInvalidIssuerException>(() => new JwtSecurityTokenHandler().ValidateToken(
            CreateToken(metadataIssuer, "shutterspool-server", signingKey), parameters, out _));
    }

    private static string CreateToken(string issuer, string audience, byte[] signingKey)
    {
        DateTime now = DateTime.UtcNow;
        JwtSecurityToken token = new(issuer, audience,
            [new Claim("groups", "shutterspool-admin")], now.AddMinutes(-1), now.AddMinutes(2),
            new SigningCredentials(new SymmetricSecurityKey(signingKey), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
