using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Janus.Api;

var builder=WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<JanusDbContext>(o=>o.UseNpgsql(builder.Configuration.GetConnectionString("JanusDb")));
var app=builder.Build();

await using(var scope=app.Services.CreateAsyncScope()) {
 var db=scope.ServiceProvider.GetRequiredService<JanusDbContext>();
 for(int attempt=0;;attempt++) {
  try {
   await db.Database.EnsureCreatedAsync();
   if(!await db.Cards.AnyAsync()) {
    db.Cards.AddRange(
     new TransitCard{Uid="04A1B2C3D4E5F6",Status=CardStatus.Active,Label="Carte démo active"},
     new TransitCard{Uid="04DEAD00000001",Status=CardStatus.Revoked,Label="Carte démo révoquée"});
    await db.SaveChangesAsync();
   }
   break;
  } catch when(attempt<29) { await Task.Delay(TimeSpan.FromSeconds(2)); }
 }
}
app.MapGet("/health",()=>Results.Ok(new{status="healthy",service="janus-api"}));
app.MapGet("/api/demo/cards",async(JanusDbContext db)=>Results.Ok(await db.Cards.OrderBy(c=>c.Uid).Select(c=>new{c.Uid,status=c.Status.ToString(),c.Label}).ToListAsync()));

app.MapPost("/api/validations",async(ValidationRequest request,HttpRequest http,IConfiguration config,JanusDbContext db)=>{
 var expected=config["TerminalAuth:DemoToken"];
 var header=http.Headers.Authorization.ToString();
 var supplied=header.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)?header[7..].Trim():"";
 if(string.IsNullOrEmpty(expected)||string.IsNullOrEmpty(supplied)||
    !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied),Encoding.UTF8.GetBytes(expected)))
  return Results.Json(new{error="unauthorized",message="Terminal credential missing or invalid."},statusCode:401);
 const string terminalId="TERMINAL-DEMO-01";
 if(request.EventId==Guid.Empty||string.IsNullOrWhiteSpace(request.CardUid)||request.CardUid.Length>64||request.EventType!="TRANSPORT_VALIDATION")
  return Results.BadRequest(new{error="invalid_request",message="eventId, cardUid and supported eventType are required."});
 var uid=request.CardUid.Trim().ToUpperInvariant();
 var canonical=JsonSerializer.Serialize(new{eventId=request.EventId,cardUid=uid,occurredAt=request.OccurredAt?.ToUniversalTime(),eventType=request.EventType});
 var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
 var existing=await db.Validations.AsNoTracking().SingleOrDefaultAsync(v=>v.TerminalId==terminalId&&v.EventId==request.EventId);
 if(existing is not null) {
  if(existing.PayloadHash!=hash) return Results.Conflict(new{error="idempotency_conflict",message="eventId already used with different payload."});
  return Results.Ok(new{eventId=existing.EventId,terminalId=existing.TerminalId,cardUid=existing.CardUid,decision=existing.Decision.ToString().ToUpperInvariant(),duplicate=true,receivedAt=existing.ReceivedAt});
 }
 var card=await db.Cards.SingleOrDefaultAsync(c=>c.Uid==uid);
 var decision=card?.Status==CardStatus.Active?ValidationDecision.Accepted:ValidationDecision.Rejected;
 var reason=card is null?"UNKNOWN_CARD":card.Status==CardStatus.Revoked?"CARD_REVOKED":"ACTIVE_CARD";
 var record=new ValidationEvent{TerminalId=terminalId,EventId=request.EventId,CardUid=uid,PayloadHash=hash,EventType=request.EventType,Decision=decision,Reason=reason,OccurredAt=request.OccurredAt?.ToUniversalTime(),ReceivedAt=DateTimeOffset.UtcNow};
 db.Validations.Add(record);
 try { await db.SaveChangesAsync(); }
 catch(DbUpdateException) {
  db.Entry(record).State=EntityState.Detached;
  var winner=await db.Validations.AsNoTracking().SingleOrDefaultAsync(v=>v.TerminalId==terminalId&&v.EventId==request.EventId);
  if(winner is null) throw;
  if(winner.PayloadHash!=hash) return Results.Conflict(new{error="idempotency_conflict",message="eventId reused with different payload."});
  return Results.Ok(new{eventId=winner.EventId,terminalId=winner.TerminalId,cardUid=winner.CardUid,decision=winner.Decision.ToString().ToUpperInvariant(),duplicate=true,receivedAt=winner.ReceivedAt});
 }
 return Results.Ok(new{eventId=record.EventId,terminalId=record.TerminalId,cardUid=record.CardUid,decision=record.Decision.ToString().ToUpperInvariant(),reason=record.Reason,duplicate=false,receivedAt=record.ReceivedAt});
});
app.Run();
