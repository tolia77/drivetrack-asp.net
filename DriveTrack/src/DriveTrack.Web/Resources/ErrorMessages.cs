namespace DriveTrack.Web.Resources;

/// <summary>
/// Marker type for the error-message catalogue (AD-18). <c>IStringLocalizer&lt;ErrorMessages&gt;</c>
/// resolves <c>ErrorMessages.resx</c> from this type's full name, which is why the folder matches the
/// namespace and why no <c>ResourcesPath</c> is configured - one convention instead of two settings
/// that have to agree.
/// <para>
/// The catalogue holds exactly one entry per <see cref="Application.Common.ErrorCode"/> member, keyed
/// by the member name, and nothing else. <c>ErrorContractTests</c> asserts that in both directions:
/// a code with no message would put a raw enum name in front of a user, and a message with no code
/// is dead weight (NFR-6).
/// </para>
/// </summary>
public sealed class ErrorMessages;
