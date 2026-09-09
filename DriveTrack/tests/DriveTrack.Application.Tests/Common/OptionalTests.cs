using DriveTrack.Application.Common;

namespace DriveTrack.Application.Tests.Common;

/// <summary>
/// AD-23's wrapper, which is the story's load-bearing primitive: every "leave this field alone"
/// in a partial update is one of these, and the whole point is that absent and present-null are
/// different answers.
/// <para>
/// The tests below are about the distinction rather than about the members. A wrapper whose
/// <c>Absent</c> and <c>Present(null)</c> compared equal, or whose default was accidentally
/// present, would satisfy every reasonable-looking unit test of <c>HasValue</c> and still merge a
/// blank password box into the database as an empty password.
/// </para>
/// </summary>
public class OptionalTests
{
    [Fact]
    public void The_default_value_is_absent()
    {
        // Load-bearing: System.Text.Json passes `default` for a constructor parameter the payload
        // did not mention, and a command property nobody assigns holds `default` too. If the
        // default were present, every omitted field would silently become a write.
        Optional<string?> uninitialized = default;

        Assert.False(uninitialized.HasValue);
        Assert.Equal(Optional<string?>.Absent, uninitialized);
    }

    [Fact]
    public void An_absent_value_cannot_be_read()
    {
        // Throwing rather than answering `default` is the same choice ICurrentUser makes: a default
        // that reads as a real value is how an untouched field is written back as an empty string.
        var absent = Optional<string?>.Absent;

        Assert.Throws<InvalidOperationException>(() => absent.Value);
    }

    [Fact]
    public void A_present_value_is_what_was_put_in_it()
    {
        var present = Optional<string?>.Present("New-Passw0rd");

        Assert.True(present.HasValue);
        Assert.Equal("New-Passw0rd", present.Value);
    }

    [Fact]
    public void A_present_null_is_present()
    {
        // The case the whole type exists for. `{"phoneNumber": null}` is a caller who sent the
        // field, and a wrapper that folded it back into absent would make the two indistinguishable
        // again - which is the convention this type replaced.
        var cleared = Optional<string?>.Present(null);

        Assert.True(cleared.HasValue);
        Assert.Null(cleared.Value);
        Assert.NotEqual(Optional<string?>.Absent, cleared);
    }

    [Fact]
    public void Or_answers_the_stored_value_only_when_the_field_was_not_sent()
    {
        // The merge, in one line (AD-23). Absent takes what is stored; present takes what was sent,
        // including a null that means "clear this".
        Assert.Equal("Петренко", Optional<string?>.Absent.Or("Петренко"));
        Assert.Equal("Коваль", Optional<string?>.Present("Коваль").Or("Петренко"));
        Assert.Null(Optional<string?>.Present(null).Or("Петренко"));
    }

    [Fact]
    public void Two_optionals_are_equal_when_they_say_the_same_thing()
    {
        Assert.Equal(Optional<string?>.Absent, Optional<string?>.Absent);
        Assert.Equal(Optional<string?>.Present("x"), Optional<string?>.Present("x"));
        Assert.Equal(Optional<string?>.Present(null), Optional<string?>.Present(null));

        Assert.NotEqual(Optional<string?>.Present("x"), Optional<string?>.Present("y"));
        Assert.NotEqual(Optional<string?>.Present("x"), Optional<string?>.Absent);
    }

    [Fact]
    public void Equality_is_by_value_through_every_route()
    {
        // The operators, Equals(object) and the hash code all have to agree, or an Optional in a
        // dictionary key or a record's generated equality answers differently from `==`.
        var left = Optional<int>.Present(7);
        var right = Optional<int>.Present(7);

        Assert.True(left == right);
        Assert.False(left != right);
        Assert.True(left.Equals((object)right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());

        Assert.False(left.Equals("not an optional"));
    }

    [Fact]
    public void A_record_carrying_optionals_compares_by_them()
    {
        // The commands are records, so their generated equality is only as good as this type's.
        var absentPassword = new Probe(Optional<string?>.Present("Олена"), Optional<string?>.Absent);
        var samePayload = new Probe(Optional<string?>.Present("Олена"), Optional<string?>.Absent);
        var clearedPassword = new Probe(
            Optional<string?>.Present("Олена"),
            Optional<string?>.Present(null));

        Assert.Equal(absentPassword, samePayload);
        Assert.NotEqual(absentPassword, clearedPassword);
    }

    /// <summary>A stand-in for the update commands, shaped the same way.</summary>
    private sealed record Probe(Optional<string?> FirstName, Optional<string?> Password);
}
