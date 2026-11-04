namespace Idb.Commercial.Repositories;

/// <summary>A previously verified, account-bound approval expired. Access remains denied until
/// the authority grants a fresh approval; connection owners may retry renewal.</summary>
public sealed class RepositoryAccessRenewalRequiredException()
    : UnauthorizedAccessException("The repository approval expired. Reconnect to obtain a fresh approval.");
