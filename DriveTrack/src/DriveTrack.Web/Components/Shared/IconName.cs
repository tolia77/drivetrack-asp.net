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

    /// <summary>A create action (AD-28's create colour).</summary>
    Create,

    /// <summary>An edit action (AD-28's edit colour).</summary>
    Edit,

    /// <summary>A destructive action (AD-28's destructive colour).</summary>
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

    /// <summary>The client roster (FR-46).</summary>
    Clients,

    /// <summary>The dispatcher roster (FR-49).</summary>
    Dispatchers,
    /// <summary>The driver roster (FR-35).</summary>
    Drivers,

    /// <summary>The vehicle fleet (FR-40).</summary>
    Vehicles,

    /// <summary>Deliveries: the parcel that is the product's central record (FR-18, FR-25).</summary>
    Deliveries,

    /// <summary>A delivery's append-only history (FR-105 to FR-108).</summary>
    Timeline,

    /// <summary>Looking a place up by its address (FR-104).</summary>
    Search,

    /// <summary>The log of outbound notification attempts (FR-28).</summary>
    Notifications,

    /// <summary>Capturing the hand-over: the action that takes the photographs (FR-119).</summary>
    Camera,

    /// <summary>A captured proof of delivery, as something to read rather than to take (FR-122).</summary>
    Proof,

    /// <summary>A driver's conversation, and the destination that opens it (FR-68, FR-69).</summary>
    Chat,

    /// <summary>Sending the message that has been typed (FR-70).</summary>
    Send,

    /// <summary>A client's verdict on a delivery, and the destination that opens them (FR-62, FR-63).</summary>
    Reviews,

    /// <summary>A driver's stretch of time on duty, and the two destinations that open them (FR-109, FR-113).</summary>
    Shifts,

    /// <summary>The control that reveals the navigation below the phone breakpoint (NFR-22).</summary>
    Menu,

    /// <summary>A sortable column header, and the direction it can be taken in (FR-82).</summary>
    Sort,

    /// <summary>
    /// The column the rows are actually ordered by, smallest first. The stacked pair above says
    /// "this sorts"; a single chevron says which way it went, so ascending and descending differ by
    /// shape rather than only by the <c>aria-sort</c> a sighted reader never hears.
    /// </summary>
    SortAscending,

    /// <summary>The same column ordered the other way (FR-82).</summary>
    SortDescending,
}
