using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace DriveTrack.Integration.Tests.Support;

/// <summary>
/// The project's component-rendering harness: a component in, its HTML out, with no browser, no
/// Docker and no extra package.
/// <para>
/// bunit is the obvious tool and cannot be had — <c>api.nuget.org</c> is unreachable from this
/// machine, so restore cannot resolve a new package. What the shared framework already gives us is
/// <see cref="HtmlRenderer"/>, which renders a component to HTML in-process. That is enough for
/// every render-shaped claim the story makes: the dialog's ARIA wiring, the table's three states,
/// the map's container contract. Interaction a browser alone can produce is asserted one level
/// down instead, against the pure function or the module listener that decides it.
/// </para>
/// <para>
/// The service provider is deliberately minimal — logging, localization against the Web assembly's
/// own resources, and a JS runtime that refuses to be called. Static rendering never reaches
/// <c>OnAfterRenderAsync</c>, so a component that only talks to JavaScript after first render
/// renders here exactly as it would on the server's first pass; a component that tried to call out
/// during rendering would fail loudly rather than silently pass.
/// </para>
/// </summary>
internal static class ComponentRenderer
{
    /// <summary>Renders a component to HTML with the given parameters.</summary>
    /// <typeparam name="TComponent">The component to render.</typeparam>
    /// <param name="parameters">Its parameters, or null for none.</param>
    /// <param name="configureServices">
    /// Extra registrations the component under test needs — a caller, a navigation manager, a
    /// clock. Supplied per render rather than baked in, so a test can render the same component
    /// once per role and assert on what each one is actually offered.
    /// </param>
    public static async Task<string> RenderAsync<TComponent>(
        IDictionary<string, object?>? parameters = null,
        Action<IServiceCollection>? configureServices = null)
        where TComponent : IComponent
    {
        // The product is Ukrainian and the resources are neutral-uk (NFR-14/NFR-15), so the render
        // is done under the culture the application actually runs in rather than the machine's.
        var ukrainian = CultureInfo.GetCultureInfo("uk-UA");
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;

        CultureInfo.CurrentCulture = ukrainian;
        CultureInfo.CurrentUICulture = ukrainian;

        try
        {
            await using var services = BuildServices(configureServices);
            var loggerFactory = services.GetRequiredService<ILoggerFactory>();

            await using var renderer = new HtmlRenderer(services, loggerFactory);

            var view = ParameterView.FromDictionary(
                parameters ?? new Dictionary<string, object?>());

            // Renderer work has to happen on the renderer's own dispatcher: it is single-threaded by
            // contract, and calling in from the test thread throws rather than racing.
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<TComponent>(view);

                return output.ToHtmlString();
            });
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static ServiceProvider BuildServices(Action<IServiceCollection>? configureServices)
    {
        var services = new ServiceCollection();

        services.AddLogging();

        // No ResourcesPath: DriveTrack.Web embeds its .resx under Resources/ and the localizer
        // resolves UiText by the type's own namespace, exactly as Program.cs configures it.
        services.AddLocalization();

        services.AddSingleton<IJSRuntime, UncallableJSRuntime>();

        // The interface is Ukrainian (NFR-14), and the default HTML encoder escapes every
        // non-Latin character to a numeric reference - so `Завантаження…` renders as a run of
        // `&#x417;…` and every assertion about what a component SAYS would have to be written
        // against entities. Widening the encoder keeps the assertions readable and keeps a real
        // Latin leak visible, which is the whole point of reading the text back.
        services.AddSingleton(HtmlEncoder.Create(UnicodeRanges.All));

        // Last, so a test can replace anything above it rather than only add to it.
        configureServices?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A JS runtime that exists to be injected and never to be used. Static rendering must not
    /// reach the browser, so a call here is a defect in the component rather than in the harness,
    /// and it should say so instead of returning a plausible default.
    /// </summary>
    private sealed class UncallableJSRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException(
                $"A statically rendered component called JavaScript ('{identifier}'). Interop "
                    + "belongs in OnAfterRenderAsync or later, which static rendering never runs.");

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
