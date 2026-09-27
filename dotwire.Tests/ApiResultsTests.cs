using System.Text.Json;
using Dotwire.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace dotwire.Tests;

public class ApiResultsTests
{
    private static async Task<(int StatusCode, string? RetryAfter, JsonDocument Body)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        body.Position = 0;
        var doc = await JsonDocument.ParseAsync(body);
        var retryAfter = context.Response.Headers.RetryAfter.Count > 0
            ? context.Response.Headers.RetryAfter.ToString()
            : null;
        return (context.Response.StatusCode, retryAfter, doc);
    }

    [Fact]
    public async Task InvalidUserId_Is400WithCode()
    {
        var (status, _, body) = await ExecuteAsync(ApiResults.InvalidUserId());
        Assert.Equal(400, status);
        Assert.Equal("invalid_user_id", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task InvalidContent_Is400WithCode()
    {
        var (status, _, body) = await ExecuteAsync(ApiResults.InvalidContent());
        Assert.Equal(400, status);
        Assert.Equal("invalid_content", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task InvalidRequest_CarriesOptionalReason()
    {
        var (status, _, body) = await ExecuteAsync(ApiResults.InvalidRequest("bad shape"));
        Assert.Equal(400, status);
        Assert.Equal("invalid_request", body.RootElement.GetProperty("error").GetString());
        Assert.Equal("bad shape", body.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task PresendRejected_Is422WithReason()
    {
        var (status, _, body) = await ExecuteAsync(ApiResults.PresendRejected("blocked content"));
        Assert.Equal(422, status);
        Assert.Equal("presend_rejected", body.RootElement.GetProperty("error").GetString());
        Assert.Equal("blocked content", body.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task RateLimited_Is429WithRetryAfterHeader()
    {
        var (status, retryAfter, body) = await ExecuteAsync(ApiResults.RateLimited(7));
        Assert.Equal(429, status);
        Assert.Equal("rate_limited", body.RootElement.GetProperty("error").GetString());
        Assert.Equal("7", retryAfter);
    }

    [Fact]
    public async Task PresendUnavailable_Is503WithCode()
    {
        var (status, _, body) = await ExecuteAsync(ApiResults.PresendUnavailable());
        Assert.Equal(503, status);
        Assert.Equal("presend_unavailable", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AuditUnavailable_Is503WithCode()
    {
        var (status, _, body) = await ExecuteAsync(ApiResults.AuditUnavailable());
        Assert.Equal(503, status);
        Assert.Equal("audit_unavailable", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task StreamUnavailable_Is503WithCode()
    {
        var (status, _, body) = await ExecuteAsync(ApiResults.StreamUnavailable());
        Assert.Equal(503, status);
        Assert.Equal("stream_unavailable", body.RootElement.GetProperty("error").GetString());
    }
}
