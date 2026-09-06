namespace Idb.Libraries.Abstractions.Services.Serialization;

public interface IRevisionTimestampGenerator
{
    long Next(long observedRevisionTimestampUtcMicroseconds);
}
