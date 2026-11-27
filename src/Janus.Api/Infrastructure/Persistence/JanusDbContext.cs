using Janus.Domain.Cards;
using Janus.Domain.Validations;
using Janus.Domain.Wallet;
using Microsoft.EntityFrameworkCore;

namespace Janus.Api.Infrastructure.Persistence;

/// <summary>Contexte EF Core de la plateforme (PostgreSQL).</summary>
public sealed class JanusDbContext(DbContextOptions<JanusDbContext> options) : DbContext(options)
{
    /// <summary>Cartes émises et leur solde courant.</summary>
    public DbSet<TransitCard> Cards => Set<TransitCard>();
    /// <summary>Événements de passage reçus des terminaux.</summary>
    public DbSet<ValidationEvent> Validations => Set<ValidationEvent>();
    /// <summary>Historique des débits de validation.</summary>
    public DbSet<FareTransaction> FareTransactions => Set<FareTransaction>();
    /// <summary>Audit administratif append-only.</summary>
    public DbSet<CardAuditLog> CardAuditLogs => Set<CardAuditLog>();
    /// <summary>Journal comptable append-only.</summary>
    public DbSet<WalletLedgerEntry> WalletLedgerEntries => Set<WalletLedgerEntry>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(JanusDbContext).Assembly);
    }
}
