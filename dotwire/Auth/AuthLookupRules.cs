using Dotwire.Configuration;

namespace Dotwire.Auth;

public static class AuthLookupRules
{
    public static void Validate(DotwireOptions options)
    {
        Check(options.AuthCacheSeconds, 0, 60, "Dotwire:AuthCacheSeconds");
        Check(options.AuthBatching.MaxInFlight, 1, 64, "Dotwire:AuthBatching:MaxInFlight");
        Check(options.AuthBatching.MaxBatchSize, 1, 4096, "Dotwire:AuthBatching:MaxBatchSize");
    }

    private static void Check(int value, int min, int max, string name)
    {
        if (value < min || value > max)
            throw new InvalidOperationException($"{name} must be in the range {min}..{max}; got {value}.");
    }
}
