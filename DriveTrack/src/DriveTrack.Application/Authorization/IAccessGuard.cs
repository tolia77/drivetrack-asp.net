using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Authorization;

/// <summary>
/// The single place a business authorization decision is made (AD-2).
/// <para>
/// Every public method on an Application service calls this, except the closed allowlist in
/// <see cref="PublicEntryPoints"/>; <c>GuardCoverageTests</c> fails the build when a method does
/// neither. A controller attribute, a component condition or a repository filter is never the
/// decision — those are conveniences layered on top of one that was already taken here.
/// </para>
/// <para>
/// It throws rather than returning a boolean, so a forgotten <c>if</c> cannot be mistaken for a
/// passed check.
/// </para>
/// <para>
/// One member per question, and no more (NFR-6). <c>RequireSelf</c> answers "is this the caller's
/// own row"; <c>RequireRole</c> answers "is this caller one of these people"; <c>RequireScope</c>
/// answers "which rows of a collection may this caller be shown"; <c>RequireAssignedDriver</c>
/// answers "is this the driver carrying this parcel, or dispatch"; <c>RequireChatParticipant</c>
/// answers "may this caller work in this driver's conversation, or see the list of them";
/// <c>RequireDeliveryComposer</c> answers "may this caller compose a delivery, and for which client
/// row"; <c>RequireReviewAuthor</c> answers "may this caller write reviews, and as which client
/// row"; <c>RequireReviewOwner</c> answers "may this caller change <em>this</em> review";
/// <c>RequireShiftScope</c> answers "whose shifts may this caller be shown"; and
/// <c>RequireShiftOwner</c> answers "may this caller act on <em>this</em> driver's shift". Each
/// arrived with the first capability that had a caller for it — the scope with story 5.1, the
/// assignment with 5.3, the conversation with 8.1, the composer with 7.4, the two review members
/// with 7.2, the two shift members with 4.2 — and none is a rewording of another. None of
/// <c>RequireSelf</c>, <c>RequireRole</c> and <c>RequireScope</c> can express "the assigned driver,
/// or dispatch, but never the client whose delivery it is", and writing that as a role test inside
/// a service would be a second place authorization lived.
/// </para>
/// <para>
/// AD-4's "admin satisfies every check" lives inside the implementation, once, so every member
/// inherits it and none restates it — with exactly two exceptions, recorded here because they are
/// the kind of asymmetry a reader will otherwise take for a bug.
/// <see cref="RequireChatParticipant"/> refuses an administrator. The PRD decides it as product
/// ("admins moderate rather than dispatch"), and a fifth member is what lets the exception be
/// written once, in one body, instead of as a role test scattered through a capability.
/// <see cref="RequireReviewAuthor"/> refuses one too, for the same kind of product reason: the PRD
/// retired FR-97 because an administrator authoring customer feedback is not moderation. Every
/// other member — <see cref="RequireReviewOwner"/> included, which is the moderation half — still
/// grants the override.
/// </para>
/// </summary>
public interface IAccessGuard
{
    /// <summary>
    /// Requires that the caller is <paramref name="userId"/>, or holds <see cref="UserRole.Admin"/>.
    /// </summary>
    /// <param name="userId">The user the operation is about.</param>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401) or is a different, non-admin user
    /// (<c>AUTH_FORBIDDEN</c>, 403).
    /// </exception>
    void RequireSelf(UserId userId);

    /// <summary>
    /// Requires that the caller holds <paramref name="role"/>, or holds
    /// <see cref="UserRole.Admin"/>.
    /// <para>
    /// <c>RequireRole(Dispatcher)</c> therefore reads as "a dispatcher or an admin", which is what
    /// FR-48 asks for and what the original's <c>require_role("dispatcher")</c> meant. The override
    /// is not repeated at the call site, because it is not a property of any one capability.
    /// </para>
    /// </summary>
    /// <param name="role">The role the operation is reserved to.</param>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401) or holds neither that role nor
    /// <see cref="UserRole.Admin"/> (<c>AUTH_FORBIDDEN</c>, 403).
    /// </exception>
    void RequireRole(UserRole role);

    /// <summary>
    /// Answers which rows of a collection this caller may be shown — AD-3's scope predicate, and
    /// the reason a collection repository method takes a scope and has no overload that omits it.
    /// <para>
    /// The guard decides, not the service: "a driver sees only their own deliveries" is an
    /// authorization rule, and a rule written as a <c>Where</c> inside a capability would be a
    /// second place authorization lived. A service takes the answer to the repository unchanged.
    /// </para>
    /// <para>
    /// AD-4 applies here as it does to the other two members: an admin is unrestricted by rule,
    /// decided inside the implementation and nowhere else. A dispatcher is unrestricted because
    /// dispatch is the role the whole collection exists for (FR-18).
    /// </para>
    /// </summary>
    /// <returns>
    /// The narrowing to apply. Both ids null is the unrestricted case: the scope narrows nothing
    /// and the repository adds no <c>WHERE</c>.
    /// </returns>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401), or holds a role whose scope is a
    /// row id the caller's claims do not carry (<c>AUTH_FORBIDDEN</c>, 403) — a driver with no
    /// driver claim cannot be narrowed to their own rows, and answering "unrestricted" there would
    /// disclose the whole collection.
    /// </exception>
    AccessScope RequireScope();

    /// <summary>
    /// Requires that the caller is the driver a delivery is assigned to, or runs dispatch (FR-26,
    /// FR-34).
    /// <para>
    /// The one predicate behind AD-10's single write path. A delivery's status may be advanced by
    /// the driver carrying it and by the people who dispatch it, and by nobody else — a client may
    /// never change the status of their own delivery (FR-90), which is exactly why
    /// <see cref="RequireScope"/> cannot express this: a client's scope admits their own row.
    /// </para>
    /// <para>
    /// An unassigned delivery has no driver, so no driver passes: a null
    /// <paramref name="assignedDriverId"/> never equals a caller's driver row id, and answering
    /// otherwise would let any driver advance a parcel nobody is carrying.
    /// </para>
    /// </summary>
    /// <param name="assignedDriverId">
    /// The driver the delivery is assigned to, or null when it is unassigned — or when there is no
    /// delivery at all, which is why the caller may pass this before answering 404: a driver who may
    /// not act on the row is refused before learning whether it exists.
    /// </param>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401), or is a driver who is not the
    /// assigned one, or holds a role with no part in the lifecycle (<c>AUTH_FORBIDDEN</c>, 403).
    /// </exception>
    void RequireAssignedDriver(DriverId? assignedDriverId);

    /// <summary>
    /// Answers "may this caller compose a delivery, and for which client row" (FR-89, FR-104).
    /// <para>
    /// Two call sites share it because they share one rule. A client composing a request of their
    /// own is the caller the answer names; a dispatcher or an administrator composes for somebody
    /// else, so the honest answer to "which client row" is <c>null</c> rather than a refusal — they
    /// do compose deliveries, and the client is an explicit field of the command they send. A
    /// driver composes nothing at all and is refused, which is what keeps a public geocoder out of
    /// reach of the one role that only ever reads deliveries.
    /// </para>
    /// <para>
    /// None of the members above expresses it. <c>RequireRole(UserRole.Client)</c> reads as "a
    /// client <em>or</em> an admin" by AD-4's design, and an admin carries no client row id — so a
    /// service using it would have to decide what a missing client id means, which is an
    /// authorization decision living outside this interface. <c>RequireScope</c> hands a driver a
    /// scope and therefore cannot refuse one, and its answer is about which rows may be
    /// <em>read</em> rather than about who may open a new one. <c>RequireSelf</c> compares user ids,
    /// and the row a request is attached to is a client row.
    /// </para>
    /// <para>
    /// AD-4 applies as it does everywhere but <see cref="RequireChatParticipant"/>: an
    /// administrator passes by rule. A client whose claims carry no client row id is refused rather
    /// than widened, exactly as <see cref="RequireScope"/> refuses them — the only other answer
    /// available, <c>null</c>, is dispatch's answer and would let an account with a broken claim
    /// compose a delivery attached to nobody.
    /// </para>
    /// </summary>
    /// <returns>
    /// The client row the caller is composing for, or <c>null</c> when they compose for someone
    /// else — which a caller that needs a requester treats as a refusal and a caller that does not,
    /// such as the address search, simply ignores.
    /// </returns>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401), is a driver, is a client whose
    /// claims carry no client row id, or holds a role with no part in composing a delivery at all
    /// (<c>AUTH_FORBIDDEN</c>, 403).
    /// </exception>
    ClientId? RequireDeliveryComposer();

    /// <summary>
    /// Requires that the caller is a participant in a driver's conversation — or, when
    /// <paramref name="thread"/> is null, that they are the kind of caller who has a roster of
    /// conversations at all (FR-68 to FR-76, AD-15).
    /// <para>
    /// A dispatcher passes for every thread and for the roster; a driver passes for exactly the
    /// thread keyed on their own driver row and for nothing else. <b>A client and an administrator
    /// pass nothing.</b> That is the one place AD-4's override deliberately stops: the PRD records
    /// admin lockout from chat as a product decision — "admins moderate rather than dispatch" — and
    /// not as an omission, so the admin arm is absent from the implementation on purpose.
    /// </para>
    /// <para>
    /// The two questions share one member because they share one rule. The comparison is
    /// <c>DriverId?</c> against <c>DriverId?</c>: a null thread never equals a driver's row id, so
    /// the roster stays dispatcher-only without a second test, and a driver whose claims carry no
    /// driver row id compares unequal to every thread and is refused rather than widened.
    /// </para>
    /// <para>
    /// <see cref="RequireRole"/> cannot express it, because <c>RequireRole(Dispatcher)</c> reads as
    /// "a dispatcher or an admin" by design. <see cref="RequireSelf"/> cannot either, because the
    /// thread key is a driver row id rather than a user id, and a dispatcher who owns no thread
    /// must still pass.
    /// </para>
    /// </summary>
    /// <param name="thread">
    /// The driver row the conversation is keyed on, or null to ask about the roster of
    /// conversations rather than about one of them.
    /// </param>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401), or is a driver asking about a
    /// conversation that is not theirs, or holds a role with no part in chat at all — a client or
    /// an administrator (<c>AUTH_FORBIDDEN</c>, 403).
    /// </exception>
    void RequireChatParticipant(DriverId? thread);

    /// <summary>
    /// Answers "may this caller write reviews, and as which client row" (FR-62, FR-67).
    /// <para>
    /// A client, and nobody else. This is the second member whose answer deliberately contradicts
    /// AD-4: the PRD retired FR-97 because an administrator authoring customer feedback is not
    /// moderation — it is manufacturing it — so an admin is refused here and granted everything in
    /// <see cref="RequireReviewOwner"/>, which is where moderation actually lives. A dispatcher is
    /// refused for the plainer reason that they run the deliveries being judged. A driver is
    /// refused because they are the party being judged.
    /// </para>
    /// <para>
    /// <c>RequireRole(UserRole.Client)</c> cannot express it: it reads as "a client <em>or</em> an
    /// admin" by AD-4's design, which is the one answer that is wrong here — and an admin carries
    /// no client row id, so a service using it would have to decide what a missing id means, which
    /// is an authorization decision living outside this interface.
    /// </para>
    /// <para>
    /// It serves two call sites, which is the test of a question rather than a convenience:
    /// authoring a review, and reading back the reviews the caller authored — where the answer is
    /// also the scope the list is narrowed by (AD-3).
    /// </para>
    /// </summary>
    /// <returns>The client row the caller writes reviews as.</returns>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401); or is an administrator, a
    /// dispatcher or a driver, or is a client whose claims carry no client row id — refused rather
    /// than widened, exactly as <see cref="RequireScope"/> refuses them (<c>AUTH_FORBIDDEN</c>, 403).
    /// </exception>
    ClientId RequireReviewAuthor();

    /// <summary>
    /// Requires that the caller may change a review: its author, or an administrator moderating
    /// (FR-65, FR-66).
    /// <para>
    /// AD-4 applies in full here, and that is the point of the pair: an administrator authors
    /// nothing and edits or deletes anything, which is what moderation is. A dispatcher passes
    /// nothing — they read the collection and never write to it.
    /// </para>
    /// <para>
    /// <see cref="RequireSelf"/> cannot express it, because a review's owner is a
    /// <see cref="ClientId"/> rather than a <see cref="UserId"/> and AD-22 keeps the two from being
    /// compared. <see cref="RequireScope"/> cannot, because it hands a dispatcher the unrestricted
    /// scope and so cannot refuse one.
    /// </para>
    /// </summary>
    /// <param name="ownerId">
    /// The client who wrote the review, or null when there is no review at all — which is why the
    /// caller may ask this before answering 404. The comparison follows
    /// <see cref="RequireAssignedDriver"/>: a null never equals a caller's client row id, so a
    /// missing review is refused rather than handed to whoever asked for it.
    /// </param>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401), or is a client who did not write
    /// this review, or holds a role with no part in moderating one — a dispatcher or a driver
    /// (<c>AUTH_FORBIDDEN</c>, 403).
    /// </exception>
    void RequireReviewOwner(ClientId? ownerId);

    /// <summary>
    /// Answers which drivers' shifts this caller may be shown — "every driver" for dispatch, "their
    /// own and no others" for a driver, and nothing at all for a client (FR-112 to FR-115).
    /// <para>
    /// <see cref="RequireScope"/> cannot express it, and this is the one member where reusing it
    /// would be a disclosure rather than a wording problem: a client's scope is
    /// <c>(null, clientId)</c>, whose driver half is null — which a shift query reads as
    /// <em>unrestricted by driver</em> and would answer with every shift the system holds. FR-115
    /// gives a client no part in shifts at all, so the honest answer is a refusal rather than a
    /// narrowing.
    /// </para>
    /// <para>
    /// AD-4 applies: an administrator is unrestricted by rule, and a dispatcher stands beside them
    /// because FR-113 gives dispatch the whole roster of shifts.
    /// </para>
    /// </summary>
    /// <returns>
    /// The driver row the listing is narrowed to, or <c>null</c> for the unrestricted case — which
    /// reaches the repository unchanged and adds no <c>WHERE</c>.
    /// </returns>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401); or is a client, or is a driver
    /// whose claims carry no driver row id — refused rather than widened, exactly as
    /// <see cref="RequireScope"/> refuses them (<c>AUTH_FORBIDDEN</c>, 403).
    /// </exception>
    DriverId? RequireShiftScope();

    /// <summary>
    /// Requires that the caller may act on one driver's shift: that driver, or dispatch (FR-112,
    /// FR-114).
    /// <para>
    /// The write-side half of the pair, and the same shape as
    /// <see cref="RequireAssignedDriver"/> — which already answers "this driver, or dispatch, never
    /// a client" and would behave correctly here. It is a separate member because its refusal says
    /// <em>delivery lifecycle</em>, so every refused shift request would be logged as a delivery;
    /// <see cref="RequireReviewOwner"/> beside <see cref="RequireReviewAuthor"/> is the standing
    /// precedent for a capability-specific member with the same structure.
    /// </para>
    /// </summary>
    /// <param name="ownerDriverId">
    /// The driver whose shift it is, or null when there is no shift at all — which is why a service
    /// may ask this before answering 404: a driver who may not touch the row is refused before
    /// learning whether it exists. A null never equals a caller's driver row id.
    /// </param>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401), or is a driver who is not the
    /// one the shift belongs to, or holds a role with no part in shifts at all — a client
    /// (<c>AUTH_FORBIDDEN</c>, 403).
    /// </exception>
    void RequireShiftOwner(DriverId? ownerDriverId);
}
