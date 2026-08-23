using System.Globalization;
using System.Text;
using Idb.Libraries.Abstractions.Enums;

namespace Idb.Libraries.Implementation.Indexes;

internal enum SearchQueryPlanKind
{
    FullScan,
    IndexScan,
    Intersection,
    Union
}

internal sealed record SearchQueryPlannerOptions(double ObjectLoadCost)
{
    public static SearchQueryPlannerOptions MetadataOnly { get; } = new(64d);

    public static SearchQueryPlannerOptions StrongObject { get; } = new(2d);

    public static SearchQueryPlannerOptions WeakObject { get; } = new(12d);

    public static SearchQueryPlannerOptions For(StorageObjectCacheMode cacheMode)
        => cacheMode switch
        {
            StorageObjectCacheMode.StrongObject => StrongObject,
            StorageObjectCacheMode.WeakObject => WeakObject,
            _ => MetadataOnly
        };
}

internal sealed class SearchIndexAccessPath
{
    private readonly Func<HashSet<string>> _execute;

    public SearchIndexAccessPath(
        string propertyPath,
        SearchComparisonOperator comparisonOperator,
        string description,
        int estimatedCount,
        double estimatedExecutionCost,
        Func<HashSet<string>> execute,
        int coveredPredicateCount = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedExecutionCost);
        ArgumentOutOfRangeException.ThrowIfLessThan(coveredPredicateCount, 1);

        PropertyPath = propertyPath;
        ComparisonOperator = comparisonOperator;
        Description = description;
        EstimatedCount = estimatedCount;
        EstimatedExecutionCost = estimatedExecutionCost;
        CoveredPredicateCount = coveredPredicateCount;
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public SearchComparisonOperator ComparisonOperator { get; }

    public int CoveredPredicateCount { get; }

    public string Description { get; }

    public int EstimatedCount { get; }

    public double EstimatedExecutionCost { get; }

    public string PropertyPath { get; }

    public SearchQueryPlan ToPlan()
        => SearchQueryPlan.IndexScan(this);

    public HashSet<string> Execute() => _execute();

    public static string FormatValue(object? value)
        => value switch
        {
            null => "null",
            string text => $"\"{text}\"",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
}

internal sealed class SearchQueryPlan
{
    private readonly Func<HashSet<string>?> _execute;

    private SearchQueryPlan(
        SearchQueryPlanKind kind,
        string description,
        int estimatedCount,
        double estimatedCost,
        int coveredPredicateCount,
        int residualPredicateCount,
        IReadOnlyList<SearchQueryPlan> children,
        Func<HashSet<string>?> execute)
    {
        Kind = kind;
        Description = description;
        EstimatedCount = estimatedCount;
        EstimatedCost = estimatedCost;
        CoveredPredicateCount = coveredPredicateCount;
        ResidualPredicateCount = residualPredicateCount;
        Children = children;
        _execute = execute;
    }

    public IReadOnlyList<SearchQueryPlan> Children { get; }

    public string Description { get; }

    public int CoveredPredicateCount { get; }

    public double EstimatedCost { get; }

    public int EstimatedCount { get; }

    public SearchQueryPlanKind Kind { get; }

    public int ResidualPredicateCount { get; }

    public static SearchQueryPlan FullScan(
        int totalCount,
        double objectLoadCost,
        int residualPredicateCount = 1)
        => new(
            SearchQueryPlanKind.FullScan,
            "Full storage scan",
            totalCount,
            totalCount * objectLoadCost,
            coveredPredicateCount: 0,
            residualPredicateCount,
            Array.Empty<SearchQueryPlan>(),
            static () => null);

    public static SearchQueryPlan IndexScan(SearchIndexAccessPath accessPath)
    {
        ArgumentNullException.ThrowIfNull(accessPath);
        return new SearchQueryPlan(
            SearchQueryPlanKind.IndexScan,
            accessPath.Description,
            accessPath.EstimatedCount,
            accessPath.EstimatedExecutionCost,
            accessPath.CoveredPredicateCount,
            residualPredicateCount: 0,
            Array.Empty<SearchQueryPlan>(),
            accessPath.Execute);
    }

    public static SearchQueryPlan Intersection(
        IReadOnlyList<SearchQueryPlan> children,
        int estimatedCount,
        double estimatedCost,
        int residualPredicateCount)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Count < 2)
        {
            throw new ArgumentException("An intersection requires at least two plans.", nameof(children));
        }

        return new SearchQueryPlan(
            SearchQueryPlanKind.Intersection,
            $"Intersect {children.Count} index access paths",
            estimatedCount,
            estimatedCost,
            children.Sum(static child => child.CoveredPredicateCount),
            residualPredicateCount,
            children,
            () => ExecuteIntersection(children));
    }

    public static SearchQueryPlan Union(
        IReadOnlyList<SearchQueryPlan> children,
        int estimatedCount,
        double estimatedCost,
        int residualPredicateCount)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Count < 2)
        {
            throw new ArgumentException("A union requires at least two plans.", nameof(children));
        }

        return new SearchQueryPlan(
            SearchQueryPlanKind.Union,
            $"Union {children.Count} query branches",
            estimatedCount,
            estimatedCost,
            children.Sum(static child => child.CoveredPredicateCount),
            residualPredicateCount,
            children,
            () => ExecuteUnion(children));
    }

    public SearchQueryPlan WithResidualPredicates(int residualPredicateCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(residualPredicateCount);
        return new SearchQueryPlan(
            Kind,
            Description,
            EstimatedCount,
            EstimatedCost,
            CoveredPredicateCount,
            residualPredicateCount,
            Children,
            _execute);
    }

    public HashSet<string>? Execute() => _execute();

    public string Explain()
    {
        var builder = new StringBuilder();
        AppendExplanation(builder, depth: 0);
        return builder.ToString();
    }

    private static HashSet<string>? ExecuteIntersection(IReadOnlyList<SearchQueryPlan> children)
    {
        var result = children[0].Execute();
        if (result is null)
        {
            return null;
        }

        for (var index = 1; index < children.Count && result.Count != 0; index++)
        {
            var next = children[index].Execute();
            if (next is null)
            {
                return null;
            }

            if (next.Count < result.Count)
            {
                next.IntersectWith(result);
                result = next;
            }
            else
            {
                result.IntersectWith(next);
            }
        }

        return result;
    }

    private static HashSet<string>? ExecuteUnion(IReadOnlyList<SearchQueryPlan> children)
    {
        HashSet<string>? result = null;
        foreach (var child in children)
        {
            var next = child.Execute();
            if (next is null)
            {
                return null;
            }

            if (result is null)
            {
                result = next;
                continue;
            }

            if (next.Count > result.Count)
            {
                next.UnionWith(result);
                result = next;
            }
            else
            {
                result.UnionWith(next);
            }
        }

        return result ?? new HashSet<string>(StringComparer.Ordinal);
    }

    private void AppendExplanation(StringBuilder builder, int depth)
    {
        builder.Append(' ', depth * 2)
            .Append(Kind)
            .Append(": ")
            .Append(Description)
            .Append("; estimated candidates=")
            .Append(EstimatedCount.ToString(CultureInfo.InvariantCulture))
            .Append("; estimated cost=")
            .Append(EstimatedCost.ToString("0.##", CultureInfo.InvariantCulture));
        builder.Append("; covered predicates=")
            .Append(CoveredPredicateCount.ToString(CultureInfo.InvariantCulture));
        if (ResidualPredicateCount != 0)
        {
            builder.Append("; residual predicates=")
                .Append(ResidualPredicateCount.ToString(CultureInfo.InvariantCulture));
        }

        builder.AppendLine();
        foreach (var child in Children)
        {
            child.AppendExplanation(builder, depth + 1);
        }
    }
}

internal static class SearchQueryCostModel
{
    public const double HashCandidateCost = 1d;
    public const int MinimumIntersectionCandidateCount = 256;
    public const double MinimumExpectedSavingsRatio = 0.10d;
    public static int EstimateIntersectionCount(int totalCount, int leftCount, int rightCount)
    {
        if (totalCount <= 0 || leftCount == 0 || rightCount == 0)
        {
            return 0;
        }

        var independentEstimate = (long)leftCount * rightCount / totalCount;
        return Math.Min(Math.Min(leftCount, rightCount), Math.Max(1, (int)independentEstimate));
    }

    public static bool ShouldIntersect(
        int totalCount,
        int currentCount,
        SearchQueryPlan nextPlan,
        SearchQueryPlannerOptions options,
        out int estimatedIntersectionCount)
    {
        ArgumentNullException.ThrowIfNull(nextPlan);
        estimatedIntersectionCount = EstimateIntersectionCount(
            totalCount,
            currentCount,
            nextPlan.EstimatedCount);

        if (currentCount < MinimumIntersectionCandidateCount
            || nextPlan.EstimatedCount >= totalCount)
        {
            return false;
        }

        var filterOnlyCost = currentCount * options.ObjectLoadCost;
        var intersectionCost = nextPlan.EstimatedCost
            + (Math.Min(currentCount, nextPlan.EstimatedCount) * HashCandidateCost)
            + (estimatedIntersectionCount * options.ObjectLoadCost);
        return intersectionCost <= filterOnlyCost * (1d - MinimumExpectedSavingsRatio);
    }
}
