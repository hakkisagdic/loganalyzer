using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bizigo.Contracts;

public enum TelemetrySignal { Metrics = 1, Traces = 2 }

/// <summary>An immutable admission decision bound to the exact decompressed export.</summary>
public sealed record RawSignalEnvelope(
    int Version,
    Guid EnvelopeId,
    TelemetrySignal Signal,
    string ContentType,
    DateTimeOffset ReceivedAt,
    string PayloadSha256,
    byte[] Payload,
    int ValidationVersion,
    string[] AcceptedKeys,
    int RejectedCount)
{
    public const int CurrentVersion = 2;
    public int PayloadLength { get; init; } = Payload?.Length ?? 0;
    public int RetentionDays { get; init; } = 90;
    public TelemetryOwnerBinding[]? OwnerBindings { get; init; }
    public string? OwnerBindingsSha256 { get; init; }

    public string ComputeOwnerBindingsHash() => Hash(JsonSerializer.SerializeToUtf8Bytes(
        new { EnvelopeId, PayloadSha256, OwnerBindings, RetentionDays }, RawSignalCodec.Json));

    public void Validate()
    {
        if (Version is not (1 or CurrentVersion) || ValidationVersion != 1)
            throw new InvalidDataException("Unsupported telemetry envelope/validation version.");
        if (EnvelopeId == Guid.Empty || !Enum.IsDefined(Signal)
            || ContentType is not ("application/json" or "application/x-protobuf")
            || ReceivedAt.Offset != TimeSpan.Zero || Payload is null || PayloadLength != Payload.Length || AcceptedKeys is null
            || RejectedCount < 0 || RetentionDays is < 1 or > 36500 || AcceptedKeys.Any(string.IsNullOrWhiteSpace)
            || AcceptedKeys.Distinct(StringComparer.Ordinal).Count() != AcceptedKeys.Length)
            throw new InvalidDataException("Invalid telemetry envelope.");
        if (!string.Equals(Hash(Payload), PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Telemetry payload checksum mismatch.");
        if (Version == 1 && (OwnerBindings is not null || OwnerBindingsSha256 is not null))
            throw new InvalidDataException("Legacy envelope cannot carry an unversioned owner decision.");
        if (Version == CurrentVersion && (OwnerBindings is null
            || !OwnerBindings.Select(b => b.LeafKey).SequenceEqual(AcceptedKeys, StringComparer.Ordinal)
            || OwnerBindings.Any(b => string.IsNullOrWhiteSpace(b.SourceId) || string.IsNullOrWhiteSpace(b.OwnerGroup)
                || b.HistoryRevision < 0 || b.Reason is not ("known" or "unknown" or "ambiguous" or "disabled" or "legacy-owner-unknown"))
            || !string.Equals(ComputeOwnerBindingsHash(), OwnerBindingsSha256, StringComparison.Ordinal)))
            throw new InvalidDataException("Missing or corrupt telemetry ownership binding.");
    }

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public static class RawSignalCodec
{
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Encode(RawSignalEnvelope envelope)
    {
        envelope.Validate();
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
    }

    public static RawSignalEnvelope Decode(ReadOnlySpan<byte> bytes)
    {
        var envelope = JsonSerializer.Deserialize<RawSignalEnvelope>(bytes, Json)
            ?? throw new InvalidDataException("Empty telemetry envelope.");
        envelope.Validate();
        return envelope;
    }
}
