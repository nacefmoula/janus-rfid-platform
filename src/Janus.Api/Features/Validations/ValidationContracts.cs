namespace Janus.Api.Features.Validations;

/// <summary>Événement de passage soumis par un terminal. L'identité du terminal provient du jeton, jamais du corps.</summary>
public sealed record ValidationRequest(Guid EventId, string CardUid, DateTimeOffset? OccurredAt, string EventType);
