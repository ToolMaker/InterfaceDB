using System.Globalization;
using System.Numerics;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;

public class FlatFileStorageIdentityFactory<TContract, TKey> : IEnvelopeStorageIdentityFactory<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly IDbContractConfigDto<TContract> _config;
    private readonly IEnvelopeKeyFactory<TContract, TKey> _keyFactory;
    private readonly Func<TKey, string> _keyFormatter;

    public FlatFileStorageIdentityFactory(
        IDbContractConfigDto<TContract> config,
        IEnvelopeKeyFactory<TContract, TKey> keyFactory,
        Func<TKey, string>? keyFormatter = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _keyFactory = keyFactory ?? throw new ArgumentNullException(nameof(keyFactory));
        _keyFormatter = keyFormatter ?? FormatKey;
    }

    public StorageObjectCardinality ObjectCardinality => _config.ObjectCardinality;

    public void InitEnvelope(IObjectEnvelope<TContract> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        envelope.EnvelopeCacheKey = envelope.EnvelopeCacheKey switch
        {
            { Length: > 0 } => envelope.EnvelopeCacheKey,
            _ when ObjectCardinality == StorageObjectCardinality.Single => ResolveSingleKey(),
            _ => ResolveMultipleKey(envelope)
        };

        if (envelope.Location is not null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.DataRoot))
        {
            throw new InvalidOperationException("Contract configuration must supply a DataRoot directory.");
        }

        var extension = _config.FileExtension.NormalizeExtension();
        var version = string.IsNullOrWhiteSpace(envelope.VersionTag)
            ? (_config.ActiveVersion ?? "1.0.0")
            : envelope.VersionTag;

        var versionRoot = Path.Combine(_config.DataRoot!, version);
        envelope.Location = Path.Combine(versionRoot, envelope.EnvelopeCacheKey + extension);
    }

    private string ResolveMultipleKey(IObjectEnvelope<TContract> envelope)
    {
        var instance = envelope.ObjectInstance
            ?? throw new InvalidOperationException("Envelope must contain an object before resolving its identity.");
        var key = _keyFactory.CreateKey(instance);
        var formatted = _keyFormatter(key);
        if (string.IsNullOrWhiteSpace(formatted))
        {
            throw new InvalidOperationException("Envelope key factory produced an empty storage key.");
        }

        return SanitizeFileName(formatted);
    }

    private string ResolveSingleKey()
    {
        var candidate = string.IsNullOrWhiteSpace(_config.ContractName)
            ? typeof(TContract).Name
            : _config.ContractName;
        var normalized = SanitizeFileName(candidate);

        return string.IsNullOrWhiteSpace(normalized)
            ? typeof(TContract).Name
            : normalized;
    }

    private static string FormatKey(TKey key)
    {
        return key switch
        {
            null => string.Empty,
            Guid guid => guid.ToString("N"),
            BigInteger bigInteger => bigInteger.ToString(CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => key.ToString() ?? string.Empty
        };
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character));
    }
}
