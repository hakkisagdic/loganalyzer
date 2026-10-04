using Bizigo.Query;

namespace Bizigo.UnitTests;

public sealed class TopologyRepairMembershipBuilderTests
{
    [Fact]
    public void Complete_prefix_and_conflict_only_pending_create_sorted_explicit_members()
    {
        var prefix = new[]
        {
            Legacy(2, 'b', 'c'),
            Native(1, 'a'),
        };
        var pending = new TopologyRepairMembershipPending(3, Hash('d'), true, Hash('e'));
        var candidate = TopologyRepairMembershipBuilder.Build(prefix, 2, pending);
        var reordered = TopologyRepairMembershipBuilder.Build(prefix.Reverse().ToArray(), 2, pending);

        Assert.Equal(2, candidate.Count);
        Assert.Equal(new long[] { 2, 3 }, candidate.Members.Select(member => member.Sequence).ToArray());
        Assert.Equal(Hash('b'), candidate.Members[0].PublicationKey);
        Assert.Equal(Hash('e'), candidate.Members[1].ConversionDigest);
        Assert.Equal(candidate.CanonicalSha256, reordered.CanonicalSha256);
        Assert.Equal(candidate.CanonicalByteLength, reordered.CanonicalByteLength);
    }

    [Fact]
    public void Pending_receipt_promotion_and_delete_keep_the_same_immutable_member_digest()
    {
        var first = Legacy(1, 'a', 'b');
        var pending = new TopologyRepairMembershipPending(2, Hash('c'), true, Hash('d'));
        var beforeReceipt = TopologyRepairMembershipBuilder.Build([first], 1, pending);
        var receipt = Legacy(2, 'c', 'd');
        var afterReceiptBeforeDelete = TopologyRepairMembershipBuilder.Build([receipt, first], 2, pending);
        var afterDelete = TopologyRepairMembershipBuilder.Build([first, receipt], 2, null);

        Assert.Equal(2, beforeReceipt.Count);
        Assert.Equal(beforeReceipt.CanonicalSha256, afterReceiptBeforeDelete.CanonicalSha256);
        Assert.Equal(beforeReceipt.CanonicalSha256, afterDelete.CanonicalSha256);
        Assert.Equal(beforeReceipt.CanonicalByteLength, afterDelete.CanonicalByteLength);
    }

    [Fact]
    public void Receipted_pending_residue_with_different_pair_or_digest_fails_closed()
    {
        var receipt = Legacy(1, 'a', 'b');
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build([receipt], 1,
            new TopologyRepairMembershipPending(1, Hash('c'), true, Hash('b'))));
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build([receipt], 1,
            new TopologyRepairMembershipPending(1, Hash('a'), true, Hash('c'))));
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build([receipt], 1,
            new TopologyRepairMembershipPending(3, Hash('a'), true, Hash('b'))));
    }

    [Fact]
    public void Incomplete_or_duplicate_receipt_prefix_and_missing_legacy_digest_never_seal_partial_set()
    {
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build([Native(1, 'a')], 2, null));
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build(
            [Native(1, 'a'), Native(1, 'b')], 2, null));
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build(
            [Native(1, 'a'), new TopologyRepairMembershipPublication(2, Hash('b'), true, null)], 2, null));
    }

    [Fact]
    public void Member_and_canonical_byte_caps_reject_the_plus_one_without_a_partial_candidate()
    {
        var prefix = new[] { Legacy(1, 'a', 'b'), Legacy(2, 'c', 'd') };
        var exact = TopologyRepairMembershipBuilder.Build(prefix, 2, null);
        var same = TopologyRepairMembershipBuilder.Build(prefix, 2, null,
            maxConversionMembers: 2, maxCanonicalMembershipBytes: exact.CanonicalByteLength);
        Assert.Equal(exact.CanonicalSha256, same.CanonicalSha256);
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build(prefix, 2, null,
            maxConversionMembers: 1));
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build([prefix[0]], 1,
            new TopologyRepairMembershipPending(2, Hash('c'), true, Hash('d')),
            maxConversionMembers: 1));
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build(prefix, 2, null,
            maxCanonicalMembershipBytes: exact.CanonicalByteLength - 1));
    }

    [Fact]
    public void Malformed_pair_digest_and_native_conversion_are_not_membership_authority()
    {
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build(
            [new TopologyRepairMembershipPublication(1, Hash('A'), true, Hash('b'))], 1, null));
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build(
            [new TopologyRepairMembershipPublication(1, Hash('a'), true, "short")], 1, null));
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build(
            [new TopologyRepairMembershipPublication(1, Hash('a'), false, Hash('b'))], 1, null));
    }

    [Fact]
    public void Native_only_publications_have_an_empty_conversion_set_not_a_missing_prefix()
    {
        var candidate = TopologyRepairMembershipBuilder.Build([Native(1, 'a'), Native(2, 'b')], 2, null);
        Assert.Empty(candidate.Members);
        Assert.Equal(0, candidate.Count);
        Assert.Equal(64, candidate.CanonicalSha256.Length);
    }

    private static TopologyRepairMembershipPublication Native(long sequence, char key) =>
        new(sequence, Hash(key), false, null);

    private static TopologyRepairMembershipPublication Legacy(long sequence, char key, char digest) =>
        new(sequence, Hash(key), true, Hash(digest));

    private static string Hash(char character) => new(character, 64);
}
