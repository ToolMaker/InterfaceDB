using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Migrations;

namespace Idb.Libraries.Implementation.Options;

/// <summary>
/// Default implementation of <see cref="IMigrationStrategy"/>.
/// </summary>
public sealed class MigrationStrategyOptions : IMigrationStrategy
{
    /// <inheritdoc />
    public MigrationStrategyType Strategy { get; set; } = MigrationStrategyType.EagerStrict;
}

