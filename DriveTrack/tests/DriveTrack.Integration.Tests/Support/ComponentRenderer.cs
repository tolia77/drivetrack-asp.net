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
    /// <param name="culture">
    /// The culture to render under. Null is <c>uk-UA</c>, which is what the product defaults to and
    /// what every screen test written before the second language assumed - so those tests are
    /// untouched, and a test that wants to read a screen back in English names <c>en-US</c> here.
    /// </param>
    public static Task<string> RenderAsync<TComponent>(
        IDictionary<string, object?>? parameters = null,
        Action<IServiceCollection>? configureServices = null,
        CultureInfo? culture = null)
        where TComponent : IComponent =>
        RenderAsync<TComponent>(parameters, configureServices, waitForQuiescence: true, culture);

    /// <summary>
    /// The component's <em>first</em> render pass, before anything it awaited has answered.
    /// <para>
    /// This is the only way to read a loading state back. A screen sets its flag, calls
    /// <c>StateHasChanged</c> and then awaits its service; by the time the render has quiesced the
    /// flag is false again and the rows are on the page, so the state that exists precisely to stop
    /// a screen looking broken while it waits is invisible to every other assertion in this suite.
    /// </para>
    /// <para>
    /// The component under test is given a service whose task never completes, so "the first pass"
    /// and "while it is still waiting" are the same moment. Quiescence is deliberately not awaited —
    /// waiting for it would be waiting forever.
    /// </para>
    /// </summary>
    /// <typeparam name="TComponent">The component to render.</typeparam>
    /// <param name="parameters">Its parameters, or null for none.</param>
    /// <param name="configureServices">Extra registrations, as for <see cref="RenderAsync{TComponent}"/>.</param>
    /// <param name="culture">The culture to render under; null is <c>uk-UA</c>.</param>
    public static Task<string> RenderFirstPassAsync<TComponent>(
        IDictionary<string, object?>? parameters = null,
        Action<IServiceCollection>? configureServices = null,
        CultureInfo? culture = null)
        where TComponent : IComponent =>
        RenderAsync<TComponent>(parameters, configureServices, waitForQuiescence: false, culture);

    /// <summary>
    /// Renders a component, does something to the world it is watching, and reads the markup back.
    /// <para>
    /// The one thing <see cref="RenderAsync{TComponent}"/> cannot show: a component that re-renders
    /// because something outside it moved. A navigation is the case that matters here - the shell
    /// subscribes to <c>LocationChanged</c>, and rendering twice at two paths proves only that the
    /// component reads the path it was handed, not that it notices the path changing under it.
    /// </para>
    /// <para>
    /// <paramref name="act"/> runs on the renderer's own dispatcher, between the first render and
    /// the read, so a notification raised inside it is dispatched exactly as the framework would
    /// dispatch it and the re-render it provokes has finished by the time the markup is written.
    /// </para>
    /// </summary>
    /// <typeparam name="TComponent">The component to render.</typeparam>
    /// <param name="configureServices">The registrations the component needs.</param>
    /// <param name="act">What to do to the world between the first render and the read.</param>
    /// <param name="culture">The culture to render under; null is <c>uk-UA</c>.</param>
    public static Task<string> RenderThenAsync<TComponent>(
        Action<IServiceCollection> configureServices,
        Func<IServiceProvider, Task> act,
        CultureInfo? culture = null)
        where TComponent : IComponent =>
        RenderAsync<TComponent>(
            parameters: null,
            configureServices,
            waitForQuiescence: true,
            culture,
            act);

    private static async Task<string> RenderAsync<TComponent>(
        IDictionary<string, object?>? parameters,
        Action<IServiceCollection>? configureServices,
        bool waitForQuiescence,
        CultureInfo? culture = null,
        Func<IServiceProvider, Task>? act = null)
        where TComponent : IComponent
    {
        // The render is done under the culture the application actually runs in rather than the
        // machine's. The default is uk-UA: the resources are neutral-uk (NFR-14/NFR-15) and a
        // visitor who has chosen nothing is served Ukrainian, so that is what "no argument" has to
        // mean if the screen tests written before the second language are to keep asserting what
        // they were written to assert.
        var requested = culture ?? CultureInfo.GetCultureInfo("uk-UA");
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;

        CultureInfo.CurrentCulture = requested;
        CultureInfo.CurrentUICulture = requested;

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
                if (!waitForQuiescence)
                {
                    return renderer.BeginRenderingComponent<TComponent>(view).ToHtmlString();
                }

                var output = await renderer.RenderComponentAsync<TComponent>(view);

                if (act is not null)
                {
                    await act(services);
                }

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
