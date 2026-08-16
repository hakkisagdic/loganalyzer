using Bizigo.Parsing.Grok;

namespace Bizigo.Parsing.Schema;

/// <summary>Adım başarısız olduğunda ne olacağı (F1 §3).</summary>
public enum OnFailure
{
    /// <summary>Ayrıştırma durur, <c>parse_status=failed</c>. <b>Varsayılan budur</b> —
    /// dispatcher'ın "ilk <c>ok</c> kazanır" kuralı ancak eşleşmeyen parser'ın
    /// açıkça başarısız olmasıyla anlam kazanır.</summary>
    Fail = 0,

    /// <summary>Adım atlanır, <c>parse_status=partial</c>.</summary>
    Continue,

    /// <summary>Adım atlanır, etiket eklenir, <c>parse_status=partial</c>.</summary>
    Tag,
}

public abstract record PipelineStep
{
    public abstract string Type { get; }

    public OnFailure OnFailure { get; init; } = OnFailure.Fail;

    /// <summary><see cref="OnFailure.Tag"/> için etiket adı. Boşsa <c>_{tip}_failure</c>.</summary>
    public string? Tag { get; init; }

    /// <summary>Hata mesajlarında kullanılacak YAML konumu.</summary>
    public int Line { get; init; }
}

public sealed record GrokStep : PipelineStep
{
    public override string Type => "grok";

    public required string Field { get; init; }

    public required IReadOnlyList<string> Patterns { get; init; }

    /// <summary>Yalnızca bu parser'a özel pattern tanımları.</summary>
    public IReadOnlyDictionary<string, string> PatternDefinitions { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record KvStep : PipelineStep
{
    public override string Type => "kv";

    public required string Field { get; init; }

    public string Separator { get; init; } = " ";

    public string Assign { get; init; } = "=";

    /// <summary>Değer tırnak içindeyse tırnaklar çıkarılır ve ayraç yok sayılır.</summary>
    public bool Quoted { get; init; } = true;

    public string? TargetPrefix { get; init; }

    public IReadOnlyList<string> Include { get; init; } = [];

    public IReadOnlyList<string> Exclude { get; init; } = [];
}

public sealed record JsonStep : PipelineStep
{
    public override string Type => "json";

    public required string Field { get; init; }

    public string? TargetPrefix { get; init; }

    /// <summary>İç içe nesneler <c>a.b.c</c> olarak düzleştirilir.</summary>
    public bool Flatten { get; init; } = true;
}

public sealed record CsvStep : PipelineStep
{
    public override string Type => "csv";

    public required string Field { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }

    public char Separator { get; init; } = ',';

    public char Quote { get; init; } = '"';

    public bool TrimWhitespace { get; init; } = true;
}

public sealed record DateStep : PipelineStep
{
    public override string Type => "date";

    public required string Field { get; init; }

    /// <summary>
    /// .NET biçim dizeleri artı özel belirteçler: <c>UNIX</c>, <c>UNIX_MS</c>,
    /// <c>ISO8601</c>, <c>SYSLOG</c> (RFC3164 — yıl yok).
    /// </summary>
    public required IReadOnlyList<string> Formats { get; init; }

    /// <summary>Saat dilimini taşıyan alan adı (cihaz gönderiyorsa).</summary>
    public string? TimezoneField { get; init; }

    /// <summary>Alan yoksa/çözülemezse kullanılan IANA saat dilimi.</summary>
    public string DefaultTimezone { get; init; } = "UTC";

    /// <summary>Sonucun yazılacağı alan. Varsayılan olay zaman damgasıdır.</summary>
    public string Target { get; init; } = ParseContext.TimestampField;
}

public sealed record ConvertStep : PipelineStep
{
    public override string Type => "convert";

    public required IReadOnlyDictionary<string, GrokFieldType> Fields { get; init; }
}

public sealed record DropStep : PipelineStep
{
    public override string Type => "drop";

    public required IReadOnlyList<string> Fields { get; init; }
}

public abstract record MapValue;

/// <summary>Sabit değer: <c>class_uid: 4001</c>.</summary>
public sealed record LiteralMapValue(object Value) : MapValue;

/// <summary>Şablon: <c>"{{ srcip }}"</c> veya <c>"{{ a }}:{{ b }}"</c>.</summary>
public sealed record TemplateMapValue(string Template, IReadOnlyList<string> Fields) : MapValue;

/// <summary>Eşleme tablosu: <c>{ from: action, table: ocsf_network_activity }</c>.</summary>
public sealed record LookupMapValue(string From, string Table, object? Default) : MapValue;

public sealed record ParserMap
{
    public IReadOnlyDictionary<string, MapValue> Core { get; init; } =
        new Dictionary<string, MapValue>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, MapValue> Ocsf { get; init; } =
        new Dictionary<string, MapValue>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, MapValue> Otel { get; init; } =
        new Dictionary<string, MapValue>(StringComparer.Ordinal);
}

public sealed record ParserMetadata
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public string Vendor { get; init; } = string.Empty;

    public string Product { get; init; } = string.Empty;

    public string License { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Aday denemesi sırası (F1 §4.2 kademe 3). Yüksek olan önce denenir;
    /// dar kapsamlı parser'ın genel olandan önce koşması için.
    /// </summary>
    public int Specificity { get; init; }
}

public sealed record ParserMatch
{
    public IReadOnlyList<string> Transport { get; init; } = [];

    /// <summary>Aho-Corasick ön filtresine girecek literaller (T06).</summary>
    public IReadOnlyList<string> Contains { get; init; } = [];

    /// <summary>Envanterden gelen etiketler — en güçlü bağ.</summary>
    public IReadOnlyDictionary<string, string> SourceLabels { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record ParserTestCase
{
    public required string Name { get; init; }

    public required string Input { get; init; }

    /// <summary>
    /// <c>core.src_ip</c>, <c>ocsf.class_uid</c>, <c>fields.srcport</c>,
    /// <c>parse_status</c>, <c>tags</c> anahtarları.
    /// </summary>
    public required IReadOnlyDictionary<string, object?> Expect { get; init; }

    public int Line { get; init; }
}

/// <summary>YAML parser plugin'inin doğrulanmış hali (F1 §3).</summary>
public sealed record ParserDefinition
{
    public required string ApiVersion { get; init; }

    public required ParserMetadata Metadata { get; init; }

    public ParserMatch Match { get; init; } = new();

    public required IReadOnlyList<PipelineStep> Pipeline { get; init; }

    public ParserMap Map { get; init; } = new();

    public IReadOnlyList<ParserTestCase> Tests { get; init; } = [];

    /// <summary>Parser genelinde geçerli grok pattern tanımları.</summary>
    public IReadOnlyDictionary<string, string> PatternDefinitions { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string SourcePath { get; init; } = string.Empty;

    /// <summary>Derlenmiş grok önbelleğinin anahtarı (T05 kabul kriteri).</summary>
    public string CacheKey => $"{Metadata.Id}@{Metadata.Version}";
}
