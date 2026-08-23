namespace Idb.Libraries.Abstractions.Dtos;

/// <summary>
/// Describes how InterfaceDB intends to obtain candidates for an inspectable search expression.
/// </summary>
/// <param name="PlanText">Hierarchical, human-readable query-plan text.</param>
/// <param name="UsesIndexes">Whether at least one search index contributes candidates.</param>
/// <param name="EstimatedCandidateCount">Estimated candidate count, or <see langword="null" /> when unavailable.</param>
/// <param name="EstimatedCost">Relative planner cost used to compare access paths.</param>
/// <param name="ResidualPredicateCount">Conditions applied after candidate retrieval.</param>
public sealed record SearchQueryPlanExplanation(
    string PlanText,
    bool UsesIndexes,
    int? EstimatedCandidateCount,
    double EstimatedCost,
    int ResidualPredicateCount);
