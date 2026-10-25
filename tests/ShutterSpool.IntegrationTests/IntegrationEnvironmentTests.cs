namespace ShutterSpool.IntegrationTests;

public sealed class IntegrationEnvironmentTests
{
    [Fact]
    public void Required_database_job_cannot_silently_skip_postgresql_evidence()
    {
        if (Environment.GetEnvironmentVariable("SHUTTERSPOOL_REQUIRE_POSTGRES_TESTS") == "true")
            Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION")),
                "The required integration job must provide its controlled PostgreSQL service connection.");
    }
}
