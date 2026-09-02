using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Implementation.Indexes;

internal sealed class SearchIndexStore<TContract> : IDisposable
    where TContract : class
{
    private static readonly ConditionalWeakTable<Expression, Func<object?>> ConstantEvaluators = new();
    private readonly IReadOnlyList<SearchIndexDefinition> _definitions;
    private readonly Dictionary<string, PropertySearchIndex> _indexes;
    private readonly PropertySearchIndex[] _indexesByDefinition;
    private readonly Type[] _valueTypes;
    private readonly byte[] _schemaFingerprint;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly SearchQueryPlannerOptions _plannerOptions;
    private readonly Dictionary<string, object?[]> _valuesByPhysicalKey = new(StringComparer.Ordinal);
    private int _disposed;

    public SearchIndexStore(SearchQueryPlannerOptions? plannerOptions = null)
    {
        _plannerOptions = plannerOptions ?? SearchQueryPlannerOptions.MetadataOnly;
        _definitions = SearchIndexDiscovery.Discover(typeof(TContract));
        _valueTypes = _definitions.Select(static definition => definition.ValueType).ToArray();
        _schemaFingerprint = ComputeSchemaFingerprint(typeof(TContract), _definitions);
        _indexesByDefinition = new PropertySearchIndex[_definitions.Count];
        _indexes = new Dictionary<string, PropertySearchIndex>(_definitions.Count, StringComparer.Ordinal);
        for (var index = 0; index < _definitions.Count; index++)
        {
            var propertyIndex = new PropertySearchIndex(_definitions[index]);
            _indexesByDefinition[index] = propertyIndex;
            _indexes[_definitions[index].Path] = propertyIndex;
        }
    }

    public bool HasIndexes => _indexes.Count > 0;

    internal IReadOnlyList<Type> ValueTypes => _valueTypes;

    internal ReadOnlyMemory<byte> SchemaFingerprint => _schemaFingerprint;

    internal int IndexedObjectCount
    {
        get
        {
            ThrowIfDisposed();
            _lock.EnterReadLock();
            try
            {
                return _valuesByPhysicalKey.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    internal IDisposable BeginBulkLoad()
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            foreach (var index in _indexes.Values)
            {
                index.BeginBulkLoad();
            }

            return new BulkLoadScope(this);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Clear()
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            ClearCore();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lock.EnterWriteLock();
        try
        {
            _valuesByPhysicalKey.Clear();
            foreach (var index in _indexes.Values)
            {
                index.Dispose();
            }
        }
        finally
        {
            _lock.ExitWriteLock();
            _lock.Dispose();
        }
    }

    public void Upsert(string physicalKey, TContract entity)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(entity);
        if (string.IsNullOrWhiteSpace(physicalKey) || !HasIndexes)
        {
            return;
        }

        UpsertValues(physicalKey, ExtractValues(entity));
    }

    internal object?[] ExtractValues(TContract entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var values = new object?[_definitions.Count];
        for (var index = 0; index < _definitions.Count; index++)
        {
            values[index] = NormalizeValue(_definitions[index].Getter(entity));
        }

        return values;
    }

    internal void UpsertValues(string physicalKey, IReadOnlyList<object?> values)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalKey);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != _definitions.Count)
        {
            throw new ArgumentException("The persisted index value count does not match the index schema.", nameof(values));
        }

        if (!HasIndexes)
        {
            return;
        }

        _lock.EnterWriteLock();
        try
        {
            RemoveCore(physicalKey);
            var ownedValues = new object?[values.Count];
            for (var index = 0; index < values.Count; index++)
            {
                var value = NormalizeValue(values[index]);
                ownedValues[index] = value;
                if (value is not null)
                {
                    _indexesByDefinition[index].Add(value, physicalKey);
                }
            }

            _valuesByPhysicalKey[physicalKey] = ownedValues;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    internal IReadOnlyDictionary<string, object?[]> GetValuesSnapshot()
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            return _valuesByPhysicalKey.ToDictionary(
                static pair => pair.Key,
                static pair => (object?[])pair.Value.Clone(),
                StringComparer.Ordinal);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    internal object?[]? GetValues(string physicalKey)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            return _valuesByPhysicalKey.TryGetValue(physicalKey, out var values)
                ? (object?[])values.Clone()
                : null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Remove(string physicalKey)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            RemoveCore(physicalKey);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public HashSet<string>? TryEvaluate(Expression<Func<TContract, bool>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            return BuildPlan(expression.Body).Execute();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    internal string Explain(Expression<Func<TContract, bool>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            return BuildPlan(expression.Body).Explain();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    internal SearchQueryPlanExplanation ExplainPlan(Expression<Func<TContract, bool>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            var plan = BuildPlan(expression.Body);
            return new SearchQueryPlanExplanation(
                plan.Explain(),
                plan.Kind is not SearchQueryPlanKind.FullScan,
                plan.EstimatedCount,
                plan.EstimatedCost,
                plan.ResidualPredicateCount);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private SearchQueryPlan BuildPlan(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso })
        {
            return BuildAndPlan(expression);
        }

        if (expression is BinaryExpression { NodeType: ExpressionType.OrElse })
        {
            return BuildOrPlan(expression);
        }

        return TryCreateAccessPath(expression)?.ToPlan()
            ?? SearchQueryPlan.FullScan(
                _valuesByPhysicalKey.Count,
                _plannerOptions.ObjectLoadCost);
    }

    private SearchQueryPlan BuildAndPlan(Expression expression)
    {
        var terms = new List<Expression>();
        Flatten(expression, ExpressionType.AndAlso, terms);
        var candidates = new List<SearchQueryPlan>(terms.Count);
        var coveredTerms = new bool[terms.Count];
        var bestRanges = FindBestCombinedRanges(terms);
        foreach (var combinedRange in bestRanges.Values)
        {
            candidates.Add(combinedRange.AccessPath.ToPlan());
            coveredTerms[combinedRange.LeftIndex] = true;
            coveredTerms[combinedRange.RightIndex] = true;
        }

        var explicitResidualCount = 0;
        for (var index = 0; index < terms.Count; index++)
        {
            if (coveredTerms[index])
            {
                continue;
            }

            var plan = BuildPlan(terms[index]);
            if (plan.Kind is SearchQueryPlanKind.FullScan)
            {
                explicitResidualCount += Math.Max(1, plan.ResidualPredicateCount);
            }
            else
            {
                candidates.Add(plan);
            }
        }

        if (candidates.Count == 0)
        {
            return SearchQueryPlan.FullScan(
                _valuesByPhysicalKey.Count,
                _plannerOptions.ObjectLoadCost,
                Math.Max(1, explicitResidualCount));
        }

        candidates.Sort(static (left, right) =>
        {
            var cardinality = left.EstimatedCount.CompareTo(right.EstimatedCount);
            return cardinality != 0
                ? cardinality
                : left.EstimatedCost.CompareTo(right.EstimatedCost);
        });

        var selected = new List<SearchQueryPlan> { candidates[0] };
        var currentEstimate = candidates[0].EstimatedCount;
        for (var index = 1; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (!SearchQueryCostModel.ShouldIntersect(
                    _valuesByPhysicalKey.Count,
                    currentEstimate,
                    candidate,
                    _plannerOptions,
                    out var intersectionEstimate))
            {
                continue;
            }

            selected.Add(candidate);
            currentEstimate = intersectionEstimate;
        }

        var residualCount = explicitResidualCount;
        foreach (var candidate in candidates)
        {
            if (selected.Contains(candidate))
            {
                residualCount += candidate.ResidualPredicateCount;
            }
            else
            {
                residualCount += candidate.CoveredPredicateCount + candidate.ResidualPredicateCount;
            }
        }

        if (selected.Count == 1)
        {
            return selected[0].WithResidualPredicates(residualCount);
        }

        var estimatedCost = selected.Sum(static plan => plan.EstimatedCost)
            + selected.Skip(1).Sum(plan =>
                Math.Min(selected[0].EstimatedCount, plan.EstimatedCount)
                * SearchQueryCostModel.HashCandidateCost);
        return SearchQueryPlan.Intersection(
            selected,
            currentEstimate,
            estimatedCost,
            residualCount);
    }

    private SearchQueryPlan BuildOrPlan(Expression expression)
    {
        var branches = new List<Expression>();
        Flatten(expression, ExpressionType.OrElse, branches);
        var plans = new List<SearchQueryPlan>(branches.Count);
        foreach (var branch in branches)
        {
            var plan = BuildPlan(branch);
            if (plan.Kind is SearchQueryPlanKind.FullScan)
            {
                return SearchQueryPlan.FullScan(
                    _valuesByPhysicalKey.Count,
                    _plannerOptions.ObjectLoadCost,
                    plans.Sum(static item => item.ResidualPredicateCount)
                        + Math.Max(1, plan.ResidualPredicateCount));
            }

            plans.Add(plan);
        }

        if (plans.Count == 1)
        {
            return plans[0];
        }

        var estimatedCount = Math.Min(
            _valuesByPhysicalKey.Count,
            plans.Sum(static plan => plan.EstimatedCount));
        var estimatedCost = plans.Sum(static plan => plan.EstimatedCost)
            + plans.Sum(static plan => plan.EstimatedCount) * SearchQueryCostModel.HashCandidateCost;
        return SearchQueryPlan.Union(
            plans,
            estimatedCount,
            estimatedCost,
            plans.Sum(static plan => plan.ResidualPredicateCount));
    }

    private Dictionary<string, CombinedRangeCandidate> FindBestCombinedRanges(
        IReadOnlyList<Expression> terms)
    {
        var bestByPath = new Dictionary<string, CombinedRangeCandidate>(StringComparer.Ordinal);
        for (var leftIndex = 0; leftIndex < terms.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < terms.Count; rightIndex++)
            {
                if (!TryCreateCombinedRangeAccessPath(
                        terms[leftIndex],
                        terms[rightIndex],
                        out var path,
                        out var accessPath))
                {
                    continue;
                }

                var candidate = new CombinedRangeCandidate(accessPath, leftIndex, rightIndex);
                if (!bestByPath.TryGetValue(path, out var current)
                    || candidate.AccessPath.EstimatedCount < current.AccessPath.EstimatedCount)
                {
                    bestByPath[path] = candidate;
                }
            }
        }

        return bestByPath;
    }

    private SearchIndexAccessPath? TryCreateAccessPath(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is BinaryExpression binary)
        {
            var comparisonOperator = binary.NodeType switch
            {
                ExpressionType.Equal => SearchComparisonOperator.Equal,
                ExpressionType.GreaterThan => SearchComparisonOperator.GreaterThan,
                ExpressionType.GreaterThanOrEqual => SearchComparisonOperator.GreaterThanOrEqual,
                ExpressionType.LessThan => SearchComparisonOperator.LessThan,
                ExpressionType.LessThanOrEqual => SearchComparisonOperator.LessThanOrEqual,
                _ => (SearchComparisonOperator?)null
            };
            return comparisonOperator.HasValue
                ? TryCreateComparisonAccessPath(binary.Left, binary.Right, comparisonOperator.Value)
                : null;
        }

        if (expression is MethodCallExpression call)
        {
            return TryCreateMethodAccessPath(call);
        }

        if (expression is MemberExpression member && member.Type == typeof(bool))
        {
            return TryCreateConditionAccessPath(
                ReadMemberPath(member),
                SearchComparisonOperator.Equal,
                true);
        }

        if (expression is UnaryExpression { NodeType: ExpressionType.Not } not
            && StripConvert(not.Operand) is MemberExpression boolMember
            && boolMember.Type == typeof(bool))
        {
            return TryCreateConditionAccessPath(
                ReadMemberPath(boolMember),
                SearchComparisonOperator.Equal,
                false);
        }

        return null;
    }

    private SearchIndexAccessPath? TryCreateComparisonAccessPath(
        Expression left,
        Expression right,
        SearchComparisonOperator comparisonOperator)
    {
        if (TryReadMemberPath(left, out var path)
            && TryEvaluate(right, out var rightValue))
        {
            return TryCreateConditionAccessPath(path, comparisonOperator, rightValue);
        }

        if (!TryReadMemberPath(right, out path)
            || !TryEvaluate(left, out var leftValue))
        {
            return null;
        }

        return TryCreateConditionAccessPath(path, Reverse(comparisonOperator), leftValue);
    }

    private SearchIndexAccessPath? TryCreateMethodAccessPath(MethodCallExpression call)
    {
        return TryReadOrdinalStringSearch(
                call,
                out var path,
                out var comparisonOperator,
                out var searchText)
            ? TryCreateConditionAccessPath(path, comparisonOperator, searchText)
            : null;
    }

    private SearchIndexAccessPath? TryCreateConditionAccessPath(
        string? path,
        SearchComparisonOperator comparisonOperator,
        object? value)
    {
        value = NormalizeValue(value);
        if (value is null
            || string.IsNullOrWhiteSpace(path)
            || !_indexes.TryGetValue(path, out var index))
        {
            return null;
        }

        var estimate = index.EstimateCount(comparisonOperator, value);
        if (!estimate.HasValue)
        {
            return null;
        }

        return new SearchIndexAccessPath(
            path,
            comparisonOperator,
            $"{comparisonOperator} index on {path} for {SearchIndexAccessPath.FormatValue(value)}",
            estimate.Value,
            EstimateIndexExecutionCost(comparisonOperator, estimate.Value),
            () => index.Search(comparisonOperator, value)
                ?? new HashSet<string>(StringComparer.Ordinal));
    }

    private bool TryCreateCombinedRangeAccessPath(
        Expression left,
        Expression right,
        out string path,
        out SearchIndexAccessPath accessPath)
    {
        accessPath = null!;
        if (!TryCreateCombinedRange(left, right, out path, out var query)
            || !_indexes.TryGetValue(path, out var index))
        {
            return false;
        }

        var estimate = index.EstimateRange(query);
        if (!estimate.HasValue)
        {
            return false;
        }

        accessPath = new SearchIndexAccessPath(
            path,
            SearchComparisonOperator.GreaterThanOrEqual,
            $"Bounded range index on {path}: {FormatRange(query)}",
            estimate.Value,
            EstimateIndexExecutionCost(SearchComparisonOperator.GreaterThanOrEqual, estimate.Value),
            () => index.SearchRange(query) ?? new HashSet<string>(StringComparer.Ordinal),
            coveredPredicateCount: 2);
        return true;
    }

    private static bool TryCreateCombinedRange(
        Expression left,
        Expression right,
        out string path,
        out ObjectRangeQuery query)
    {
        path = string.Empty;
        query = default;
        if (!TryReadRangeCondition(left, out var leftPath, out var leftOperator, out var leftValue)
            || !TryReadRangeCondition(right, out var rightPath, out var rightOperator, out var rightValue)
            || !string.Equals(leftPath, rightPath, StringComparison.Ordinal))
        {
            return false;
        }

        var leftIsLower = IsLowerBound(leftOperator);
        var rightIsLower = IsLowerBound(rightOperator);
        if (leftIsLower == rightIsLower)
        {
            return false;
        }

        var lowerOperator = leftIsLower ? leftOperator : rightOperator;
        var upperOperator = leftIsLower ? rightOperator : leftOperator;
        var lowerValue = NormalizeValue(leftIsLower ? leftValue : rightValue);
        var upperValue = NormalizeValue(leftIsLower ? rightValue : leftValue);
        if (lowerValue is null || upperValue is null)
        {
            return false;
        }

        path = leftPath;
        query = new ObjectRangeQuery(
            HasLowerBound: true,
            lowerValue,
            IncludeLowerBound: lowerOperator is SearchComparisonOperator.GreaterThanOrEqual,
            HasUpperBound: true,
            upperValue,
            IncludeUpperBound: upperOperator is SearchComparisonOperator.LessThanOrEqual);
        return true;
    }

    private static bool TryReadRangeCondition(
        Expression expression,
        out string path,
        out SearchComparisonOperator comparisonOperator,
        out object? value)
    {
        path = string.Empty;
        comparisonOperator = default;
        value = null;
        expression = StripConvert(expression);
        if (expression is not BinaryExpression binary
            || !TryMapRangeOperator(binary.NodeType, out comparisonOperator))
        {
            return false;
        }

        if (TryReadMemberPath(binary.Left, out path))
        {
            return TryEvaluate(binary.Right, out value);
        }

        if (!TryReadMemberPath(binary.Right, out path))
        {
            return false;
        }

        comparisonOperator = Reverse(comparisonOperator);
        return TryEvaluate(binary.Left, out value);
    }

    private static bool TryReadOrdinalStringSearch(
        MethodCallExpression call,
        out string path,
        out SearchComparisonOperator comparisonOperator,
        out string searchText)
    {
        path = string.Empty;
        comparisonOperator = default;
        searchText = string.Empty;
        if (call.Method.DeclaringType != typeof(string)
            || call.Object is null
            || !TryReadMemberPath(call.Object, out path))
        {
            return false;
        }

        comparisonOperator = call.Method.Name switch
        {
            nameof(string.StartsWith) => SearchComparisonOperator.StartsWith,
            nameof(string.EndsWith) => SearchComparisonOperator.EndsWith,
            nameof(string.Contains) => SearchComparisonOperator.Contains,
            _ => default
        };
        if (call.Method.Name is not nameof(string.StartsWith)
            and not nameof(string.EndsWith)
            and not nameof(string.Contains))
        {
            return false;
        }

        if (call.Arguments.Count == 1)
        {
            if (comparisonOperator is not SearchComparisonOperator.Contains
                || !TryEvaluate(call.Arguments[0], out var argument)
                || argument is not string ordinalContainsText)
            {
                return false;
            }

            searchText = ordinalContainsText;
            return true;
        }

        if (call.Arguments.Count != 2
            || !TryEvaluate(call.Arguments[1], out var comparison)
            || comparison is not StringComparison.Ordinal
            || !TryEvaluate(call.Arguments[0], out var searchValue)
            || searchValue is not string ordinalText)
        {
            return false;
        }

        searchText = ordinalText;
        return true;
    }

    private static void Flatten(
        Expression expression,
        ExpressionType nodeType,
        ICollection<Expression> output)
    {
        expression = StripConvert(expression);
        if (expression is BinaryExpression binary && binary.NodeType == nodeType)
        {
            Flatten(binary.Left, nodeType, output);
            Flatten(binary.Right, nodeType, output);
            return;
        }

        output.Add(expression);
    }

    private static bool TryMapRangeOperator(
        ExpressionType nodeType,
        out SearchComparisonOperator comparisonOperator)
    {
        comparisonOperator = nodeType switch
        {
            ExpressionType.GreaterThan => SearchComparisonOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => SearchComparisonOperator.GreaterThanOrEqual,
            ExpressionType.LessThan => SearchComparisonOperator.LessThan,
            ExpressionType.LessThanOrEqual => SearchComparisonOperator.LessThanOrEqual,
            _ => default
        };
        return nodeType is ExpressionType.GreaterThan
            or ExpressionType.GreaterThanOrEqual
            or ExpressionType.LessThan
            or ExpressionType.LessThanOrEqual;
    }

    private static bool IsLowerBound(SearchComparisonOperator comparisonOperator)
        => comparisonOperator is SearchComparisonOperator.GreaterThan
            or SearchComparisonOperator.GreaterThanOrEqual;

    private static SearchComparisonOperator Reverse(SearchComparisonOperator comparisonOperator)
        => comparisonOperator switch
        {
            SearchComparisonOperator.GreaterThan => SearchComparisonOperator.LessThan,
            SearchComparisonOperator.GreaterThanOrEqual => SearchComparisonOperator.LessThanOrEqual,
            SearchComparisonOperator.LessThan => SearchComparisonOperator.GreaterThan,
            SearchComparisonOperator.LessThanOrEqual => SearchComparisonOperator.GreaterThanOrEqual,
            _ => comparisonOperator
        };

    private static string? ReadMemberPath(Expression expression)
        => TryReadMemberPath(expression, out var path) ? path : null;

    private static bool TryReadMemberPath(Expression expression, out string path)
    {
        path = string.Empty;
        expression = StripConvert(expression);
        var parts = new Stack<string>();
        while (expression is MemberExpression member)
        {
            parts.Push(member.Member.Name);
            expression = StripConvert(member.Expression!);
        }

        if (expression is not ParameterExpression || parts.Count == 0)
        {
            return false;
        }

        path = string.Join(".", parts);
        return true;
    }

    private static bool TryEvaluate(Expression expression, out object? value)
    {
        expression = StripConvert(expression);
        if (expression is ConstantExpression constant)
        {
            value = constant.Value;
            return true;
        }

        if (expression is MemberExpression member
            && TryEvaluateMember(member, out value))
        {
            return true;
        }

        try
        {
            var evaluator = ConstantEvaluators.GetValue(expression, static candidate =>
            {
                var boxed = Expression.Convert(candidate, typeof(object));
                return Expression.Lambda<Func<object?>>(boxed).Compile();
            });
            value = evaluator();
            return true;
        }
        catch (InvalidOperationException)
        {
            value = null;
            return false;
        }
        catch (ArgumentException)
        {
            value = null;
            return false;
        }
    }

    private static bool TryEvaluateMember(MemberExpression member, out object? value)
    {
        object? instance = null;
        if (member.Expression is not null
            && !TryEvaluate(member.Expression, out instance))
        {
            value = null;
            return false;
        }

        value = member.Member switch
        {
            FieldInfo field => field.GetValue(instance),
            PropertyInfo property when property.GetIndexParameters().Length == 0
                => property.GetValue(instance),
            _ => null
        };
        return member.Member is FieldInfo
            || member.Member is PropertyInfo propertyInfo
                && propertyInfo.GetIndexParameters().Length == 0;
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression unary
               && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private static object? NormalizeValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var type = value.GetType();
        return type.IsEnum ? Convert.ChangeType(value, Enum.GetUnderlyingType(type)) : value;
    }

    private static byte[] ComputeSchemaFingerprint(
        Type indexedType,
        IReadOnlyList<SearchIndexDefinition> definitions)
    {
        var builder = new StringBuilder(indexedType.AssemblyQualifiedName ?? indexedType.FullName);
        foreach (var definition in definitions)
        {
            builder.Append('\n')
                .Append(definition.Path)
                .Append('|')
                .Append(definition.ValueType.AssemblyQualifiedName)
                .Append('|')
                .Append((int)definition.Mode);
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static double EstimateIndexExecutionCost(
        SearchComparisonOperator comparisonOperator,
        int estimatedCount)
    {
        var startupCost = comparisonOperator switch
        {
            SearchComparisonOperator.Equal => 1d,
            SearchComparisonOperator.StartsWith or SearchComparisonOperator.EndsWith => 8d,
            SearchComparisonOperator.Contains => 32d,
            _ => 12d
        };
        var candidateCost = comparisonOperator is SearchComparisonOperator.Contains ? 2d : 1d;
        return startupCost + estimatedCount * candidateCost;
    }

    private static string FormatRange(ObjectRangeQuery query)
    {
        var lower = query.IncludeLowerBound ? '[' : '(';
        var upper = query.IncludeUpperBound ? ']' : ')';
        return $"{lower}{SearchIndexAccessPath.FormatValue(query.LowerBound)}, "
            + $"{SearchIndexAccessPath.FormatValue(query.UpperBound)}{upper}";
    }

    private void RemoveCore(string physicalKey)
    {
        if (!_valuesByPhysicalKey.TryGetValue(physicalKey, out var values))
        {
            return;
        }

        for (var index = 0; index < values.Length && index < _definitions.Count; index++)
        {
            var value = values[index];
            if (value is not null)
            {
                _indexesByDefinition[index].Remove(value, physicalKey);
            }
        }

        _valuesByPhysicalKey.Remove(physicalKey);
    }

    private void ClearCore()
    {
        _valuesByPhysicalKey.Clear();
        foreach (var index in _indexes.Values)
        {
            index.Clear();
        }
    }

    private void CompleteBulkLoad()
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            Exception? completionException = null;
            foreach (var index in _indexes.Values)
            {
                try
                {
                    index.CompleteBulkLoad();
                }
                catch (Exception exception)
                {
                    completionException ??= exception;
                }
            }

            if (completionException is not null)
            {
                ExceptionDispatchInfo.Capture(completionException).Throw();
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private sealed record CombinedRangeCandidate(
        SearchIndexAccessPath AccessPath,
        int LeftIndex,
        int RightIndex);

    private sealed class BulkLoadScope(SearchIndexStore<TContract> owner) : IDisposable
    {
        private SearchIndexStore<TContract>? _owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.CompleteBulkLoad();
    }
}
