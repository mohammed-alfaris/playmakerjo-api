using SportsVenueApi.Helpers;

namespace SportsVenueApi.Tests.Customers;

/// <summary>
/// The phone is the natural key for a customer — (owner_id, phone) is unique — so every
/// spelling of the same number MUST collapse to one string. If it doesn't, the same person
/// becomes three customers with a third of their history each and the whole feature is
/// worthless. No database needed; this is pure string handling.
/// </summary>
public class PhoneNormalizerTests
{
    [Theory]
    // The four ways a Jordanian number gets typed...
    [InlineData("0791234567")]
    [InlineData("791234567")]
    [InlineData("+962791234567")]
    [InlineData("00962791234567")]
    // ...with the separators people actually use...
    [InlineData("079 123 4567")]
    [InlineData("079-123-4567")]
    [InlineData("+962 79 123 4567")]
    [InlineData("(079) 1234567")]
    // ...and on an Arabic keyboard, which nothing else in this codebase handles.
    [InlineData("٠٧٩١٢٣٤٥٦٧")]
    [InlineData("۰۷۹۱۲۳۴۵۶۷")]
    public void AllAcceptedSpellings_CollapseToOneCanonicalForm(string input)
    {
        Assert.Equal("+962791234567", PhoneNormalizer.ToE164Jo(input));
    }

    [Theory]
    [InlineData("077")]           // too short
    [InlineData("07912345678")]   // too long
    [InlineData("0761234567")]    // 76 is not a Jordanian mobile prefix
    [InlineData("062345678")]     // landline
    [InlineData("+9715551234")]   // UAE
    [InlineData("not a phone")]
    [InlineData("")]
    [InlineData(null)]
    public void UnusableInput_YieldsNull(string? input)
    {
        // Null means "do not record a customer", never "reject the booking" — the person is
        // standing at the counter and the slot still has to be written down.
        Assert.Null(PhoneNormalizer.ToE164Jo(input));
    }

    [Theory]
    [InlineData("0771234567")]
    [InlineData("0781234567")]
    [InlineData("0791234567")]
    public void AllThreeMobilePrefixes_AreAccepted(string input)
    {
        Assert.NotNull(PhoneNormalizer.ToE164Jo(input));
    }

    [Fact]
    public void NormalisationIsIdempotent()
    {
        var once = PhoneNormalizer.ToE164Jo("0791234567");
        Assert.Equal(once, PhoneNormalizer.ToE164Jo(once));
    }
}
