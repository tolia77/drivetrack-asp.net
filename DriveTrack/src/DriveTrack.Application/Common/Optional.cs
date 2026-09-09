namespace DriveTrack.Application.Common;

/// <summary>
/// AD-23's absent-versus-null wrapper: a value a partial update either carries or does not.
/// <para>
/// A nullable field cannot express the difference. <c>null</c> on an update command has to mean two
/// incompatible things at once — "leave this alone" and "clear this" — and every codebase that tries
/// settles the ambiguity with a convention: a magic empty string for an unchanged password, a
/// sentinel date, a second boolean per field. Making it a type is what stops the convention being
/// something each caller has to remember.
/// </para>
/// <para>
/// <see cref="Absent"/> is <c>default</c>, deliberately: a command property nobody assigns, and a
/// JSON property the caller omitted, are both absent without anyone writing code to say so.
/// <c>Present(null)</c> is the other case — the caller sent the field and sent nothing in it, which
/// for a clearable field means clear it.
/// </para>
/// </summary>
/// <typeparam name="T">The wrapped value's type.</typeparam>
public readonly struct Optional<T> : IEquatable<Optional<T>>
{
    private readonly T _value;

    private Optional(T value)
    {
        _value = value;
        HasValue = true;
    }

    /// <summary>The absent case: the caller did not send this field. Equal to <c>default</c>.</summary>
    public static Optional<T> Absent => default;

    /// <summary>True when the caller sent the field, whatever it sent in it.</summary>
    public bool HasValue { get; }

    /// <summary>
    /// What the caller sent.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The value is absent. Throwing rather than answering <c>default</c> is the same choice
    /// <c>ICurrentUser</c> makes: a default that reads as a real value is how an absent field ends
    /// up written to a row as an empty string.
    /// </exception>
    public T Value => HasValue
        ? _value
        : throw new InvalidOperationException(
            "This Optional<" + typeof(T).Name + "> is absent; check HasValue, or call Or(...).");

    /// <summary>The present case, including a present null.</summary>
    /// <param name="value">What the caller sent.</param>
    /// <remarks>
    /// The parameter is nullable because a present null is the whole point of the type: the caller
    /// sent the field and sent nothing in it, which for a clearable field means clear it. A
    /// non-nullable parameter would have made the one case this type exists to express the one case
    /// it could not accept without a suppression at every call site.
    /// </remarks>
    public static Optional<T> Present(T? value) => new(value!);

    /// <summary>Two absents are equal; two presents are equal when their values are.</summary>
    public static bool operator ==(Optional<T> left, Optional<T> right) => left.Equals(right);

    /// <summary>The negation of <see cref="op_Equality"/>.</summary>
    public static bool operator !=(Optional<T> left, Optional<T> right) => !left.Equals(right);

    /// <summary>
    /// The merge (AD-23): what the caller sent, or the state already stored when they sent nothing.
    /// This is the one call that turns a partial payload into a whole one.
    /// </summary>
    /// <param name="fallback">The current value, used only when this is absent.</param>
    public T Or(T fallback) => HasValue ? _value : fallback;

    /// <inheritdoc />
    public bool Equals(Optional<T> other) =>
        HasValue == other.HasValue
        && (!HasValue || EqualityComparer<T>.Default.Equals(_value, other._value));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Optional<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HasValue
        ? HashCode.Combine(true, _value)
        : HashCode.Combine(false);
}
