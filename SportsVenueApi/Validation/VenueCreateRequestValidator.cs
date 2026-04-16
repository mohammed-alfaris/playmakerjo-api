using FluentValidation;
using SportsVenueApi.DTOs.Venues;

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
    }
}
