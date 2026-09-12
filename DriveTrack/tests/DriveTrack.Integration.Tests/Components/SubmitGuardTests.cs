using System.Text.RegularExpressions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Users;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// The in-flight guard on every submit control in the product (DW-15, DW-31, DW-37).
/// <para>
/// A Blazor Server handler that awaits its service yields the circuit at that await, and the control
/// that started it stays live: a second click dispatches a second write. Nothing downstream
/// deduplicates one — a delivery carries no natural key the way a vehicle carries a licence plate —
/// so the second click is a second Pending delivery, a second dispatcher account, or, on the
/// password form, a change that succeeded reported back as <c>AUTH_CURRENT_PASSWORD_INCORRECT</c>
/// because the second attempt sent the now-stale current password.
/// </para>
/// <para>
/// Asserted as source text, which is what <see cref="ChatScreenTests"/> already does for this guard
/// and the only thing that scales to the thirteen files the roster spans, whose render harnesses all
/// differ: a static render dispatches no events, so the second click is unreachable. That is
/// the honest limit of this class — it proves the guard is written, not that it is honoured at run
/// time. The re-entry check is the part that makes the guard real, so its presence is asserted
/// rather than its effect.
/// </para>
/// </summary>
public class SubmitGuardTests
{
    /// <summary>
    /// The one row whose guard is markup-only: the capture panel has no re-entry check in C#,
    /// because its <c>disabled</c> is a composite (<c>@(_saving || _location is null)</c>) whose
    /// second condition means the control is already disabled before the first click. Exempt from
    /// the re-entry assertion for that reason, and from nothing else.
    /// <para>
    /// Composite bindings are why every row is matched on "the binding mentions the flag" rather
    /// than on equality — the timeline's <c>@(_addingNote || _loading)</c> is one too. This constant
    /// marks the missing C# check, not the shape of the attribute.
    /// </para>
    /// </summary>
    private const string MarkupOnlyGuard = "Pages/ProofCapture.razor";

    /// <summary>
    /// Every submit control in the product, the handler behind it and the flag that disables it: the
    /// fourteen handlers the sweep added a guard to, plus the two that already shipped one and are
    /// the shape the rest were copied from.
    /// <para>
    /// The handler is named as well as the flag, and that is load-bearing rather than descriptive.
    /// Read file-wide, every assertion below is satisfied by a screen that guards the wrong handler:
    /// the profile screen has three flags and three saves, and "somewhere in this file a flag is
    /// read, set and cleared" is true of it whichever save each one actually belongs to.
    /// </para>
    /// <para>
    /// Named rather than discovered, because a control wired through <c>@onclick</c> cannot be told
    /// from any other button by shape — that is exactly why
    /// <see cref="No_button_that_submits_a_form_is_left_without_a_disabled_binding"/> exists beside
    /// this one, closing the half of the surface that <em>is</em> shaped like a submit.
    /// </para>
    /// </summary>
    private static readonly (string File, string Handler, string Flag, string Marker)[] Roster =
    [
        ("Pages/Chat.razor", "SendAsync", "_sending", "dt-chat-send"),
        (MarkupOnlyGuard, "CaptureAsync", "_saving", "dt-proof-submit"),
        ("Pages/Deliveries.razor", "SaveAsync", "_saving", @"form=""delivery-form"""),
        ("Pages/Vehicles.razor", "SaveAsync", "_saving", @"form=""vehicle-form"""),
        ("Pages/Drivers.razor", "SaveAsync", "_saving", @"form=""driver-form"""),
        ("Account/Profile.razor", "SaveProfileAsync", "_savingProfile", "dt-profile-save"),
        ("Account/Profile.razor", "SavePasswordAsync", "_savingPassword", "dt-password-save"),
        ("Account/Profile.razor", "SaveEmailAsync", "_savingEmail", "dt-email-save"),
        ("Pages/MyDeliveries.razor", "SubmitRequestAsync", "_saving", "dt-request-submit"),
        ("Pages/Reviews.razor", "SaveAsync", "_saving", "dt-review-save"),
        ("Pages/Shifts.razor", "SaveAsync", "_saving", "dt-shift-save"),
        ("Pages/MyShifts.razor", "SaveAsync", "_saving", "dt-shift-save"),
        ("Pages/Admin/Clients.razor", "SaveAsync", "_saving", "dt-client-save"),
        ("Pages/Admin/Dispatchers.razor", "CreateAsync", "_creating", "dt-dispatcher-create"),
        ("Pages/Admin/Dispatchers.razor", "SaveAsync", "_saving", "dt-dispatcher-save"),
        ("Pages/DeliveryTimeline.razor", "AddNoteAsync", "_addingNote", "dt-timeline-add-note"),
    ];

    /// <summary>
    /// The two account forms carry a <c>&lt;button type="submit"&gt;</c> and are still outside the
    /// rule, for the reason <see cref="CircuitCancellationTests"/> already records about them: they
    /// are marked <c>[ExcludeFromInteractiveRouting]</c> and render statically so the form POST can
    /// reach <c>HttpContext.SignInAsync</c> (AD-14). There is no circuit behind them to re-render a
    /// flag, so a <c>disabled</c> binding there could never become true and would be a guard in name
    /// only. The sign-out control is excluded on the same ground from the other direction: it sits in
    /// a plain <c>&lt;form method="post"&gt;</c> posting to an endpoint, and has no C# handler at all.
    /// <para>
    /// Paths relative to <c>Components/</c>, forward-slashed, and checked rather than merely allowed:
    /// a file that was renamed or converted to interactive rendering would otherwise leave a licence
    /// behind that excuses a screen nobody meant to excuse.
    /// </para>
    /// </summary>
    private static readonly string[] PostedOverHttpRatherThanACircuit =
    [
        "Account/Register.razor",
        "Account/SignIn.razor",
        "Layout/NavMenu.razor",
    ];

    public static TheoryData<string, string, string, string> Controls()
    {
        var data = new TheoryData<string, string, string, string>();

        foreach (var (file, handler, flag, marker) in Roster)
        {
            data.Add(file, handler, flag, marker);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Controls))]
    public void A_submit_control_is_disabled_by_a_flag_its_handler_sets_and_always_clears(
        string file,
        string handler,
        string flag,
        string marker)
    {
        var source = SharedMarkup.ReadComponent(file.Split('/'));
        var code = WithoutComments(source);

        // Declared, and declared without an initializer: a flag born true would disable its control
        // on the first render and never let the first click through at all.
        Assert.Contains($"private bool {flag};", code, StringComparison.Ordinal);

        // Everything below reads this handler's own body rather than the file. File-wide, all three
        // claims are satisfied by a screen that guards a different handler with this flag - which is
        // not a hypothetical on the two screens carrying more than one.
        var body = HandlerBody(code, handler);

        Assert.True(body is not null, $"{file} declares no handler named {handler}.");

        // The scenario the matrix calls "second click while in flight": the handler reads the flag
        // and returns before it reaches the write. The `disabled` attribute alone is not the guard -
        // it is a hint to the browser, and a click already dispatched, a keyboard activation, or a
        // second circuit reaches the handler whatever the button looked like.
        //
        // Un-negated, and with the return inside it: `if (!_saving) { return; }` reads the same flag
        // in the same shape and lets exactly the second click through while stopping the first.
        var check = ReturnsEarlyOn(body!, flag);

        if (!string.Equals(file, MarkupOnlyGuard, StringComparison.Ordinal))
        {
            Assert.True(
                check >= 0,
                $"{handler} in {file} has no `if ({flag})` that returns, so a second click is not "
                    + "stopped.");
        }

        // The scenario the matrix calls "first click": set before the first await, so the render
        // that ComponentBase performs between the handler returning to its await and the server
        // answering already carries the disabled control. Set after it, the flag is true only once
        // the write it was meant to cover has already been dispatched.
        var set = body!.IndexOf($"{flag} = true;", StringComparison.Ordinal);

        Assert.True(set >= 0, $"{handler} in {file} never sets {flag}.");

        // And the check reads the flag before the handler sets it. Written the other way round -
        // `_saving = true;` and then `if (_saving) { return; }` - every other assertion here still
        // holds, while the first click returns before its `try` is ever entered: the `finally` never
        // runs, the flag stays true for the life of the circuit, and the form is dead from the first
        // press. The two positions are what make this a guard rather than two unrelated statements.
        Assert.True(
            check < 0 || check < set,
            $"{handler} in {file} sets {flag} before it checks it, so the first click returns "
                + "without writing and leaves the control disabled for good.");

        var awaited = Regex.Match(body, @"\bawait\b", RegexOptions.None, TimeSpan.FromSeconds(5));

        Assert.True(
            !awaited.Success || set < awaited.Index,
            $"{handler} in {file} sets {flag} after its first await, so the circuit has already "
                + "yielded with the control still live.");

        // "Service refuses" and "success" are the same assertion: the reset is in a `finally`, so the
        // `return` inside `catch (DriveTrackException)` still runs it and the control comes back on a
        // dialog that is still open. A reset written after the try would leave a refused save dead.
        Assert.True(
            FinallyBodies(body).Any(block =>
                block.Contains($"{flag} = false;", StringComparison.Ordinal)),
            $"{handler} in {file} clears {flag} outside a finally, so a refused write leaves its "
                + "control dead.");

        // And the control itself: the opening tag the marker identifies carries `disabled` bound to
        // that flag. Read from the markup with its comments stripped, so a comment naming the class
        // cannot stand in for the attribute.
        var button = Regex.Match(
            code,
            @"<button\b[^>]*" + Regex.Escape(marker) + @"[^>]*>",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(button.Success, $"{file} has no <button> carrying '{marker}'.");

        // "Mentions the flag" rather than equals `@{flag}`, because the capture panel's binding is a
        // composite. A binding rather than a literal: `disabled="disabled"` would satisfy a plain
        // contains-check while disabling the control forever.
        Assert.True(
            Regex.IsMatch(
                button.Value,
                @"disabled\s*=\s*""@[^""]*\b" + Regex.Escape(flag) + @"\b",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)),
            $"The '{marker}' control in {file} is not disabled by {flag}: {button.Value}");
    }

    [Fact]
    public void No_button_that_submits_a_form_is_left_without_a_disabled_binding()
    {
        // The roster above is a list, and a list is a thing a new screen is added without. This is
        // the half of the surface that needs no list: a `type="submit"` button is submit-shaped, so
        // a form screen written tomorrow that awaits its service behind an unguarded one fails here
        // without anyone remembering to come back and add a row.
        //
        // No per-file exclusions. The three controls that post over HTTP rather than over a circuit
        // are recognised by what they are - a statically rendered page, or a plain method="post"
        // form - and are checked below rather than waved through.
        var offenders = new List<string>();
        var exemptionsSeen = new List<string>();

        foreach (var file in Directory
                     .EnumerateFiles(SharedMarkup.ComponentsDirectory, "*.razor", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = Path
                .GetRelativePath(SharedMarkup.ComponentsDirectory, file)
                .Replace('\\', '/');

            var code = WithoutComments(File.ReadAllText(file));
            var isStatic = code.Contains("[ExcludeFromInteractiveRouting]", StringComparison.Ordinal);

            foreach (Match button in Regex.Matches(
                         code,
                         @"<button\b[^>]*>",
                         RegexOptions.None,
                         TimeSpan.FromSeconds(5)))
            {
                if (!button.Value.Contains(@"type=""submit""", StringComparison.Ordinal))
                {
                    continue;
                }

                // The exemption is recorded before the binding is looked for, not after. Ordered the
                // other way, adding a binding to one of these three - a harmless change, and a safer
                // one - would drop it from the set below and fail a test for getting better.
                //
                // A page with no circuit renders once per request and never again, so no field of
                // its own could ever flip an attribute on it; a button inside a plain posting form
                // has no C# handler to guard in the first place. Both post over HTTP, where the
                // browser's own navigation is what ends the submission.
                if (isStatic || PostsOverHttp(code, button.Index))
                {
                    exemptionsSeen.Add(relative);

                    continue;
                }

                // A binding, not the word. `disabled="disabled"` contains it and is a control that
                // never comes back; `class="btn disabled"` contains it and is a colour. Both would
                // satisfy the tripwire that exists to catch tomorrow's unguarded screen.
                if (Regex.IsMatch(
                        button.Value,
                        @"disabled\s*=\s*""@",
                        RegexOptions.None,
                        TimeSpan.FromSeconds(5)))
                {
                    continue;
                }

                offenders.Add(relative);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A <button type=\"submit\"> under Components/ carries no disabled binding, so a second "
                + "click dispatches a second write: " + string.Join(", ", offenders.Distinct()));

        Assert.Equal(
            PostedOverHttpRatherThanACircuit.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            exemptionsSeen.Distinct().OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_guarded_control_renders_with_no_disabled_attribute_before_anything_is_in_flight()
    {
        // The claim every assertion above rests on and none of them can see: that `disabled="@_flag"`
        // is a binding rather than a hardcoded attribute, so an idle screen renders a button a user
        // can press. Blazor omits a false boolean attribute entirely, which is a framework behaviour
        // rather than this product's decision - so it is pinned here once, against a real render,
        // instead of being assumed sixteen times.
        //
        // The dispatcher screen, because it is the one carrying two independent flags on two
        // dialogs: if a shared or inverted flag ever crept in, both controls would show it here.
        var html = await ComponentRenderer.RenderAsync<Web.Components.Pages.Admin.Dispatchers>(
            parameters: null,
            configureServices: services =>
            {
                services.AddSingleton<ICurrentUser>(new StubAdmin());
                services.AddSingleton<IDispatcherAdministrationService>(new StubDispatchers());
            });

        foreach (var marker in new[] { "dt-dispatcher-create", "dt-dispatcher-save" })
        {
            var button = Regex.Match(
                html,
                @"<button\b[^>]*" + marker + @"[^>]*>",
                RegexOptions.None,
                TimeSpan.FromSeconds(5));

            Assert.True(button.Success, $"The rendered screen has no <button> carrying '{marker}'.");
            Assert.DoesNotContain("disabled", button.Value, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// True when the button at <paramref name="index"/> sits inside a plain <c>method="post"</c>
    /// form: an HTML form posting to an endpoint, which the browser submits by navigating away.
    /// </summary>
    /// <remarks>
    /// Decided by the nearest enclosing form opening tag rather than by the file, so a screen that
    /// held both a posting form and an interactive one would still be read one button at a time.
    /// </remarks>
    private static bool PostsOverHttp(string code, int index)
    {
        var opened = code.LastIndexOf("<form", index, StringComparison.Ordinal);

        if (opened < 0)
        {
            return false;
        }

        var closed = code.LastIndexOf("</form>", index, StringComparison.Ordinal);

        if (closed > opened)
        {
            return false;
        }

        var tag = code.IndexOf('>', opened);

        return tag > 0
            && code[opened..tag].Contains(@"method=""post""", StringComparison.Ordinal);
    }

    /// <summary>
    /// The body of the named handler, or null when the file declares no such method. Matched on the
    /// declaration rather than on the name alone, so a call to <c>Roster.CreateAsync</c> or an
    /// <c>@onclick="SaveAsync"</c> in markup is not mistaken for the method itself.
    /// </summary>
    private static string? HandlerBody(string code, string handler)
    {
        var declaration = Regex.Match(
            code,
            @"\bTask\s+" + Regex.Escape(handler) + @"\s*\(",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        if (!declaration.Success)
        {
            return null;
        }

        var open = code.IndexOf('{', declaration.Index);

        return open < 0 ? null : BlockAt(code, open);
    }

    /// <summary>
    /// Where <paramref name="body"/> tests <paramref name="flag"/> un-negated and returns inside
    /// that test — the re-entry check itself, rather than any mention of the flag in an <c>if</c> —
    /// or -1 when it does not. The position is returned rather than a bool because the caller has to
    /// place the check against the flag's assignment: a check that runs after the set guards nothing
    /// and stops the first click instead of the second.
    /// </summary>
    private static int ReturnsEarlyOn(string body, string flag)
    {
        foreach (Match test in Regex.Matches(
                     body, @"\bif\s*\(([^)]*)\)\s*\{", RegexOptions.None, TimeSpan.FromSeconds(5)))
        {
            // Negation is the mutation worth naming: `if (!_saving) { return; }` reads the same
            // field in the same shape, passes every other assertion, and inverts the guard - the
            // first click returns and the second one writes.
            if (!Regex.IsMatch(
                    test.Groups[1].Value,
                    @"(?<!!\s{0,8})\b" + Regex.Escape(flag) + @"\b",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(5)))
            {
                continue;
            }

            var block = BlockAt(body, body.IndexOf('{', test.Index + test.Length - 1));

            if (block is not null
                && Regex.IsMatch(block, @"\breturn\b", RegexOptions.None, TimeSpan.FromSeconds(5)))
            {
                return test.Index;
            }
        }

        return -1;
    }

    /// <summary>
    /// The body of every <c>finally</c> block in the source, brace-matched rather than pattern-cut:
    /// the capture panel's disposes a list of streams in a loop, so a pattern that stopped at the
    /// first closing brace would read that block as ending before its reset and report a guard that
    /// is plainly there as missing.
    /// </summary>
    private static IEnumerable<string> FinallyBodies(string code)
    {
        foreach (Match keyword in Regex.Matches(
                     code, @"\bfinally\b", RegexOptions.None, TimeSpan.FromSeconds(5)))
        {
            var open = code.IndexOf('{', keyword.Index);

            if (open >= 0 && BlockAt(code, open) is { } body)
            {
                yield return body;
            }
        }
    }

    /// <summary>
    /// What sits between the brace at <paramref name="open"/> and the one that closes it, or null
    /// when nothing does.
    /// </summary>
    /// <remarks>
    /// Counts braces without knowing about quoting, which is safe on this tree only because the
    /// comments are stripped first and no handler here holds a brace inside a string literal. A
    /// property pattern's <c>is { } value</c> is balanced, so it passes through without effect.
    /// </remarks>
    private static string? BlockAt(string code, int open)
    {
        if (open < 0 || open >= code.Length || code[open] != '{')
        {
            return null;
        }

        var depth = 0;

        for (var index = open; index < code.Length; index++)
        {
            if (code[index] == '{')
            {
                depth++;
            }

            if (code[index] != '}')
            {
                continue;
            }

            depth--;

            if (depth == 0)
            {
                return code[(open + 1)..index];
            }
        }

        return null;
    }

    /// <summary>
    /// The source with its <c>//</c>, <c>/* */</c> and Razor <c>@* *@</c> comments removed: this
    /// sweep's own screens explain their guards in prose, and an explanation of a rule must not be
    /// able to stand in for keeping it.
    /// </summary>
    /// <remarks>
    /// The line comment is matched only where it is not preceded by a colon, so a URL in markup is
    /// left alone. Without that, <c>Icon.razor</c>'s <c>xmlns="http://www.w3.org/2000/svg"</c> loses
    /// the rest of its line — including the tag's own <c>&gt;</c> — and the directory-wide scan below
    /// then reads that opening tag as running on to the next <c>&gt;</c> in the file, which is a
    /// silent widening rather than a loud failure.
    /// </remarks>
    private static string WithoutComments(string source)
    {
        source = Regex.Replace(
            source, @"@\*.*?\*@", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));

        source = Regex.Replace(
            source, @"/\*.*?\*/", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));

        return Regex.Replace(
            source, @"(?<!:)//[^\r\n]*", " ", RegexOptions.None, TimeSpan.FromSeconds(5));
    }

    /// <summary>A signed-in administrator, and nothing else the screen reads.</summary>
    private sealed class StubAdmin : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public UserId UserId => new(1);

        public UserRole Role => UserRole.Admin;

        public DriverId? DriverId => null;

        public ClientId? ClientId => null;
    }

    /// <summary>
    /// The roster, answered without a database. Every write throws: a static render never reaches
    /// one, so a screen that called it during rendering would fail loudly rather than pass quietly.
    /// </summary>
    private sealed class StubDispatchers : IDispatcherAdministrationService
    {
        private static readonly DispatcherAccount[] Accounts =
        [
            new(new UserId(21), "Ігор", "Ковальчук", "ihor@drivetrack.test"),
        ];

        public Task<DispatcherAccount> CreateAsync(
            CreateDispatcherCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task<IReadOnlyList<DispatcherAccount>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DispatcherAccount>>(Accounts);

        public Task<DispatcherAccount> GetAsync(UserId userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A rendered screen does not read one dispatcher.");

        public Task<DispatcherAccount> UpdateAsync(
            UserId userId,
            UpdateDispatcherCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task DeleteAsync(UserId userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");
    }
}
