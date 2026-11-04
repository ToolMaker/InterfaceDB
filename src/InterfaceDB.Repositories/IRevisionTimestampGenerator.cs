namespace Idb.Commercial.Repositories;

/// <summary>
/// Generates a UTC microsecond revision later than the supplied observed revision.
/// </summary>
public interface IRevisionTimestampGenerator : Idb.Libraries.Abstractions.Services.Serialization.IRevisionTimestampGenerator
{
}
