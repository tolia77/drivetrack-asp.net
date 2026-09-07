using FluentValidation;

namespace DriveTrack.Application.Common;

/// <summary>
/// AD-9's explicit validation seam: the one bridge between FluentValidation and the error
/// vocabulary. An application service calls this itself, after authorization and before acting.
/// No MVC filter, model binder or other adapter pipeline invokes a validator - which is why the
/// deprecated auto-validation package is absent and why <c>FluentValidation</c> is named nowhere
/// under <c>DriveTrack.Web</c>.
/// <para>
/// <b>Calling it, and the one way to get it wrong.</b>
/// <c>FluentValidation.DefaultValidatorExtensions</c> declares a method of this exact name and
/// signature that throws <em>its</em> <c>ValidationException</c> - a type carrying no
/// <see cref="ErrorCode"/>, so the adapter's generic catch answers 500 where 422 was meant.
/// The compiler only helps in one of the two cases. A file holding both
/// <c>using FluentValidation;</c> and <c>using DriveTrack.Application.Common;</c> gets CS0121, which
/// is loud. A file in a sibling namespace - <c>DriveTrack.Application.Deliveries</c>, where the
/// services will live - that imports only <c>FluentValidation</c> does <em>not</em> have this class
/// in scope, because <c>Common</c> is not an enclosing namespace of it: the call binds silently to
/// FluentValidation's method and compiles clean. So an application service must import
/// <c>DriveTrack.Application.Common</c>, or call
/// <c>ValidatorExtensions.ValidateAndThrowAsync(validator, command, cancellationToken)</c> in
/// static form, which cannot bind anywhere else.
/// </para>
/// </summary>
public static class ValidatorExtensions
{
    /// <summary>
    /// Validates <paramref name="instance"/> and throws on failure.
    /// </summary>
    /// <exception cref="ValidationException">
    /// The instance is invalid. Carries one <see cref="FieldError"/> per failure, so every
    /// offending field survives to the wire (NFR-4).
    /// </exception>
    public static async Task ValidateAndThrowAsync<T>(
        this IValidator<T> validator,
        T instance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validator);

        var result = await validator.ValidateAsync(instance, cancellationToken);

        if (result.IsValid)
        {
            return;
        }

        // A validator's message is a resource key, not a sentence: the adapter resolves it through
        // the same catalogue as error.code, so NFR-14 holds for field messages too and nothing a
        // validator writes reaches a user untranslated.
        var fieldErrors = result.Errors
            .Select(failure => new FieldError(failure.PropertyName, failure.ErrorMessage))
            .ToArray();

        throw new ValidationException(
            ErrorCode.COMMON_VALIDATION_FAILED,
            "Validation of " + typeof(T).Name + " failed on: "
                + string.Join(", ", fieldErrors.Select(field => field.Field)),
            fieldErrors);
    }
}
