namespace DriveTrack.Web.Resources;

/// <summary>
/// Marker type for the Blazor shell's text (NFR-14). Kept apart from
/// <see cref="ErrorMessages"/> deliberately: the error catalogue is closed in both directions
/// against <see cref="Application.Common.ErrorCode"/>, and one stray chrome string in it would break
/// that exhaustiveness test for a reason that has nothing to do with the failure vocabulary.
/// </summary>
public sealed class UiText;
