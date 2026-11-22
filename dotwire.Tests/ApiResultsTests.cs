using System.Diagnostics.Metrics;
using System.Text.Json;
using Dotwire.Api;
using Dotwire.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace dotwire.Tests;

public class ApiResultsTests
{
    private static async Task<(int StatusCode, string? RetryAfter, JsonDocument Body)> ExecuteAsync(IResult result, IServiceProvider? services = null)
    {
        services ??= new ServiceCollection()
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
    public async Task AuditUnavailable_RecordsOncePerExecution()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddMetrics()
            .AddSingleton<DotwireMetrics>()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { })
            .BuildServiceProvider();
        using var capture = new MetricCapture(services.GetRequiredService<IMeterFactory>());

        var (status, _, body) = await ExecuteAsync(ApiResults.AuditUnavailable(), services);

        Assert.Equal(503, status);
        Assert.Equal("audit_unavailable", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(1, capture.Sum("dotwire.audit.unavailable"));
    }

    [Fact]
    public async Task SendRateLimitFilter_RecordsRestSendRejection()
    {
        var services = new ServiceCollection()
            .AddMetrics()
            .AddSingleton<DotwireMetrics>()
            .BuildServiceProvider();
        using var capture = new MetricCapture(services.GetRequiredService<IMeterFactory>());
        var options = Options.Create(new RateLimitOptions
        {
            Send = new RateLimitBucketOptions { PermitLimit = 1, TokensPerPeriod = 1, PeriodSeconds = 3600 },
        });
        var filter = new SendRateLimitFilter(new SendRateLimiter(options), services.GetRequiredService<DotwireMetrics>());
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = HttpMethods.Post;
        http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", "u1")]));

        static ValueTask<object?> Next(EndpointFilterInvocationContext _) => ValueTask.FromResult<object?>(Results.Ok());
        await filter.InvokeAsync(EndpointFilterInvocationContext.Create(http), Next);
        var second = await filter.InvokeAsync(EndpointFilterInvocationContext.Create(http), Next) as IResult;

        var (status, _, _) = await ExecuteAsync(second!);
        Assert.Equal(429, status);
        Assert.Equal(1, capture.Sum("dotwire.rejections", ("transport", "rest"), ("reason", "rate_limited"), ("operation", "send")));
    }

    [Fact]
    public async Task StreamUnavailable_Is503WithCode()
    {
        var (status, _, body) = await ExecuteAsync(ApiResults.StreamUnavailable());
        Assert.Equal(503, status);
        Assert.Equal("stream_unavailable", body.RootElement.GetProperty("error").GetString());
    }
}
