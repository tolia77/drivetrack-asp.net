using DriveTrack.Application.Common;

namespace DriveTrack.Application.Tests.Common;

/// <summary>
/// AD-23's wrapper, and the one distinction the whole of FR-38 rests on.
/// <para>
/// The original system read a plain <c>vehicle_id</c> off an update payload, so a field the caller
/// omitted and a field the caller sent as <c>null</c> were the same request. An assignment could
/// therefore be set and never cleared, which made an assigned vehicle permanently undeletable.
/// These three claims are what stop that being expressible again.
/// </para>
/// </summary>
public class OptionalTests
{
    [Fact]
    public void The_default_is_absent()
    {
        // Load-bearing rather than tidy: System.Text.Json only invokes a converter for a property
        // that is present, so a field the caller omitted keeps the default. If the default were
        // present-and-null, every omitted field would clear its column.
        Assert.False(default(Optional<int?>).HasValue);
        Assert.False(Optional<int?>.Absent.HasValue);
        Assert.Equal(Optional<int?>.Absent, default(Optional<int?>));
    }

    [Fact]
    public void An_explicit_null_is_present_and_null()
    {
        // FR-38's arm. "The caller said null" and "the caller said nothing" are different requests,
        // and this is the line where they stop being the same value.
        var cleared = Optional<int?>.Of(null);

        Assert.True(cleared.HasValue);
        Assert.Null(cleared.Value);
        Assert.NotEqual(Optional<int?>.Absent, cleared);
    }

    [Fact]
    public void A_value_is_present_and_carries_it()
    {
        var assigned = Optional<int?>.Of(7);

        Assert.True(assigned.HasValue);
        Assert.Equal(7, assigned.Value);
    }

    [Fact]
    public void Or_falls_back_to_the_current_value_only_when_absent()
    {
        // The merge AD-23 requires: an absent field keeps what the row holds, a present one
        // replaces it - including with null.
        Assert.Equal(3, Optional<int?>.Absent.Or(3));
        Assert.Equal(9, Optional<int?>.Of(9).Or(3));
        Assert.Null(Optional<int?>.Of(null).Or(3));
    }

    [Fact]
    public void Or_carries_a_reference_type_the_same_way()
    {
        // Optional<string> is the shape an update's text field takes, and a present-null there is a
        // value the validator refuses rather than a clear - which only works if Or reports it.
        Assert.Equal("stored", Optional<string>.Absent.Or("stored"));
        Assert.Equal("sent", Optional<string>.Of("sent").Or("stored"));
        Assert.Null(Optional<string>.Of(null).Or("stored"));
    }

    [Fact]
    public void Two_optionals_carrying_the_same_answer_are_equal()
    {
        // A record struct, so equality is by value. Commands are records too, and a command
        // compared field by field is only meaningful if its fields compare this way.
        Assert.Equal(Optional<string>.Of("same"), Optional<string>.Of("same"));
        Assert.NotEqual(Optional<string>.Of("same"), Optional<string>.Of("other"));
    }
}
