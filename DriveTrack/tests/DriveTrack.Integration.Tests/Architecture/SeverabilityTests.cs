using System.Text.RegularExpressions;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Architecture;

/// <summary>
/// AD-16, as a build-failing test: chat is a module that could be deleted.
/// <para>
/// "Severable" is an acceptance criterion of story 8.1 and is otherwise enforced only by comments —
/// <c>MessageConfiguration</c> declares a navigation-less foreign key and says why, and <c>Driver</c>
/// holds no path back into chat. Neither of those statements can notice the day somebody adds a
/// <c>Messages</c> collection to an entity, injects <c>IChatService</c> into a delivery screen, or
/// reaches for <c>ChatHub</c> from a controller. This can.
/// </para>
/// <para>
/// The seams are named individually rather than allowed by folder. A capability with a wildcard
/// exemption is a capability whose boundary moves every time a file is added to the wrong place, and
/// the whole point of the list is that growing it is a decision somebody had to write down.
/// </para>
/// </summary>
public class SeverabilityTests
{
    /// <summary>
    /// The names that only chat may say. The entity is matched by its namespace rather than by the
    /// bare word <c>Message</c>: <c>failure.Message</c> and <c>ValidationException.Message</c> are
    /// properties of half the framework, and a rule that flagged them would be relaxed within a week.
    /// Nothing can name the entity without naming this namespace.
    /// </summary>
    private static readonly string[] ChatNames =
    [
        "DriveTrack.Domain.Chat",
        "DriveTrack.Application.Chat",
        "IChatService",
        "ChatService",
        "ChatHub",

        // The repository port, which is chat's as much as the entity is: it is declared for
        // Message and for nothing else. Watched by name because the two files that hold it -
        // IUnitOfWork and UnitOfWork - name neither the entity's namespace nor the capability's,
        // so without this the scan reported the boundary clean while two files outside the list
        // would have stopped compiling the moment the module was deleted.
        "IMessageRepository",

        // The screen's decisions and its interop shapes. Pure chat code, and invisible to every
        // other name here for the same reason.
        "ChatViews",

        // The interop records and the line component, which are public types in
        // DriveTrack.Web.Components.Pages and spell none of the names above. A screen that declared
        // a ChatMessageView field, or a layout that dropped a <ChatLine> into its own markup, would
        // stop compiling the day chat was deleted while the scan reported the boundary clean -
        // which is the one thing this test exists to make impossible.
        "ChatMessageView",
        "ChatThreadView",
        "ChatThreadSummaryView",
        "ChatLine",
    ];

    /// <summary>
    /// The chat module, plus the seams AD-16 licenses. Every entry is a path relative to
    /// <c>src/</c>, and every one of them is a place the module has to be visible from:
    /// <list type="bullet">
    /// <item>the module itself — the entity, the capability, the hub, the screen;</item>
    /// <item>its persistence — the repository port, the two EF types that implement and map it, the
    /// context that must declare a set for the table to exist at all, and the per-operation scope on
    /// both sides of its own port, which offers that repository beside every other;</item>
    /// <item>the migrations, which are generated from the model and name every entity in it;</item>
    /// <item>the composition root of each layer — <c>Infrastructure/DependencyInjection.cs</c>
    /// registers the service, <c>Web/Program.cs</c> maps the hub — because AD-1 puts registration
    /// there and nowhere else;</item>
    /// <item><c>Web/Account/HubCaller.cs</c>, which exists only so the hub has a caller to hand to
    /// <c>ICurrentUser</c> and names the hub in explaining why it is public.</item>
    /// </list>
    /// </summary>
    private static readonly string[] AllowedSeams =
    [
        Path.Combine("DriveTrack.Domain", "Chat", "Message.cs"),
        Path.Combine("DriveTrack.Application", "Chat", "ChatContracts.cs"),
        Path.Combine("DriveTrack.Application", "Chat", "ChatService.cs"),
        Path.Combine("DriveTrack.Application", "Chat", "ChatValidators.cs"),
        Path.Combine("DriveTrack.Application", "Chat", "IChatService.cs"),
        Path.Combine("DriveTrack.Application", "Abstractions", "IMessageRepository.cs"),
        Path.Combine("DriveTrack.Application", "Abstractions", "IUnitOfWork.cs"),
        Path.Combine("DriveTrack.Infrastructure", "DependencyInjection.cs"),
        Path.Combine("DriveTrack.Infrastructure", "Persistence", "AppDbContext.cs"),
        Path.Combine("DriveTrack.Infrastructure", "Persistence", "UnitOfWork.cs"),
        Path.Combine("DriveTrack.Infrastructure", "Persistence", "Configurations", "MessageConfiguration.cs"),
        Path.Combine("DriveTrack.Infrastructure", "Persistence", "Repositories", "EfMessageRepository.cs"),
        Path.Combine("DriveTrack.Web", "Account", "HubCaller.cs"),
        Path.Combine("DriveTrack.Web", "Components", "Pages", "Chat.razor"),
        Path.Combine("DriveTrack.Web", "Components", "Pages", "ChatLine.razor"),
        Path.Combine("DriveTrack.Web", "Components", "Pages", "ChatViews.cs"),
        Path.Combine("DriveTrack.Web", "Hubs", "ChatHub.cs"),
        Path.Combine("DriveTrack.Web", "Program.cs"),
    ];

    /// <summary>
    /// Generated from the model rather than written, so it names every entity the context maps and
    /// there is nothing to sever. Excluded as a folder for that reason and for that reason only.
    /// </summary>
    private static readonly string MigrationsFolder =
        Path.Combine("DriveTrack.Infrastructure", "Persistence", "Migrations") + Path.DirectorySeparatorChar;

    [Fact]
    public void There_are_sources_to_scan()
    {
        // Guards every assertion below: an empty file set makes them all vacuously true, which is
        // exactly how an architecture test quietly stops testing an architecture.
        Assert.NotEmpty(Sources());
    }

    [Fact]
    public void No_file_outside_the_chat_module_and_its_seams_names_chat()
    {
        // AD-16's acceptance criterion, stated as the question it is: if the chat module were
        // deleted, would the solution still build? Every file listed here is one that would then
        // stop compiling, so the answer is exactly the length of this list.
        var offenders = Sources()
            .Where(path => !IsSeam(path))
            .Where(path => ChatNames.Any(name =>
                File.ReadAllText(path).Contains(name, StringComparison.Ordinal)))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_named_seam_still_exists_and_still_names_chat()
    {
        // The other direction, and the reason it matters: a seam that has stopped naming chat is a
        // seam that has been closed, and leaving it on the list makes the boundary look wider than
        // it is. A seam whose file is gone is worse - the entry is then an exemption for nothing,
        // and the next file to take that path inherits it.
        var source = RepositoryLayout.Source.FullName;

        var stale = AllowedSeams
            .Where(seam =>
            {
                var path = Path.Combine(source, seam);

                return !File.Exists(path)
                    || !ChatNames.Any(name =>
                        File.ReadAllText(path).Contains(name, StringComparison.Ordinal));
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(stale);
    }

    [Fact]
    public void No_entity_holds_a_navigation_back_into_the_conversation()
    {
        // The structural half of AD-16, and the one a name scan cannot see: `Driver` could grow an
        // `ICollection<Message> Messages` and every assertion above would still pass, because the
        // file naming the type would be inside the Domain layer where the entity lives.
        //
        // MessageConfiguration's `WithMany()` - with no navigation named - is what keeps the foreign
        // key one-directional, and it is the line this asserts.
        var configuration = File.ReadAllText(Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Infrastructure"),
            "Persistence",
            "Configurations",
            "MessageConfiguration.cs"));

        Assert.Contains(".WithMany()", configuration, StringComparison.Ordinal);

        // Comments stripped first: Driver.cs explains in prose why it holds no such collection, and
        // an explanation of a rule is not a breach of it.
        var driver = WithoutComments(File.ReadAllText(Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Domain"), "Drivers", "Driver.cs")));

        Assert.DoesNotContain("Message", driver, StringComparison.Ordinal);
    }

    /// <summary>The source with its <c>//</c> and <c>/* */</c> comments removed.</summary>
    private static string WithoutComments(string source)
    {
        source = Regex.Replace(
            source, @"/\*.*?\*/", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));

        return Regex.Replace(
            source, @"//[^\r\n]*", " ", RegexOptions.None, TimeSpan.FromSeconds(5));
    }

    /// <summary>Every checked-in C# and Razor source under <c>src/</c>.</summary>
    private static string[] Sources() =>
    [
        .. Directory
            .EnumerateFiles(RepositoryLayout.Source.FullName, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsBuildOutput(path))
            .Order(StringComparer.Ordinal),
    ];

    private static bool IsSeam(string path)
    {
        var relative = Relative(path);

        return relative.StartsWith(MigrationsFolder, StringComparison.Ordinal)
            || AllowedSeams.Contains(relative, StringComparer.Ordinal);
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(RepositoryLayout.Source.FullName, path);

    private static bool IsBuildOutput(string path)
    {
        var relative = Relative(path);

        return relative.Contains(
                   Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                   StringComparison.Ordinal)
            || relative.Contains(
                   Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                   StringComparison.Ordinal);
    }
}
