using System.Text.RegularExpressions;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Components.Shared;
using Microsoft.AspNetCore.Components;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// FR-81 / FR-85 / NFR-19: the shared dialog and the confirmation built on it.
/// <para>
/// Two halves, because the guarantees live in two places. What the markup promises — the modal
/// role, the label wired to the heading, the destructive confirm beside a distinct cancel — is
/// asserted against real rendered HTML through <see cref="ComponentRenderer"/>. What only a browser
/// can produce — Escape, a click on the backdrop — is asserted one level down, against the listener
/// in the component's own module, because no harness here can press a key.
/// </para>
/// <para>
/// Focus trapping and focus restoration are asserted by neither: they are the native modal
/// <c>&lt;dialog&gt;</c> element's contract rather than ours, and the only thing this suite can
/// honestly check is that the component opens with <c>showModal()</c> and never with the
/// <c>open</c> attribute, which is what buys them.
/// </para>
/// </summary>
public class DialogTests
{
    [Fact]
    public async Task The_dialog_is_a_modal_dialog_labelled_by_its_own_title()
    {
        var html = await ComponentRenderer.RenderAsync<DtDialog>(new Dictionary<string, object?>
        {
            ["Title"] = "Підтвердження дії",
            ["ChildContent"] = (RenderFragment)(builder => builder.AddContent(0, "Тіло діалогу")),
        });

        Assert.Contains("<dialog", html, StringComparison.Ordinal);
        Assert.Contains(@"role=""dialog""", html, StringComparison.Ordinal);
        Assert.Contains(@"aria-modal=""true""", html, StringComparison.Ordinal);
        Assert.Contains("Тіло діалогу", html, StringComparison.Ordinal);

        // The label has to point at THIS dialog's heading, so the id is read out of the rendered
        // attribute and looked for on the rendered element rather than assumed.
        var labelledBy = Regex.Match(
            html,
            @"aria-labelledby=""(?<id>[^""]+)""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(labelledBy.Success, $"The dialog carries no aria-labelledby:{Environment.NewLine}{html}");

        var id = labelledBy.Groups["id"].Value;
        var title = Regex.Match(
            html,
            $@"<h2[^>]*\bid=""{Regex.Escape(id)}""[^>]*>(?<text>.*?)</h2>",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        Assert.True(
            title.Success,
            $"aria-labelledby names '{id}', but no heading carries that id:{Environment.NewLine}{html}");

        Assert.Equal("Підтвердження дії", SharedMarkup.TextOf(title.Groups["text"].Value));
    }

    [Fact]
    public async Task The_dialog_is_never_rendered_already_open()
    {
        // NFR-19's focus guarantees come from showModal(). A dialog rendered with the `open`
        // attribute is displayed non-modally: no focus trap, no backdrop, no Escape - and it looks
        // right on screen, which is why this is asserted rather than reviewed.
        var html = await ComponentRenderer.RenderAsync<DtDialog>(new Dictionary<string, object?>
        {
            ["Title"] = "Заголовок",
        });

        var dialogTag = Regex.Match(html, "<dialog[^>]*>", RegexOptions.None, TimeSpan.FromSeconds(5));

        Assert.True(dialogTag.Success);
        Assert.DoesNotContain("open", dialogTag.Value, StringComparison.Ordinal);

        var module = SharedMarkup.ReadShared("DtDialog.razor.js");

        Assert.Contains("element.showModal();", module, StringComparison.Ordinal);
    }

    [Fact]
    public void The_module_reports_every_dismissal_to_dotnet()
    {
        // Escape, the backdrop and the close action all raise the element's own `close` event, so
        // one listener is the whole notification path. Reading it here is the only way to assert it
        // without a browser.
        var module = SharedMarkup.ReadShared("DtDialog.razor.js");

        Assert.Contains(@"addEventListener(""close""", module, StringComparison.Ordinal);
        Assert.Contains(@"invokeMethodAsync(""NotifyClosedAsync"")", module, StringComparison.Ordinal);

        // And the component has to offer the method the module names, or the call is a run-time
        // error nobody sees until a dialog is dismissed.
        Assert.Contains(
            "public Task NotifyClosedAsync()",
            SharedMarkup.ReadShared("DtDialog.razor"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_click_closes_the_dialog_only_when_it_lands_on_the_backdrop()
    {
        // The backdrop is not a child element - a click on it reports the dialog itself as the
        // target - so the identity check IS the guard. Without it every click inside the panel
        // would close the dialog, including the one on the confirm button.
        var module = SharedMarkup.ReadShared("DtDialog.razor.js");

        const string Guard = "event.target === element";
        const string Close = "element.close();";

        var guard = module.IndexOf(Guard, StringComparison.Ordinal);

        Assert.True(guard >= 0, "The click listener does not compare the event target to the dialog.");

        var close = module.IndexOf(Close, guard, StringComparison.Ordinal);

        Assert.True(close > guard, "Nothing closes the dialog after the backdrop guard.");

        // Nothing but the block opener may sit between the two, or the close is not actually
        // governed by the guard - it merely follows it in the file.
        var between = module[(guard + Guard.Length)..close];

        Assert.DoesNotContain(";", between, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_confirmation_offers_a_destructive_confirm_and_a_distinct_cancel()
    {
        var html = await RenderConfirmationAsync();

        // Inside the shared dialog rather than beside it: the confirmation must not be a second
        // dialog implementation with its own idea of what modal means.
        Assert.Contains("<dialog", html, StringComparison.Ordinal);
        Assert.Contains("Цю дію не можна скасувати.", html, StringComparison.Ordinal);

        var confirm = SharedMarkup.ElementWithClass(html, "button", "dt-confirm-accept");
        var cancel = SharedMarkup.ElementWithClass(html, "button", "dt-confirm-cancel");

        // NFR-23: destructive actions are red, and the themed Bootstrap variant is where that
        // colour comes from.
        Assert.Contains("btn-danger", html, StringComparison.Ordinal);

        // NFR-24: an icon AND text. Either on its own is a guess.
        Assert.Contains("<svg", confirm, StringComparison.Ordinal);

        var confirmText = SharedMarkup.TextOf(confirm);
        var cancelText = SharedMarkup.TextOf(cancel);

        Assert.True(SharedMarkup.IsUkrainian(confirmText), $"Confirm reads '{confirmText}'.");
        Assert.False(SharedMarkup.HasLatinWord(confirmText), $"Confirm reads '{confirmText}'.");
        Assert.True(SharedMarkup.IsUkrainian(cancelText), $"Cancel reads '{cancelText}'.");
        Assert.False(SharedMarkup.HasLatinWord(cancelText), $"Cancel reads '{cancelText}'.");

        // Distinct, not two spellings of the same action.
        Assert.NotEqual(confirmText, cancelText, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("dt-confirm-accept", "ConfirmAsync")]
    [InlineData("dt-confirm-cancel", "CancelAsync")]
    public void Each_action_is_bound_to_the_handler_that_matches_its_label(string className, string handler)
    {
        // Which button calls which handler, not merely that both handlers exist and behave. Swap
        // the two bindings and cancel deletes while confirm does nothing - and every other
        // assertion in this class, which reads the method bodies rather than the markup that
        // reaches them, keeps passing.
        var source = SharedMarkup.ReadShared("DtConfirmDialog.razor");

        var button = Regex.Match(
            source,
            $"<button[^>]*{Regex.Escape(className)}[^>]*>",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(button.Success, $"DtConfirmDialog has no button carrying '{className}'.");
        Assert.Contains($"@onclick=\"{handler}\"", button.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_confirmation_offers_one_way_to_decline_rather_than_two()
    {
        // The shared dialog renders a close action of its own only when the screen supplied none.
        // A confirmation supplies cancel, so a second dismissing button beside it would be exactly
        // the ambiguity the component exists to remove.
        var confirmation = await RenderConfirmationAsync();

        Assert.DoesNotContain("dt-dialog-close", confirmation, StringComparison.Ordinal);

        // And the built-in action is still there for a dialog that brings no actions of its own.
        var plain = await ComponentRenderer.RenderAsync<DtDialog>(new Dictionary<string, object?>
        {
            ["Title"] = "Заголовок",
        });

        Assert.Contains("dt-dialog-close", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancelling_never_reaches_the_confirm_callback()
    {
        var source = SharedMarkup.ReadShared("DtConfirmDialog.razor");

        // One call site, and it is the confirm path. A second one - a stray invocation on close,
        // say - is exactly the defect this guards, and it would look harmless in review.
        Assert.Equal(1, SharedMarkup.Occurrences(source, "OnConfirmed.InvokeAsync()"));

        var confirmMethod = source.IndexOf("private async Task ConfirmAsync()", StringComparison.Ordinal);
        var invocation = source.IndexOf("OnConfirmed.InvokeAsync()", StringComparison.Ordinal);

        Assert.True(confirmMethod >= 0, "DtConfirmDialog has no ConfirmAsync.");
        Assert.True(invocation > confirmMethod, "The confirm callback is raised outside ConfirmAsync.");

        // The cancel action closes and nothing else.
        Assert.Contains("private Task CancelAsync() => CloseAsync();", source, StringComparison.Ordinal);

        // Once per confirmation: the latch, and the reset that lets the same prompt be used again.
        Assert.Contains("if (_confirmed)", source, StringComparison.Ordinal);
        Assert.Contains("_confirmed = false;", source, StringComparison.Ordinal);
    }

    private static Task<string> RenderConfirmationAsync() =>
        ComponentRenderer.RenderAsync<DtConfirmDialog>(new Dictionary<string, object?>
        {
            ["Title"] = "Видалити запис?",
            ["Message"] = "Цю дію не можна скасувати.",
        });
}
