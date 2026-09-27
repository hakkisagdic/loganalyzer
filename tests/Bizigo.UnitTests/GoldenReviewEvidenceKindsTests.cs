using System.Text.Json;
using Bizigo.Api;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Evidence;

namespace Bizigo.UnitTests;

public sealed class GoldenReviewEvidenceKindsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"missing_evidence_kinds\":null}", false)]
    [InlineData("{\"missing_evidence_kinds\":[]}", true)]
    [InlineData("{\"missing_evidence_kinds\":[\"Trace\",\"Metric\"]}", true)]
    public void Review_and_close_nullable_capture_matrix(string fields, bool asked)
    {
        var wire = fields.Insert(1, "\"verdict\":\"correct\"" + (fields.Length > 2 ? "," : ""));
        var review = JsonSerializer.Deserialize<RcaReviewRequest>(wire)!;
        var close = JsonSerializer.Deserialize<CloseTriggerRequest>(wire)!;
        var answer = MissingEvidenceAnswers.Validate(review.MissingEvidenceKinds);
        Assert.Equal(answer, MissingEvidenceAnswers.Validate(close.MissingEvidenceKinds));
        Assert.Equal(asked, answer is not null);
        if (answer is { Length: > 0 }) Assert.Equal(["Metric", "Trace"], answer);
    }

    [Theory]
    [InlineData("[\"Metric\",\"Metric\"]")]
    [InlineData("[\"metric\"]")]
    [InlineData("[\"Future\"]")]
    [InlineData("[null]")]
    [InlineData("[\"Log\",\"Change\",\"Metric\",\"Trace\",\"Topology\",\"Other\"]")]
    public void Invalid_answer_is_rejected_by_shared_capture_validator(string json)
    {
        var answer = JsonSerializer.Deserialize<string[]>(json);
        Assert.Throws<ReviewRejectedException>(() => MissingEvidenceAnswers.Validate(answer));
    }

    [Fact]
    public void Client_cannot_select_schema_version()
    {
        const string json = "{\"verdict\":\"correct\",\"schema_version\":2}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RcaReviewRequest>(json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CloseTriggerRequest>(json));
    }

    [Fact]
    public async Task Golden_missing_kind_fixed_denominator()
    {
        using var factory = new InMemoryControlPlaneFactory();
        var scope = AccessScope.ForGroups("reviewer", ["A"]);
        await using (var db = factory.CreateDbContext())
        {
            var answers = new (int Version, string[]? Answer)[]
            { (1, null), (2, null), (3, null), (3, []), (3, ["Metric"]), (3, ["Metric", "Trace"]) };
            foreach (var (version, answer) in answers)
                db.GoldenReviews.Add(new()
                {
                    BundleId = Guid.NewGuid(), OwnerGroup = "A", ReviewerSubject = "reviewer",
                    SchemaVersion = version, MissingEvidenceKinds = answer, Verdict = ReviewVerdict.Correct,
                });
            db.GoldenReviews.Add(new() { BundleId = Guid.NewGuid(), OwnerGroup = "B", ReviewerSubject = "B", MissingEvidenceKinds = [] });
            await db.SaveChangesAsync(Ct);
        }
        var store = new GoldenReviewStore(factory);
        var value = await store.MissingEvidenceQualityAsync(scope, Ct);
        Assert.Equal(6, value.Total); Assert.Equal(3, value.Measured); Assert.Equal(3, value.Unanswered);
        Assert.Equal(2, value.MissingAny); Assert.Equal(2, value.Metric); Assert.Equal(1, value.Trace);
        Assert.Equal(2.0 / 3, value.MissingAnyRatio); Assert.Equal(2.0 / 3, value.MetricRatio);
        Assert.Equal(1.0 / 3, value.TraceRatio);
        Assert.Null((await store.MissingEvidenceQualityAsync(AccessScope.ForGroups("none", ["C"]), Ct)).MissingAnyRatio);
        var quality = await store.QualityAsync(scope, Ct);
        Assert.Equal(6, quality.Total); Assert.Equal(1.0, quality.Accuracy);
    }

    [Fact]
    public async Task Store_reopen_preserves_null_empty_and_sorted_nonempty()
    {
        using var factory = new InMemoryControlPlaneFactory();
        var id = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.EvidenceBundles.Add(new() { Id = id, ContentHash = "test", Payload = "{}" });
            await db.SaveChangesAsync(Ct);
        }
        foreach (var answer in new string[]?[] { null, [], ["Trace", "Metric"] })
        {
            var saved = await new GoldenReviewStore(factory).AddAsync(new ReviewInput(id, null,
                ReviewVerdict.Correct, ContradictingEvidenceVerdict.Unspecified, "", MissingEvidenceKinds: answer),
                AccessScope.ForGroups("user", ["A"]), Ct);
            await using var reopened = factory.CreateDbContext();
            var read = await reopened.GoldenReviews.FindAsync([saved.Id], Ct);
            Assert.NotNull(read); Assert.Equal(3, read.SchemaVersion);
            Assert.Equal(answer is not null, read.MissingEvidenceAsked);
            var response = RcaReviewResponse.Of(read);
            var json = JsonSerializer.SerializeToElement(response);
            Assert.Equal(answer is null ? JsonValueKind.Null : JsonValueKind.Array,
                json.GetProperty("missing_evidence_kinds").ValueKind);
            Assert.Equal(answer is not null, json.GetProperty("missing_evidence_asked").GetBoolean());
            if (answer is { Length: > 0 }) Assert.Equal(["Metric", "Trace"], Assert.IsType<string[]>(read.MissingEvidenceKinds));
        }
    }
}
