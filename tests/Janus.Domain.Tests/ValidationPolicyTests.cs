using Janus.Domain.Cards;
using Janus.Domain.Validations;

namespace Janus.Domain.Tests;

public class ValidationPolicyTests
{
    private const int Fare = 1500;

    [Theory]
    [InlineData(null, 10000, false, ValidationDecision.Rejected, ValidationReasons.UnknownCard)]
    [InlineData(CardStatus.Revoked, 10000, false, ValidationDecision.Rejected, ValidationReasons.CardRevoked)]
    [InlineData(CardStatus.Active, 10000, true, ValidationDecision.Rejected, ValidationReasons.PassbackViolation)]
    [InlineData(CardStatus.Active, 1499, false, ValidationDecision.Rejected, ValidationReasons.InsufficientFunds)]
    [InlineData(CardStatus.Active, 1500, false, ValidationDecision.Accepted, ValidationReasons.ActiveCard)]
    [InlineData(CardStatus.Active, 10000, false, ValidationDecision.Accepted, ValidationReasons.ActiveCard)]
    public void Decide_ReturnsExpectedOutcome(CardStatus? status, int balance, bool isPassback, ValidationDecision expectedDecision, string expectedReason)
    {
        var outcome = ValidationPolicy.Decide(status, balance, Fare, isPassback);

        Assert.Equal(expectedDecision, outcome.Decision);
        Assert.Equal(expectedReason, outcome.Reason);
        Assert.Equal(expectedDecision == ValidationDecision.Accepted, outcome.IsAccepted);
    }

    [Fact]
    public void Decide_RevokedTakesPrecedenceOverPassbackAndFunds()
    {
        var outcome = ValidationPolicy.Decide(CardStatus.Revoked, 0, Fare, isPassback: true);

        Assert.Equal(ValidationReasons.CardRevoked, outcome.Reason);
    }

    [Fact]
    public void Decide_PassbackTakesPrecedenceOverInsufficientFunds()
    {
        var outcome = ValidationPolicy.Decide(CardStatus.Active, 0, Fare, isPassback: true);

        Assert.Equal(ValidationReasons.PassbackViolation, outcome.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1500)]
    public void Decide_RejectsNonPositiveFare(int fare)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ValidationPolicy.Decide(CardStatus.Active, 10000, fare, isPassback: false));
    }

    [Theory]
    [InlineData(CardStatus.Active, true, true)]
    [InlineData(CardStatus.Active, false, false)]
    [InlineData(CardStatus.Revoked, true, false)]
    [InlineData(null, true, false)]
    public void RequiresPassbackCheck_OnlyForActiveCardsWhenEnabled(CardStatus? status, bool enabled, bool expected)
    {
        Assert.Equal(expected, ValidationPolicy.RequiresPassbackCheck(status, enabled));
    }

    [Fact]
    public void IsWellFormed_AcceptsValidTransportEvent()
    {
        Assert.True(ValidationPolicy.IsWellFormed(Guid.NewGuid(), "04A1B2C3D4E5F6", ValidationEventTypes.TransportValidation));
    }

    [Theory]
    [InlineData(true, "04A1", "TRANSPORT_VALIDATION")]
    [InlineData(false, "", "TRANSPORT_VALIDATION")]
    [InlineData(false, "   ", "TRANSPORT_VALIDATION")]
    [InlineData(false, null, "TRANSPORT_VALIDATION")]
    [InlineData(false, "04A1", "transport_validation")]
    [InlineData(false, "04A1", "HOTEL_CHECKIN")]
    [InlineData(false, "04A1", null)]
    public void IsWellFormed_RejectsMalformedEvents(bool emptyEventId, string? cardUid, string? eventType)
    {
        var eventId = emptyEventId ? Guid.Empty : Guid.NewGuid();

        Assert.False(ValidationPolicy.IsWellFormed(eventId, cardUid, eventType));
    }

    [Fact]
    public void IsWellFormed_EnforcesUidMaxLength()
    {
        Assert.True(ValidationPolicy.IsWellFormed(Guid.NewGuid(), new string('A', JanusSchemaLimits.CardUidMaxLength), ValidationEventTypes.TransportValidation));
        Assert.False(ValidationPolicy.IsWellFormed(Guid.NewGuid(), new string('A', JanusSchemaLimits.CardUidMaxLength + 1), ValidationEventTypes.TransportValidation));
    }

    [Theory]
    [InlineData("04a1b2c3d4e5f6", "04A1B2C3D4E5F6")]
    [InlineData("  04A1B2  ", "04A1B2")]
    public void NormalizeCardUid_TrimsAndUppercases(string input, string expected)
    {
        Assert.Equal(expected, ValidationPolicy.NormalizeCardUid(input));
    }
}
