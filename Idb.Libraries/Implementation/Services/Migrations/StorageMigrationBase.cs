using Idb.Libraries.Abstractions.Services.Migrations;

namespace Idb.Libraries.Implementation.Services.Migrations
{
    /// <summary>
    /// Base class that wires up contract metadata based on <see cref="StorageMigrationAttribute"/>.
    /// </summary>
    /// <typeparam name="TContract">The storage contract interface.</typeparam>
    public abstract class StorageMigrationBase<TContract> : IStorageMigration
    {
        protected StorageMigrationBase()
        {
            var attribute = GetType().GetCustomAttributes(typeof(StorageMigrationAttribute), inherit: false)
                                      .OfType<StorageMigrationAttribute>()
                                      .FirstOrDefault()
                           ?? throw new InvalidOperationException($"Migration '{GetType().FullName}' must be decorated with {nameof(StorageMigrationAttribute)}.");

            if (attribute.ContractType != typeof(TContract))
            {
                throw new InvalidOperationException($"Migration '{GetType().FullName}' declares contract '{attribute.ContractType.FullName}', but generic argument '{typeof(TContract).FullName}' does not match.");
            }

            ContractType = attribute.ContractType;
            SourceVersionTag = attribute.SourceVersionTag;
            TargetVersionTag = attribute.TargetVersionTag;
        }

        public Type ContractType { get; }
        public string SourceVersionTag { get; }
        public string TargetVersionTag { get; }

        public Task ExecuteAsync(IStorageMigrationContext context, CancellationToken cancellationToken)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.ContractType != ContractType)
            {
                throw new InvalidOperationException($"Migration '{GetType().Name}' received context for '{context.ContractType.FullName}' but expects '{ContractType.FullName}'.");
            }

            if (!string.Equals(context.SourceVersionTag, SourceVersionTag, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Migration '{GetType().Name}' expected source '{SourceVersionTag}' but received '{context.SourceVersionTag}'.");
            }

            if (!string.Equals(context.TargetVersionTag, TargetVersionTag, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Migration '{GetType().Name}' expected target '{TargetVersionTag}' but received '{context.TargetVersionTag}'.");
            }

            return ExecuteCoreAsync(context, cancellationToken);
        }

        protected abstract Task ExecuteCoreAsync(IStorageMigrationContext context, CancellationToken cancellationToken);
    }
}
