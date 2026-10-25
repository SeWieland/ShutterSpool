using Microsoft.EntityFrameworkCore;
using ShutterSpool.Infrastructure.Persistence;
using System.Net;
using System.Net.Http.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Management_reads_tests_and_batches_deny_anonymous_and_revoked_grants_before_work()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = (await SeedMetadataCapturesAsync(fixture, 1))[0];
        Guid missing = Guid.NewGuid();
        (HttpMethod Method, string Path)[] requests =
        [
            (HttpMethod.Get, "/management/sources"),
            (HttpMethod.Get, $"/management/sources/{missing}"),
            (HttpMethod.Get, "/management/destinations"),
            (HttpMethod.Get, "/management/destinations/local-folders"),
            (HttpMethod.Get, $"/management/destinations/{missing}"),
            (HttpMethod.Get, $"/management/destinations/{missing}/work"),
            (HttpMethod.Get, "/management/collections"),
            (HttpMethod.Get, "/management/tags?q=travel"),
            (HttpMethod.Get, "/management/captures"),
            (HttpMethod.Get, $"/management/captures/{captureId}"),
            (HttpMethod.Get, $"/management/captures/{captureId}/routing"),
            (HttpMethod.Get, $"/management/captures/{captureId}/deliveries"),
            (HttpMethod.Get, $"/management/captures/{captureId}/actions"),
            (HttpMethod.Get, $"/management/deliveries/{missing}/attempts"),
            (HttpMethod.Post, $"/management/destinations/{missing}/local/test"),
            (HttpMethod.Post, $"/management/destinations/{missing}/webdav/test"),
            (HttpMethod.Post, "/management/captures/batch/curation"),
            (HttpMethod.Post, "/management/collections"),
            (HttpMethod.Put, $"/management/captures/{captureId}/collection"),
            (HttpMethod.Post, "/management/captures/collection-moves"),
        ];
        var authorization = fixture.Client.DefaultRequestHeaders.Authorization;
        fixture.Client.DefaultRequestHeaders.Authorization = null;
        await AssertDeniedAsync(HttpStatusCode.Unauthorized);
        fixture.Client.DefaultRequestHeaders.Authorization = authorization;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            var grant = await database.AuthorizationGrants.SingleAsync(token);
            grant.IsActive = false;
            await database.SaveChangesAsync(token);
        }
        await AssertDeniedAsync(HttpStatusCode.Forbidden);
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.Equal(0, (await verify.Captures.SingleAsync(item => item.Id == captureId, token)).Revision);

        async Task AssertDeniedAsync(HttpStatusCode expected)
        {
            foreach ((HttpMethod method, string path) in requests)
            {
                using var request = new HttpRequestMessage(method, path);
                if (method == HttpMethod.Post || method == HttpMethod.Put)
                    request.Content = JsonContent.Create(new
                    {
                        items = new[] { new { id = captureId, expectedRevision = 0, rating = 5, label = (string?)null, tags = Array.Empty<string>() } },
                    });
                using HttpResponseMessage response = await fixture.Client.SendAsync(request, token);
                Assert.Equal(expected, response.StatusCode);
            }
        }
    }
}
