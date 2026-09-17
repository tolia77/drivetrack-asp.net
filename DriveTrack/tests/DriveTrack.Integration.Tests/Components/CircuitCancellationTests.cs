using System.Reflection;
using System.Text.RegularExpressions;
using DriveTrack.Application.Common;
using DriveTrack.Application.Deliveries;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// What happens to a screen's in-flight work when the screen goes away.
/// <para>
/// Every interactive screen cancels its own work on teardown, and each one used to dispose its
/// <see cref="CancellationTokenSource"/> immediately afterwards. That disposal is a torn circuit
/// waiting to happen: a continuation resuming after the component is gone reads the token, gets
/// <see cref="ObjectDisposedException"/> — which is not cancellation and is swallowed by nothing —
/// and the renderer takes the whole connection down. Nothing in the suite said so, because a
/// component that is never disposed never exhibits it.
/// </para>
/// <para>
/// So the claims here are about disposal itself, asserted by disposing every screen the way the
/// renderer would and reading its token afterwards, and about the one screen whose blanket
/// <c>catch</c> could turn a cancellation back into a reported failure.
/// </para>
/// </summary>
public class CircuitCancellationTests
{
    /// <summary>
    /// Every component that is rendered into a circuit <em>and</em> calls a capability taking a
    /// <see cref="CancellationToken"/>. Mostly screens, and one shared component: DtLocationPicker
    /// runs FR-104's address lookup itself, so it has work of its own to scope and is torn down on
    /// its own schedule - the dialog that holds it can go before the screen does. Both halves are
    /// load-bearing, and two kinds of screen sit outside the rule for two different reasons:
    /// <list type="bullet">
    /// <item><description>
    /// <c>Components/Pages/Chat.razor</c> is interactive but injects no capability at all — it does
    /// its work over the SignalR module through <c>IJSRuntime</c>, so there is no token to scope
    /// and a base class would give it nothing to cancel.
    /// </description></item>
    /// <item><description>
    /// <c>Components/Account/SignIn.razor</c> and <c>Components/Account/Register.razor</c> do call a
    /// capability that takes a token, but they carry <c>[ExcludeFromInteractiveRouting]</c> and
    /// render statically so that the form POST can reach <c>HttpContext.SignInAsync</c> (AD-14).
    /// There is no circuit behind them and no component lifetime to scope to, so they hand over
    /// <c>HttpContext.RequestAborted</c> — the request's own lifetime, which is the right one.
    /// </description></item>
    /// </list>
    /// <para>
    /// Named rather than counted: a screen that quietly stopped deriving from the base would still
    /// leave a plausible number behind, and a new screen with a token source of its own is exactly
    /// the regression this suite exists for. The list is a snapshot of the type graph, so it cannot
    /// see a screen that never joined the hierarchy at all;
    /// <see cref="Every_screen_that_scopes_work_to_a_token_derives_from_the_base"/> reads the source
    /// for that one and re-checks the two exemptions above against it.
    /// </para>
    /// </summary>
    private static readonly string[] ExpectedScreens =
    [
        "DriveTrack.Web.Components.Account.Profile",
        "DriveTrack.Web.Components.Pages.Admin.Clients",
        "DriveTrack.Web.Components.Pages.Admin.Dispatchers",
        "DriveTrack.Web.Components.Pages.Admin.Notifications",
        "DriveTrack.Web.Components.Pages.Deliveries",
        "DriveTrack.Web.Components.Pages.DeliveryTimeline",
        "DriveTrack.Web.Components.Pages.Drivers",
        "DriveTrack.Web.Components.Pages.MyDeliveries",
        "DriveTrack.Web.Components.Pages.MyShifts",
        "DriveTrack.Web.Components.Pages.ProofCapture",
        "DriveTrack.Web.Components.Pages.ProofView",
        "DriveTrack.Web.Components.Pages.Reviews",
        "DriveTrack.Web.Components.Pages.Shifts",
        "DriveTrack.Web.Components.Pages.Vehicles",
        "DriveTrack.Web.Components.Shared.DtLocationPicker",
    ];

    /// <summary>
    /// The screens that hand out a cancellation token and still do not derive from the base,
    /// by design: statically rendered account forms, which have a request behind them rather than a
    /// circuit and so scope their work to <c>HttpContext.RequestAborted</c>. Paths relative to
    /// <c>Components/</c>, forward-slashed.
    /// </summary>
    private static readonly string[] StaticallyRenderedForms =
    [
        "Account/Register.razor",
        "Account/SignIn.razor",
    ];

    private static string ComponentsDirectory { get; } = Path.Combine(
        RepositoryLayout.ProjectDirectory("DriveTrack.Web"), "Components");

    [Fact]
    public void Every_interactive_screen_scopes_its_work_to_the_circuit()
    {
        var actual = Screens()
            .Select(screen => screen.FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ExpectedScreens.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            actual);
    }

    [Fact]
    public void Nothing_in_the_web_assembly_but_the_base_owns_a_cancellation_source()
    {
        // Deliberately the whole assembly rather than the screens alone. The base is meant to be
        // the one place in DriveTrack.Web a source is constructed, and a screen that declared its
        // own would compile, render and pass every other test in this project while carrying the
        // defect back in. Scanning only CircuitScopedComponent subtypes would miss the likelier
        // regression anyway: a helper, a layout or a new component that never joined the hierarchy.
        // Anything in this project that legitimately needs its own source has to come here first.
        var offenders = typeof(CircuitScopedComponent).Assembly
            .GetTypes()
            .Where(type => type != typeof(CircuitScopedComponent))
            .SelectMany(type => type.GetFields(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(field => field.FieldType == typeof(CancellationTokenSource))
            .Select(field => $"{field.DeclaringType!.FullName}.{field.Name}")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "DriveTrack.Web declares a CancellationTokenSource outside CircuitScopedComponent: "
                + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_screen_that_scopes_work_to_a_token_derives_from_the_base()
    {
        // Both guards above read the compiled type graph, so both are blind to the cheapest way
        // back into the defect: a screen written tomorrow that injects a capability, passes it some
        // token of its own, and simply never joins the hierarchy. It declares no
        // CancellationTokenSource field and adds no CircuitScopedComponent subtype, so it is
        // invisible to reflection - but it is plain in the source, which is what this reads.
        //
        // The membership question a screen answers here is "do you hand anything a cancellation
        // token?", because that is the only thing the base exists to own. A screen that answers yes
        // either inherits the base or is one of the two statically rendered account forms, which
        // have a request rather than a circuit behind them.
        var offenders = new List<string>();
        var exemptionsSeen = new List<string>();

        foreach (var file in ScreenSources())
        {
            var source = WithoutComments(File.ReadAllText(file));
            var relative = Path.GetRelativePath(ComponentsDirectory, file).Replace('\\', '/');

            if (!Regex.IsMatch(
                    source,
                    @"\bCancellationToken\b|\bRequestAborted\b|\bCircuitToken\b",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(5)))
            {
                continue;
            }

            if (StaticallyRenderedForms.Contains(relative, StringComparer.Ordinal))
            {
                exemptionsSeen.Add(relative);

                continue;
            }

            if (source.Contains("@inherits CircuitScopedComponent", StringComparison.Ordinal))
            {
                continue;
            }

            offenders.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            "A screen under Components/ hands out a cancellation token without deriving from "
                + "CircuitScopedComponent: " + string.Join(", ", offenders));

        // And the exemptions are checked rather than asserted: a file that was renamed, deleted, or
        // converted to interactive rendering would otherwise leave a licence behind that excuses a
        // screen nobody meant to excuse.
        Assert.Equal(
            StaticallyRenderedForms.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            exemptionsSeen.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void No_screen_hands_a_capability_an_uncancellable_token()
    {
        // The mutation the whole suite otherwise survives. Every guard here is satisfied by a screen
        // that derives from the base, owns no source of its own, cancels correctly on teardown -
        // and then passes CancellationToken.None at the call site, which is the original defect
        // wearing the fix's clothes. A_screen_hands_its_capability_the_token_its_teardown_cancels
        // closes one call site of roughly sixty by rendering it; this closes the rest by reading
        // them, which is the only surface that reaches a submit path static rendering cannot press.
        var offenders = new List<string>();

        foreach (var file in ScreenSources())
        {
            var lines = WithoutComments(File.ReadAllText(file)).Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                // `default` as well as the spelled-out None: `default` in an argument position is
                // the same uncancellable token with a shorter name, and a call site that omits the
                // argument entirely gets it from the parameter's own default.
                if (!Regex.IsMatch(
                        lines[index],
                        @"\bCancellationToken\s*\.\s*None\b|(?<=[(,]\s{0,40})default\s*[,)]",
                        RegexOptions.None,
                        TimeSpan.FromSeconds(5)))
                {
                    continue;
                }

                offenders.Add($"{Path.GetRelativePath(ComponentsDirectory, file).Replace('\\', '/')}:{index + 1}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A screen under Components/ passes an uncancellable token where its own CircuitToken "
                + "belongs: " + string.Join(", ", offenders));
    }

    [Theory]
    [MemberData(nameof(ScreenNames))]
    public async Task A_disposed_screen_leaves_a_cancelled_token_that_can_still_be_read(string screenName)
    {
        var screen = (CircuitScopedComponent)Activator.CreateInstance(Screen(screenName))!;

        // Read once before, so "cancelled" is a state the disposal produced rather than one the
        // component was born in.
        Assert.False(screen.CircuitToken.IsCancellationRequested);

        await DisposeAsRendererWouldAsync(screen);

        // The whole point: this read happens after teardown, which is precisely when a continuation
        // that outlived its component asks. A disposed source answers ObjectDisposedException here.
        var token = screen.CircuitToken;

        Assert.True(token.IsCancellationRequested);

        // And again, because a source that had been disposed would throw on every read, not only
        // the first.
        Assert.True(screen.CircuitToken.IsCancellationRequested);
        Assert.True(screen.CircuitToken.CanBeCanceled);

        // The two members that actually reach the disposed source's state. Reading
        // IsCancellationRequested off a disposed CancellationTokenSource happens to be allowed, so
        // the assertions above are weaker than they look; Register and WaitHandle are the ones that
        // throw ObjectDisposedException, and they are what a real call site does next - every
        // awaited call in this product registers on the token it was handed.
        var registered = false;

        using (token.Register(() => registered = true))
        {
            // Registering on an already-cancelled token runs the callback there and then, so this
            // also proves the registration was honoured rather than merely accepted.
            Assert.True(registered);
        }

        Assert.NotNull(token.WaitHandle);
        Assert.True(token.WaitHandle.WaitOne(0));
    }

    [Fact]
    public async Task The_capture_panel_is_cancelled_even_though_it_is_disposed_asynchronously()
    {
        // ProofCapture is the one screen the renderer disposes through IAsyncDisposable, and the
        // renderer never also calls IDisposable.Dispose - so the base's cancellation is reachable
        // only because DisposeAsync invokes it by hand. Deleting that one line breaks exactly this
        // screen and nothing else, which is why it is asserted on its own as well as in the sweep.
        var panel = new Web.Components.Pages.ProofCapture();

        Assert.False(panel.CircuitToken.IsCancellationRequested);

        // The JS module was never loaded, which is the state static rendering leaves it in and the
        // state a panel disposed before first render is in.
        await panel.DisposeAsync();

        Assert.True(panel.CircuitToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_proof_read_cancelled_mid_flight_does_not_escape_the_renderer()
    {
        // Navigating away mid-load, from the renderer's side: the capability raises
        // OperationCanceledException where a real one raises it, and the render has to finish.
        // ComponentBase discards a lifecycle task that ended cancelled - this asserts the screen
        // does nothing to stop it, which is what a catch-all or a disposed token would.
        var html = await ComponentRenderer.RenderAsync<Web.Components.Pages.ProofView>(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["DeliveryId"] = 1,
            },
            services => services.AddSingleton<IProofOfDeliveryService>(new CancellingProofService()));

        // Nothing reported: a cancelled read is not a failure the viewer has to be told about.
        Assert.DoesNotContain("alert-danger", html, StringComparison.Ordinal);

        // And nothing rendered from a proof that never arrived.
        Assert.DoesNotContain("dt-proof-facts", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_screen_hands_its_capability_the_token_its_teardown_cancels()
    {
        // The claim the rest of this suite cannot make. Everything above is about the base's own
        // behaviour and the shape of the type tree, and all of it stays green if a screen derives
        // from CircuitScopedComponent and then passes CancellationToken.None at the call site -
        // which is the whole defect back, wearing the fix's clothes.
        //
        // So this composes the two halves through a real render: what the capability was actually
        // handed, and what teardown does to it afterwards. It is one screen rather than fourteen
        // because a token that survives a round trip through the renderer is a property of the base,
        // not of ProofView; the type-level sweep is what generalises it.
        var capability = new RecordingProofService();

        await ComponentRenderer.RenderAsync<Web.Components.Pages.ProofView>(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["DeliveryId"] = 1,
            },
            services => services.AddSingleton<IProofOfDeliveryService>(capability));

        var handed = Assert.Single(capability.Handed);

        // CancellationToken.None and default(CancellationToken) both answer false here, so this
        // alone fails for a call site that passes either.
        Assert.True(handed.CanBeCanceled);

        // ComponentRenderer disposes its renderer before RenderAsync returns, and the renderer
        // disposes the components it built - so by this line the screen has been torn down exactly
        // as navigating away tears it down. A token that reached the capability from anywhere but
        // this screen's own source would still be uncancelled.
        Assert.True(handed.IsCancellationRequested);
    }

    [Fact]
    public async Task A_capture_cancelled_by_closing_the_panel_is_not_reported()
    {
        // The rule the capture panel's blanket catch is filtered on, asserted directly because the
        // arm itself is out of reach: static rendering dispatches no events, so nothing here can
        // press Submit. This is the same move ProofScreenTests makes for the predicate that decides
        // who is offered capture - the decision is lifted to where a test can reach it, and the
        // catch is wired to that decision and nothing else.
        var panel = new Web.Components.Pages.ProofCapture();

        await panel.DisposeAsync();

        // Closing the panel is what cancelled the token, so the exception it produces is this
        // screen's own teardown: reported to nobody, because the panel is already gone.
        Assert.False(panel.Reports(new OperationCanceledException()));

        // TaskCanceledException is what an awaited call actually raises, and it is an
        // OperationCanceledException - so the arm must not be spelled against the derived type.
        Assert.False(panel.Reports(new TaskCanceledException()));
    }

    [Fact]
    public void A_capture_that_really_failed_is_still_reported()
    {
        // The other half, and the one that keeps the fix honest: AD-26's whole reason for the
        // blanket arm is an unreachable or unconfigured object store, and narrowing it must not
        // have cost that. The panel is alive here, which is the state a real capture runs in.
        var panel = new Web.Components.Pages.ProofCapture();

        Assert.True(panel.Reports(new InvalidOperationException("The object store is not configured.")));

        // Including a cancellation that is not this screen's. An outbound port that gave up on its
        // own raises the same type, and the driver has to be told the capture did not come off -
        // which is not the same as "nothing happened". Since the bytes are written after the row
        // commits, a store that failed may have left a committed proof whose assets are missing, and
        // the panel says only that the capture failed for exactly that reason.
        Assert.True(panel.Reports(new OperationCanceledException()));
    }

    [Fact]
    public void No_blanket_catch_in_the_component_tree_reports_a_cancellation()
    {
        // A source scan rather than a render, for the same reason ProofScreenTests lifts the
        // capture panel's decisions out: static rendering dispatches no events, so the submit path
        // that owns the only `catch (Exception` in the tree is unreachable from a test. The
        // invariant worth keeping is cross-screen anyway - "no blanket catch anywhere under
        // Components/ turns a cancellation into a reported failure" - not a fact about one file.
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ComponentsDirectory, "*.*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                         || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            // Comments stripped first, the way SeverabilityTests does it: this file's own prose
            // discusses `catch (Exception` at length, and an explanation of a rule is not a breach
            // of it. Line structure is preserved through the strip so a report still names the line
            // a reader can open.
            var lines = WithoutComments(File.ReadAllText(file)).Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];

                // Every spelling of "catches everything": the bare `catch`, the unqualified type and
                // the fully qualified one. Narrowing to one of them is how this guard would go
                // quietly blind - and the bare one has to be spelled as "catch not followed by a
                // paren" rather than "catch {", because this codebase opens its braces on the next
                // line, so `catch {` never appears and a pattern written for it sees nothing.
                if (!Regex.IsMatch(
                        line,
                        @"\bcatch\b(?!\s*\()|\bcatch\s*\(\s*(System\.)?Exception\b",
                        RegexOptions.None,
                        TimeSpan.FromSeconds(5)))
                {
                    continue;
                }

                // The exemption has to be on the clause itself: a filter is the only thing that
                // lets the exception keep travelling, and an arm that caught it and rethrew would
                // already have lost the stack the framework discards it by. It has to be the shared
                // rule, not a spelling of its own - a screen that inlined its own version would
                // drift from CircuitScopedComponent.Reports the first time the rule changed - and it
                // has to be the rule the right way up: `when (!Reports(failure))` is the shipped
                // behaviour inverted, and matching on the words alone would call that exempt.
                if (Regex.IsMatch(
                        line,
                        @"when\s*\(\s*Reports\s*\(",
                        RegexOptions.None,
                        TimeSpan.FromSeconds(5)))
                {
                    continue;
                }

                offenders.Add($"{Path.GetRelativePath(ComponentsDirectory, file)}:{index + 1}");
            }
        }

        // A clause split over two lines - the `catch` on one and its `when` on the next - lands here
        // rather than passing unseen, which is the intended failure: this guard reads one line at a
        // time, and the fix is to put the clause on one line, not to teach the scan to span them.
        Assert.True(
            offenders.Count == 0,
            "A catch under Components/ swallows everything without carrying `when (Reports(...))` "
                + "on the same line: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The source with its <c>//</c>, <c>/* */</c> and Razor <c>@* *@</c> comments removed, and
    /// with every line break they spanned kept, so a line number still means something afterwards.
    /// <para>
    /// Lexically unaware of quoting, deliberately: a <c>//</c> inside a string literal is cut too,
    /// so <c>"http://example"</c> survives as <c>"http:</c>. That is harmless for the guards here,
    /// which look for <c>catch</c> clauses and token arguments rather than URLs, but a guard that
    /// wanted to read a literal back would need a real lexer instead of this.
    /// </para>
    /// </summary>
    private static string WithoutComments(string source)
    {
        source = Regex.Replace(
            source, @"@\*.*?\*@", Blank, RegexOptions.Singleline, TimeSpan.FromSeconds(5));

        source = Regex.Replace(
            source, @"/\*.*?\*/", Blank, RegexOptions.Singleline, TimeSpan.FromSeconds(5));

        return Regex.Replace(
            source, @"//[^\r\n]*", " ", RegexOptions.None, TimeSpan.FromSeconds(5));

        static string Blank(Match comment) =>
            new('\n', comment.Value.Count(character => character == '\n'));
    }

    public static TheoryData<string> ScreenNames()
    {
        var data = new TheoryData<string>();

        foreach (var name in ExpectedScreens)
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>
    /// Every screen in the component tree as written: <c>.razor</c> only, because a screen is a
    /// Razor file and the base class itself is not one.
    /// </summary>
    private static IEnumerable<string> ScreenSources() =>
        Directory.EnumerateFiles(ComponentsDirectory, "*.razor", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal);

    private static IEnumerable<Type> Screens() =>
        typeof(CircuitScopedComponent).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(CircuitScopedComponent).IsAssignableFrom(type));

    private static Type Screen(string name) =>
        typeof(CircuitScopedComponent).Assembly.GetType(name, throwOnError: true)!;

    /// <summary>
    /// Disposal as the renderer performs it: a component that is <see cref="IAsyncDisposable"/> is
    /// disposed that way and never also through <see cref="IDisposable"/>, which is the whole
    /// reason the capture panel has to reach the base itself.
    /// </summary>
    private static async Task DisposeAsRendererWouldAsync(CircuitScopedComponent screen)
    {
        if (screen is IAsyncDisposable asynchronous)
        {
            await asynchronous.DisposeAsync();

            return;
        }

        screen.Dispose();
    }

    /// <summary>
    /// A capability whose read is cancelled, which is what navigating away mid-load produces: the
    /// command is abandoned where it stands and the caller sees OperationCanceledException.
    /// </summary>
    private sealed class CancellingProofService : IProofOfDeliveryService
    {
        public Task<ProofOfDeliveryView> CaptureAsync(
            int deliveryId,
            CaptureProofCommand command,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException();

        public Task<ProofOfDeliveryView> GetAsync(int deliveryId, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();

        public Task<ProofAssetContent> OpenAssetAsync(int assetId, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }

    /// <summary>
    /// A capability that answers normally and keeps the token it was handed, so the test can ask
    /// the one question a rendered screen otherwise swallows: <em>which</em> token was that?
    /// </summary>
    private sealed class RecordingProofService : IProofOfDeliveryService
    {
        private readonly List<CancellationToken> _handed = [];

        public IReadOnlyList<CancellationToken> Handed => _handed;

        public Task<ProofOfDeliveryView> CaptureAsync(
            int deliveryId,
            CaptureProofCommand command,
            CancellationToken cancellationToken) =>
            Record(cancellationToken);

        public Task<ProofOfDeliveryView> GetAsync(int deliveryId, CancellationToken cancellationToken) =>
            Record(cancellationToken);

        public Task<ProofAssetContent> OpenAssetAsync(int assetId, CancellationToken cancellationToken)
        {
            _handed.Add(cancellationToken);

            return Task.FromResult(new ProofAssetContent("image/png", Stream.Null));
        }

        private Task<ProofOfDeliveryView> Record(CancellationToken cancellationToken)
        {
            _handed.Add(cancellationToken);

            // A real proof, because the screen has to get far enough to render: a stub that threw
            // would leave the token recorded but the render half untested.
            return Task.FromResult(new ProofOfDeliveryView(
                1,
                "Олена Петренко",
                new LocationView(new MapLocation(50.4501, 30.5234), null),
                new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero),
                null,
                []));
        }
    }
}
