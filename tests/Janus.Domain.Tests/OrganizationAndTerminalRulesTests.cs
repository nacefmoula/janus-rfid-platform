using Janus.Domain.Cards;
using Janus.Domain.Organizations;
using Janus.Domain.Terminals;

namespace Janus.Domain.Tests;

public class TerminalTokenTests
{
    [Fact]
    public void Generate_HasPrefixAnd43Base64UrlCharacters()
    {
        var token = TerminalToken.Generate();

        Assert.StartsWith(TerminalToken.Prefix, token, StringComparison.Ordinal);
        var body = token[TerminalToken.Prefix.Length..];
        Assert.Equal(43, body.Length);
        Assert.All(body, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_', $"Unexpected character '{c}'"));
    }

    [Fact]
    public void Generate_ProducesDistinctTokens()
    {
        var tokens = Enumerable.Range(0, 1000).Select(_ => TerminalToken.Generate()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(1000, tokens.Count);
    }

    [Fact]
    public void ComputeHash_IsUppercaseSha256Hex_WithFrozenReferenceValue()
    {
        // Valeur de référence calculée indépendamment (Python hashlib) : le format des empreintes stockées est contractuel.
        var hash = TerminalToken.ComputeHash("jt_golden-reference-token-lot5a");

        Assert.Equal("60423CC6B3062D96642B438A947C0D64BF9451EC37F8332442741818CC552318", hash);
        Assert.Equal(JanusSchemaLimits.Sha256HexLength, hash.Length);
    }

    [Fact]
    public void ComputeHash_RejectsEmptyToken()
    {
        Assert.ThrowsAny<ArgumentException>(() => TerminalToken.ComputeHash(""));
    }
}

public class TerminalAccessPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ActiveTerminal_ValidToken_ActiveOrganization_IsAllowed()
    {
        var decision = TerminalAccessPolicy.Evaluate(TerminalStatus.Active, Now.AddDays(1), OrganizationStatus.Active, Now);

        Assert.Equal(TerminalAccessDecision.Allowed, decision);
        Assert.Null(TerminalAccessPolicy.ToFailureReason(decision));
    }

    [Fact]
    public void RevokedTerminal_IsRefused_EvenIfOtherwiseValid()
    {
        var decision = TerminalAccessPolicy.Evaluate(TerminalStatus.Revoked, Now.AddDays(1), OrganizationStatus.Active, Now);

        Assert.Equal("revoked_token", TerminalAccessPolicy.ToFailureReason(decision));
    }

    [Fact]
    public void Token_ExpiresExactlyAtExpiresAt()
    {
        Assert.Equal(TerminalAccessDecision.Allowed, TerminalAccessPolicy.Evaluate(TerminalStatus.Active, Now.AddTicks(1), OrganizationStatus.Active, Now));
        Assert.Equal(TerminalAccessDecision.Expired, TerminalAccessPolicy.Evaluate(TerminalStatus.Active, Now, OrganizationStatus.Active, Now));
        Assert.Equal("expired_token", TerminalAccessPolicy.ToFailureReason(TerminalAccessDecision.Expired));
    }

    [Fact]
    public void SuspendedOrganization_RefusesAValidToken()
    {
        var decision = TerminalAccessPolicy.Evaluate(TerminalStatus.Active, Now.AddDays(1), OrganizationStatus.Suspended, Now);

        Assert.Equal("organization_suspended", TerminalAccessPolicy.ToFailureReason(decision));
    }

    [Fact]
    public void Order_IsRevokedThenExpiredThenSuspended()
    {
        Assert.Equal(TerminalAccessDecision.Revoked, TerminalAccessPolicy.Evaluate(TerminalStatus.Revoked, Now, OrganizationStatus.Suspended, Now));
        Assert.Equal(TerminalAccessDecision.Expired, TerminalAccessPolicy.Evaluate(TerminalStatus.Active, Now, OrganizationStatus.Suspended, Now));
    }
}

public class TerminalRulesTests
{
    private static Terminal NewTerminal() => new()
    {
        OrganizationId = 1,
        Code = "BORNE-01",
        Label = "Borne",
        TokenHash = new string('A', 64),
        CreatedBy = "ADMIN",
        ExpiresAt = DateTimeOffset.UnixEpoch.AddDays(1),
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    [Fact]
    public void Revoke_SetsAllRevocationFields()
    {
        var terminal = NewTerminal();
        var at = DateTimeOffset.UnixEpoch.AddHours(1);

        terminal.Revoke("ADMIN", "Borne volée", at);

        Assert.Equal(TerminalStatus.Revoked, terminal.Status);
        Assert.Equal(at, terminal.RevokedAt);
        Assert.Equal("ADMIN", terminal.RevokedBy);
        Assert.Equal("Borne volée", terminal.RevocationReason);
    }

    [Fact]
    public void Revoke_IsIrreversible_AndRequiresActorAndReason()
    {
        var terminal = NewTerminal();
        Assert.ThrowsAny<ArgumentException>(() => terminal.Revoke("ADMIN", " ", DateTimeOffset.UnixEpoch));

        terminal.Revoke("ADMIN", "motif", DateTimeOffset.UnixEpoch);

        Assert.Throws<InvalidOperationException>(() => terminal.Revoke("ADMIN", "motif", DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData(null, true, TerminalTokenLifetime.DefaultDays)]
    [InlineData("", true, TerminalTokenLifetime.DefaultDays)]
    [InlineData("1", true, 1)]
    [InlineData("3650", true, 3650)]
    [InlineData("0", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("3651", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData("30.5", false, 0)]
    public void TokenLifetime_IsBoundedWithDefault(string? configured, bool valid, int expectedDays)
    {
        Assert.Equal(valid, TerminalTokenLifetime.TryParse(configured, out var days));
        Assert.Equal(expectedDays, days);
        if (valid)
            Assert.Equal(expectedDays, TerminalTokenLifetime.ResolveDays(configured));
        else
            Assert.Throws<InvalidOperationException>(() => TerminalTokenLifetime.ResolveDays(configured));
    }
}

public class OrganizationCodesTests
{
    [Theory]
    [InlineData("DEMO-TRANSIT", true)]
    [InlineData("AB", true)]
    [InlineData("A", false)]
    [InlineData("-ABC", false)]
    [InlineData("demo", false)]
    [InlineData("DEMO_TRANSIT", false)]
    [InlineData("DÉMO", false)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456", false)]
    public void OrganizationCode_Format(string code, bool expected)
    {
        Assert.Equal(expected, OrganizationCodes.IsValidOrganizationCode(code));
    }

    [Theory]
    [InlineData("TERMINAL-DEMO-01", true)]
    [InlineData("AB", false)]
    [InlineData("ABC", true)]
    [InlineData("BORNE 01", false)]
    public void TerminalCode_Format(string code, bool expected)
    {
        Assert.Equal(expected, OrganizationCodes.IsValidTerminalCode(code));
    }

    [Fact]
    public void TerminalCode_MaxLengthMatchesValidationTerminalIdColumn()
    {
        Assert.True(OrganizationCodes.IsValidTerminalCode(new string('T', JanusSchemaLimits.TerminalCodeMaxLength)));
        Assert.False(OrganizationCodes.IsValidTerminalCode(new string('T', JanusSchemaLimits.TerminalCodeMaxLength + 1)));
    }

    [Fact]
    public void Normalize_TrimsAndUppercases_AndMapsBlankToNull()
    {
        Assert.Equal("DEMO-TRANSIT", OrganizationCodes.Normalize("  demo-transit "));
        Assert.Null(OrganizationCodes.Normalize("   "));
    }
}

public class CardOrganizationPolicyTests
{
    [Fact]
    public void Creation_WithoutCode_UsesDemoOrganization_OnlyWhenAllowed()
    {
        Assert.Equal((OrganizationDefaults.DemoOrganizationCode, CardOrganizationRejection.None),
            CardOrganizationPolicy.ResolveForCreation(null, allowDemoOrganizationDefault: true));
        Assert.Equal((null, CardOrganizationRejection.OrganizationRequired),
            CardOrganizationPolicy.ResolveForCreation(null, allowDemoOrganizationDefault: false));
    }

    [Fact]
    public void Creation_WithCode_UsesIt_AndRejectsInvalidFormat()
    {
        Assert.Equal(("ACME", CardOrganizationRejection.None), CardOrganizationPolicy.ResolveForCreation("ACME", false));
        Assert.Equal((null, CardOrganizationRejection.InvalidOrganizationCode), CardOrganizationPolicy.ResolveForCreation("a b", false));
    }

    [Fact]
    public void ExistingCard_NeverChangesOrganization()
    {
        Assert.Equal(CardOrganizationRejection.None, CardOrganizationPolicy.ValidateForExistingCard(null, 1));
        Assert.Equal(CardOrganizationRejection.None, CardOrganizationPolicy.ValidateForExistingCard(1, 1));
        Assert.Equal(CardOrganizationRejection.OrganizationMismatch, CardOrganizationPolicy.ValidateForExistingCard(2, 1));
    }

    [Fact]
    public void DemoOrganization_HasFrozenIdentity()
    {
        // Référencée par la migration AddOrganizationsAndTerminals : ne jamais modifier.
        Assert.Equal(1, OrganizationDefaults.DemoOrganizationId);
        Assert.Equal("DEMO-TRANSIT", OrganizationDefaults.DemoOrganizationCode);
        Assert.True(OrganizationCodes.IsValidOrganizationCode(OrganizationDefaults.DemoOrganizationCode));
    }
}
