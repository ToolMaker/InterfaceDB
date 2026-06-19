using Idb.Libraries.Abstractions.Enums;

namespace Idb.Libraries.Implementation.Indexes;

internal sealed record SearchIndexDefinition(
    string Path,
    Type ValueType,
    SearchIndexMode Mode,
    Func<object, object?> Getter);
