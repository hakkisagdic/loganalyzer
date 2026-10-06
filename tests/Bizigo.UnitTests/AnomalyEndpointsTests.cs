using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Bizigo.Api;
using Bizigo.Api.Anomaly;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bizigo.UnitTests;

public sealed class AnomalyEndpointsTests : IDisposable
{
    private readonly InMemoryControlPlaneFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private sealed class TestCurrentUser(AccessScope scope) : ICurrentUser
    {
        public AccessScope Scope => scope;
        public ClaimsPrincipal? Principal => new(new ClaimsIdentity([
            new Claim("sub", "test-user"),
            new Claim("preferred_username", "test-user"),
        ], "TestScheme"));
    }

    private async Task<(WebApplication app, HttpClient client)> CreateTestServerAsync(AccessScope? scope = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddRouting();
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddScoped<ControlPlaneDbContext>(_ => _factory.CreateDbContext());
        var admin = new AccessScope { Subject = "test-user", OwnerGroups = new HashSet<string>(StringComparer.Ordinal), IsUnrestricted = true };
        builder.Services.AddSingleton<ICurrentUser>(new TestCurrentUser(scope ?? admin));

        var app = builder.Build();
        app.MapAnomalies();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, app.GetTestClient());
    }

    [Fact]
    public async Task DeletePolicy_Returns405_WithAllowHeader()
    {
        var (app, client) = await CreateTestServerAsync();
        await using (app)
        {
            var resApi = await client.DeleteAsync("/api/anomaly-policies/pol-1", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, resApi.StatusCode);
            Assert.Contains("GET", resApi.Content.Headers.Allow);
            Assert.Contains("PATCH", resApi.Content.Headers.Allow);

            var resV1 = await client.DeleteAsync("/v1/anomaly-policies/pol-1", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, resV1.StatusCode);
            Assert.Contains("GET", resV1.Content.Headers.Allow);
            Assert.Contains("PATCH", resV1.Content.Headers.Allow);
        }
    }

    [Fact]
    public async Task DeleteRun_Returns405_WithAllowHeader()
    {
        var (app, client) = await CreateTestServerAsync();
        await using (app)
        {
            var resApi = await client.DeleteAsync("/api/anomaly-runs/run-1", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, resApi.StatusCode);
            Assert.Equal("GET", resApi.Content.Headers.Allow.ToString());

            var resV1 = await client.DeleteAsync("/v1/anomaly-runs/run-1", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, resV1.StatusCode);
            Assert.Equal("GET", resV1.Content.Headers.Allow.ToString());
        }
    }

    [Fact]
    public async Task CreatePolicy_ValidationFails_WhenNumericParametersInvalid()
    {
        var (app, client) = await CreateTestServerAsync();
        await using (app)
        {
            // Negative sensitivity
            var req1 = new CreateAnomalyPolicyRequest(
                Name: "P1", OwnerGroup: "core", Signal: AnomalySignals.MetricSeriesSum,
                Target: "m1", EventWindowSeconds: 60, BaselineWindowSeconds: 300,
                Sensitivity: -1.0, MinSamples: 5, ZeroBaselineMinAbsolute: null, CadenceSeconds: 60);

            var res1 = await client.PostAsJsonAsync("/v1/anomaly-policies", req1, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, res1.StatusCode);

            // Zero min_samples
            var req2 = new CreateAnomalyPolicyRequest(
                Name: "P2", OwnerGroup: "core", Signal: AnomalySignals.MetricSeriesSum,
                Target: "m1", EventWindowSeconds: 60, BaselineWindowSeconds: 300,
                Sensitivity: 2.0, MinSamples: 0, ZeroBaselineMinAbsolute: null, CadenceSeconds: 60);

            var res2 = await client.PostAsJsonAsync("/v1/anomaly-policies", req2, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, res2.StatusCode);

            // Negative baseline_window_seconds
            var req3 = new CreateAnomalyPolicyRequest(
                Name: "P3", OwnerGroup: "core", Signal: AnomalySignals.MetricSeriesSum,
                Target: "m1", EventWindowSeconds: 60, BaselineWindowSeconds: -30,
                Sensitivity: 2.0, MinSamples: 5, ZeroBaselineMinAbsolute: null, CadenceSeconds: 60);

            var res3 = await client.PostAsJsonAsync("/v1/anomaly-policies", req3, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, res3.StatusCode);
        }
    }

    [Fact]
    public async Task PatchPolicy_OptimisticConcurrency_Returns409OnVersionMismatch()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.AnomalyPolicies.Add(new AnomalyPolicyEntity
            {
                Id = "pol-conc-1",
                OwnerGroup = "core",
                Name = "Concurrency Policy",
                Signal = AnomalySignals.MetricSeriesSum,
                Target = "cpu",
                EventWindowSeconds = 60,
                BaselineWindowSeconds = 300,
                Sensitivity = 2.0,
                MinSamples = 3,
                CadenceSeconds = 60,
                State = AnomalyPolicyStates.Enabled,
                Version = 3
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (app, client) = await CreateTestServerAsync();
        await using (app)
        {
            // Wrong version: expected 2, but DB has 3 -> 409 Conflict (S18)
            var patchWrong = new UpdateAnomalyPolicyRequest(
                ExpectedVersion: 2,
                State: AnomalyPolicyStates.Disabled,
                Name: null, Sensitivity: null, MinSamples: null,
                BaselineWindowSeconds: null, EventWindowSeconds: null,
                ZeroBaselineMinAbsolute: null);

            var resConflict = await client.PatchAsJsonAsync("/v1/anomaly-policies/pol-conc-1", patchWrong, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, resConflict.StatusCode);

            // Correct version: expected 3 -> 200 OK, version becomes 4
            var patchRight = new UpdateAnomalyPolicyRequest(
                ExpectedVersion: 3,
                State: AnomalyPolicyStates.Disabled,
                Name: null, Sensitivity: null, MinSamples: null,
                BaselineWindowSeconds: null, EventWindowSeconds: null,
                ZeroBaselineMinAbsolute: null);

            var resOk = await client.PatchAsJsonAsync("/v1/anomaly-policies/pol-conc-1", patchRight, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, resOk.StatusCode);

            var updated = await resOk.Content.ReadFromJsonAsync<AnomalyPolicyResponse>(cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(updated);
            Assert.Equal(4, updated.Version);
            Assert.Equal(AnomalyPolicyStates.Disabled, updated.State);
        }
    }
}
