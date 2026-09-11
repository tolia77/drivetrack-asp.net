using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Common;
using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// The work <see cref="IDeliverySideEffects"/> queued, performed on the worker's own thread and on
/// the token the worker hands it (AD-12, FR-28, FR-94, FR-95).
/// <para>
/// AD-5 gives an operation one scope and one commit, and the scope that wrote the delivery was
/// spent and disposed before this ran — so every job opens its own. The address job opens
/// <em>two</em>: one to read the coordinates and one to write the result, with the network call
/// between them and no transaction held across it. A pooled connection sitting idle-in-transaction
/// for the length of a geocoder timeout is a connection nothing else can have, and the cost is paid
/// per delivery.
/// </para>
/// <para>
/// The two failures this is actually about are contained where they happen: a geocoder that would
/// not answer leaves the address absent and the screen falls back to the coordinates (DR-11), and a
/// send that failed leaves a <see cref="NotificationAttempt"/> row saying so, which is FR-28's whole
/// remediation. Anything else — a scope that would not open, a commit that failed — is a storage
/// failure and is allowed out, so the worker logs it. Swallowing it here would leave no row and no
/// log line anywhere, which is the silent failure FR-28 exists to end, in a narrower form.
/// </para>
/// </summary>
public sealed class DeliverySideEffectRunner(
    IUnitOfWorkFactory unitOfWorkFactory,
    IGeocoder geocoder,
    IEmailSender emailSender,
    TimeProvider timeProvider) : IDeliverySideEffectRunner
{
    /// <summary>How much of a failure the <c>error</c> column holds.</summary>
    /// <remarks>
    /// The column is 1000 characters and a mail library's failure message can be longer. Truncating
    /// rather than letting the insert fail is the difference between a record that is slightly
    /// clipped and no record at all — and no record at all is exactly the state FR-28 exists to end.
    /// </remarks>
    internal const int ErrorMaximumLength = 1000;

    /// <summary>How much of a resolved address the <c>address</c> columns hold.</summary>
    /// <remarks>
    /// For the reason <see cref="ErrorMaximumLength"/> exists, on the other column this job writes.
    /// A geocoder answers with whatever it answers with — Nominatim's <c>display_name</c> is a whole
    /// administrative hierarchy joined with commas — and a reply past the column's width would fail
    /// the insert and leave the delivery with no address at all, which is the worse of the two
    /// outcomes: a clipped address still tells a dispatcher which street this is.
    /// </remarks>
    internal const int AddressMaximumLength = 500;

    /// <inheritdoc />
    public Task RunAsync(DeliverySideEffectJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Total over the enum, like Delivery.NextStatuses and NavDestinations.For. Two independent
        // `if` statements would let a third kind queue, drain and mark itself complete having done
        // nothing at all, with no exception and no log to say so - which is the worst shape a
        // background job can fail in.
        return job.Kind switch
        {
            DeliverySideEffectKind.ResolveAddresses =>
                ResolveAddressesAsync(job.DeliveryId, cancellationToken),
            DeliverySideEffectKind.StatusChangeEmail =>
                NotifyStatusChangedAsync(job, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(job),
                job.Kind,
                "No side effect is defined for this kind."),
        };
    }

    /// <summary>
    /// FR-94 and FR-95: fills in whichever of the two points has no address, and leaves the other
    /// exactly as it was.
    /// </summary>
    /// <remarks>
    /// Read, then geocode, then write, in three separate steps. The read scope is disposed before
    /// the first network call, so no transaction is held open across it; the write scope reloads the
    /// row, because the seconds in between are seconds a dispatcher can spend moving a point, and
    /// writing back the entity that was loaded before the lookups would put the old point's address
    /// beside the new coordinates — the one thing DR-11 forbids.
    /// </remarks>
    private async Task ResolveAddressesAsync(int deliveryId, CancellationToken cancellationToken)
    {
        Location pickup;
        Location dropoff;

        await using (var read = await unitOfWorkFactory.CreateAsync(cancellationToken))
        {
            var delivery = await read.Deliveries.GetByIdAsync(deliveryId, cancellationToken);

            if (delivery is null)
            {
                // Deleted between the commit and this run. Not an error: the job is about a row that
                // no longer exists, and there is nothing to describe.
                return;
            }

            pickup = delivery.PickupLocation;
            dropoff = delivery.DropoffLocation;
        }

        // Separately, and each contained on its own: a dropoff lookup that throws must not discard a
        // pickup address that already resolved.
        var pickupAddress = await DescribeAsync(pickup, cancellationToken);
        var dropoffAddress = await DescribeAsync(dropoff, cancellationToken);

        if (pickupAddress is null && dropoffAddress is null)
        {
            // Nothing to write, so no scope is opened at all. Committing anyway would be a write for
            // every delivery the geocoder could not place.
            return;
        }

        await using var write = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var current = await write.Deliveries.GetByIdAsync(deliveryId, cancellationToken);

        if (current is null)
        {
            return;
        }

        var changed = false;

        if (Filled(current.PickupLocation, pickup, pickupAddress) is { } filledPickup)
        {
            current.PickupLocation = filledPickup;
            changed = true;
        }

        if (Filled(current.DropoffLocation, dropoff, dropoffAddress) is { } filledDropoff)
        {
            current.DropoffLocation = filledDropoff;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        await write.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The address for a point that has none, or null when there is nothing to fill in — because it
    /// already had one, because the geocoder had nothing to offer, or because it could not answer.
    /// </summary>
    /// <remarks>
    /// The catch is per point rather than around both, which is the whole reason this returns a
    /// string instead of throwing: the two lookups are independent questions, and one provider
    /// failure must not throw away the other's answer. Every exception is caught because the
    /// distinction between a socket failure, a refusal and a timeout is not one this method can act
    /// on — DR-11 has already decided that the answer to all of them is an absent address.
    /// </remarks>
    private async Task<string?> DescribeAsync(Location location, CancellationToken cancellationToken)
    {
        if (location.Address is not null)
        {
            return null;
        }

        try
        {
            var address = await geocoder.DescribeAsync(
                location.Latitude,
                location.Longitude,
                cancellationToken);

            return string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The stored point with the resolved address written into it, or null when it must be left
    /// alone.
    /// </summary>
    /// <remarks>
    /// Both conditions are about the seconds between the lookup and this write. The coordinates must
    /// still be the coordinates that were looked up, or the address describes where the point used
    /// to be (DR-11); and the address must still be absent, or something already answered this
    /// question and this job has nothing to add.
    /// </remarks>
    private Location? Filled(Location stored, Location geocoded, string? address)
    {
        if (address is null || stored.Address is not null)
        {
            return null;
        }

        if (stored.Latitude != geocoded.Latitude || stored.Longitude != geocoded.Longitude)
        {
            return null;
        }

        // DR-11: the coordinates are untouched and remain authoritative. The record is immutable, so
        // the cache is refilled with `with` rather than by assigning through it. AD-13's clock, not
        // the machine's, records when.
        return stored with
        {
            Address = Truncate(address, AddressMaximumLength),
            AddressResolvedAt = timeProvider.GetUtcNow(),
        };
    }

    /// <summary>
    /// FR-28: the client is told their delivery moved, and the attempt is recorded whether it
    /// worked or not.
    /// </summary>
    /// <remarks>
    /// <see cref="NotificationOutcome.Sent"/> is written as well as
    /// <see cref="NotificationOutcome.Failed"/>. The entity is called <em>Attempt</em> and the enum
    /// declares both, and recording only failures would make "no row" mean either "nothing was
    /// owed" or "it worked" — which is the ambiguity an administrator opens this screen to resolve.
    /// </remarks>
    private async Task NotifyStatusChangedAsync(
        DeliverySideEffectJob job,
        CancellationToken cancellationToken)
    {
        if (job is not { Previous: { } previous, Next: { } next })
        {
            return;
        }

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var delivery = await unitOfWork.Deliveries.GetByIdAsync(job.DeliveryId, cancellationToken);

        // FR-28's "no client" case, which is not an error: a delivery may legally have none
        // (FR-16), and there is nobody to write to and nobody the row would be about.
        if (delivery?.ClientId is not { } clientId)
        {
            return;
        }

        var account = await unitOfWork.Users.FindByClientIdAsync(clientId, cancellationToken);

        if (account is null)
        {
            return;
        }

        var attempt = new NotificationAttempt
        {
            DeliveryId = delivery.Id,
            Kind = NotificationKind.StatusChange,
            Recipient = account.Email,

            // AD-13, and it is what makes the log readable: the order the screen sorts by is this
            // column, so a fixed clock in a test produces a deterministic order rather than one that
            // depends on how fast the worker got round to the job.
            AttemptedAt = timeProvider.GetUtcNow(),
            Outcome = NotificationOutcome.Sent,
        };

        try
        {
            await emailSender.SendAsync(
                StatusChangeEmail.For(account.Email, delivery.Id, previous, next),
                cancellationToken);
        }
        catch (Exception exception)
        {
            // Every exception, deliberately. A transport raises whatever its own library raises -
            // a socket failure, a protocol refusal, a timeout - and the distinction between them is
            // not one this method can act on. What FR-28 asks for is that the attempt is recorded
            // and that the status change it followed is left alone, and both are true of all of them.
            attempt.Outcome = NotificationOutcome.Failed;
            attempt.Error = Truncate(exception.Message, ErrorMaximumLength);
        }

        unitOfWork.NotificationAttempts.Add(attempt);

        // Not the token the send was cancelled by. Shutdown and the per-job timeout both cancel that
        // one, and an attempt abandoned halfway is exactly the attempt an administrator most needs
        // to see - passing it on here would catch the send's failure and then decline to record it.
        await unitOfWork.CommitAsync(CancellationToken.None);
    }

    /// <summary>A value, cut to the width of the column that holds it.</summary>
    /// <param name="value">What is to be stored.</param>
    /// <param name="maximumLength">The column's width in characters.</param>
    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
