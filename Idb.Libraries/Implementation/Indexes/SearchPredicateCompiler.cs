using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace Idb.Libraries.Implementation.Indexes;

/// <summary>
/// Reuses executable query structure without retaining a query's constants or freezing its closures.
/// Unusual expression nodes retain the ordinary expression-identity compilation path.
/// </summary>
internal static class SearchPredicateCompiler<T>
{
    internal const int MaximumTemplates = 256;
    private const int MaximumTemplateNodes = 256;
    private static readonly ConditionalWeakTable<Expression<Func<T, bool>>, Func<T, bool>> Predicates = new();
    private static readonly Dictionary<ShapeKey, Func<T, object?[], bool>> Templates = new();
    private static readonly Queue<ShapeKey> AdmissionOrder = new();
    private static readonly object Gate = new();
    private static int _templateCompilationCount;

    internal static int CachedTemplateCount
    {
        get { lock (Gate) { return Templates.Count; } }
    }

    internal static int TemplateCompilationCount => Volatile.Read(ref _templateCompilationCount);

    internal static Func<T, bool> Compile(Expression<Func<T, bool>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return Predicates.GetValue(expression, CreatePredicate);
    }

    private static Func<T, bool> CreatePredicate(Expression<Func<T, bool>> expression)
    {
        var parameterizer = new Parameterizer(expression.Parameters[0]);
        var body = parameterizer.Visit(expression.Body)!;
        if (!parameterizer.Supported)
        {
            return expression.Compile(preferInterpretation: !RuntimeFeature.IsDynamicCodeSupported);
        }

        var key = new ShapeKey(parameterizer.Tokens.ToArray());
        Func<T, object?[], bool> template;
        lock (Gate)
        {
            if (!Templates.TryGetValue(key, out template!))
            {
                template = Expression.Lambda<Func<T, object?[], bool>>(
                    body, parameterizer.Entity, parameterizer.Values)
                    .Compile(preferInterpretation: !RuntimeFeature.IsDynamicCodeSupported);
                if (Templates.Count == MaximumTemplates)
                {
                    Templates.Remove(AdmissionOrder.Dequeue());
                }

                Templates.Add(key, template);
                AdmissionOrder.Enqueue(key);
                Interlocked.Increment(ref _templateCompilationCount);
            }
        }

        // Only this weakly cached query delegate owns the actual constants. A captured object is
        // passed through unchanged, so its fields and properties are read on each predicate call.
        var values = parameterizer.Constants.ToArray();
        return entity => template(entity, values);
    }

    private sealed class ShapeKey(object?[] tokens) : IEquatable<ShapeKey>
    {
        private readonly int _hashCode = GetHashCode(tokens);

        public bool Equals(ShapeKey? other)
            => other is not null && tokens.AsSpan().SequenceEqual(other.Tokens);

        private ReadOnlySpan<object?> Tokens => tokens;

        public override bool Equals(object? obj) => obj is ShapeKey other && Equals(other);

        public override int GetHashCode() => _hashCode;

        private static int GetHashCode(object?[] values)
        {
            var hash = new HashCode();
            foreach (var value in values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }

    private sealed class Parameterizer(ParameterExpression source) : ExpressionVisitor
    {
        private int _nodeCount;
        internal bool Supported { get; private set; } = true;
        internal ParameterExpression Entity { get; } = Expression.Parameter(typeof(T), "entity");
        internal ParameterExpression Values { get; } = Expression.Parameter(typeof(object[]), "values");
        internal List<object?> Tokens { get; } = new();
        internal List<object?> Constants { get; } = new();

        public override Expression? Visit(Expression? node)
        {
            if (!Supported)
            {
                return node;
            }

            if (node is null)
            {
                Tokens.Add(null);
                return null;
            }

            if (++_nodeCount > MaximumTemplateNodes || !IsSupported(node))
            {
                Supported = false;
                return node;
            }

            // These tokens contain types, members, operators and arity, never expression text or
            // constant values. In particular, overloaded operators and methods cannot collide.
            Tokens.Add(node.NodeType);
            Tokens.Add(node.Type);
            return base.Visit(node);
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            var index = Constants.Count;
            Constants.Add(node.Value);
            return Expression.Convert(Expression.ArrayIndex(Values, Expression.Constant(index)), node.Type);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node != source)
            {
                Supported = false;
                return node;
            }

            return Entity;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            Tokens.Add(node.Member);
            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            Tokens.Add(node.Method);
            Tokens.Add(node.Arguments.Count);
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            Tokens.Add(node.Method);
            Tokens.Add(node.IsLifted);
            Tokens.Add(node.IsLiftedToNull);
            return base.VisitBinary(node);
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            Tokens.Add(node.Method);
            Tokens.Add(node.IsLifted);
            Tokens.Add(node.IsLiftedToNull);
            return base.VisitUnary(node);
        }

        protected override Expression VisitNewArray(NewArrayExpression node)
        {
            Tokens.Add(node.Expressions.Count);
            return base.VisitNewArray(node);
        }

        protected override Expression VisitTypeBinary(TypeBinaryExpression node)
        {
            Tokens.Add(node.TypeOperand);
            return base.VisitTypeBinary(node);
        }

        private static bool IsSupported(Expression node)
            => node switch
            {
                ConstantExpression or ParameterExpression or MemberExpression
                    or ConditionalExpression or DefaultExpression or NewArrayExpression
                    or TypeBinaryExpression => true,
                MethodCallExpression call => !call.Method.ReturnType.IsByRef
                    && !call.Method.GetParameters().Any(static parameter => parameter.ParameterType.IsByRef),
                BinaryExpression binary => binary.Conversion is null && binary.NodeType is
                    ExpressionType.Add or ExpressionType.AddChecked or ExpressionType.Subtract
                    or ExpressionType.SubtractChecked or ExpressionType.Multiply or ExpressionType.MultiplyChecked
                    or ExpressionType.Divide or ExpressionType.Modulo or ExpressionType.And or ExpressionType.AndAlso
                    or ExpressionType.Or or ExpressionType.OrElse or ExpressionType.ExclusiveOr
                    or ExpressionType.LeftShift or ExpressionType.RightShift or ExpressionType.Equal
                    or ExpressionType.NotEqual or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
                    or ExpressionType.LessThan or ExpressionType.LessThanOrEqual or ExpressionType.Coalesce
                    or ExpressionType.ArrayIndex or ExpressionType.Power,
                UnaryExpression unary => unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
                    or ExpressionType.Not or ExpressionType.Negate or ExpressionType.NegateChecked
                    or ExpressionType.UnaryPlus or ExpressionType.ArrayLength or ExpressionType.TypeAs
                    or ExpressionType.IsTrue or ExpressionType.IsFalse or ExpressionType.OnesComplement
                    or ExpressionType.Unbox,
                _ => false
            };
    }
}
