using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extentions;

public class DefaultFileNameFactory<TContract> : IEnvelopeKeyAndLocationFactory<TContract>
    where TContract : class, IRepoDto<TContract>
{
    private readonly IDbContractConfigDto<TContract> _config;
    public DefaultFileNameFactory(IDbContractConfigDto<TContract> config)
    {
        _config = config;
    }

    public void InitEnvelope(IObjectEnvelope<TContract> envelope)
    {
        envelope.EnvelopeCacheKey = envelope.EnvelopeCacheKey switch
        {
            { Length: > 0 } => envelope.EnvelopeCacheKey,
            // Generate a stable identifier the first time we persist this envelope.
            // It is serialized alongside the payload and reused on subsequent saves.
            _ => Guid.NewGuid().ToString("N")
        };
        if (envelope.Location == null)
        {
            if (string.IsNullOrWhiteSpace(_config.DataRoot))
            {
                throw new InvalidOperationException("Contract configuration must supply a DataRoot directory.");
            }

            var extension = _config.FileExtension.NormalizeExtension();
            var version = string.IsNullOrWhiteSpace(envelope.VersionTag)
                ? (_config.ActiveVersion ?? "1.0.0")
                : envelope.VersionTag;

            var versionRoot = Path.Combine(_config.DataRoot!, version);
            // Determine whether to use sharded directory layout
            var useShards = _config.StoragePolicy?.UseShardedDirectories == true;
            var key = envelope.EnvelopeCacheKey;
            var shard = key.Length >= 2 ? key.Substring(0, 2) : "00";
            var targetPath = useShards
                ? Path.Combine(versionRoot, shard, key + extension)
                : Path.Combine(versionRoot, key + extension);
            envelope.Location = targetPath;
        }
    }
}
