using TransactionManager.Attributes;

namespace TransactionManager.Dtos
{
    [StorageScope()]
    public interface IArms
    {
        IFingers Fingers { get; set; }
    }
}