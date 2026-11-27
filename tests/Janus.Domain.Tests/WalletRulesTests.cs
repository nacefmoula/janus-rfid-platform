using Janus.Domain.Cards;
using Janus.Domain.Wallet;

namespace Janus.Domain.Tests;

public class LedgerIdempotencyKeysTests
{
    private static readonly Guid Id = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    [Fact]
    public void Keys_HaveStableContractualFormats()
    {
        // Formats figés : ils sont stockés dans l'index unique du journal comptable.
        Assert.Equal("fare:TERMINAL-DEMO-01:3f2504e04f8941d39a0c0305e82c3301", LedgerIdempotencyKeys.FareDebit("TERMINAL-DEMO-01", Id));
        Assert.Equal("credit:3f2504e04f8941d39a0c0305e82c3301", LedgerIdempotencyKeys.AdminCredit(Id));
        Assert.Equal("admin-set:3f2504e04f8941d39a0c0305e82c3301", LedgerIdempotencyKeys.AdminSet(Id));
        Assert.Equal("opening:seed:04A1B2C3D4E5F6", LedgerIdempotencyKeys.SeedOpening("04A1B2C3D4E5F6"));
    }

    [Fact]
    public void FareDebitKey_FitsTheColumnForTheLongestTerminalId()
    {
        var key = LedgerIdempotencyKeys.FareDebit(new string('T', 80), Id);

        Assert.True(key.Length <= JanusSchemaLimits.LedgerIdempotencyKeyMaxLength);
    }
}

public class CreditPolicyTests
{
    private const int Max = CreditPolicy.DefaultMaxCreditMillimes;

    [Theory]
    [InlineData(1, "Recharge", CreditRejection.None)]
    [InlineData(Max, "Recharge", CreditRejection.None)]
    [InlineData(0, "Recharge", CreditRejection.InvalidAmount)]
    [InlineData(-500, "Recharge", CreditRejection.InvalidAmount)]
    [InlineData(Max + 1, "Recharge", CreditRejection.InvalidAmount)]
    [InlineData(1000, null, CreditRejection.InvalidReason)]
    [InlineData(1000, "   ", CreditRejection.InvalidReason)]
    public void ValidateRequest_AppliesAmountAndReasonRules(int amount, string? reason, CreditRejection expected)
    {
        Assert.Equal(expected, CreditPolicy.ValidateRequest(Guid.NewGuid(), amount, reason, Max));
    }

    [Fact]
    public void ValidateRequest_RequiresOperationIdFirst()
    {
        Assert.Equal(CreditRejection.MissingOperationId, CreditPolicy.ValidateRequest(Guid.Empty, 0, null, Max));
    }

    [Fact]
    public void ValidateRequest_RejectsReasonLongerThanAuditColumn()
    {
        var reason = new string('R', JanusSchemaLimits.AuditReasonMaxLength + 1);

        Assert.Equal(CreditRejection.InvalidReason, CreditPolicy.ValidateRequest(Guid.NewGuid(), 1000, reason, Max));
    }

    [Fact]
    public void ValidateAgainstCard_RejectsRevokedCardAndOverflow()
    {
        var revoked = new TransitCard { Uid = "A", Label = "a", Status = CardStatus.Revoked, BalanceMillimes = 0 };
        var nearlyFull = new TransitCard { Uid = "B", Label = "b", Status = CardStatus.Active, BalanceMillimes = int.MaxValue - 10 };
        var active = new TransitCard { Uid = "C", Label = "c", Status = CardStatus.Active, BalanceMillimes = 5000 };

        Assert.Equal(CreditRejection.CardRevoked, CreditPolicy.ValidateAgainstCard(revoked, 1000));
        Assert.Equal(CreditRejection.BalanceLimitExceeded, CreditPolicy.ValidateAgainstCard(nearlyFull, 11));
        Assert.Equal(CreditRejection.None, CreditPolicy.ValidateAgainstCard(nearlyFull, 10));
        Assert.Equal(CreditRejection.None, CreditPolicy.ValidateAgainstCard(active, 1000));
    }
}

public class TransitCardTests
{
    private static TransitCard Card(int balance, CardStatus status = CardStatus.Active) =>
        new() { Uid = "04A1", Label = "Test", Status = status, BalanceMillimes = balance };

    [Fact]
    public void Debit_DecreasesBalance_AndAllowsReachingZero()
    {
        var card = Card(1500);

        card.Debit(1500);

        Assert.Equal(0, card.BalanceMillimes);
    }

    [Fact]
    public void Debit_NeverMakesBalanceNegative()
    {
        var card = Card(1000);

        Assert.Throws<InvalidOperationException>(() => card.Debit(1500));
        Assert.Equal(1000, card.BalanceMillimes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DebitAndCredit_RejectNonPositiveAmounts(int amount)
    {
        var card = Card(1000);

        Assert.Throws<ArgumentOutOfRangeException>(() => card.Debit(amount));
        Assert.Throws<ArgumentOutOfRangeException>(() => card.Credit(amount));
        Assert.Equal(1000, card.BalanceMillimes);
    }

    [Fact]
    public void Credit_IncreasesBalance()
    {
        var card = Card(1000);

        card.Credit(4000);

        Assert.Equal(5000, card.BalanceMillimes);
    }

    [Fact]
    public void Credit_IsForbiddenOnRevokedCardsAndOnOverflow()
    {
        var revoked = Card(1000, CardStatus.Revoked);
        var full = Card(int.MaxValue);

        Assert.Throws<InvalidOperationException>(() => revoked.Credit(1));
        Assert.Throws<InvalidOperationException>(() => full.Credit(1));
        Assert.Equal(1000, revoked.BalanceMillimes);
        Assert.Equal(int.MaxValue, full.BalanceMillimes);
    }
}
