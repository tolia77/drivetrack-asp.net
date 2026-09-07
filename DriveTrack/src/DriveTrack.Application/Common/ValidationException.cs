using System.Collections.ObjectModel;

namespace DriveTrack.Application.Common;

/// <summary>
/// One offending field of a rejected request: the CLR property name, and the resource key of the
/// message explaining why (NFR-4). The key, not a message - the adapter resolves it through the same
/// localized catalogue as <c>error.code</c>, so nothing here can put untranslated or caller-derived
/// prose on the wire.
/// <para>
/// <see cref="Field"/> is the name this layer knows (<c>Rating</c>). The adapter converts it to the
/// name the caller sent (<c>rating</c>) with the same naming policy it serializes everything else
/// with; Application does not know how the wire spells its properties and must not guess.
/// </para>
/// </summary>
/// <param name="Field">The offending property name, as the CLR spells it.</param>
/// <param name="MessageKey">Resource key of the explanation.</param>
public sealed record FieldError(string Field, string MessageKey);

/// <summary>
/// The request was understood and refused on its content (422). Carries every offending field, not
/// only the first, because a form that reports one error at a time is a form filled in five times.
/// </summary>
public sealed class ValidationException : DriveTrackException
{
    /// <summary>Creates a validation failure listing every offending field.</summary>
    public ValidationException(ErrorCode code, string message, IEnumerable<FieldError> fieldErrors)
        : base(code, message) => FieldErrors = Freeze(fieldErrors);

    /// <summary>Creates a validation failure listing every offending field, with a cause.</summary>
    public ValidationException(
        ErrorCode code,
        string message,
        IEnumerable<FieldError> fieldErrors,
        Exception? innerException)
        : base(code, message, innerException) => FieldErrors = Freeze(fieldErrors);

    /// <summary>The offending fields. Empty when the failure names no particular field.</summary>
    public IReadOnlyList<FieldError> FieldErrors { get; }

    private static ReadOnlyCollection<FieldError> Freeze(IEnumerable<FieldError> fieldErrors)
    {
        ArgumentNullException.ThrowIfNull(fieldErrors);

        return new ReadOnlyCollection<FieldError>([.. fieldErrors]);
    }
}
