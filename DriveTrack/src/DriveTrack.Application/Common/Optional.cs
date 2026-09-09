namespace DriveTrack.Application.Common;

/// <summary>
/// AD-23's absent-versus-null wrapper: the shape an update command's field takes so that "leave
/// this alone" and "clear this" are two different requests rather than one.
/// <para>
/// The original system could not express the difference. Its driver update read a plain
/// <c>vehicle_id</c> off the payload, so a field the caller omitted and a field the caller sent as
/// <c>null</c> arrived identically — which meant an assignment could be set and never cleared, and
/// an assigned vehicle became permanently undeletable. FR-38 is that defect, and this type is what
/// makes it expressible.
/// </para>
/// <para>
/// <see cref="Absent"/> is the default, deliberately: System.Text.Json only invokes a converter for
/// a property that is present on the wire, so a field the caller omitted keeps <c>default</c> and
/// reads as absent without anything having to notice.
/// </para>
/// </summary>
/// <typeparam name="T">The field's type. <c>Optional&lt;int?&gt;</c> is the shape that can carry all
/// three answers: absent, present-and-null, present-with-a-value.</typeparam>
public readonly record struct Optional<T>
{
    private Optional(T? value)
    {
        HasValue = true;
        Value = value;
    }

    /// <summary>The field the caller did not send. This is <c>default</c>, so it needs no ceremony.</summary>
    public static Optional<T> Absent => default;

    /// <summary>The field the caller did send, <c>null</c> included.</summary>
    public static Optional<T> Of(T? value) => new(value);

    /// <summary>
    /// True when the caller sent the field. It says nothing about whether the value is null — that
    /// is <see cref="Value"/>'s to answer, and conflating the two is the defect this type closes.
    /// </summary>
    public bool HasValue { get; }

    /// <summary>The value the caller sent. Meaningless unless <see cref="HasValue"/>.</summary>
    public T? Value { get; }

    /// <summary>
    /// The value this field should hold after the update: what the caller sent when they sent
    /// anything, and <paramref name="current"/> when they did not.
    /// </summary>
    /// <param name="current">The value the stored row holds today.</param>
    public T? Or(T? current) => HasValue ? Value : current;
}
