namespace Janus.Api.Features.Wallet;

/// <summary>Crédit idempotent : OperationId est généré par le client et réutilisé tel quel en cas de nouvel essai.</summary>
public sealed record CardCreditRequest(Guid OperationId, int AmountMillimes, string? Reason);

/// <summary>Écriture du journal comptable exposée par l'API.</summary>
public sealed record LedgerEntryDto(
    long Id,
    string EntryType,
    int AmountMillimes,
    int BalanceAfterMillimes,
    string Actor,
    string? Reason,
    DateTimeOffset CreatedAt
);
