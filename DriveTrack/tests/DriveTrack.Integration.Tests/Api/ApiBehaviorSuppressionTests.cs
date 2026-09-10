using System.Net;
using System.Net.Http.Json;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DriveTrack.Integration.Tests.Api;

/// <summary>
/// AD-7's first two suppressions, asserted twice each: once on the resolved options and once on the
/// behaviour they govern.
/// <para>
/// Both flags are silent when wrong. <c>SuppressMapClientErrors</c> in particular defaults to
/// <em>on</em> and quietly rewrites not-found, conflict and unauthorized results into problem
/// documents at exactly the status codes NFR-1 governs - a regression that shows up as a changed
/// response body and nothing else. A direct assertion on the flag says which line to fix.
/// </para>
/// </summary>
public class ApiBehaviorSuppressionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Both_suppressions_are_on_in_the_running_host()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);

        // Forces the host to start, so these are the options the pipeline is actually using.
        using var client = factory.CreateClient();

        var options = factory.Services.GetRequiredService<IOptions<ApiBehaviorOptions>>().Value;

        Assert.True(options.SuppressModelStateInvalidFilter);
        Assert.True(options.SuppressMapClientErrors);
    }

    [Fact]
    public async Task An_invalid_model_reaches_the_action_body()
    {
        // The behavioural half of SuppressModelStateInvalidFilter: without it the framework answers
        // 400 with a ProblemDetails before the action runs, and AD-9's validator - the thing that
        // actually decides what is valid here - never gets a say.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/probe/model-state", UriKind.Relative),
            new { },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>(cancellationToken);

        Assert.NotNull(envelope);
        Assert.True(envelope.RootElement.GetProperty("success").GetBoolean());

        // The action ran and saw the model state for itself - it was not short-circuited.
        Assert.False(envelope.RootElement.GetProperty("data").GetProperty("modelStateIsValid").GetBoolean());
    }
}
