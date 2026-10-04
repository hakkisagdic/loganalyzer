using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Wal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

public sealed class TopologyLegacyWalCandidateTests
{
    [Fact]
    public async Task Full_v3_roster_and_exact_archive_are_only_a_candidate_not_upgrade_authority()
    {
        var first = Envelope(Guid.NewGuid(), observedDays: 1);
        var second = Envelope(Guid.NewGuid(), observedDays: 90);
        await WithWalAsync([RawSignalCodec.Encode(first), RawSignalCodec.Encode(second), RawSignalCodec.Encode(first)], paths =>
        {
            TopologyLegacyWalCandidateVerifier.VerifyCandidate(paths, Archives(first, second), Claims(first, second),
                maxRosterBytes: 100_000, maxFrames: 3);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Divergent_same_envelope_id_anywhere_in_roster_fails_before_target_cut()
    {
        var original = Envelope(Guid.NewGuid(), observedDays: 1);
        var replacement = original with { ObservedRetentionDays = 90 };
        replacement = replacement with { TopologyBindingsSha256 = replacement.ComputeTopologyBindingsHash() };
        await WithWalAsync([RawSignalCodec.Encode(original), RawSignalCodec.Encode(replacement)], paths =>
        {
            var error = Assert.Throws<InvalidDataException>(() =>
                TopologyLegacyWalCandidateVerifier.VerifyCandidate(paths, Archives(original), Claims(original),
                    maxRosterBytes: 100_000, maxFrames: 2));
            Assert.Contains("Divergent WAL frames", error.Message, StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Corrupt_tail_and_exhausted_complete_scan_cap_never_yield_partial_candidate()
    {
        var first = Envelope(Guid.NewGuid(), observedDays: 90);
        var second = Envelope(Guid.NewGuid(), observedDays: 90);
        await WithWalAsync([RawSignalCodec.Encode(first), RawSignalCodec.Encode(second)], async paths =>
        {
            var archive = Archives(first, second);
            var claims = Claims(first, second);
            Assert.Throws<InvalidDataException>(() => TopologyLegacyWalCandidateVerifier.VerifyCandidate(
                paths, archive, claims, maxRosterBytes: 100_000, maxFrames: 1));
            Assert.Throws<InvalidDataException>(() => TopologyLegacyWalCandidateVerifier.VerifyCandidate(
                paths, archive, claims, maxRosterBytes: 1, maxFrames: 2));
            var token = TestContext.Current.CancellationToken;
            await using (var stream = new FileStream(paths[0], FileMode.Append, FileAccess.Write))
            {
                await stream.WriteAsync(new byte[] { 0x42, 0x5a, 0x47 }, token);
                await stream.FlushAsync(token);
            }
            Assert.Throws<InvalidDataException>(() => TopologyLegacyWalCandidateVerifier.VerifyCandidate(
                paths, archive, claims, maxRosterBytes: 100_000, maxFrames: 3));
        });
    }

    [Fact]
    public async Task Missing_archive_or_owner_claim_never_becomes_a_witness()
    {
        var envelope = Envelope(Guid.NewGuid(), observedDays: 90);
        await WithWalAsync([RawSignalCodec.Encode(envelope)], paths =>
        {
            Assert.Throws<InvalidDataException>(() => TopologyLegacyWalCandidateVerifier.VerifyCandidate(
                paths, new Dictionary<Guid, ReadOnlyMemory<byte>>(), Claims(envelope), 100_000, 1));
            Assert.Throws<InvalidDataException>(() => TopologyLegacyWalCandidateVerifier.VerifyCandidate(
                paths, Archives(envelope), new Dictionary<Guid, string>(), 100_000, 1));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Existing_claim_check_is_read_only_and_fails_when_claim_is_missing_or_different()
    {
        using var factory = new InMemoryControlPlaneFactory();
        var token = TestContext.Current.CancellationToken;
        var envelope = Envelope(Guid.NewGuid(), observedDays: 90);
        var resolver = new HistoricalTelemetryOwners(factory);

        await Assert.ThrowsAsync<InvalidDataException>(() => resolver.VerifyExistingClaimAsync(
            envelope.EnvelopeId, envelope.OwnerBindingsSha256!, token));
        await using (var check = factory.CreateDbContext())
            Assert.Empty(await check.TelemetryOwnerClaims.ToArrayAsync(token));

        await using (var db = factory.CreateDbContext())
        {
            db.TelemetryOwnerClaims.Add(new() { EnvelopeId = envelope.EnvelopeId,
                BindingHash = envelope.OwnerBindingsSha256! });
            await db.SaveChangesAsync(token);
        }
        await resolver.VerifyExistingClaimAsync(envelope.EnvelopeId, envelope.OwnerBindingsSha256!, token);
        await Assert.ThrowsAsync<InvalidDataException>(() => resolver.VerifyExistingClaimAsync(
            envelope.EnvelopeId, new string('f', 64), token));
        await using var final = factory.CreateDbContext();
        var claim = Assert.Single(await final.TelemetryOwnerClaims.ToArrayAsync(token));
        Assert.Equal(envelope.OwnerBindingsSha256, claim.BindingHash);
    }

    private static async Task WithWalAsync(IReadOnlyList<byte[]> frames,
        Func<IReadOnlyList<string>, Task> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "bizigo-legacy-wal-candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var wal = new WriteAheadLog(Options.Create(new WalOptions { Directory = root }),
                NullLogger<WriteAheadLog>.Instance))
            {
                var token = TestContext.Current.CancellationToken;
                foreach (var frame in frames) await wal.AppendAsync(frame, token);
                await wal.SealAsync(token);
            }
            var paths = Directory.GetFiles(root, "wal-*.log").Order(StringComparer.Ordinal).ToArray();
            await check(paths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static RawSignalEnvelope Envelope(Guid id, int observedDays)
    {
        var payload = Encoding.UTF8.GetBytes("[]");
        const ulong eventTime = 1_800_000_000_000_000_000UL;
        var owners = new[]
        {
            new TelemetryOwnerBinding("trace-a", "source-a", "A", 7, eventTime, "known"),
            new TelemetryOwnerBinding("trace-b", "source-b", "B", 8, eventTime + 1, "known"),
        };
        var topology = new[]
        {
            new TopologyLeafBinding("trace-a", eventTime, "source-a", "A", 7,
                TopologyIdentity.Node(TopologyNodeKind.Service, Guid.NewGuid()), null, 5, null, 9, "A", "Resolved"),
            new TopologyLeafBinding("trace-b", eventTime + 1, "source-b", "B", 8,
                TopologyIdentity.Node(TopologyNodeKind.Service, Guid.NewGuid()), null, 6, null, 10, "B", "Resolved"),
        };
        var envelope = new RawSignalEnvelope(RawSignalEnvelope.CurrentVersion, id, TelemetrySignal.Traces,
            "application/json", DateTimeOffset.UnixEpoch, RawSignalEnvelope.Hash(payload), payload, 1,
            ["trace-a", "trace-b"], 0)
        {
            OwnerBindings = owners, TopologyBindings = topology, ObservedRetentionDays = observedDays,
        };
        envelope = envelope with { OwnerBindingsSha256 = envelope.ComputeOwnerBindingsHash() };
        return envelope with { TopologyBindingsSha256 = envelope.ComputeTopologyBindingsHash() };
    }

    private static Dictionary<Guid, ReadOnlyMemory<byte>> Archives(params RawSignalEnvelope[] envelopes) =>
        envelopes.ToDictionary(e => e.EnvelopeId, e => (ReadOnlyMemory<byte>)RawSignalCodec.Encode(e));

    private static Dictionary<Guid, string> Claims(params RawSignalEnvelope[] envelopes) =>
        envelopes.ToDictionary(e => e.EnvelopeId, e => e.OwnerBindingsSha256!);
}
