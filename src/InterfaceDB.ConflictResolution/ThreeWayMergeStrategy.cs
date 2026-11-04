using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using MessagePack;

namespace Idb.Commercial.ConflictResolution;

public sealed record ThreeWayMergeOptions<T> where T : class, IStoredContract<T>
{
    /// <summary>Explicit persisted identity properties, which cannot change during a merge.</summary>
    public IReadOnlyList<string> IdentityProperties { get; init; } = [];
    public IReadOnlyDictionary<string, IEqualityComparer<object?>> PropertyComparers { get; init; }
        = new Dictionary<string, IEqualityComparer<object?>>();
    public int MaximumCollectionItems { get; init; } = 16_384;
    public int MaximumProperties { get; init; } = 256;
    /// <summary>Return null to accept, or a safe diagnostic code to reject. Do not perform remote/reentrant writes.</summary>
    public Func<T, CancellationToken, ValueTask<string?>>? ValidateCandidateAsync { get; init; }
}

/// <summary>Optional, opt-in whole-property three-way conflict resolution. Both sides changing a property always conflicts.</summary>
public sealed class ThreeWayMergeStrategy<T> : IWriteConflictStrategy<T> where T : class, IStoredContract<T>
{
    private static readonly Lazy<PropertyInfo[]> ContractProperties = new(Discover);
    private readonly PropertyInfo[] _properties;
    private readonly HashSet<string> _identities;
    private readonly IReadOnlyDictionary<string, IEqualityComparer<object?>> _comparers;
    private readonly int _maximumItems;
    private readonly Func<T, CancellationToken, ValueTask<string?>>? _validate;
    private readonly ConcurrentDictionary<Type, bool> _validatedTypes = new();
    private readonly ConcurrentDictionary<string, Type> _encodedTypes = new(StringComparer.Ordinal);

    public ThreeWayMergeStrategy(ThreeWayMergeOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumProperties < 1 || options.MaximumCollectionItems < 1 || options.IdentityProperties.Count == 0)
            throw new ArgumentException("Configure bounded comparison limits and explicit identity properties.", nameof(options));
        _properties = ContractProperties.Value;
        _identities = new(options.IdentityProperties, StringComparer.Ordinal);
        _comparers = new Dictionary<string, IEqualityComparer<object?>>(options.PropertyComparers, StringComparer.Ordinal);
        _maximumItems = options.MaximumCollectionItems; _validate = options.ValidateCandidateAsync;
        if (_properties.Length > options.MaximumProperties) throw new NotSupportedException("Contract exceeds the configured property limit.");
        var names = _properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (_identities.Any(name => !names.Contains(name)) || _comparers.Keys.Any(name => !names.Contains(name)))
            throw new ArgumentException("An identity or comparer property is not a persisted writable contract property.", nameof(options));
        foreach (var property in _properties)
            if (!_comparers.ContainsKey(property.Name) && !Supported(property.PropertyType, 0))
                throw new NotSupportedException($"Property '{property.Name}' needs an explicit whole-property comparer.");
    }

    public WriteHistoryRequirement HistoryRequirement => WriteHistoryRequirement.OriginalCommittedVersion;

    public async ValueTask<EnvelopeWriteDecision<T>> DecideAsync(EnvelopeWriteConflict<T> context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Matching writes do not read history or compute differences. Validation remains opt-in.
        if (context.IsInsert)
            return context.CurrentRevision is null && context.ExpectedRevision == 0
                ? await ValidateMatchedAsync(context, cancellationToken).ConfigureAwait(false) : Reject("insert-conflict");
        if (context.CurrentRevision == context.ExpectedRevision)
            return context.IsDelete ? new(true) : await ValidateMatchedAsync(context, cancellationToken).ConfigureAwait(false);
        if (context.IsDelete || context.CurrentRevision is null) return Reject("deleted-or-missing-current");
        if (context.OriginalVersion is null) return Reject("original-version-required");

        T baseline, current, incoming;
        try
        {
            baseline = await context.ReadOriginalAsync(cancellationToken).ConfigureAwait(false);
            current = await context.ReadCurrentAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new RetainedVersionUnavailableException("Current entity is missing.");
            incoming = await context.ReadIncomingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RetainedVersionUnavailableException) { return Reject("original-version-unavailable"); }
        var baselineEnvelope = baseline.Envelope!;
        if (baselineEnvelope.RevisionTimestampUtcMicroseconds != context.ExpectedRevision
            || baselineEnvelope.EnvelopeCacheKey != context.Key
            || baselineEnvelope.VersionTag != current.Envelope!.VersionTag
            || baselineEnvelope.VersionTag != incoming.Envelope!.VersionTag
            || baselineEnvelope.ImplementationTypeName != current.Envelope.ImplementationTypeName
            || baselineEnvelope.ImplementationTypeName != incoming.Envelope.ImplementationTypeName)
            return Reject("incompatible-baseline-or-contract");
        ValidateRuntime(baseline); ValidateRuntime(current); ValidateRuntime(incoming);

        var incomingChanges = new List<PropertyInfo>();
        var overlaps = new List<string>();
        var work = 0;
        foreach (var property in _properties)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object? b, c, i;
            try { b = property.GetValue(baseline); c = property.GetValue(current); i = property.GetValue(incoming); }
            catch (Exception exception) when (exception is TargetInvocationException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
            { return Reject("property-access-unavailable", [property.Name]); }
            var currentChanged = !Equal(property, b, c, ref work);
            var incomingChanged = !Equal(property, b, i, ref work);
            if (_identities.Contains(property.Name))
            {
                if (currentChanged || incomingChanged) return Reject("identity-change", [property.Name]);
                continue;
            }
            if (currentChanged && incomingChanged) overlaps.Add(property.Name);
            else if (incomingChanged) incomingChanges.Add(property);
        }
        if (overlaps.Count != 0) return Reject("overlapping-properties", overlaps);
        if (incomingChanges.Count == 0) return new EnvelopeWriteNoChange<T>(current.Envelope!);

        // A second materialization is an independently owned candidate; neither B nor C is mutated.
        var merged = await context.ReadCurrentAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new RetainedVersionUnavailableException("Current entity is missing.");
        foreach (var property in incomingChanges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            property.SetValue(merged, property.GetValue(incoming));
        }
        if (_validate is not null)
        {
            var rejection = await _validate(merged, cancellationToken).ConfigureAwait(false);
            if (rejection is not null) return Reject("candidate-validation"); // Do not log a validator's potentially sensitive value.
        }
        return new(true, await context.PrepareResolvedAsync(merged, cancellationToken).ConfigureAwait(false));
    }

    private static EnvelopeWriteRejected<T> Reject(string code, IReadOnlyList<string>? names = null) => new(code, names);

    private async ValueTask<EnvelopeWriteDecision<T>> ValidateMatchedAsync(EnvelopeWriteConflict<T> context, CancellationToken token)
    {
        if (_validate is null) return new(true);
        var candidate = await context.ReadIncomingAsync(token).ConfigureAwait(false);
        if (await _validate(candidate, token).ConfigureAwait(false) is not null) return Reject("candidate-validation");
        token.ThrowIfCancellationRequested();
        return new(true);
    }

    private static PropertyInfo[] Discover()
        => typeof(T).GetInterfaces().Append(typeof(T)).SelectMany(type => type.GetProperties())
            .Where(property => property.Name != nameof(IStoredContract<T>.Envelope) && !property.IsDefined(typeof(IgnoreMemberAttribute)))
            .GroupBy(property => property.Name, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                if (group.Select(p => p.PropertyType).Distinct().Count() != 1)
                    throw new NotSupportedException($"Property '{group.Key}' has ambiguous inherited types.");
                var property = group.First();
                if (property.GetMethod is null || property.SetMethod is null || property.GetIndexParameters().Length != 0)
                    throw new NotSupportedException($"Property '{property.Name}' must be persisted, readable and writable.");
                return property;
            }).ToArray();

    private void ValidateRuntime(T value)
    {
        var type = value.GetType();
        // Validate the persisted implementation behind encryption proxies too.
        if (type.Assembly.IsDynamic)
        {
            var identity = value.Envelope?.ImplementationTypeName
                ?? throw new NotSupportedException("The persisted implementation type is unavailable.");
            type = _encodedTypes.GetOrAdd(identity, static name => Type.GetType(name, throwOnError: false)
                ?? throw new NotSupportedException("The persisted implementation type cannot be validated."));
        }
        if (_validatedTypes.ContainsKey(type)) return;
        var names = _properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!field.IsDefined(typeof(IgnoreMemberAttribute)))
                throw new NotSupportedException($"Implementation has unconfigured persisted field '{field.Name}'.");
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name == "Envelope" || property.GetIndexParameters().Length != 0) continue;
            if (property.IsDefined(typeof(IgnoreMemberAttribute)))
            { if (names.Contains(property.Name)) throw new NotSupportedException($"Implementation ignores contract property '{property.Name}'."); continue; }
            if (!names.Contains(property.Name)) throw new NotSupportedException($"Implementation has unconfigured persisted property '{property.Name}'.");
        }
        _validatedTypes.TryAdd(type, true);
    }

    private bool Equal(PropertyInfo property, object? left, object? right, ref int work)
        => _comparers.TryGetValue(property.Name, out var comparer) ? comparer.Equals(left, right) : StructuralEqual(left, right, ref work);

    private bool StructuralEqual(object? left, object? right, ref int work)
    {
        if (++work > _maximumItems) throw new InvalidOperationException("Property comparison exceeds the configured work limit.");
        if (left is null || right is null) return left is null && right is null;
        if (left.GetType() != right.GetType()) return false;
        if (left is byte[] bytes && right is byte[] otherBytes)
        {
            if (bytes.Length != otherBytes.Length) return false;
            if (bytes.Length > _maximumItems - work) throw new InvalidOperationException("Byte array exceeds the comparison limit.");
            work += bytes.Length;
            return bytes.AsSpan().SequenceEqual(otherBytes);
        }
        if (left is IList a && right is IList b)
        {
            if (a.Count != b.Count) return false;
            if (a.Count > _maximumItems - work) throw new InvalidOperationException("Collection exceeds the comparison limit.");
            for (var i = 0; i < a.Count; i++) if (!StructuralEqual(a[i], b[i], ref work)) return false;
            return true;
        }
        if (left is IDictionary x && right is IDictionary y)
        {
            if (x.Count != y.Count) return false;
            // Match using the declared scalar value equality, independent of dictionary lookup comparers.
            foreach (DictionaryEntry entry in x)
            {
                var found = false;
                foreach (DictionaryEntry other in y)
                    if (StructuralEqual(entry.Key, other.Key, ref work))
                    { if (!StructuralEqual(entry.Value, other.Value, ref work)) return false; found = true; break; }
                if (!found) return false;
            }
            return true;
        }
        return left.Equals(right);
    }

    private static bool Supported(Type type, int depth)
    {
        if (depth > 8) return false;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly)
            || type == typeof(TimeSpan)) return true;
        if (type.IsArray) return type.GetArrayRank() == 1 && Supported(type.GetElementType()!, depth + 1);
        if (!type.IsGenericType) return false;
        var definition = type.GetGenericTypeDefinition();
        return (definition == typeof(List<>) || definition == typeof(Dictionary<,>))
            && type.GetGenericArguments().All(argument => Supported(argument, depth + 1));
    }
}
