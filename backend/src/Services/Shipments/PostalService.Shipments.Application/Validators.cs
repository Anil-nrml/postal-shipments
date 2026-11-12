using FluentValidation;
using PostalService.Shipments.Domain;

namespace PostalService.Shipments.Application;

internal static class Rules
{
    public const string ZipPattern = "^[A-Za-z0-9]{3,10}$";
    public const string ZipMessage = "Must be a 3-10 character alphanumeric ZIP code.";

    public static readonly string[] SortFields = { "createdat", "updatedat", "shipmentnumber", "weight", "status" };
}

public sealed class CreateShipmentRequestValidator : AbstractValidator<CreateShipmentRequest>
{
    public CreateShipmentRequestValidator()
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.WeightKg).GreaterThan(0).LessThanOrEqualTo(Weight.MaxKilograms)
            .PrecisionScale(7, 3, ignoreTrailingZeros: true).WithMessage("Weight supports at most 3 decimals (grams).");
        RuleFor(x => x.OriginZip).NotEmpty().Matches(Rules.ZipPattern).WithMessage(Rules.ZipMessage);
        RuleFor(x => x.DestinationZip).NotEmpty().Matches(Rules.ZipPattern).WithMessage(Rules.ZipMessage);
    }
}

public sealed class UpdateShipmentRequestValidator : AbstractValidator<UpdateShipmentRequest>
{
    public UpdateShipmentRequestValidator()
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.WeightKg).GreaterThan(0).LessThanOrEqualTo(Weight.MaxKilograms)
            .PrecisionScale(7, 3, ignoreTrailingZeros: true);
        RuleFor(x => x.DestinationZip).NotEmpty().Matches(Rules.ZipPattern).WithMessage(Rules.ZipMessage);
        RuleFor(x => x.Version).GreaterThan(0);
    }
}

public sealed class ChangeStatusRequestValidator : AbstractValidator<ChangeStatusRequest>
{
    public ChangeStatusRequestValidator()
    {
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.PostOfficeZip).NotEmpty().Matches(Rules.ZipPattern).WithMessage(Rules.ZipMessage);
        RuleFor(x => x.Remarks).MaximumLength(500);
        RuleFor(x => x.Version).GreaterThan(0);
    }
}

public sealed class CorrectStatusRequestValidator : AbstractValidator<CorrectStatusRequest>
{
    public CorrectStatusRequestValidator()
    {
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.PostOfficeZip).NotEmpty().Matches(Rules.ZipPattern).WithMessage(Rules.ZipMessage);
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(10).MaximumLength(500);
        RuleFor(x => x.Version).GreaterThan(0);
    }
}

public sealed class ShipmentSearchCriteriaValidator : AbstractValidator<ShipmentSearchCriteria>
{
    public ShipmentSearchCriteriaValidator()
    {
        RuleFor(x => x.PageNumber).GreaterThan(0).When(x => x.PageNumber.HasValue);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).When(x => x.PageSize.HasValue);
        RuleFor(x => x.SortBy)
            .Must(s => Rules.SortFields.Contains(s!.ToLowerInvariant()))
            .When(x => !string.IsNullOrWhiteSpace(x.SortBy))
            .WithMessage($"sortBy must be one of: createdAt, updatedAt, shipmentNumber, weight, status.");
        RuleFor(x => x.SortDirection)
            .Must(s => s!.Equals("asc", StringComparison.OrdinalIgnoreCase) || s.Equals("desc", StringComparison.OrdinalIgnoreCase))
            .When(x => !string.IsNullOrWhiteSpace(x.SortDirection))
            .WithMessage("sortDirection must be 'asc' or 'desc'.");
        RuleFor(x => x.ShipmentNumber).MaximumLength(30).Matches("^[A-Za-z0-9-]+$")
            .When(x => !string.IsNullOrEmpty(x.ShipmentNumber))
            .WithMessage("shipmentNumber may contain letters, digits and '-' only.");
        RuleFor(x => x.Type).IsInEnum().When(x => x.Type.HasValue);
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status.HasValue);
        RuleFor(x => x.WeightCategory).IsInEnum().When(x => x.WeightCategory.HasValue);
        RuleFor(x => x.OriginPostOffice).Matches(Rules.ZipPattern).When(x => !string.IsNullOrEmpty(x.OriginPostOffice));
        RuleFor(x => x.DestinationPostOffice).Matches(Rules.ZipPattern).When(x => !string.IsNullOrEmpty(x.DestinationPostOffice));
        RuleFor(x => x.CurrentPostOffice).Matches(Rules.ZipPattern).When(x => !string.IsNullOrEmpty(x.CurrentPostOffice));
        RuleFor(x => x).Must(x => x.CreatedFrom <= x.CreatedTo)
            .When(x => x.CreatedFrom.HasValue && x.CreatedTo.HasValue)
            .WithName("createdFrom").WithMessage("createdFrom must be on or before createdTo.");
    }
}
