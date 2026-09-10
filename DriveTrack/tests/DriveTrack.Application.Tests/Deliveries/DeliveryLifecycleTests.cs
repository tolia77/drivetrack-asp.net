using DriveTrack.Domain.Common;
using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Application.Tests.Deliveries;

/// <summary>
/// FR-30 to FR-33 where they live: on the entity.
/// <para>
/// AD-10 puts the transition table in exactly one place, so this is the only suite that can assert
/// it directly — and it needs no Docker, no host and no clock, which is the point. The endpoint
/// suite asserts that a refusal reaches the wire as a 409 naming both statuses; this asserts that
/// it is a refusal at all, and that the legal moves are the ones the requirements list.
/// </para>
/// </summary>
public class DeliveryLifecycleTests
{
    [Fact]
    public void A_new_delivery_is_pending()
    {
        // FR-30, and structural rather than a rule anybody applies: the initializer is the only
        // thing that can set an opening status, and nothing outside the entity can set any status.
        Assert.Equal(DeliveryStatus.Pending, New().Status);
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending, new[] { DeliveryStatus.InTransit })]
    [InlineData(DeliveryStatus.InTransit, new[] { DeliveryStatus.Delivered, DeliveryStatus.Failed })]
    [InlineData(DeliveryStatus.Failed, new[] { DeliveryStatus.InTransit })]
    [InlineData(DeliveryStatus.Delivered, new DeliveryStatus[0])]
    public void The_lifecycle_leads_where_the_requirements_say_it_does(
        DeliveryStatus from,
        DeliveryStatus[] expected)
    {
        // The whole table in one place: Pending to In-Transit, In-Transit to Delivered or Failed,
        // Failed back to In-Transit as a retry (FR-31), and Delivered terminal. Asserted as
        // equality rather than as containment, so a transition added without a decision fails here
        // rather than widening the lifecycle unnoticed.
        Assert.Equal(expected, Delivery.NextStatuses(from));
    }

    [Fact]
    public void Every_status_has_a_declared_answer()
    {
        // Totality, walked rather than listed, so a fifth status added to the enum is covered here
        // without anyone remembering to add a row. NextStatuses throws for an undeclared status,
        // which is what turns "we forgot" into a failing test rather than a silent dead end.
        var statuses = Enum.GetValues<DeliveryStatus>();

        Assert.NotEmpty(statuses);

        foreach (var status in statuses)
        {
            Assert.NotNull(Delivery.NextStatuses(status));
        }
    }

    [Fact]
    public void A_status_outside_the_enum_is_refused_rather_than_read_as_a_dead_end()
    {
        // The arm the walk above cannot reach. Without it, a `_ => []` default would pass every
        // other assertion here and turn a typo into a delivery nothing can move.
        Assert.Throws<ArgumentOutOfRangeException>(() => Delivery.NextStatuses((DeliveryStatus)42));
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending, DeliveryStatus.InTransit)]
    [InlineData(DeliveryStatus.InTransit, DeliveryStatus.Delivered)]
    [InlineData(DeliveryStatus.InTransit, DeliveryStatus.Failed)]
    [InlineData(DeliveryStatus.Failed, DeliveryStatus.InTransit)]
    public void A_legal_move_is_made_and_reported_as_made(DeliveryStatus from, DeliveryStatus to)
    {
        var delivery = At(from);

        Assert.True(delivery.TryChangeStatus(to));
        Assert.Equal(to, delivery.Status);
    }

    [Theory]
    // FR-32: nothing jumps the lifecycle.
    [InlineData(DeliveryStatus.Pending, DeliveryStatus.Delivered)]
    [InlineData(DeliveryStatus.Pending, DeliveryStatus.Failed)]
    [InlineData(DeliveryStatus.Failed, DeliveryStatus.Delivered)]
    [InlineData(DeliveryStatus.InTransit, DeliveryStatus.Pending)]
    [InlineData(DeliveryStatus.Delivered, DeliveryStatus.Pending)]
    [InlineData(DeliveryStatus.Delivered, DeliveryStatus.InTransit)]
    [InlineData(DeliveryStatus.Delivered, DeliveryStatus.Failed)]
    [InlineData(DeliveryStatus.Failed, DeliveryStatus.Pending)]
    // Re-sending the status a delivery already holds. Refused rather than treated as a no-op,
    // because the caller of this is about to write a timeline entry, and an entry saying a parcel
    // moved from In-Transit to In-Transit records something that did not happen.
    [InlineData(DeliveryStatus.Pending, DeliveryStatus.Pending)]
    [InlineData(DeliveryStatus.InTransit, DeliveryStatus.InTransit)]
    [InlineData(DeliveryStatus.Delivered, DeliveryStatus.Delivered)]
    [InlineData(DeliveryStatus.Failed, DeliveryStatus.Failed)]
    public void An_illegal_move_is_refused_and_changes_nothing(DeliveryStatus from, DeliveryStatus to)
    {
        var delivery = At(from);

        Assert.False(delivery.TryChangeStatus(to));

        // The second half, and the half a bare `Assert.False` would miss: a mutator that reported
        // failure after assigning would leave the row in the status it just refused to move to.
        Assert.Equal(from, delivery.Status);
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending)]
    [InlineData(DeliveryStatus.InTransit)]
    [InlineData(DeliveryStatus.Delivered)]
    [InlineData(DeliveryStatus.Failed)]
    public void Delivered_is_terminal_whatever_is_asked_of_it(DeliveryStatus requested)
    {
        // Stated as its own claim rather than left to the matrix above: "Delivered leads nowhere"
        // is the requirement, and a table that happened to list every pair is not the same
        // assertion as one that walks the enum.
        var delivered = At(DeliveryStatus.Delivered);

        Assert.False(delivered.TryChangeStatus(requested));
        Assert.Equal(DeliveryStatus.Delivered, delivered.Status);
    }

    [Fact]
    public void A_failed_delivery_goes_back_on_the_road_and_can_then_be_delivered()
    {
        // FR-31's retry, walked end to end: the failure is not terminal, and the retry rejoins the
        // ordinary lifecycle rather than becoming a branch of its own.
        var delivery = At(DeliveryStatus.Failed);

        Assert.True(delivery.TryChangeStatus(DeliveryStatus.InTransit));
        Assert.True(delivery.TryChangeStatus(DeliveryStatus.Delivered));
        Assert.Equal(DeliveryStatus.Delivered, delivery.Status);
    }

    /// <summary>A new delivery, in the only status a new one can hold.</summary>
    private static Delivery New() =>
        new()
        {
            PickupLocation = new Location(50.4501, 30.5234, null, null),
            DropoffLocation = new Location(49.8397, 24.0297, null, null),
            PackageDetails = "Одна палета",
            PackageWeightKg = 12.5m,
            CreatedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero),
        };

    /// <summary>
    /// A delivery standing in <paramref name="status"/>, reached the only way one can be: by
    /// walking the lifecycle. There is no back door here for the same reason there is none in
    /// production — a fixture that could fabricate an unreachable state would test one.
    /// </summary>
    private static Delivery At(DeliveryStatus status)
    {
        var delivery = New();

        if (status != DeliveryStatus.Pending)
        {
            Assert.True(delivery.TryChangeStatus(DeliveryStatus.InTransit));
        }

        if (status is DeliveryStatus.Delivered or DeliveryStatus.Failed)
        {
            Assert.True(delivery.TryChangeStatus(status));
        }

        Assert.Equal(status, delivery.Status);

        return delivery;
    }
}
