using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Components.Shared;
using Microsoft.AspNetCore.Components;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// FR-82 / FR-84 / NFR-22: one table with three states, and a row on the page in every one of them.
/// <para>
/// The baseline rendered nothing while loading and nothing when there was nothing to show, which
/// reads to a user exactly like a screen that is broken. The three states are asserted against real
/// rendered HTML, including the null-items case: a screen whose query has not answered yet holds
/// null, and that has to be the empty state rather than an exception.
/// </para>
/// </summary>
public class DataTableTests
{
    private static readonly string[] ThreeRows = ["перший", "другий", "третій"];

    [Fact]
    public async Task Loading_shows_one_status_row_and_no_data()
    {
        var html = await RenderAsync(isLoading: true, items: ThreeRows);

        Assert.Equal(1, SharedMarkup.Occurrences(html, "dt-table-status"));
        Assert.Contains("spinner-border", html, StringComparison.Ordinal);
        Assert.Equal(0, SharedMarkup.Occurrences(html, "dt-table-empty"));
        Assert.Equal(0, SharedMarkup.Occurrences(html, "dt-table-row"));

        // Loading beats having rows: a table that showed stale rows under a spinner would be
        // showing two answers at once.
        Assert.DoesNotContain("перший", html, StringComparison.Ordinal);

        var status = SharedMarkup.TextOf(SharedMarkup.ElementWithClass(html, "tr", "dt-table-status"));

        Assert.True(SharedMarkup.IsUkrainian(status), $"The loading row reads '{status}'.");
        Assert.False(SharedMarkup.HasLatinWord(status), $"The loading row reads '{status}'.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Empty_shows_one_empty_state_row_whether_the_items_are_null_or_empty(bool isNull)
    {
        var html = await RenderAsync(isLoading: false, items: isNull ? null : []);

        Assert.Equal(1, SharedMarkup.Occurrences(html, "dt-table-empty"));
        Assert.Equal(0, SharedMarkup.Occurrences(html, "dt-table-status"));
        Assert.Equal(0, SharedMarkup.Occurrences(html, "dt-table-row"));
        Assert.DoesNotContain("spinner-border", html, StringComparison.Ordinal);

        var empty = SharedMarkup.TextOf(SharedMarkup.ElementWithClass(html, "tr", "dt-table-empty"));

        Assert.True(SharedMarkup.IsUkrainian(empty), $"The empty state reads '{empty}'.");
        Assert.False(SharedMarkup.HasLatinWord(empty), $"The empty state reads '{empty}'.");
    }

    [Fact]
    public async Task A_screen_may_say_what_is_empty_in_its_own_words()
    {
        var html = await RenderAsync(isLoading: false, items: [], emptyText: "Доставок ще немає.");

        Assert.Contains("Доставок ще немає.", html, StringComparison.Ordinal);
        Assert.Equal(1, SharedMarkup.Occurrences(html, "dt-table-empty"));
    }

    [Fact]
    public async Task Rows_are_rendered_one_per_item_and_neither_status_row_appears()
    {
        var html = await RenderAsync(isLoading: false, items: ThreeRows);

        Assert.Equal(ThreeRows.Length, SharedMarkup.Occurrences(html, "dt-table-row"));
        Assert.Equal(0, SharedMarkup.Occurrences(html, "dt-table-status"));
        Assert.Equal(0, SharedMarkup.Occurrences(html, "dt-table-empty"));

        foreach (var item in ThreeRows)
        {
            Assert.Contains(item, html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_table_is_striped_hovered_and_wrapped_in_its_own_scroll_container()
    {
        // NFR-22, and AD-28: striping and hover come from the themed Bootstrap build rather than
        // from a rule of our own, so the table already wears the palette.
        var html = await RenderAsync(isLoading: false, items: ThreeRows);

        Assert.Contains("dt-table-scroll", html, StringComparison.Ordinal);
        Assert.Contains("table-striped", html, StringComparison.Ordinal);
        Assert.Contains("table-hover", html, StringComparison.Ordinal);
        Assert.Contains("<thead", html, StringComparison.Ordinal);
        Assert.Contains("Назва", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_sticks_inside_the_scroll_container_and_paints_over_the_rows()
    {
        // A sticky header needs three things and fails silently without any of them: a scroll
        // container to stick inside, the stick itself, and an opaque background - a transparent
        // header shows the rows scrolling through the column names.
        var css = SharedMarkup.ReadShared("DtDataTable.razor.css");

        Assert.Contains("overflow: auto", css, StringComparison.Ordinal);
        Assert.Contains("position: sticky", css, StringComparison.Ordinal);
        Assert.Contains("top: 0", css, StringComparison.Ordinal);
        Assert.Contains("background-color: var(--dt-surface-card)", css, StringComparison.Ordinal);
        Assert.Contains("var(--dt-border)", css, StringComparison.Ordinal);

        // ::deep, because the header cells come from the consumer's template and carry the
        // consumer's scope. Without it the selector compiles to one that matches nothing.
        Assert.Contains("::deep th", css, StringComparison.Ordinal);
    }

    private static Task<string> RenderAsync(
        bool isLoading,
        IReadOnlyCollection<string>? items,
        string? emptyText = null) =>
        ComponentRenderer.RenderAsync<DtDataTable<string>>(new Dictionary<string, object?>
        {
            ["Items"] = items,
            ["IsLoading"] = isLoading,
            ["EmptyText"] = emptyText,
            ["HeaderTemplate"] = (RenderFragment)(builder =>
            {
                builder.OpenElement(0, "th");
                builder.AddContent(1, "Назва");
                builder.CloseElement();
            }),
            ["RowTemplate"] = (RenderFragment<string>)(item => builder =>
            {
                builder.OpenElement(0, "td");
                builder.AddContent(1, item);
                builder.CloseElement();
            }),
        });
}
