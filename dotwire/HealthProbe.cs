namespace Dotwire;

public static class HealthProbe
{
    public static Uri DefaultUri()
    {
        var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        var port = string.IsNullOrWhiteSpace(ports) ? "8080" : ports.Split([';', ','], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return new Uri($"http://127.0.0.1:{port}/healthz");
    }

    public static async Task<int> RunAsync(HttpClient http, Uri uri)
    {
        try
        {
            using var response = await http.GetAsync(uri);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception)
        {
            return 1;
        }
        finally
        {
            http.Dispose();
        }
    }
}
