using FluentValidation;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Helpers;

namespace SportsVenueApi.Validation;

public class VenueCreateRequestValidator : AbstractValidator<VenueCreateRequest>
{
    public VenueCreateRequestValidator()
    {
        RuleFor(v => v.Name)
            .NotEmpty().WithMessage("Name is required")
            .MaximumLength(120);

        RuleFor(v => v.City)
            .NotEmpty().WithMessage("City is required")
            .MaximumLength(80);

        RuleFor(v => v.Address)
            .NotEmpty().WithMessage("Address is required")
            .MaximumLength(255);

        RuleFor(v => v.PricePerHour)
            .GreaterThan(0).WithMessage("Price per hour must be greater than 0");

        RuleFor(v => v.Latitude)
            .InclusiveBetween(-90, 90).When(v => v.Latitude.HasValue);

        RuleFor(v => v.Longitude)
            .InclusiveBetween(-180, 180).When(v => v.Longitude.HasValue);

        RuleFor(v => v.Sports)
            .NotEmpty().WithMessage("At least one sport is required");

        RuleFor(v => v.NameAr).MaximumLength(120).When(v => v.NameAr != null);
        RuleFor(v => v.CityAr).MaximumLength(80).When(v => v.CityAr != null);
        RuleFor(v => v.AddressAr).MaximumLength(255).When(v => v.AddressAr != null);

        // Coarse bounds only; VenueFeatureRules applies the exact rules against the catalog
        // and answers in the ApiResponse envelope.
        RuleFor(v => v.FeatureIds!.Count)
            .LessThanOrEqualTo(VenueFeatureRules.MaxFeatureIdsPerVenue)
            .When(v => v.FeatureIds != null);
        RuleFor(v => v.CustomFeatures!.Count)
            .LessThanOrEqualTo(VenueFeatureRules.MaxCustomPerVenue)
            .WithMessage($"A venue can list at most {VenueFeatureRules.MaxCustomPerVenue} custom features.")
            .When(v => v.CustomFeatures != null);
        RuleForEach(v => v.CustomFeatures)
            .Must(label => (label ?? "").Trim().Length <= VenueFeatureRules.MaxCustomLabelLength)
            .WithMessage($"Custom features must be at most {VenueFeatureRules.MaxCustomLabelLength} characters.")
            .When(v => v.CustomFeatures != null);
    }
}
