using FluentValidation;
using Restaurant.Contracts.Tenancy;

namespace Restaurant.Application.Features.Tenancy;

public class UpdateTenantRequestValidator : AbstractValidator<UpdateTenantRequest>
{
    public UpdateTenantRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.LegalName).MaximumLength(200);
        RuleFor(x => x.NtnNumber).MaximumLength(30);
        RuleFor(x => x.StrnNumber).MaximumLength(30);
    }
}

public class SaveBranchRequestValidator : AbstractValidator<SaveBranchRequest>
{
    public SaveBranchRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Address).MaximumLength(300);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.OrderNumberPrefix).NotNull().MaximumLength(5).Matches("^[A-Za-z0-9]*$")
            .WithMessage("Use up to 5 letters or digits.");
    }
}

public class BranchSettingsValidator : AbstractValidator<BranchSettingsDto>
{
    public BranchSettingsValidator()
    {
        RuleFor(x => x.TimeZoneId).NotEmpty()
            .Must(id => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _)).WithMessage("Unknown time zone.");
        RuleFor(x => x.DayStartHour).InclusiveBetween(0, 23);
        RuleFor(x => x.ServiceChargePercent).InclusiveBetween(0m, 100m);
        RuleFor(x => x.CashDifferenceThreshold).GreaterThanOrEqualTo(0m);
        RuleFor(x => x.LateTicketMinutes).InclusiveBetween(1, 240);
        RuleFor(x => x.ServiceChargeAppliesTo).IsInEnum();
        RuleFor(x => x.Rounding).IsInEnum();
        RuleFor(x => x.SessionScope).IsInEnum();
        RuleFor(x => x.InsufficientStock).IsInEnum();
    }
}

public class SaveRegisterRequestValidator : AbstractValidator<SaveRegisterRequest>
{
    public SaveRegisterRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.ReceiptPrinterName).MaximumLength(200);
        RuleFor(x => x.KitchenPrinterName).MaximumLength(200);
    }
}
