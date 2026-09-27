namespace Dotwire.Auth;

/// <summary>
/// User ids and sender ids (spec §3.14): 1-256 characters, no code points below U+0020 and
/// not U+007F. This also guarantees the canonical audit form (§3.6) is unambiguous, since
/// ids can never contain the newline that separates its fields.
/// </summary>
public static class IdValidation
{
    private const int MaxLength = 256;
    private const char Del = '';

    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength)
            return false;

        foreach (var c in id)
        {
            if (c < ' ' || c == Del)
                return false;
        }

        return true;
    }
}
