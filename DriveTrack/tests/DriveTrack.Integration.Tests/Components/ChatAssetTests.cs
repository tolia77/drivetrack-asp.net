using System.Net;
using System.Text.RegularExpressions;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// NFR-26 for the SignalR browser client: vendored and pinned, exactly as Leaflet is.
/// <para>
/// There is no front-end package manager here, so "pinned" has to mean something a build can check.
/// The version constant in the shipped file is the version, the path the module names is the
/// vendored one, and no CDN host appears anywhere — a chat screen that fetched its client over the
/// public internet would work on a developer's machine and be dead in a network the customer
/// controls.
/// </para>
/// </summary>
public class ChatAssetTests
{
    /// <summary>The pinned client version, as the bundle states it about itself.</summary>
    private const string PinnedVersion = "10.0.11";

    private static string SignalRDirectory { get; } = Path.Combine(
        RepositoryLayout.ProjectDirectory("DriveTrack.Web"), "wwwroot", "lib", "signalr");

    private static string ChatModulePath { get; } = Path.Combine(
        RepositoryLayout.ProjectDirectory("DriveTrack.Web"),
        "Components",
        "Pages",
        "Chat.razor.js");

    [Fact]
    public void The_vendored_client_is_on_disk_and_is_not_empty()
    {
        var file = new FileInfo(Path.Combine(SignalRDirectory, "signalr.js"));

        Assert.True(file.Exists, "The SignalR client is missing from wwwroot/lib/signalr/.");
        Assert.True(file.Length > 0, "The vendored SignalR client is empty.");
    }

    [Fact]
    public void The_shipped_client_is_the_pinned_version()
    {
        var bundle = File.ReadAllText(Path.Combine(SignalRDirectory, "signalr.js"));

        // The bundle states its own version, which is what the client sends on the negotiate
        // handshake - so this is the number the server actually sees, not a file name.
        var banner = Regex.Match(
            bundle,
            @"VERSION\s*=\s*'(?<version>\d+\.\d+\.\d+)'",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(banner.Success, "The vendored SignalR bundle carries no version constant.");
        Assert.Equal(PinnedVersion, banner.Groups["version"].Value);

        // Vendored verbatim except for the source map reference: shipping a sourceMappingURL for a
        // file that is not there produces a 404 in every developer's console.
        Assert.DoesNotContain("sourceMappingURL", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_major_version_matches_the_server_it_talks_to()
    {
        // A browser client and a hub of different majors negotiate a protocol one of them does not
        // implement, and the symptom is a connection that opens and then closes with no message.
        var bundle = File.ReadAllText(Path.Combine(SignalRDirectory, "signalr.js"));

        Assert.Contains("VERSION = '10.", bundle, StringComparison.Ordinal);

        // The server half is the shared framework the Web project targets, stated once in
        // Directory.Build.props and inherited there. The Web project takes no SignalR package of its
        // own - the one in this solution is the *client*, referenced by this test project alone, and
        // a server package appearing beside the framework is two versions of one thing.
        var props = File.ReadAllText(
            Path.Combine(RepositoryLayout.SolutionRoot.FullName, "Directory.Build.props"));

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", props, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "Microsoft.AspNetCore.SignalR",
            File.ReadAllText(RepositoryLayout.ProjectFile("DriveTrack.Web")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_module_loads_the_vendored_copy_and_never_a_cdn()
    {
        var module = File.ReadAllText(ChatModulePath);

        Assert.Contains(@"""/lib/signalr/signalr.js""", module, StringComparison.Ordinal);
        Assert.Contains(@"""/hubs/chat""", module, StringComparison.Ordinal);

        foreach (var host in new[] { "unpkg.com", "cdnjs", "jsdelivr", "microsoft.com", "http://", "https://" })
        {
            Assert.DoesNotContain(host, module, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_client_is_injected_at_run_time_so_the_shell_keeps_its_one_script()
    {
        // The bundle is UMD and assigns window.signalR rather than exporting, so it is injected as a
        // classic script at first use. Linking it from App.razor instead would fetch a hundred and
        // fifty kilobytes for every caller who never opens the chat screen.
        var module = File.ReadAllText(ChatModulePath);
        var shell = SharedMarkup.ReadComponent("App.razor");

        Assert.Contains("document.head.appendChild(script)", module, StringComparison.Ordinal);
        Assert.Contains("window.signalR", module, StringComparison.Ordinal);
        Assert.DoesNotContain("signalr", shell, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_screen_imports_the_module_at_the_path_that_is_served()
    {
        // The served route and the string the component hands to import() have to be the same path.
        // They are written in two files and nothing else compares them.
        var screen = SharedMarkup.ReadComponent("Pages", "Chat.razor");

        Assert.Contains(
            "private const string ModulePath = \"./Components/Pages/Chat.razor.js\";",
            screen,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_newest_line_is_scrolled_to_after_a_join_and_after_every_arrival()
    {
        // FR-74, and the only requirement in this story a browser alone can demonstrate - so it is
        // asserted where it is decided: the module exports the move, and the screen calls it at both
        // moments the list grows. A conversation that loads scrolled to its oldest line, or that
        // quietly appends below the fold, is a screen that looks empty and stale to its reader.
        var module = File.ReadAllText(ChatModulePath);

        Assert.Contains("export function scrollToNewest(elementId)", module, StringComparison.Ordinal);

        // The move itself: to the bottom of the element's own scrollable height, not to a fixed
        // offset and not to the page's.
        Assert.Contains("list.scrollTop = list.scrollHeight;", module, StringComparison.Ordinal);

        // Guarded, because the list is gone the moment the caller navigates away and an unguarded
        // read would throw inside the interop call rather than do nothing.
        Assert.Contains("document.getElementById(elementId)", module, StringComparison.Ordinal);

        var screen = SharedMarkup.ReadComponent("Pages", "Chat.razor");

        // Called through one helper, from both places the list grows: the join that loads a
        // conversation, and the broadcast that appends to one already open.
        Assert.Contains(@"InvokeVoidAsync(""scrollToNewest"", _listId)", screen, StringComparison.Ordinal);
        Assert.Equal(2, SharedMarkup.Occurrences(screen, "await ScrollAsync();"));

        // And the element it is given is the one the list actually carries, so the id written in two
        // places is the same id.
        Assert.Contains(@"<ol id=""@_listId""", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reconnected_connection_rejoins_the_conversation_it_was_in()
    {
        // A SignalR group membership is keyed on the connection id, and a reconnect is a new
        // connection id: the server cannot restore it and does not try. Without the rejoin below, a
        // screen that survived a transient drop looks entirely healthy - the composer still sends,
        // the server still stores the row - and no broadcast ever arrives again, not even the
        // caller's own lines, until the page is reloaded.
        var module = File.ReadAllText(ChatModulePath);

        Assert.Contains("withAutomaticReconnect()", module, StringComparison.Ordinal);
        Assert.Contains("connection.onreconnected(", module, StringComparison.Ordinal);
        Assert.Contains(@"connection.invoke(""JoinThread"", handle.joined)", module, StringComparison.Ordinal);

        // Remembered on a join and forgotten on a leave, so a reconnect rejoins what the caller is
        // actually reading rather than a conversation they have since left.
        Assert.Contains("handle.joined = driverId;", module, StringComparison.Ordinal);
        Assert.Contains("handle.joined = null;", module, StringComparison.Ordinal);

        // And the history the rejoin answers with is replayed rather than dropped. Nothing was
        // broadcast to this connection while it was down, so restoring only the group leaves the
        // outage's messages missing from a screen that once again looks entirely healthy - and a
        // driver, who never picks a thread, cannot reach them again without reloading the page.
        Assert.Contains(
            @"dotNetRef.invokeMethodAsync(""ReceiveMessageAsync"", thread.driverId, message)",
            module,
            StringComparison.Ordinal);

        // Replayed through the same callback a broadcast arrives on, which is only safe because
        // that callback drops a line it is already showing.
        var screen = SharedMarkup.ReadComponent("Pages", "Chat.razor");

        Assert.Contains(
            "_thread.Messages.Any(line => line.Id == message.Id)",
            screen,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_screen_is_told_when_the_hub_drops_and_when_it_comes_back()
    {
        // The module owns the socket, so the screen learns what happened to it only when it is told.
        // `withAutomaticReconnect()` retries silently, which is the whole problem: nothing on the
        // page changes, the composer still looks live, and the first news of the outage is a Send
        // that fails.
        var module = File.ReadAllText(ChatModulePath);

        // Both failure events, and they are not the same one: `onreconnecting` is a drop the client
        // still expects to recover from, `onclose` is the retry having given up for good. A module
        // wired to only the first leaves a screen with its dock cleared on the connection that never
        // came back.
        Assert.Contains("connection.onreconnecting(", module, StringComparison.Ordinal);
        Assert.Contains("connection.onclose(", module, StringComparison.Ordinal);

        // Told apart by the second argument, because they are two different sentences on the screen:
        // one is a wait, the other has to offer a way out. Collapsed into one, a screen sits under
        // "reconnecting…" for the rest of the session after the retries have stopped.
        Assert.Contains(
            @"""ConnectionChangedAsync"", false, true", module, StringComparison.Ordinal);

        Assert.Contains(
            @"""ConnectionChangedAsync"", false, false", module, StringComparison.Ordinal);

        // And the recovery, so the dock clears and the composer comes back.
        Assert.Contains(
            @"invokeMethodAsync(""ConnectionChangedAsync"", true, true)",
            module,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_deliberate_stop_is_not_reported_back_as_an_outage_and_never_skips_the_rejoin()
    {
        // Two faults on one seam. `stop()` fires `onclose`, so every navigation away from chat was
        // invoking into a DotNetObjectReference mid-teardown and calling StateHasChanged on a
        // component that was going away - for a disconnection the component itself asked for.
        var module = File.ReadAllText(ChatModulePath);

        Assert.Contains("closing: false", module, StringComparison.Ordinal);
        Assert.Contains("handle.closing = true;", module, StringComparison.Ordinal);
        Assert.Contains("if (handle.closing) {", module, StringComparison.Ordinal);

        // The flag is raised before the stop, not after: `stop()` reaches `onclose` too quickly for
        // a flag set afterwards to have been read.
        var raised = module.IndexOf("handle.closing = true;", StringComparison.Ordinal);
        var stopped = module.IndexOf("connection.stop()", StringComparison.Ordinal);

        Assert.True(raised >= 0 && stopped >= 0);
        Assert.True(raised < stopped, "The stop is asked for before it is marked as deliberate.");

        // And the other one: the rejoin is what a reconnect exists for. A SignalR group membership is
        // keyed on the connection id, so a handler that abandoned itself on a rejected interop call
        // would leave a healthy socket that never receives another broadcast - with nothing on
        // screen to say so.
        var told = module.IndexOf(
            @"await dotNetRef.invokeMethodAsync(""ConnectionChangedAsync"", true, true);",
            StringComparison.Ordinal);

        var guarded = module.IndexOf("} catch {", StringComparison.Ordinal);
        var rejoined = module.IndexOf(
            @"connection.invoke(""JoinThread"", handle.joined)", StringComparison.Ordinal);

        Assert.True(told >= 0, "The module never tells the screen the hub came back.");
        Assert.True(
            guarded > told && guarded < rejoined,
            "A rejected interop call on reconnect skips the rejoin the reconnect exists for.");
    }

    [Fact]
    public void An_arriving_message_scrolls_only_a_reader_who_was_already_at_the_bottom()
    {
        // FR-74 says the newest line is what a reader following a conversation sees. It does not say
        // a reader who has scrolled up into last week should be dragged back down because somebody
        // typed - that is the page being taken away mid-sentence, and on a long history it is
        // unrecoverable without scrolling the whole way back.
        var module = File.ReadAllText(ChatModulePath);

        Assert.Contains("export function isAtBottom(elementId)", module, StringComparison.Ordinal);

        // Asked before the append, so the answer is about where the reader was rather than where
        // the new line left them - after it, the list is always taller than it was and the answer is
        // always "no".
        var screen = SharedMarkup.ReadComponent("Pages", "Chat.razor");

        var asked = screen.IndexOf("await IsAtBottomAsync();", StringComparison.Ordinal);
        var appended = screen.IndexOf("_thread with", StringComparison.Ordinal);

        Assert.True(asked >= 0, "Chat.razor never asks where the reader is before appending a line.");
        Assert.True(
            asked < appended,
            "Chat.razor asks where the reader is after appending the line, so the answer is always "
                + "the bottom it just moved.");
    }

    [Fact]
    public void A_failed_library_fetch_is_not_remembered_forever()
    {
        // The module-scope promise is what stops two components injecting two script tags. Left set
        // on the failure path it becomes the opposite: a single dropped fetch hands the same
        // rejected promise to every later caller, and chat is dead for the life of the page.
        var module = File.ReadAllText(ChatModulePath);

        // Three: the declaration at module scope, and one reset on each of the two failure paths -
        // the fetch that never arrived, and the script that arrived and defined nothing.
        Assert.Equal(3, SharedMarkup.Occurrences(module, "loading = null;"));
        Assert.Contains("script.onerror", module, StringComparison.Ordinal);

        // A script that loaded but defined no global is a failure wearing a success: resolving with
        // undefined would surface at `new signalR.HubConnectionBuilder()`, naming neither the file
        // nor the fetch.
        Assert.Contains("if (window.signalR) {", module, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hub_route_the_module_dials_is_the_one_the_host_maps()
    {
        // Written in two files - the module and the composition root - and nothing else compares
        // them. A rename on either side is a screen that connects to nothing, with every other
        // assertion in this class still green.
        var module = File.ReadAllText(ChatModulePath);

        var program = File.ReadAllText(
            Path.Combine(RepositoryLayout.ProjectDirectory("DriveTrack.Web"), "Program.cs"));

        Assert.Contains(@"const hubUrl = ""/hubs/chat"";", module, StringComparison.Ordinal);
        Assert.Contains(@"MapHub<ChatHub>(""/hubs/chat"")", program, StringComparison.Ordinal);

        // Outside /api, which is what lets a browser's cookie authenticate the handshake: AD-22
        // makes /api bearer-only and the circuit holds no token.
        Assert.DoesNotContain("/api/hubs", program, StringComparison.Ordinal);
    }
}

/// <summary>
/// The other half of NFR-26: an asset has to be <em>served</em>, not merely present.
/// <para>
/// Everything above reads the developer's disk. None of it would notice the file being excluded from
/// the static web asset manifest, which is how it actually reaches a browser — the same gap
/// <c>ThemeDeliveryTests</c> exists to close for the compiled theme.
/// </para>
/// </summary>
public class ChatAssetDeliveryTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("lib/signalr/signalr.js")]
    [InlineData("Components/Pages/Chat.razor.js")]
    public async Task The_vendored_asset_is_served(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        Assert.NotEmpty(content);
    }
}
