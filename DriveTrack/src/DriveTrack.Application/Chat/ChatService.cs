using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Common;
using DriveTrack.Application.Drivers;
using DriveTrack.Domain.Chat;
using DriveTrack.Domain.Drivers;
using DriveTrack.Domain.Identity;
using FluentValidation;

namespace DriveTrack.Application.Chat;

/// <summary>
/// AD-3's pipeline over a driver's conversation: open the scope → load → guard → not found →
/// validate → act → commit → map.
/// <para>
/// The guard call is written out in each of the three public bodies rather than factored into a
/// helper. That is not repetition for its own sake: <c>GuardCoverageTests</c> reads the compiled IL
/// of each method's own async state machine, so a decision taken one call deeper is a decision the
/// build cannot see.
/// </para>
/// <para>
/// <b>What chat reads that it does not own (AD-16, AD-24).</b> The roster goes through the owning
/// capability, <see cref="IDriverService.ListAsync"/>, exactly as AD-24 asks. Two reads cannot, and
/// both are identity lookups: whether a conversation exists for a driver opening <em>their own</em>
/// (<c>IDriverService.GetAsync</c> is dispatcher-guarded and would refuse them), and a display name
/// per sender (no service exposes another user's name). Both follow
/// <c>ProofOfDeliveryService</c>'s existing precedent of reading <c>unitOfWork.Users</c> for a name.
/// Chat writes neither entity.
/// </para>
/// <para>
/// <c>ValidatorExtensions.ValidateAndThrowAsync</c> is called in static form, for the reason
/// <c>DeliveryService</c> states: a file in this namespace that imported only
/// <c>FluentValidation</c> would bind to that library's identically named extension, whose exception
/// carries no <see cref="ErrorCode"/> and leaves as a 500 where 422 was meant.
/// </para>
/// </summary>
public sealed class ChatService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IAccessGuard accessGuard,
    ICurrentUser currentUser,
    IDriverService drivers,
    IValidator<SendMessageCommand> messageValidator,
    TimeProvider timeProvider) : IChatService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatThreadSummary>> ListThreadsAsync(CancellationToken cancellationToken)
    {
        // Nothing to load before the decision: the operation is about the roster rather than about
        // one conversation, which is exactly what the null argument says. Asked here rather than
        // left to the driver capability's own RequireRole(Dispatcher), because that one reads as
        // "a dispatcher or an admin" (AD-4) and chat is where that is deliberately wrong.
        accessGuard.RequireChatParticipant(null);

        // AD-24: another capability's rows are read through its service, never through its
        // repository. The driver capability decides what a driver summary is and what a dispatcher
        // may see of one; chat only keys a conversation on it.
        var roster = await drivers.ListAsync(cancellationToken);

        return
        [
            .. roster
                // Everything the row carries comes out of the summary the driver capability already
                // returned. The vehicle, its plate and the duty flag were being read and dropped on
                // the floor; the roster shows them, which costs no query and no round trip and is
                // what lets a dispatcher tell two drivers of the same name apart.
                .Select(driver => new ChatThreadSummary(
                    driver.Id,
                    DisplayName(driver.FirstName, driver.LastName),
                    driver.VehicleModel,
                    driver.VehicleLicensePlate,
                    driver.OnDuty))
                // FR-68 names the roster by person, so it is ordered by person. The driver
                // capability orders its own list by row id, which is the order they were taken on -
                // useful for a fleet roster and meaningless in a list somebody scans for a name.
                // Ordinal rather than a culture comparison because this order has to be the same on
                // every machine that renders it, and Cyrillic sorts alphabetically by code point.
                //
                // The row id breaks a tie, for the reason the thread read's does: two drivers can
                // share a display name, and without it their order is whatever the database
                // happened to return - so the roster would reshuffle between calls.
                .OrderBy(summary => summary.DriverName, StringComparer.Ordinal)
                .ThenBy(summary => summary.DriverId.Value),
        ];
    }

    /// <inheritdoc />
    public async Task<ChatThread> GetThreadAsync(DriverId driverId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var driver = await unitOfWork.Drivers.GetByIdAsync(driverId, cancellationToken);

        // AD-3: the row is read before the decision, and nothing about it is disclosed unless the
        // guard passes - not even whether it exists, which is why the 404 below comes second.
        accessGuard.RequireChatParticipant(driverId);

        RequireThread(driver, driverId);

        var messages = await unitOfWork.Messages.ListForDriverAsync(driverId, cancellationToken);

        var senders = await SenderNamesAsync(unitOfWork, messages, cancellationToken);

        return new ChatThread(
            driverId,
            await DriverNameAsync(unitOfWork, driver!, cancellationToken),
            [.. messages.Select(message => Map(message, senders))]);
    }

    /// <inheritdoc />
    public async Task<ChatMessage> SendAsync(SendMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var driver = await unitOfWork.Drivers.GetByIdAsync(command.DriverId, cancellationToken);

        accessGuard.RequireChatParticipant(command.DriverId);

        RequireThread(driver, command.DriverId);

        // Trimmed as it is validated, not afterwards: the validator has to judge the string that
        // will actually be stored, or three spaces pass as content and land as nothing.
        var trimmed = new SendMessageCommand(command.DriverId, command.Text?.Trim());

        await ValidatorExtensions.ValidateAndThrowAsync(messageValidator, trimmed, cancellationToken);

        // The caller is the sender. Never a parameter: a sender the caller could name is a sender
        // the caller could forge, and the guard above has already established who they are.
        var senderUserId = currentUser.UserId;

        var message = new Message
        {
            DriverId = command.DriverId,
            SenderUserId = senderUserId,
            Text = trimmed.Text!,

            // AD-13: the injected clock, never the ambient one. The whole conversation's order
            // depends on this value, so it has to be a value a test can fix.
            SentAt = timeProvider.GetUtcNow(),
        };

        unitOfWork.Messages.Add(message);

        // Read before the commit, not after. Nothing about the sender's name depends on the write,
        // and a read that failed once the row was already stored would leave this method throwing
        // at a caller whose message is in the table - so the sender is told it failed and nobody is
        // ever broadcast the line that exists.
        var sender = await unitOfWork.Users.GetByIdAsync(senderUserId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        // Mapped from the entity after the commit, so the id and the instant on the returned line
        // are the ones the row actually holds - which is what makes broadcasting it instead of the
        // typed text an honest echo rather than an optimistic one (FR-73).
        return new ChatMessage(
            message.Id,
            senderUserId.Value,
            sender is null ? string.Empty : DisplayName(sender.FirstName, sender.LastName),
            message.SentAt,
            message.Text);
    }

    /// <summary>
    /// Refuses a conversation whose driver row is absent. A conversation is keyed on the driver, so
    /// a driver that does not exist is a conversation that does not exist — not an empty one.
    /// </summary>
    private static void RequireThread(Driver? driver, DriverId driverId)
    {
        if (driver is null)
        {
            throw new NotFoundException(
                ErrorCode.CHAT_THREAD_NOT_FOUND,
                "No driver exists with id "
                    + driverId.Value.ToString(CultureInfo.InvariantCulture)
                    + ", so there is no conversation keyed on it.");
        }
    }

    /// <summary>
    /// The display name of every account that wrote a line in this conversation.
    /// <para>
    /// Resolved once per distinct sender rather than once per line: a conversation of two hundred
    /// messages between two people is two lookups, not two hundred.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<UserId, string>> SenderNamesAsync(
        IUnitOfWork unitOfWork,
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<UserId, string>();

        foreach (var senderUserId in messages
                     .Select(message => message.SenderUserId)
                     .OfType<UserId>()
                     .Distinct())
        {
            var account = await unitOfWork.Users.GetByIdAsync(senderUserId, cancellationToken);

            names[senderUserId] = account is null
                ? string.Empty
                : DisplayName(account.FirstName, account.LastName);
        }

        return names;
    }

    /// <summary>
    /// The driver's display name for the conversation header. Chat reads driver and user identity
    /// and owns neither (AD-16), so the name comes from the account rather than from a copy of it.
    /// </summary>
    private static async Task<string> DriverNameAsync(
        IUnitOfWork unitOfWork,
        Driver driver,
        CancellationToken cancellationToken)
    {
        var account = await unitOfWork.Users.GetByIdAsync(driver.UserId, cancellationToken);

        return account is null ? string.Empty : DisplayName(account.FirstName, account.LastName);
    }

    private static string DisplayName(string firstName, string lastName) =>
        (firstName + " " + lastName).Trim();

    private static ChatMessage Map(Message message, IReadOnlyDictionary<UserId, string> senders) =>
        new(
            message.Id,
            message.SenderUserId?.Value,

            // Empty for a deleted account, and empty is what the screen renders the unattributed
            // label for: the label is a sentence and sentences live in the resource catalogue.
            message.SenderUserId is { } sender && senders.TryGetValue(sender, out var name)
                ? name
                : string.Empty,
            message.SentAt,
            message.Text);
}
