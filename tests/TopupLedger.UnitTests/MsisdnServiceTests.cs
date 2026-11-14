using TopupLedger.Application.Exceptions;
using TopupLedger.Application.Services;
using TopupLedger.Domain.Enums;
using Xunit;

namespace TopupLedger.UnitTests;

public class MsisdnServiceTests
{
    private readonly MsisdnService _svc = new();

    [Theory]
    // Local formats
    [InlineData("0559123456", "971559123456", Operator.Du)]
    [InlineData("559123456", "971559123456", Operator.Du)]
    [InlineData("0501234567", "971501234567", Operator.Etisalat)]
    [InlineData("0541234567", "971541234567", Operator.Etisalat)]
    [InlineData("0561234567", "971561234567", Operator.Etisalat)]
    [InlineData("0521234567", "971521234567", Operator.Du)]
    [InlineData("0581234567", "971581234567", Operator.Du)]
    // International
    [InlineData("+971559123456", "971559123456", Operator.Du)]
    [InlineData("00971559123456", "971559123456", Operator.Du)]
    // With spaces / dashes
    [InlineData("050 123 4567", "971501234567", Operator.Etisalat)]
    [InlineData("050-123-4567", "971501234567", Operator.Etisalat)]
    [InlineData("+971 50 123 4567", "971501234567", Operator.Etisalat)]
    public void Msisdn_Normalises_And_Resolves_Operator(string raw, string expectedNormalised, Operator expectedOp)
    {
        var normalised = _svc.Normalize(raw);
        Assert.Equal(expectedNormalised, normalised);
        var op = _svc.ResolveOperator(normalised);
        Assert.Equal(expectedOp, op);
    }

    [Theory]
    [InlineData("041234567")]     // Landline (not 05 prefix)
    [InlineData("021234567")]     // Landline
    [InlineData("97141234567")]   // Landline international
    [InlineData("")]             // Empty
    [InlineData("abcdefg")]      // Non-numeric
    [InlineData("97149")]        // Truncated landline
    public void Msisdn_LandlinesAndGarbage_Rejected(string raw)
    {
        Assert.Throws<DomainException>(() => _svc.Normalize(raw));
    }

    [Fact]
    public void Msisdn_OperatorPrefixMismatch_ReturnsFalse()
    {
        // 055... is du; asking for Etisalat must return false.
        var normalised = _svc.Normalize("0559123456");
        Assert.False(_svc.OperatorMatchesPrefix(normalised, Operator.Etisalat));
        Assert.True(_svc.OperatorMatchesPrefix(normalised, Operator.Du));
    }

    [Theory]
    [InlineData("971501234567", "97150* ***567")]
    public void Msisdn_Masking_IsCorrect(string normalised, string expectedMasked)
    {
        Assert.Equal(expectedMasked, MsisdnService.MaskForDisplay(normalised));
    }
}
