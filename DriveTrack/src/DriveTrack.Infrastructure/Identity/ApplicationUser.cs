using Microsoft.AspNetCore.Identity;

namespace DriveTrack.Infrastructure.Identity;

/// <summary>
/// The account behind every actor in the system (FR-1, FR-35, FR-49).
/// <para>
/// It lives in Infrastructure, not Domain, because ASP.NET Core Identity is an infrastructure
/// concern and AD-1 keeps Domain free of every package reference. Domain entities reach a user
/// only through a <c>UserId</c>, never through this type.
/// </para>
/// <para>
/// It carries a name and nothing else. Authentication, roles and seeding are story 2.1; adding
/// a member for them now would be the speculative surface NFR-6 forbids.
/// </para>
/// </summary>
public class ApplicationUser : IdentityUser<int>
{
    /// <summary>Given name, shown wherever a person is named.</summary>
    public required string FirstName { get; set; }

    /// <summary>Family name, shown wherever a person is named.</summary>
    public required string LastName { get; set; }
}
