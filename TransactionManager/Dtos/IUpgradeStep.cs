using TransactionManager.Enums;

namespace TransactionManager.Dtos
{
    public interface IUpgradeStep
    {
        short FromVersion { get; }
        short ToVersion { get; }      // usually From+1
        UpgradeMode Mode { get; }

        /// <summary>Convert old payload into bytes for ToVersion.</summary>
        byte[] Transform(ReadOnlyMemory<byte> oldPayload, ISerializer serializer);
    }

}
