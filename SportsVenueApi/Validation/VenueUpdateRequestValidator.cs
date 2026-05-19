using FluentValidation;
using SportsVenueApi.DTOs.Venues;

namespace SportsVenueApi.Validation;

public class VenueUpdateRequestValidator : AbstractValidator<VenueUpdateRequest>
{
    public VenueUpdateRequestValidator()
    {
        RuleFor(v => v.Name)
            .MaximumLength(120)
            .When(v => v.Name != null);

        RuleFor(v => v.City)
            .MaximumLength(80)
            .When(v => v.City != null);

        RuleFor(v => v.Address)
            .MaximumLength(255)
            .When(v => v.Address != null);

        RuleFor(v => v.PricePerHour)
            .GreaterThan(0).WithMessage("Price per hour must be greater than 0")
            .When(v => v.PricePerHour.HasValue);

        RuleFor(v => v.Latitude)
            .InclusiveBetween(-90, 90)
            .When(v => v.Latitude.HasValue);

        RuleFor(v => v.Longitude)
            .InclusiveBetween(-180, 180)
            .When(v => v.Longitude.HasValue);

        RuleFor(v => v.DepositPercentage)
            .InclusiveBetween(0, 100).WithMessage("Deposit percentage must be 0–100")
            .When(v => v.DepositPercentage.HasValue);

        RuleFor(v => v.MinBookingDuration)
            .GreaterThan(0)
            .When(v => v.MinBookingDuration.HasValue);

        RuleFor(v => v.MaxBookingDuration)
            .GreaterThan(0)
            .When(v => v.MaxBookingDuration.HasValue);

        RuleFor(v => v.CliqAlias)
            .MaximumLength(255)
            .When(v => v.CliqAlias != null);

        RuleFor(v => v.NameAr).MaximumLength(120).When(v => v.NameAr != null);
        RuleFor(v => v.CityAr).MaximumLength(80).When(v => v.CityAr != null);
        RuleFor(v => v.AddressAr).MaximumLength(255).When(v => v.AddressAr != null);
    }
}
