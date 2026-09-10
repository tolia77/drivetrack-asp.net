namespace DriveTrack.Application.Deliveries;

/// <summary>
/// NFR-27's paging over a delivery list, expressed as a request rather than as two loose integers —
/// the same shape <c>ListVehiclesQuery</c> takes, because the rule is the same rule.
/// <para>
/// There is deliberately no sort or filter here. PRD section 8 excludes server-side filtering,
/// sorting and result counts, so the screen sorts and filters the page it already fetched; a
/// parameter here would be the first half of a feature the product does not have.
/// </para>
/// <para>
/// Nor is there a scope. The caller does not get to say whose rows it wants: that answer comes
/// from <c>IAccessGuard.RequireScope</c> and reaches the repository as a separate argument (AD-3).
/// </para>
/// </summary>
/// <param name="Offset">Rows to skip. Zero is the first page.</param>
/// <param name="Limit">Rows to take, at most <see cref="ListDeliveriesQueryValidator.MaximumLimit"/>.</param>
public sealed record ListDeliveriesQuery(int Offset, int Limit);
