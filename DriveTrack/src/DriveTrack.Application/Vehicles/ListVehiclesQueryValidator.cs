using DriveTrack.Application.Common;
using FluentValidation;

namespace DriveTrack.Application.Vehicles;

/// <summary>
/// NFR-27's "validated rather than passed through unchecked".
/// <para>
/// Every message is an <see cref="ErrorCode"/> name applied per rule, never once at the end of a
/// chain — see <c>RegisterClientCommandValidator</c> for why that distinction is load-bearing.
/// </para>
/// </summary>
public sealed class ListVehiclesQueryValidator : AbstractValidator<ListVehiclesQuery>
{
    /// <summary>The largest page a caller may ask for, which is also the default page size.</summary>
    public const int MaximumLimit = 100;

    /// <summary>Declares the rules.</summary>
    public ListVehiclesQueryValidator()
    {
        RuleFor(query => query.Offset)
            .GreaterThanOrEqualTo(0).WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        RuleFor(query => query.Limit)
            .GreaterThan(0).WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .LessThanOrEqualTo(MaximumLimit).WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));
    }
}
