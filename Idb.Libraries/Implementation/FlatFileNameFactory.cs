using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extentions;

public class FlatFileNameFactory<TContract> : IEnvelopeKeyAndLocationFactory<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly IDbContractConfigDto<TContract> _config;

    public FlatFileNameFactory(IDbContractConfigDto<TContract> config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public StorageObjectCardinality ObjectCardinality => _config.ObjectCardinality;

    public void InitEnvelope(IObjectEnvelope<TContract> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        envelope.EnvelopeCacheKey = envelope.EnvelopeCacheKey switch
        {
            { Length: > 0 } => envelope.EnvelopeCacheKey,
            _ when ObjectCardinality == StorageObjectCardinality.Single => ResolveSingleKey(),
            _ => Guid.NewGuid().ToString("N")
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

    private string ResolveSingleKey()
    {
        var candidate = string.IsNullOrWhiteSpace(_config.ContractName)
            ? typeof(TContract).Name
            : _config.ContractName;
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var normalized = string.Concat(candidate.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character));

        return string.IsNullOrWhiteSpace(normalized)
            ? typeof(TContract).Name
            : normalized;
    }
}
