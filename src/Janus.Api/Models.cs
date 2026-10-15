using Microsoft.EntityFrameworkCore;
namespace Janus.Api;
public enum CardStatus { Active=0, Revoked=1 }
public enum ValidationDecision { Accepted=0, Rejected=1 }
public sealed class TransitCard { public int Id {get;set;} public required string Uid {get;set;} public CardStatus Status {get;set;} public required string Label {get;set;} }
public sealed class ValidationEvent {
 public long Id {get;set;} public required string TerminalId {get;set;} public Guid EventId {get;set;}
 public required string CardUid {get;set;} public required string PayloadHash {get;set;} public required string EventType {get;set;}
 public ValidationDecision Decision {get;set;} public required string Reason {get;set;}
 public DateTimeOffset? OccurredAt {get;set;} public DateTimeOffset ReceivedAt {get;set;}
}
public sealed record ValidationRequest(Guid EventId, string CardUid, DateTimeOffset? OccurredAt, string EventType);
public sealed class JanusDbContext(DbContextOptions<JanusDbContext> options):DbContext(options) {
 public DbSet<TransitCard> Cards=>Set<TransitCard>(); public DbSet<ValidationEvent> Validations=>Set<ValidationEvent>();
 protected override void OnModelCreating(ModelBuilder b) {
  b.Entity<TransitCard>(e=>{e.HasKey(x=>x.Id);e.HasIndex(x=>x.Uid).IsUnique();e.Property(x=>x.Uid).HasMaxLength(64).IsRequired();e.Property(x=>x.Label).HasMaxLength(120).IsRequired();});
  b.Entity<ValidationEvent>(e=>{e.HasKey(x=>x.Id);e.HasIndex(x=>new{x.TerminalId,x.EventId}).IsUnique();e.Property(x=>x.TerminalId).HasMaxLength(80).IsRequired();e.Property(x=>x.CardUid).HasMaxLength(64).IsRequired();e.Property(x=>x.PayloadHash).HasMaxLength(64).IsRequired();e.Property(x=>x.EventType).HasMaxLength(80).IsRequired();e.Property(x=>x.Reason).HasMaxLength(80).IsRequired();});
 }
}
