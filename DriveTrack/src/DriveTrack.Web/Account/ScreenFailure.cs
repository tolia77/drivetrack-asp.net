namespace DriveTrack.Web.Account;

/// <summary>
/// One refusal a screen has to show: which input it is about, and the resource key of the sentence
/// explaining it.
/// <para>
/// Public rather than internal, unlike <see cref="FailureKeys"/> itself, because this is the type of
/// <c>DtFailureBanner</c>'s <c>[Parameter]</c>: Razor generates the component's property as public,
/// and a public member cannot be typed by an internal record.
/// </para>
/// </summary>
/// <param name="Field">
/// The <em>root segment</em> of the offending field's path, as the CLR spells it -
/// <c>Pickup.Latitude</c> arrives here as <c>Pickup</c> and <c>Assets[0]</c> as <c>Assets</c>,
/// because the root is the input the form actually has. Null when the failure names no field at
/// all, which is every refusal that is not a validation failure.
/// </param>
/// <param name="MessageKey">Resource key of the explanation, resolved through <c>ErrorMessages</c>.</param>
public sealed record ScreenFailure(string? Field, string MessageKey);
