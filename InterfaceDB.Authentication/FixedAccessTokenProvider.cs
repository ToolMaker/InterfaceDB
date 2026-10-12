namespace Idb.Authentication;

/// <summary>Wraps a token acquired by an application or an existing identity SDK. Does not refresh or persist it.</summary>
public sealed class FixedAccessTokenProvider : IAccessTokenProvider
{
    private readonly AccessToken _token;
    private readonly TimeProvider _time;

    public FixedAccessTokenProvider(AccessToken token, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        TokenValueValidation.Validate(token.Value, 16 * 1024);
        _token = token;
        _time = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<AccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_token.ExpiresAt is { } expiry && expiry <= _time.GetUtcNow())
            throw new TokenAcquisitionException(TokenAcquisitionFailure.TokenResponse);
        return ValueTask.FromResult(_token);
    }

    public override string ToString() => "[FIXED ACCESS TOKEN PROVIDER]";
}

internal static class TokenValueValidation
{
    internal static void Validate(string? value, int maximumBytes)
    {
        // Bearer tokens are ASCII credentials. Reject whitespace/control characters before any transport use.
        if (string.IsNullOrEmpty(value) || value.Length > maximumBytes || value.Any(c => c <= 0x20 || c >= 0x7f))
            throw new TokenAcquisitionException(TokenAcquisitionFailure.TokenResponse);
    }
}
