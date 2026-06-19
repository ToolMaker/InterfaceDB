using System.Collections;
using System.Linq.Expressions;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;

namespace Idb.Libraries.Implementation.Indexes;

internal sealed class SearchIndexStore<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly IReadOnlyList<SearchIndexDefinition> _definitions;
    private readonly Dictionary<string, PropertySearchIndex> _indexes;
    private readonly Dictionary<string, Dictionary<string, object?>> _valuesByPhysicalKey = new(StringComparer.Ordinal);

    public SearchIndexStore()
    {
        _definitions = SearchIndexDiscovery.Discover(typeof(TContract));
        _indexes = _definitions.ToDictionary(
            static definition => definition.Path,
            static definition => new PropertySearchIndex(definition),
            StringComparer.Ordinal);
    }

    public bool HasIndexes => _indexes.Count > 0;

    public void Clear()
    {
        _valuesByPhysicalKey.Clear();
        foreach (var index in _indexes.Values)
        {
            index.Clear();
        }
    }

    public void Upsert(string physicalKey, TContract entity)
    {
        if (string.IsNullOrWhiteSpace(physicalKey) || !HasIndexes)
        {
            return;
        }

        Remove(physicalKey);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var definition in _definitions)
        {
            var value = NormalizeValue(definition.Getter(entity));
            values[definition.Path] = value;
            if (value is not null && _indexes.TryGetValue(definition.Path, out var index))
            {
                index.Add(value, physicalKey);
            }
        }

        _valuesByPhysicalKey[physicalKey] = values;
    }

    public void Remove(string physicalKey)
    {
        if (!_valuesByPhysicalKey.TryGetValue(physicalKey, out var values))
        {
            return;
        }

        foreach (var item in values)
        {
            if (item.Value is not null && _indexes.TryGetValue(item.Key, out var index))
            {
                index.Remove(item.Value, physicalKey);
            }
        }

        _valuesByPhysicalKey.Remove(physicalKey);
    }

    public HashSet<string>? TryEvaluate(Expression<Func<TContract, bool>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return TryEvaluateNode(expression.Body);
    }

    private HashSet<string>? TryEvaluateNode(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is BinaryExpression binary)
        {
            return binary.NodeType switch
            {
                ExpressionType.AndAlso => CombineAnd(binary.Left, binary.Right),
                ExpressionType.OrElse => CombineOr(binary.Left, binary.Right),
                ExpressionType.Equal => TryEvaluateComparison(binary.Left, binary.Right, SearchComparisonOperator.Equal),
                ExpressionType.GreaterThan => TryEvaluateComparison(binary.Left, binary.Right, SearchComparisonOperator.GreaterThan),
                ExpressionType.GreaterThanOrEqual => TryEvaluateComparison(binary.Left, binary.Right, SearchComparisonOperator.GreaterThanOrEqual),
                ExpressionType.LessThan => TryEvaluateComparison(binary.Left, binary.Right, SearchComparisonOperator.LessThan),
                ExpressionType.LessThanOrEqual => TryEvaluateComparison(binary.Left, binary.Right, SearchComparisonOperator.LessThanOrEqual),
                _ => null
            };
        }

        if (expression is MethodCallExpression call)
        {
            return TryEvaluateMethodCall(call);
        }

        if (expression is MemberExpression member && member.Type == typeof(bool))
        {
            return TryEvaluateComparison(member, Expression.Constant(true), SearchComparisonOperator.Equal);
        }

        if (expression is UnaryExpression { NodeType: ExpressionType.Not } not
            && StripConvert(not.Operand) is MemberExpression boolMember
            && boolMember.Type == typeof(bool))
        {
            return TryEvaluateComparison(boolMember, Expression.Constant(false), SearchComparisonOperator.Equal);
        }

        return null;
    }

    private HashSet<string>? CombineAnd(Expression left, Expression right)
    {
        var leftEstimate = TryEstimateCandidateCount(left);
        var rightEstimate = TryEstimateCandidateCount(right);
        if (leftEstimate.HasValue || rightEstimate.HasValue)
        {
            return ShouldUseRight(leftEstimate, rightEstimate)
                ? TryEvaluateBestCandidate(right)
                : TryEvaluateBestCandidate(left);
        }

        var leftResult = TryEvaluateNode(left);
        var rightResult = TryEvaluateNode(right);
        if (leftResult is null || rightResult is null)
        {
            return null;
        }

        if (rightResult.Count < leftResult.Count)
        {
            (leftResult, rightResult) = (rightResult, leftResult);
        }

        leftResult.IntersectWith(rightResult);
        return leftResult;
    }

    private HashSet<string>? TryEvaluateBestCandidate(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is BinaryExpression binary)
        {
            if (binary.NodeType is ExpressionType.AndAlso)
            {
                var leftEstimate = TryEstimateCandidateCount(binary.Left);
                var rightEstimate = TryEstimateCandidateCount(binary.Right);
                return ShouldUseRight(leftEstimate, rightEstimate)
                    ? TryEvaluateBestCandidate(binary.Right)
                    : TryEvaluateBestCandidate(binary.Left);
            }

            return binary.NodeType switch
            {
                ExpressionType.Equal => TryEvaluateComparison(binary.Left, binary.Right, SearchComparisonOperator.Equal),
                ExpressionType.GreaterThan => TryEvaluateComparison(binary.Left, binary.Right, SearchComparisonOperator.GreaterThan),
                ExpressionType.GreaterThanOrEqual => TryEvaluateComparison(binary.Left, binary.Right, SearchComparisonOperator.GreaterThanOrEqual),
                ExpressionType.LessThan => TryEvaluateComparison(binary.Left, binary.Right, SearchComparisonOperator.LessThan),
                ExpressionType.LessThanOrEqual => TryEvaluateComparison(binary.Left, binary.Right, SearchComparisonOperator.LessThanOrEqual),
                _ => null
            };
        }

        if (expression is MethodCallExpression call)
        {
            return TryEvaluateMethodCall(call);
        }

        if (expression is MemberExpression member && member.Type == typeof(bool))
        {
            return TryEvaluateComparison(member, Expression.Constant(true), SearchComparisonOperator.Equal);
        }

        if (expression is UnaryExpression { NodeType: ExpressionType.Not } not
            && StripConvert(not.Operand) is MemberExpression boolMember
            && boolMember.Type == typeof(bool))
        {
            return TryEvaluateComparison(boolMember, Expression.Constant(false), SearchComparisonOperator.Equal);
        }

        return null;
    }

    private int? TryEstimateCandidateCount(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is BinaryExpression binary)
        {
            if (binary.NodeType is ExpressionType.AndAlso)
            {
                var leftEstimate = TryEstimateCandidateCount(binary.Left);
                var rightEstimate = TryEstimateCandidateCount(binary.Right);
                if (!leftEstimate.HasValue)
                {
                    return rightEstimate;
                }

                if (!rightEstimate.HasValue)
                {
                    return leftEstimate;
                }

                return Math.Min(leftEstimate.Value, rightEstimate.Value);
            }

            return binary.NodeType switch
            {
                ExpressionType.Equal => TryEstimateComparison(binary.Left, binary.Right, SearchComparisonOperator.Equal),
                ExpressionType.GreaterThan => TryEstimateComparison(binary.Left, binary.Right, SearchComparisonOperator.GreaterThan),
                ExpressionType.GreaterThanOrEqual => TryEstimateComparison(binary.Left, binary.Right, SearchComparisonOperator.GreaterThanOrEqual),
                ExpressionType.LessThan => TryEstimateComparison(binary.Left, binary.Right, SearchComparisonOperator.LessThan),
                ExpressionType.LessThanOrEqual => TryEstimateComparison(binary.Left, binary.Right, SearchComparisonOperator.LessThanOrEqual),
                _ => null
            };
        }

        if (expression is MethodCallExpression call)
        {
            return TryEstimateMethodCall(call);
        }

        if (expression is MemberExpression member && member.Type == typeof(bool))
        {
            return TryEstimateComparison(member, Expression.Constant(true), SearchComparisonOperator.Equal);
        }

        if (expression is UnaryExpression { NodeType: ExpressionType.Not } not
            && StripConvert(not.Operand) is MemberExpression boolMember
            && boolMember.Type == typeof(bool))
        {
            return TryEstimateComparison(boolMember, Expression.Constant(false), SearchComparisonOperator.Equal);
        }

        return null;
    }

    private int? TryEstimateComparison(
        Expression left,
        Expression right,
        SearchComparisonOperator comparisonOperator)
    {
        if (!TryReadMemberPath(left, out var path))
        {
            if (!TryReadMemberPath(right, out path))
            {
                return null;
            }

            var reversedOperator = Reverse(comparisonOperator);
            var leftValue = Evaluate(left);
            return TryEstimateCondition(path, reversedOperator, leftValue);
        }

        var rightValue = Evaluate(right);
        return TryEstimateCondition(path, comparisonOperator, rightValue);
    }

    private int? TryEstimateMethodCall(MethodCallExpression call)
    {
        if (call.Method.Name != nameof(string.StartsWith)
            || call.Object is null
            || !TryReadMemberPath(call.Object, out var path)
            || call.Arguments.Count is 0 or > 2)
        {
            return null;
        }

        if (call.Arguments.Count == 2)
        {
            var comparison = Evaluate(call.Arguments[1]);
            if (comparison is not StringComparison.Ordinal)
            {
                return null;
            }
        }

        var prefix = Evaluate(call.Arguments[0]) as string;
        return prefix is null
            ? null
            : TryEstimateCondition(path, SearchComparisonOperator.StartsWith, prefix);
    }

    private int? TryEstimateCondition(
        string path,
        SearchComparisonOperator comparisonOperator,
        object? value)
    {
        value = NormalizeValue(value);
        if (value is null || !_indexes.TryGetValue(path, out var index))
        {
            return null;
        }

        return index.EstimateCount(comparisonOperator, value);
    }

    private static bool ShouldUseRight(int? leftEstimate, int? rightEstimate)
        => !leftEstimate.HasValue || (rightEstimate.HasValue && rightEstimate.Value < leftEstimate.Value);

    private HashSet<string>? CombineOr(Expression left, Expression right)
    {
        var leftResult = TryEvaluateNode(left);
        var rightResult = TryEvaluateNode(right);
        if (leftResult is null || rightResult is null)
        {
            return null;
        }

        if (rightResult.Count > leftResult.Count)
        {
            (leftResult, rightResult) = (rightResult, leftResult);
        }

        leftResult.UnionWith(rightResult);
        return leftResult;
    }

    private HashSet<string>? TryEvaluateComparison(
        Expression left,
        Expression right,
        SearchComparisonOperator comparisonOperator)
    {
        if (!TryReadMemberPath(left, out var path))
        {
            if (!TryReadMemberPath(right, out path))
            {
                return null;
            }

            var reversedOperator = Reverse(comparisonOperator);
            var leftValue = Evaluate(left);
            return TryEvaluateCondition(path, reversedOperator, leftValue);
        }

        var rightValue = Evaluate(right);
        return TryEvaluateCondition(path, comparisonOperator, rightValue);
    }

    private HashSet<string>? TryEvaluateMethodCall(MethodCallExpression call)
    {
        if (call.Method.Name != nameof(string.StartsWith)
            || call.Object is null
            || !TryReadMemberPath(call.Object, out var path)
            || call.Arguments.Count is 0 or > 2)
        {
            return null;
        }

        if (call.Arguments.Count == 2)
        {
            var comparison = Evaluate(call.Arguments[1]);
            if (comparison is not StringComparison.Ordinal)
            {
                return null;
            }
        }

        var prefix = Evaluate(call.Arguments[0]) as string;
        return prefix is null
            ? null
            : TryEvaluateCondition(path, SearchComparisonOperator.StartsWith, prefix);
    }

    private HashSet<string>? TryEvaluateCondition(
        string path,
        SearchComparisonOperator comparisonOperator,
        object? value)
    {
        value = NormalizeValue(value);
        if (value is null || !_indexes.TryGetValue(path, out var index))
        {
            return null;
        }

        return index.Search(comparisonOperator, value);
    }

    private static SearchComparisonOperator Reverse(SearchComparisonOperator comparisonOperator)
        => comparisonOperator switch
        {
            SearchComparisonOperator.GreaterThan => SearchComparisonOperator.LessThan,
            SearchComparisonOperator.GreaterThanOrEqual => SearchComparisonOperator.LessThanOrEqual,
            SearchComparisonOperator.LessThan => SearchComparisonOperator.GreaterThan,
            SearchComparisonOperator.LessThanOrEqual => SearchComparisonOperator.GreaterThanOrEqual,
            _ => comparisonOperator
        };

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

    private static object? Evaluate(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is ConstantExpression constant)
        {
            return constant.Value;
        }

        var boxed = Expression.Convert(expression, typeof(object));
        return Expression.Lambda<Func<object?>>(boxed).Compile().Invoke();
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

    private enum SearchComparisonOperator
    {
        Equal,
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual,
        StartsWith
    }

    private sealed class PropertySearchIndex
    {
        private readonly SearchIndexDefinition _definition;
        private readonly Dictionary<object, HashSet<string>> _exact = new();
        private readonly SortedDictionary<object, HashSet<string>> _range = new(Comparer<object>.Create(CompareObjects));
        private readonly PrefixNode _prefix = new();

        public PropertySearchIndex(SearchIndexDefinition definition)
        {
            _definition = definition;
        }

        public void Clear()
        {
            _exact.Clear();
            _range.Clear();
            _prefix.Children.Clear();
            _prefix.Keys.Clear();
        }

        public void Add(object value, string physicalKey)
        {
            if (_definition.Mode.HasFlag(SearchIndexMode.Exact))
            {
                AddTo(_exact, value, physicalKey);
            }

            if (_definition.Mode.HasFlag(SearchIndexMode.Range))
            {
                AddTo(_range, value, physicalKey);
            }

            if (_definition.Mode.HasFlag(SearchIndexMode.Prefix) && value is string text)
            {
                AddPrefix(text, physicalKey);
            }
        }

        public void Remove(object value, string physicalKey)
        {
            if (_definition.Mode.HasFlag(SearchIndexMode.Exact))
            {
                RemoveFrom(_exact, value, physicalKey);
            }

            if (_definition.Mode.HasFlag(SearchIndexMode.Range))
            {
                RemoveFrom(_range, value, physicalKey);
            }

            if (_definition.Mode.HasFlag(SearchIndexMode.Prefix) && value is string text)
            {
                RemovePrefix(text, physicalKey);
            }
        }

        public HashSet<string>? Search(SearchComparisonOperator comparisonOperator, object value)
        {
            if (comparisonOperator is SearchComparisonOperator.Equal && _definition.Mode.HasFlag(SearchIndexMode.Exact))
            {
                return _exact.TryGetValue(value, out var keys)
                    ? new HashSet<string>(keys, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
            }

            if (comparisonOperator is SearchComparisonOperator.StartsWith
                && _definition.Mode.HasFlag(SearchIndexMode.Prefix)
                && value is string prefix)
            {
                return SearchPrefix(prefix);
            }

            if (comparisonOperator is SearchComparisonOperator.Equal && _definition.Mode.HasFlag(SearchIndexMode.Range))
            {
                return _range.TryGetValue(value, out var keys)
                    ? new HashSet<string>(keys, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
            }

            if (!_definition.Mode.HasFlag(SearchIndexMode.Range))
            {
                return null;
            }

            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in _range)
            {
                var comparison = CompareObjects(item.Key, value);
                var include = comparisonOperator switch
                {
                    SearchComparisonOperator.GreaterThan => comparison > 0,
                    SearchComparisonOperator.GreaterThanOrEqual => comparison >= 0,
                    SearchComparisonOperator.LessThan => comparison < 0,
                    SearchComparisonOperator.LessThanOrEqual => comparison <= 0,
                    _ => false
                };

                if (include)
                {
                    result.UnionWith(item.Value);
                }
            }

            return result;
        }

        public int? EstimateCount(SearchComparisonOperator comparisonOperator, object value)
        {
            if (comparisonOperator is SearchComparisonOperator.Equal && _definition.Mode.HasFlag(SearchIndexMode.Exact))
            {
                return _exact.TryGetValue(value, out var keys) ? keys.Count : 0;
            }

            if (comparisonOperator is SearchComparisonOperator.StartsWith
                && _definition.Mode.HasFlag(SearchIndexMode.Prefix)
                && value is string prefix)
            {
                return EstimatePrefix(prefix);
            }

            if (comparisonOperator is SearchComparisonOperator.Equal && _definition.Mode.HasFlag(SearchIndexMode.Range))
            {
                return _range.TryGetValue(value, out var keys) ? keys.Count : 0;
            }

            if (!_definition.Mode.HasFlag(SearchIndexMode.Range))
            {
                return null;
            }

            var count = 0;
            foreach (var item in _range)
            {
                var comparison = CompareObjects(item.Key, value);
                var include = comparisonOperator switch
                {
                    SearchComparisonOperator.GreaterThan => comparison > 0,
                    SearchComparisonOperator.GreaterThanOrEqual => comparison >= 0,
                    SearchComparisonOperator.LessThan => comparison < 0,
                    SearchComparisonOperator.LessThanOrEqual => comparison <= 0,
                    _ => false
                };

                if (include)
                {
                    count += item.Value.Count;
                }
            }

            return count;
        }

        private static void AddTo(IDictionary<object, HashSet<string>> index, object value, string physicalKey)
        {
            if (!index.TryGetValue(value, out var keys))
            {
                keys = new HashSet<string>(StringComparer.Ordinal);
                index[value] = keys;
            }

            keys.Add(physicalKey);
        }

        private static void RemoveFrom(IDictionary<object, HashSet<string>> index, object value, string physicalKey)
        {
            if (!index.TryGetValue(value, out var keys))
            {
                return;
            }

            keys.Remove(physicalKey);
            if (keys.Count == 0)
            {
                index.Remove(value);
            }
        }

        private void AddPrefix(string value, string physicalKey)
        {
            var node = _prefix;
            node.Keys.Add(physicalKey);
            foreach (var character in value)
            {
                if (!node.Children.TryGetValue(character, out var child))
                {
                    child = new PrefixNode();
                    node.Children[character] = child;
                }

                node = child;
                node.Keys.Add(physicalKey);
            }
        }

        private void RemovePrefix(string value, string physicalKey)
        {
            var node = _prefix;
            node.Keys.Remove(physicalKey);
            foreach (var character in value)
            {
                if (!node.Children.TryGetValue(character, out node!))
                {
                    return;
                }

                node.Keys.Remove(physicalKey);
            }
        }

        private HashSet<string> SearchPrefix(string value)
        {
            var node = _prefix;
            foreach (var character in value)
            {
                if (!node.Children.TryGetValue(character, out node!))
                {
                    return new HashSet<string>(StringComparer.Ordinal);
                }
            }

            return new HashSet<string>(node.Keys, StringComparer.Ordinal);
        }

        private int EstimatePrefix(string value)
        {
            var node = _prefix;
            foreach (var character in value)
            {
                if (!node.Children.TryGetValue(character, out node!))
                {
                    return 0;
                }
            }

            return node.Keys.Count;
        }

        private static int CompareObjects(object? left, object? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            if (left is IComparable comparable)
            {
                return comparable.CompareTo(right);
            }

            return Comparer.DefaultInvariant.Compare(left, right);
        }

        private sealed class PrefixNode
        {
            public Dictionary<char, PrefixNode> Children { get; } = new();
            public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
        }
    }
}
