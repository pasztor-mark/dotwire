using Dotwire.Nats;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace dotwire.Tests;

public static class ErasureWorkerSeam
{
    public static IWebHostBuilder WithoutErasureWorker(this IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            foreach (var descriptor in services
                         .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ErasureWorkerService))
                         .ToList())
                services.Remove(descriptor);
        });
}
