using System.Text.RegularExpressions;
using DriveTrack.Application.Common;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Components.Shared;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// NFR-26 / FR-15 / FR-21: one map component for picking a point and for showing one.
/// <para>
/// Leaflet builds its own DOM after Blazor has finished, so what a render can prove is the
/// container contract — a stable id for the library to attach to and a localized label for anyone
/// who cannot see tiles. What only a browser could prove — a click moving the marker — is asserted
/// against the module's own source instead, and the coordinate guard is asserted where it lives, as
/// a pure construction.
/// </para>
/// </summary>
public class MapTests
{
    [Fact]
    public async Task The_container_carries_a_generated_id_and_a_localized_label()
    {
        var html = await ComponentRenderer.RenderAsync<DtMap>(new Dictionary<string, object?>
        {
            ["Value"] = new MapLocation(50.4501, 30.5234),
            ["Zoom"] = 14,
        });

        var id = Regex.Match(
            html,
            @"id=""(?<id>dt-map-[0-9a-f]{32})""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(
            id.Success,
            $"The map container has no generated id, so Leaflet has nothing to attach to:"
                + $"{Environment.NewLine}{html}");

        var label = Regex.Match(
            html,
            @"aria-label=""(?<label>[^""]*)""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(label.Success, $"The map container carries no aria-label:{Environment.NewLine}{html}");

        var text = label.Groups["label"].Value;

        Assert.True(SharedMarkup.IsUkrainian(text), $"The map label reads '{text}'.");
        Assert.False(SharedMarkup.HasLatinWord(text), $"The map label reads '{text}'.");
    }

    [Fact]
    public void The_coordinates_the_component_is_handed_reach_the_module_unchanged()
    {
        // The map's contract with JavaScript, read where it is written: the create call takes the
        // container, the callback target, the two coordinates, the zoom and the picker flag - in
        // that order - and the module's export has to agree.
        var component = SharedMarkup.ReadShared("DtMap.razor");
        var module = SharedMarkup.ReadShared("DtMap.razor.js");

        Assert.Contains(
            @"""create"", _containerId, _self, latitude, longitude, Zoom, Picker",
            component,
            StringComparison.Ordinal);

        Assert.Contains(
            "export async function create(containerId, dotNetRef, latitude, longitude, zoom, picker)",
            module,
            StringComparison.Ordinal);

        // FR-21: a map opened on a value that already exists is centred on it, both at creation and
        // when the value later changes.
        Assert.Contains(@"""setView"", _handle, value.Latitude, value.Longitude", component, StringComparison.Ordinal);
        Assert.Contains("export function setView(handle, latitude, longitude)", module, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(90.0001, 0)]
    [InlineData(-90.0001, 0)]
    [InlineData(double.NaN, 0)]
    public void A_latitude_outside_the_decimal_degree_range_is_refused(double latitude, double longitude)
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(
            () => new MapLocation(latitude, longitude));

        Assert.Equal("latitude", failure.ParamName);
    }

    [Theory]
    [InlineData(0, 180.0001)]
    [InlineData(0, -180.0001)]
    [InlineData(0, double.NaN)]
    public void A_longitude_outside_the_decimal_degree_range_is_refused(double latitude, double longitude)
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(
            () => new MapLocation(latitude, longitude));

        Assert.Equal("longitude", failure.ParamName);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 180)]
    [InlineData(-90, -180)]
    [InlineData(50.4501, 30.5234)]
    public void A_coordinate_on_the_boundary_is_a_place(double latitude, double longitude)
    {
        // The exclusive-vs-inclusive mistake is the one a range guard actually makes, and it turns
        // the poles and the antimeridian into errors.
        var location = new MapLocation(latitude, longitude);

        Assert.Equal(latitude, location.Latitude);
        Assert.Equal(longitude, location.Longitude);
    }

    [Fact]
    public void A_click_handler_is_registered_only_while_picking_is_on()
    {
        var module = SharedMarkup.ReadShared("DtMap.razor.js");

        // Exactly one registration, and it is inside the picker branch. With the flag false no
        // handler exists at all, so a display-only map cannot move its marker or call into .NET
        // however hard it is clicked.
        Assert.Equal(1, SharedMarkup.Occurrences(module, @"map.on(""click"""));

        var guard = module.IndexOf("if (picker)", StringComparison.Ordinal);
        var registration = module.IndexOf(@"map.on(""click""", StringComparison.Ordinal);

        Assert.True(guard >= 0, "The module does not branch on the picker flag.");
        Assert.True(registration > guard, "The click handler is registered outside the picker branch.");

        // And the registration has to still be INSIDE the branch: one that merely follows the guard
        // in the file would satisfy the comparison above while running unconditionally. More braces
        // opened than closed between the two is what "still inside" means.
        var branch = module[guard..registration];

        Assert.True(
            SharedMarkup.Occurrences(branch, "{") > SharedMarkup.Occurrences(branch, "}"),
            "The picker branch has already closed by the time the click handler is registered.");

        Assert.Contains(@"invokeMethodAsync(""OnPickedAsync""", module, StringComparison.Ordinal);
        Assert.Contains("public Task OnPickedAsync(double latitude, double longitude)",
            SharedMarkup.ReadShared("DtMap.razor"), StringComparison.Ordinal);
    }
}
