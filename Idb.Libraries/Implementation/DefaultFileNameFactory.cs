using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extentions;

public class DefaultFileNameFactory<TContract> : IEnvelopeKeyAndLocationFactory<TContract, string>
    where TContract : class, IRepoDto<TContract, string>
{
    private readonly IDbContractConfigDto<TContract, string> _config;
    public DefaultFileNameFactory(IDbContractConfigDto<TContract, string> config)
    {
        _config = config;
    }

    public void InitEnvelope(IObjectEnvelope<TContract, string> envelope)
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
            // Hash-partition: first 2 chars of key to keep directories small
            var key = envelope.EnvelopeCacheKey;
            var shard = key.Length >= 2 ? key.Substring(0, 2) : "00";
            var targetPath = Path.Combine(versionRoot, shard, key + extension);
            envelope.Location = targetPath;
        }
    }
}
