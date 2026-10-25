namespace ShutterSpool.Server.Hosting;

internal static class HealthCheckCommand
{
    public static async Task<int> RunAsync(HttpMessageHandler? handler = null)
    {
        using HttpClient client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(3);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, "http://127.0.0.1:8080/health/ready");
            request.Headers.Host = "localhost:8080";
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException) { return 1; }
        catch (TaskCanceledException) { return 1; }
    }
}
