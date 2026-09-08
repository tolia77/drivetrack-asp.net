namespace DriveTrack.Web.Components.Shared;

/// <summary>
/// NFR-24's icon vocabulary, closed at compile time.
/// <para>
/// The scaffold's navigation carried three <c>bi-*-nav-menu</c> spans with no CSS rule behind
/// them — an empty box on every screen, and nothing in the build said so. Naming the set as an
/// enum makes a typo a compile error instead, and makes "which icons does this product have?" a
/// question with one answer.
/// </para>
/// <para>
/// Every member is drawn by <c>Icon.razor</c>. Adding one without a path there leaves a blank
/// <c>&lt;svg&gt;</c>, which <c>SharedComponentTests</c> catches by counting the two against each
/// other.
/// </para>
/// </summary>
public enum IconName
{
    /// <summary>The landing page.</summary>
    Home,

    /// <summary>The signed-in caller's own profile.</summary>
    Profile,

    /// <summary>Entering the application.</summary>
    SignIn,

    /// <summary>Leaving the application.</summary>
    SignOut,

    /// <summary>Creating an account.</summary>
    Register,

    /// <summary>A create action (AD-28's green).</summary>
    Create,

    /// <summary>An edit action (AD-28's blue).</summary>
    Edit,

    /// <summary>A destructive action (AD-28's red).</summary>
    Delete,

    /// <summary>Accepting a prompt.</summary>
    Confirm,

    /// <summary>Dismissing a dialog.</summary>
    Close,

    /// <summary>The way back from a dead end.</summary>
    Back,

    /// <summary>A caution notice.</summary>
    Warning,

    /// <summary>Access refused (FR-79).</summary>
    Forbidden,

    /// <summary>Nothing at this address (FR-78).</summary>
    NotFound,

    /// <summary>A point on the map (FR-15, FR-21).</summary>
    Location,
}
