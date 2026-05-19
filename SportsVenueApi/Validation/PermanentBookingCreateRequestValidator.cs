using FluentValidation;
using SportsVenueApi.DTOs.PermanentBookings;

namespace SportsVenueApi.Validation;

public class PermanentBookingCreateRequestValidator : AbstractValidator<CreatePermanentBookingRequest>
{
    public PermanentBookingCreateRequestValidator()
    {
        RuleFor(r => r.DayOfWeek)
            .InclusiveBetween(0, 6).WithMessage("dayOfWeek must be 0..6 (0=Sunday)");

        RuleFor(r => r.StartTime)
            .NotEmpty().WithMessage("startTime is required")
            .Matches(@"^([01]\d|2[0-3]):[0-5]\d$").WithMessage("startTime must be HH:mm format (00:00–23:59)");

        RuleFor(r => r.Duration)
            .GreaterThan(0).WithMessage("Duration must be positive");

        RuleFor(r => r.Label)
            .MaximumLength(120).When(r => r.Label != null);
    }
}
