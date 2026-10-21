using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LogAnalyzer.ControlPlane.Migrations;
using LogAnalyzer.Query;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace LogAnalyzer.UnitTests;

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
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build([receipt], 1,
            new TopologyRepairMembershipPending(2, Hash('a'), true, Hash('b'))));
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build([receipt], 1,
            new TopologyRepairMembershipPending(2, Hash('a'), false, null)));
    }

    [Fact]
    public void Incomplete_or_duplicate_receipt_prefix_and_missing_legacy_digest_never_seal_partial_set()
    {
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build([Native(1, 'a')], 2, null));
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build(
            [Native(1, 'a'), Native(1, 'b')], 2, null));
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.Build(
            [Native(1, 'a'), Native(2, 'a')], 2, null));
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
        var first = Native(1, 'a');
        var second = Native(2, 'b');
        var pending = new TopologyRepairMembershipPending(3, Hash('c'), false, null);
        var beforeReceipt = TopologyRepairMembershipBuilder.Build([first, second], 2, pending);
        var third = Native(3, 'c');
        var residue = TopologyRepairMembershipBuilder.Build([first, second, third], 3, pending);
        var afterDelete = TopologyRepairMembershipBuilder.Build([first, second, third], 3, null);
        Assert.Empty(beforeReceipt.Members);
        Assert.Equal(0, beforeReceipt.Count);
        Assert.Equal(64, beforeReceipt.CanonicalSha256.Length);
        Assert.Equal(beforeReceipt.CanonicalSha256, residue.CanonicalSha256);
        Assert.Equal(beforeReceipt.CanonicalSha256, afterDelete.CanonicalSha256);
    }

    [Fact]
    public void Extra_missing_or_divergent_physical_member_cannot_match_exact_converted_union()
    {
        var candidate = TopologyRepairMembershipBuilder.Build(
            [Legacy(1, 'a', 'b'), Legacy(2, 'c', 'd')], 2, null);
        TopologyRepairMembershipBuilder.VerifyExactUnion(candidate, candidate.Members.Reverse().ToArray());
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.VerifyExactUnion(candidate,
            [candidate.Members[0]]));
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.VerifyExactUnion(candidate,
            [candidate.Members[0], candidate.Members[1], new(3, Hash('e'), Hash('f'))]));
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.VerifyExactUnion(candidate,
            [candidate.Members[0], candidate.Members[0]]));
        Assert.Throws<InvalidDataException>(() => TopologyRepairMembershipBuilder.VerifyExactUnion(candidate,
            [candidate.Members[0], candidate.Members[1] with { ConversionDigest = Hash('e') }]));
    }

    [Fact]
    public void Canonical_empty_and_single_member_vectors_pin_framing_and_all_tuple_fields()
    {
        // Vectors computed outside this code base from the documented framing
        // (le32 len + "loganalyzer-topology-members-v2" + le32 2 + le32 count,
        // then per member le64 seq + le32 64 + key + le32 64 + digest). The same
        // script reproduces the pre-rename 26-byte magic pair
        // (38 / b6a5b92c…, 182 / e3c43322…), so only the magic changed here.
        var empty = TopologyRepairMembershipBuilder.Build([], 0, null);
        Assert.Equal(43, empty.CanonicalByteLength);
        Assert.Equal("f36882dea2e58a9d27d40c59a0bfc57e6a3f1457cc10848a2a760c25c1f64acf",
            empty.CanonicalSha256);
        var single = TopologyRepairMembershipBuilder.Build([Legacy(1, 'a', 'b')], 1, null);
        Assert.Equal(187, single.CanonicalByteLength);
        Assert.Equal("0888ee5bdbac35b22de7371acc11fd7082c55afcd01c2a951c6b67ad2a4ff847",
            single.CanonicalSha256);
        Assert.NotEqual(single.CanonicalSha256,
            TopologyRepairMembershipBuilder.Build([Legacy(1, 'c', 'b')], 1, null).CanonicalSha256);
        Assert.NotEqual(single.CanonicalSha256,
            TopologyRepairMembershipBuilder.Build([Legacy(1, 'a', 'c')], 1, null).CanonicalSha256);
        Assert.NotEqual(single.CanonicalSha256,
            TopologyRepairMembershipBuilder.Build([Native(1, 'd'), Legacy(2, 'a', 'b')], 2, null)
                .CanonicalSha256);
    }

    /// <summary>
    /// The PG seal recomputes these bytes in <c>topology_repair_member_bytes</c>;
    /// if its framing drifts from the C# candidate every repair ends in
    /// <c>RepairIncomplete</c> while the unit vectors above stay green. Measured:
    /// the d10673e product rename left a hand-written <c>le32(26)</c> in
    /// SQL and ~226 integration tests went red with no unit signal. The SQL is
    /// read from the migration object itself, not from the source file.
    /// </summary>
    [Fact]
    public void Sql_membership_framing_agrees_with_the_csharp_candidate()
    {
        var sql = string.Concat(new AddTopologyLegacyMembership().UpOperations
            .OfType<SqlOperation>().Select(operation => operation.Sql));

        var literal = Regex.Match(sql,
            @"magic bytea := convert_to\('(?<magic>[^']+)', 'UTF8'\);");
        Assert.True(literal.Success, "topology_repair_member_bytes no longer declares its magic literal.");
        Assert.Contains("loganalyzer.topology_repair_le32(octet_length(magic))", sql, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"topology_repair_le32\(\d+\)\s*\|\|\s*convert_to\('", sql);

        var magic = Encoding.UTF8.GetBytes(literal.Groups["magic"].Value);
        var framed = new byte[4 + magic.Length + 4 + 4];
        BinaryPrimitives.WriteInt32LittleEndian(framed, magic.Length);
        magic.CopyTo(framed, 4);
        BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(4 + magic.Length), 2);
        var empty = TopologyRepairMembershipBuilder.Build([], 0, null);
        Assert.Equal(empty.CanonicalByteLength, framed.Length);
        Assert.Equal(empty.CanonicalSha256, Convert.ToHexStringLower(SHA256.HashData(framed)));

        var floor = Regex.Match(sql, @"canonical_byte_length BETWEEN (?<floor>\d+) AND");
        Assert.True(floor.Success);
        Assert.Equal(empty.CanonicalByteLength,
            int.Parse(floor.Groups["floor"].Value, CultureInfo.InvariantCulture));
    }

    private static TopologyRepairMembershipPublication Native(long sequence, char key) =>
        new(sequence, Hash(key), false, null);

    private static TopologyRepairMembershipPublication Legacy(long sequence, char key, char digest) =>
        new(sequence, Hash(key), true, Hash(digest));

    private static string Hash(char character) => new(character, 64);
}
