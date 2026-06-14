using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extentions;

public class ShardedFileNameFactory<TContract> : IEnvelopeKeyAndLocationFactory<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly IDbContractConfigDto<TContract> _config;

    public ShardedFileNameFactory(IDbContractConfigDto<TContract> config)
    {
        _config = config;
    }

    public void InitEnvelope(IObjectEnvelope<TContract> envelope)
    {
        envelope.EnvelopeCacheKey = envelope.EnvelopeCacheKey switch
        {
            { Length: > 0 } => envelope.EnvelopeCacheKey,
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
        var key = envelope.EnvelopeCacheKey;
        var shard = key.Length >= 2 ? key[..2] : "00";
        envelope.Location = Path.Combine(versionRoot, shard, key + extension);
    }
}
