using System.Globalization;
using DriveTrack.Application.Chat;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Web.Account;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DriveTrack.Web.Hubs;

/// <summary>
/// FR-70 and FR-75: the live half of chat. A transport, and deliberately nothing more.
/// <para>
/// The hub takes no authorization decision of its own. It asks <see cref="IChatService"/> — which
/// asks <c>IAccessGuard</c> — and adds the connection to a group only once that call has returned.
/// The order is the whole point: a group join that happened first would leave a refused caller
/// receiving every broadcast in a conversation they were told they could not read, and the refusal
/// would look correct in every log (FR-75, AD-15).
/// </para>
/// <para>
/// A group is named from the <c>DriverId</c> (AD-15, AD-22), never from a user id, so the group a
/// connection listens on and the row a message is stored against are the same key.
/// </para>
/// <para>
/// The two schemes are named because two very different callers arrive here. A browser holds
/// <c>drivetrack.session</c> and the hub sits outside <c>/api</c>, so the path selector would send
/// it to the cookie handler anyway; the integration suite drives the same hub with a bearer token,
/// which the path selector would never offer to the JWT handler. Naming both is what lets one hub
/// serve both without a second endpoint. The order matters as well: an unauthenticated negotiate is
/// challenged on each scheme in turn, and the bearer handler's envelope writer runs last and
/// overwrites the cookie handler's redirect, so a programmatic caller is answered <c>401</c> rather
/// than a sign-in page it cannot read.
/// </para>
/// </summary>
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie + "," + JwtBearerDefaults.AuthenticationScheme)]
public sealed class ChatHub(IChatService chat, HubCaller caller) : Hub
{
    /// <summary>The client method a broadcast message arrives on.</summary>
    public const string ReceiveMessageMethod = "ReceiveMessage";

    /// <summary>
    /// The group a driver's conversation is broadcast on. Keyed on the driver row, so it cannot be
    /// confused with a user id even by accident (AD-22).
    /// </summary>
    /// <param name="driverId">The driver row the conversation belongs to.</param>
    public static string GroupFor(DriverId driverId) =>
        "driver-" + driverId.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Every driver's conversation, for the dispatch desk's roster (FR-68).</summary>
    public async Task<IReadOnlyList<ChatThreadSummary>> ListThreads()
    {
        Adopt();

        try
        {
            return await chat.ListThreadsAsync(Context.ConnectionAborted);
        }
        catch (DriveTrackException failure)
        {
            throw Translate(failure);
        }
    }

    /// <summary>
    /// Joins the conversation of <paramref name="driverId"/> and answers with its history
    /// (FR-71, FR-73).
    /// </summary>
    /// <param name="driverId">The driver row the conversation is keyed on.</param>
    public async Task<ChatThread> JoinThread(int driverId)
    {
        Adopt();

        var thread = new DriverId(driverId);

        ChatThread history;

        try
        {
            // First, and this is the only ordering that is correct: the service refuses a caller who
            // is not a participant, and a connection that had already been added to the group would
            // keep receiving the conversation regardless of what it was told.
            history = await chat.GetThreadAsync(thread, Context.ConnectionAborted);
        }
        catch (DriveTrackException failure)
        {
            throw Translate(failure);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(thread), Context.ConnectionAborted);

        return history;
    }

    /// <summary>
    /// Leaves the conversation of <paramref name="driverId"/> — what a dispatcher does when they
    /// pick a different driver.
    /// </summary>
    /// <param name="driverId">The driver row the conversation is keyed on.</param>
    /// <remarks>
    /// No service call, because there is no decision to take: giving up a subscription discloses
    /// nothing, and a caller who was never in the group leaves one they were not in.
    /// </remarks>
    public Task LeaveThread(int driverId)
    {
        Adopt();

        return Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            GroupFor(new DriverId(driverId)),
            Context.ConnectionAborted);
    }

    /// <summary>
    /// Writes a message into a conversation and broadcasts the stored row to everyone joined to it
    /// (FR-70, FR-73).
    /// </summary>
    /// <param name="driverId">The driver row the conversation is keyed on.</param>
    /// <param name="text">
    /// The message body, or null. Null is a designed input rather than a defensive allowance: the
    /// screen sends its draft, which is null until the box has been typed in, and
    /// <see cref="SendMessageCommand.Text"/> is nullable to receive it. A non-nullable parameter
    /// would have the hub's own binder answer for an empty box, in a shape that is not AD-8's.
    /// </param>
    public async Task Send(int driverId, string? text)
    {
        Adopt();

        var thread = new DriverId(driverId);

        ChatMessage stored;

        try
        {
            stored = await chat.SendAsync(
                new SendMessageCommand(thread, text),
                Context.ConnectionAborted);
        }
        catch (DriveTrackException failure)
        {
            throw Translate(failure);
        }

        // The row that was written, not the text that was typed: the sender sees their own message
        // when the server broadcasts it back, so what appears on screen is what is in the database
        // and a refused or lost write leaves nothing behind pretending to have been sent.
        //
        // No cancellation token, and deliberately: the group broadcast is not the caller's work.
        // Riding Context.ConnectionAborted would mean a sender whose connection drops between the
        // commit and the broadcast cancels delivery to everyone else - the row is stored and nobody
        // is told, which is the one failure this capability cannot have.
        await Clients.Group(GroupFor(thread)).SendAsync(ReceiveMessageMethod, driverId, stored);
    }

    /// <summary>
    /// Hands this invocation's principal to the scope's <c>ICurrentUser</c> adapter.
    /// <para>
    /// The first statement of every method, because everything below it asks who the caller is and
    /// a hub scope has no other way to answer. Cheap, and safe to repeat: SignalR gives each
    /// invocation its own scope, so this writes to a holder nobody else can read.
    /// </para>
    /// </summary>
    private void Adopt() => caller.Principal = Context.User;

    /// <summary>
    /// Turns one of AD-8's typed failures into the only shape SignalR can carry back to a caller.
    /// <para>
    /// The message is a resource key from <c>ErrorMessages.resx</c>, chosen by the same rule the
    /// Blazor forms use: the offending field's key for a validation failure, the contract code
    /// otherwise. So the screen renders the localized sentence for what actually went wrong rather
    /// than a generic one, and nothing but a key crosses the wire — a <c>HubException</c>'s message
    /// reaches the browser verbatim, and an exception message is for logs (NFR-3).
    /// </para>
    /// <para>
    /// Only the message key: a <c>HubException</c> carries one string, so the field name a screen
    /// banner would show has nowhere to go here. That is the contract, not an omission — the one
    /// command this hub takes has a single field.
    /// </para>
    /// </summary>
    private static HubException Translate(DriveTrackException failure) =>
        new(FailureKeys.For(failure)[0].MessageKey);
}
