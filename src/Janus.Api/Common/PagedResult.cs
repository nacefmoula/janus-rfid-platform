namespace Janus.Api.Common;

/// <summary>Page de résultats d'une liste paginée côté serveur.</summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages
);
