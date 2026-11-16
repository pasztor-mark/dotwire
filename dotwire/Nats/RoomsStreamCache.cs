using Dotwire.Configuration;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;

namespace Dotwire.Nats;

public sealed class RoomsStreamCache(INatsJSContext js, IOptions<NatsOptions> options)
{
    private INatsJSStream? _stream;

    public async ValueTask<INatsJSStream> GetAsync(CancellationToken ct)
    {
        var cached = Volatile.Read(ref _stream);
        if (cached is not null)
            return cached;
        var fetched = await js.GetStreamAsync(options.Value.RoomsStream, cancellationToken: ct);
        Volatile.Write(ref _stream, fetched);
        return fetched;
    }

    public void Invalidate() => Volatile.Write(ref _stream, null);
}
