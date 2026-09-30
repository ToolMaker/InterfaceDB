using System.Collections.ObjectModel;
using System.Reflection;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Defines the concrete runtime types that a persisted contract may materialize.
/// The enrolled types and their persisted identifiers are frozen when the allowlist is created.
/// </summary>
public sealed class StoredRuntimeTypeAllowlist<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly HashSet<Type> _runtimeTypeSet;
    private readonly Dictionary<string, Type> _exactIdentifiers;
    private readonly Dictionary<(string TypeName, string AssemblyName), Type> _compatibleIdentifiers;

    /// <summary>
    /// Creates an allowlist from the supplied concrete contract implementations.
    /// </summary>
    public StoredRuntimeTypeAllowlist(params Type[] runtimeTypes)
        : this((IEnumerable<Type>)runtimeTypes)
    {
    }

    /// <summary>
    /// Creates an allowlist from the supplied concrete contract implementations.
    /// </summary>
    public StoredRuntimeTypeAllowlist(IEnumerable<Type> runtimeTypes)
    {
        ArgumentNullException.ThrowIfNull(runtimeTypes);

        var frozenTypes = runtimeTypes
            .Select(ValidateRuntimeType)
            .Distinct()
            .OrderBy(static type => type.AssemblyQualifiedName, StringComparer.Ordinal)
            .ToArray();

        RuntimeTypes = new ReadOnlyCollection<Type>(frozenTypes);
        _runtimeTypeSet = new HashSet<Type>(frozenTypes);
        _exactIdentifiers = new Dictionary<string, Type>(StringComparer.Ordinal);
        _compatibleIdentifiers = new Dictionary<(string TypeName, string AssemblyName), Type>();

        foreach (var runtimeType in frozenTypes)
        {
            var identifier = runtimeType.AssemblyQualifiedName
                ?? runtimeType.FullName
                ?? runtimeType.Name;
            AddUnique(_exactIdentifiers, identifier, runtimeType);

            if (TryReadStableIdentity(identifier, out var identity))
            {
                AddUnique(_compatibleIdentifiers, identity, runtimeType);
            }
        }
    }

    /// <summary>
    /// Gets the frozen concrete runtime types in deterministic identifier order.
    /// </summary>
    public IReadOnlyList<Type> RuntimeTypes { get; }

    /// <summary>
    /// Gets the compatibility default. It enrolls concrete implementations already declared in
    /// the contract assembly and never expands in response to assemblies loaded later.
    /// </summary>
    public static StoredRuntimeTypeAllowlist<TContract> Default { get; } = CreateDefault();

    internal bool Contains(Type runtimeType) => _runtimeTypeSet.Contains(runtimeType);

    internal bool TryResolve(string persistedIdentifier, out Type runtimeType)
    {
        if (_exactIdentifiers.TryGetValue(persistedIdentifier, out runtimeType!))
        {
            return true;
        }

        return TryReadStableIdentity(persistedIdentifier, out var identity)
            && _compatibleIdentifiers.TryGetValue(identity, out runtimeType!);
    }

    private static StoredRuntimeTypeAllowlist<TContract> CreateDefault()
    {
        Type[] assemblyTypes;
        try
        {
            assemblyTypes = typeof(TContract).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            assemblyTypes = exception.Types.OfType<Type>().ToArray();
        }

        return new StoredRuntimeTypeAllowlist<TContract>(assemblyTypes.Where(static type =>
            type.IsClass
            && !type.IsAbstract
            && !type.ContainsGenericParameters
            && typeof(TContract).IsAssignableFrom(type)));
    }

    private static Type ValidateRuntimeType(Type runtimeType)
    {
        ArgumentNullException.ThrowIfNull(runtimeType);
        if (!runtimeType.IsClass
            || runtimeType.IsAbstract
            || runtimeType.ContainsGenericParameters
            || !typeof(TContract).IsAssignableFrom(runtimeType))
        {
            throw new ArgumentException(
                $"Stored runtime type '{runtimeType.FullName}' must be a closed concrete implementation of '{typeof(TContract).FullName}'.",
                nameof(runtimeType));
        }

        return runtimeType;
    }

    private static void AddUnique<TKey>(Dictionary<TKey, Type> destination, TKey key, Type runtimeType)
        where TKey : notnull
    {
        if (destination.TryGetValue(key, out var existing) && existing != runtimeType)
        {
            throw new ArgumentException(
                $"Stored runtime types '{existing.FullName}' and '{runtimeType.FullName}' have the same persisted identity.",
                nameof(runtimeType));
        }

        destination[key] = runtimeType;
    }

    private static bool TryReadStableIdentity(
        string identifier,
        out (string TypeName, string AssemblyName) identity)
    {
        var bracketDepth = 0;
        for (var index = 0; index < identifier.Length; index++)
        {
            switch (identifier[index])
            {
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                    {
                        bracketDepth--;
                    }
                    break;
                case ',' when bracketDepth == 0 && !IsEscaped(identifier, index):
                    var typeName = identifier[..index].Trim();
                    var assemblyStart = index + 1;
                    var assemblyEnd = identifier.IndexOf(',', assemblyStart);
                    var assemblyName = (assemblyEnd < 0
                            ? identifier[assemblyStart..]
                            : identifier[assemblyStart..assemblyEnd])
                        .Trim();
                    if (typeName.Length != 0 && assemblyName.Length != 0)
                    {
                        identity = (typeName, assemblyName);
                        return true;
                    }

                    identity = default;
                    return false;
            }
        }

        identity = default;
        return false;
    }

    private static bool IsEscaped(string value, int index)
    {
        var slashes = 0;
        for (var cursor = index - 1; cursor >= 0 && value[cursor] == '\\'; cursor--)
        {
            slashes++;
        }

        return (slashes & 1) != 0;
    }
}
