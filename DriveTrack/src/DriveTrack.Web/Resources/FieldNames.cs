namespace DriveTrack.Web.Resources;

/// <summary>
/// Marker type for the field-label catalogue: the name of an input, in Ukrainian, keyed by the CLR
/// property a validator names when it refuses one (NFR-14).
/// <para>
/// A third catalogue rather than an entry in either existing one, because both are closed in both
/// directions against something this cannot satisfy. <see cref="UiText"/> is closed against the
/// literal <c>@Localizer["Key"]</c> occurrences a component contains, and a field label is looked up
/// by a runtime string, so every entry would read as unused. <see cref="ErrorMessages"/> is closed
/// against <see cref="Application.Common.ErrorCode"/>, and a field name is not a code.
/// </para>
/// <para>
/// It is kept honest the same way they are: a test walks every validator's rules and asserts each
/// field name reachable from one resolves here, and that no entry here is unreachable.
/// </para>
/// </summary>
public sealed class FieldNames;
