using System.Globalization;
using Bizigo.Parsing.Grok;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Bizigo.Parsing.Schema;

/// <summary>
/// YAML parser plugin'ini okur ve doğrular.
///
/// <para>
/// Nesneye seri-çözme (deserialization) yerine <b>temsil modeli</b> kullanılıyor.
/// Sebebi tek: <see cref="YamlNode.Start"/> satır/sütun taşıyor. Öznitelikli
/// seri-çözme daha az kod olurdu ama hata mesajı "beklenmeyen tip" seviyesinde
/// kalırdı — parser YAML'ını yazan kişi için işe yaramaz.
/// </para>
/// <para>
/// Bilinmeyen anahtarlar <b>hata</b>dır. Sessizce yok saymak, <c>seperator</c>
/// yazan bir kullanıcının parser'ının neden çalışmadığını saatlerce aratır.
/// </para>
/// </summary>
public static class ParserYamlLoader
{
    public const string ExpectedApiVersion = "bizigo.dev/v1";
    public const string ExpectedKind = "Parser";

    /// <summary>
    /// <c>map.core</c> altında izin verilen alanlar. Kapalı liste bilinçli:
    /// <c>core</c> ClickHouse'taki sıcak kolonların birebir karşılığı, serbest
    /// alan <c>attrs</c>'a gider.
    /// </summary>
    public static readonly IReadOnlySet<string> CoreFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "ts", "host", "vendor", "product",
        "src_ip", "dst_ip", "src_port", "dst_port",
        "proto", "action", "user_name", "severity_num", "outcome", "body",
    };

    private static readonly IReadOnlySet<string> RootKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "apiVersion", "kind", "metadata", "match", "pipeline", "map", "tests", "pattern_definitions",
    };

    public static ParserLoadResult LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Load(File.ReadAllText(path), path);
    }

    public static ParserLoadResult Load(string yaml, string path = "<inline>")
    {
        var errors = new List<ParserSchemaError>();

        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);

            if (stream.Documents.Count == 0)
            {
                errors.Add(new ParserSchemaError(path, 1, 1, "YAML dosyası boş."));
                return new ParserLoadResult(null, errors);
            }

            if (stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                var node = stream.Documents[0].RootNode;
                errors.Add(Error(path, node, "Kök düğüm bir eşleme (mapping) olmalı."));
                return new ParserLoadResult(null, errors);
            }

            root = mapping;
        }
        catch (YamlException ex)
        {
            errors.Add(new ParserSchemaError(path, (int)ex.Start.Line, (int)ex.Start.Column, "YAML söz dizimi hatası: " + ex.Message));
            return new ParserLoadResult(null, errors);
        }

        RejectUnknownKeys(path, root, RootKeys, "kök", errors);

        var apiVersion = RequireScalar(path, root, "apiVersion", errors);
        if (apiVersion is not null && !string.Equals(apiVersion, ExpectedApiVersion, StringComparison.Ordinal))
        {
            errors.Add(Error(path, root["apiVersion"],
                $"Desteklenmeyen apiVersion '{apiVersion}'. Beklenen: {ExpectedApiVersion}."));
        }

        var kind = RequireScalar(path, root, "kind", errors);
        if (kind is not null && !string.Equals(kind, ExpectedKind, StringComparison.Ordinal))
        {
            errors.Add(Error(path, root["kind"], $"Desteklenmeyen kind '{kind}'. Beklenen: {ExpectedKind}."));
        }

        var metadata = ReadMetadata(path, root, errors);
        var match = ReadMatch(path, root, errors);
        var patternDefinitions = ReadStringMap(path, root, "pattern_definitions", errors);
        var pipeline = ReadPipeline(path, root, errors);
        var map = ReadMap(path, root, errors);
        var tests = ReadTests(path, root, errors);

        if (tests.Count == 0)
        {
            // F1 §3: testsiz parser yayınlanamaz. Kalite için tek en ucuz kaldıraç,
            // ve bunu şema düzeyinde zorlamazsak hiç yazılmaz.
            errors.Add(Error(path, root, "En az bir `tests` girdisi zorunlu — testsiz parser yayınlanamaz."));
        }

        if (errors.Count > 0 || metadata is null)
        {
            return new ParserLoadResult(null, errors);
        }

        var definition = new ParserDefinition
        {
            ApiVersion = apiVersion!,
            Metadata = metadata,
            Match = match,
            Pipeline = pipeline,
            Map = map,
            Tests = tests,
            PatternDefinitions = patternDefinitions,
            SourcePath = path,
        };

        return new ParserLoadResult(definition, errors);
    }

    // ---------------------------------------------------------------- metadata

    private static readonly IReadOnlySet<string> MetadataKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "id", "version", "vendor", "product", "license", "description", "specificity",
    };

    private static ParserMetadata? ReadMetadata(string path, YamlMappingNode root, List<ParserSchemaError> errors)
    {
        if (!TryGetMapping(path, root, "metadata", required: true, errors, out var node))
        {
            return null;
        }

        RejectUnknownKeys(path, node, MetadataKeys, "metadata", errors);

        var id = RequireScalar(path, node, "id", errors);
        var version = RequireScalar(path, node, "version", errors);

        if (id is not null && !IsValidParserId(id))
        {
            errors.Add(Error(path, node["id"],
                $"Geçersiz parser id '{id}'. Beklenen biçim: nokta ile ayrılmış küçük harf, örn. fortinet.fortigate.traffic."));
        }

        if (version is not null && !System.Version.TryParse(version, out _))
        {
            errors.Add(Error(path, node["version"],
                $"Geçersiz sürüm '{version}'. Semantik sürüm bekleniyor, örn. 1.3.0."));
        }

        var specificity = 0;
        if (node.Children.TryGetValue(new YamlScalarNode("specificity"), out var specificityNode))
        {
            if (specificityNode is YamlScalarNode { Value: { } raw } &&
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                specificity = parsed;
            }
            else
            {
                errors.Add(Error(path, specificityNode, "`specificity` bir tam sayı olmalı."));
            }
        }

        if (id is null || version is null)
        {
            return null;
        }

        return new ParserMetadata
        {
            Id = id,
            Version = version,
            Vendor = OptionalScalar(node, "vendor") ?? string.Empty,
            Product = OptionalScalar(node, "product") ?? string.Empty,
            License = OptionalScalar(node, "license") ?? string.Empty,
            Description = OptionalScalar(node, "description") ?? string.Empty,
            Specificity = specificity,
        };
    }

    private static bool IsValidParserId(string id)
    {
        if (id.Length == 0 || id.StartsWith('.') || id.EndsWith('.') || id.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in id)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '.' or '_' or '-';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------- match

    private static readonly IReadOnlySet<string> MatchKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "transport", "contains", "source_labels",
    };

    private static ParserMatch ReadMatch(string path, YamlMappingNode root, List<ParserSchemaError> errors)
    {
        if (!TryGetMapping(path, root, "match", required: false, errors, out var node))
        {
            return new ParserMatch();
        }

        RejectUnknownKeys(path, node, MatchKeys, "match", errors);

        return new ParserMatch
        {
            Transport = ReadStringList(path, node, "transport", errors),
            Contains = ReadStringList(path, node, "contains", errors),
            SourceLabels = ReadStringMap(path, node, "source_labels", errors),
        };
    }

    // ---------------------------------------------------------------- pipeline

    private static IReadOnlyList<PipelineStep> ReadPipeline(
        string path, YamlMappingNode root, List<ParserSchemaError> errors)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("pipeline"), out var raw))
        {
            errors.Add(Error(path, root, "`pipeline` zorunlu."));
            return [];
        }

        if (raw is not YamlSequenceNode sequence)
        {
            errors.Add(Error(path, raw, "`pipeline` bir dizi olmalı."));
            return [];
        }

        if (sequence.Children.Count == 0)
        {
            errors.Add(Error(path, raw, "`pipeline` boş olamaz."));
            return [];
        }

        var steps = new List<PipelineStep>(sequence.Children.Count);

        foreach (var element in sequence.Children)
        {
            if (element is not YamlMappingNode stepNode || stepNode.Children.Count != 1)
            {
                errors.Add(Error(path, element,
                    "Her boru hattı adımı tek anahtarlı bir eşleme olmalı, örn. `- grok: { ... }`."));
                continue;
            }

            var (keyNode, optionsNode) = stepNode.Children.First();
            var type = (keyNode as YamlScalarNode)?.Value ?? string.Empty;

            if (optionsNode is not YamlMappingNode options)
            {
                errors.Add(Error(path, optionsNode, $"`{type}` adımının seçenekleri bir eşleme olmalı."));
                continue;
            }

            var step = ReadStep(path, type, options, errors);
            if (step is not null)
            {
                steps.Add(step);
            }
        }

        return steps;
    }

    private static readonly IReadOnlySet<string> CommonStepKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "on_failure", "tag",
    };

    private static PipelineStep? ReadStep(
        string path, string type, YamlMappingNode options, List<ParserSchemaError> errors)
    {
        var onFailure = OnFailure.Fail;
        if (options.Children.TryGetValue(new YamlScalarNode("on_failure"), out var onFailureNode))
        {
            var value = (onFailureNode as YamlScalarNode)?.Value;
            onFailure = value switch
            {
                "fail" => OnFailure.Fail,
                "continue" => OnFailure.Continue,
                "tag" => OnFailure.Tag,
                _ => Invalid(),
            };

            OnFailure Invalid()
            {
                errors.Add(Error(path, onFailureNode,
                    $"Geçersiz `on_failure` değeri '{value}'. Geçerli: fail, continue, tag."));
                return OnFailure.Fail;
            }
        }

        var tag = OptionalScalar(options, "tag");
        var line = (int)options.Start.Line;

        PipelineStep? step = type switch
        {
            "grok" => ReadGrokStep(path, options, errors),
            "kv" => ReadKvStep(path, options, errors),
            "json" => ReadJsonStep(path, options, errors),
            "csv" => ReadCsvStep(path, options, errors),
            "date" => ReadDateStep(path, options, errors),
            "convert" => ReadConvertStep(path, options, errors),
            "drop" => ReadDropStep(path, options, errors),
            _ => null,
        };

        if (step is null)
        {
            if (type is not ("grok" or "kv" or "json" or "csv" or "date" or "convert" or "drop"))
            {
                errors.Add(Error(path, options,
                    $"Bilinmeyen adım tipi '{type}'. Geçerli: grok, kv, json, csv, date, convert, drop."));
            }

            return null;
        }

        return step with { OnFailure = onFailure, Tag = tag, Line = line };
    }

    private static readonly IReadOnlySet<string> GrokKeys =
        Union(CommonStepKeys, "field", "patterns", "pattern_definitions");

    private static PipelineStep? ReadGrokStep(string path, YamlMappingNode options, List<ParserSchemaError> errors)
    {
        RejectUnknownKeys(path, options, GrokKeys, "grok", errors);

        var patterns = ReadStringList(path, options, "patterns", errors);
        if (patterns.Count == 0)
        {
            errors.Add(Error(path, options, "`grok` adımı en az bir `patterns` girdisi ister."));
            return null;
        }

        return new GrokStep
        {
            Field = OptionalScalar(options, "field") ?? ParseContext.MessageField,
            Patterns = patterns,
            PatternDefinitions = ReadStringMap(path, options, "pattern_definitions", errors),
        };
    }

    private static readonly IReadOnlySet<string> KvKeys =
        Union(CommonStepKeys, "field", "separator", "assign", "quoted", "target_prefix", "include", "exclude");

    private static PipelineStep ReadKvStep(string path, YamlMappingNode options, List<ParserSchemaError> errors)
    {
        RejectUnknownKeys(path, options, KvKeys, "kv", errors);

        return new KvStep
        {
            Field = OptionalScalar(options, "field") ?? ParseContext.MessageField,
            Separator = OptionalScalar(options, "separator") ?? " ",
            Assign = OptionalScalar(options, "assign") ?? "=",
            Quoted = ReadBool(path, options, "quoted", defaultValue: true, errors),
            TargetPrefix = OptionalScalar(options, "target_prefix"),
            Include = ReadStringList(path, options, "include", errors),
            Exclude = ReadStringList(path, options, "exclude", errors),
        };
    }

    private static readonly IReadOnlySet<string> JsonKeys =
        Union(CommonStepKeys, "field", "target_prefix", "flatten");

    private static PipelineStep ReadJsonStep(string path, YamlMappingNode options, List<ParserSchemaError> errors)
    {
        RejectUnknownKeys(path, options, JsonKeys, "json", errors);

        return new JsonStep
        {
            Field = OptionalScalar(options, "field") ?? ParseContext.MessageField,
            TargetPrefix = OptionalScalar(options, "target_prefix"),
            Flatten = ReadBool(path, options, "flatten", defaultValue: true, errors),
        };
    }

    private static readonly IReadOnlySet<string> CsvKeys =
        Union(CommonStepKeys, "field", "columns", "separator", "quote", "trim");

    private static PipelineStep? ReadCsvStep(string path, YamlMappingNode options, List<ParserSchemaError> errors)
    {
        RejectUnknownKeys(path, options, CsvKeys, "csv", errors);

        var columns = ReadStringList(path, options, "columns", errors);
        if (columns.Count == 0)
        {
            errors.Add(Error(path, options, "`csv` adımı `columns` listesi ister."));
            return null;
        }

        return new CsvStep
        {
            Field = OptionalScalar(options, "field") ?? ParseContext.MessageField,
            Columns = columns,
            Separator = ReadChar(path, options, "separator", ',', errors),
            Quote = ReadChar(path, options, "quote", '"', errors),
            TrimWhitespace = ReadBool(path, options, "trim", defaultValue: true, errors),
        };
    }

    private static readonly IReadOnlySet<string> DateKeys =
        Union(CommonStepKeys, "field", "formats", "timezone_field", "default_timezone", "target");

    private static PipelineStep? ReadDateStep(string path, YamlMappingNode options, List<ParserSchemaError> errors)
    {
        RejectUnknownKeys(path, options, DateKeys, "date", errors);

        var field = OptionalScalar(options, "field");
        if (field is null)
        {
            errors.Add(Error(path, options, "`date` adımı `field` ister."));
            return null;
        }

        var formats = ReadStringList(path, options, "formats", errors);
        if (formats.Count == 0)
        {
            errors.Add(Error(path, options, "`date` adımı en az bir `formats` girdisi ister."));
            return null;
        }

        var timezone = OptionalScalar(options, "default_timezone") ?? "UTC";
        if (!TimeZoneResolver.IsKnown(timezone))
        {
            errors.Add(Error(path, options["default_timezone"],
                $"Bilinmeyen saat dilimi '{timezone}'. IANA adı bekleniyor, örn. Europe/Istanbul."));
        }

        return new DateStep
        {
            Field = field,
            Formats = formats,
            TimezoneField = OptionalScalar(options, "timezone_field"),
            DefaultTimezone = timezone,
            Target = OptionalScalar(options, "target") ?? ParseContext.TimestampField,
        };
    }

    private static readonly IReadOnlySet<string> ConvertKeys = Union(CommonStepKeys, "fields");

    private static PipelineStep? ReadConvertStep(string path, YamlMappingNode options, List<ParserSchemaError> errors)
    {
        RejectUnknownKeys(path, options, ConvertKeys, "convert", errors);

        if (!TryGetMapping(path, options, "fields", required: true, errors, out var fieldsNode))
        {
            return null;
        }

        var fields = new Dictionary<string, GrokFieldType>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in fieldsNode.Children)
        {
            var name = (keyNode as YamlScalarNode)?.Value ?? string.Empty;
            var type = (valueNode as YamlScalarNode)?.Value ?? string.Empty;

            var parsed = type switch
            {
                "int" or "integer" => GrokFieldType.Int,
                "long" => GrokFieldType.Long,
                "float" => GrokFieldType.Float,
                "double" => GrokFieldType.Double,
                "bool" or "boolean" => GrokFieldType.Bool,
                "string" or "text" => GrokFieldType.String,
                _ => (GrokFieldType?)null,
            };

            if (parsed is null)
            {
                errors.Add(Error(path, valueNode,
                    $"Bilinmeyen tip '{type}' (alan: {name}). Geçerli: int, long, float, double, bool, string."));
                continue;
            }

            fields[name] = parsed.Value;
        }

        return new ConvertStep { Fields = fields };
    }

    private static readonly IReadOnlySet<string> DropKeys = Union(CommonStepKeys, "fields");

    private static PipelineStep? ReadDropStep(string path, YamlMappingNode options, List<ParserSchemaError> errors)
    {
        RejectUnknownKeys(path, options, DropKeys, "drop", errors);

        var fields = ReadStringList(path, options, "fields", errors);
        if (fields.Count == 0)
        {
            errors.Add(Error(path, options, "`drop` adımı `fields` listesi ister."));
            return null;
        }

        return new DropStep { Fields = fields };
    }

    // --------------------------------------------------------------------- map

    private static readonly IReadOnlySet<string> MapKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "core", "ocsf", "otel",
    };

    private static ParserMap ReadMap(string path, YamlMappingNode root, List<ParserSchemaError> errors)
    {
        if (!TryGetMapping(path, root, "map", required: false, errors, out var node))
        {
            return new ParserMap();
        }

        RejectUnknownKeys(path, node, MapKeys, "map", errors);

        var core = ReadMapSection(path, node, "core", errors);

        foreach (var key in core.Keys)
        {
            if (!CoreFields.Contains(key))
            {
                errors.Add(Error(path, node["core"],
                    $"Bilinmeyen `map.core` alanı '{key}'. Geçerli alanlar: {string.Join(", ", CoreFields.Order(StringComparer.Ordinal))}."));
            }
        }

        return new ParserMap
        {
            Core = core,
            Ocsf = ReadMapSection(path, node, "ocsf", errors),
            Otel = ReadMapSection(path, node, "otel", errors),
        };
    }

    private static IReadOnlyDictionary<string, MapValue> ReadMapSection(
        string path, YamlMappingNode map, string section, List<ParserSchemaError> errors)
    {
        var result = new Dictionary<string, MapValue>(StringComparer.Ordinal);

        if (!TryGetMapping(path, map, section, required: false, errors, out var node))
        {
            return result;
        }

        foreach (var (keyNode, valueNode) in node.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value;
            if (string.IsNullOrEmpty(key))
            {
                errors.Add(Error(path, keyNode, $"`map.{section}` içinde boş anahtar."));
                continue;
            }

            var value = ReadMapValue(path, $"map.{section}.{key}", valueNode, errors);
            if (value is not null)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static readonly IReadOnlySet<string> LookupKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "from", "table", "default",
    };

    private static MapValue? ReadMapValue(string path, string location, YamlNode node, List<ParserSchemaError> errors)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
            {
                var raw = scalar.Value ?? string.Empty;
                var fields = TemplateRenderer.ExtractFields(raw);
                if (fields.Count > 0)
                {
                    return new TemplateMapValue(raw, fields);
                }

                // Şablon içermeyen skaler sabittir. Sayı ise sayı olarak taşınır;
                // `class_uid: 4001` string'e dönerse OCSF filtresi bozulur.
                if (scalar.Style == YamlDotNet.Core.ScalarStyle.Plain)
                {
                    if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    {
                        return new LiteralMapValue(l);
                    }

                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        return new LiteralMapValue(d);
                    }

                    if (bool.TryParse(raw, out var b))
                    {
                        return new LiteralMapValue(b);
                    }
                }

                return new LiteralMapValue(raw);
            }

            case YamlMappingNode mapping:
            {
                RejectUnknownKeys(path, mapping, LookupKeys, location, errors);

                var from = OptionalScalar(mapping, "from");
                var table = OptionalScalar(mapping, "table");

                if (from is null || table is null)
                {
                    errors.Add(Error(path, mapping,
                        $"{location}: eşleme tablosu kullanımı `{{ from: <alan>, table: <tablo> }}` biçiminde olmalı."));
                    return null;
                }

                object? fallback = null;
                if (mapping.Children.TryGetValue(new YamlScalarNode("default"), out var defaultNode) &&
                    defaultNode is YamlScalarNode defaultScalar)
                {
                    fallback = long.TryParse(defaultScalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dl)
                        ? dl
                        : defaultScalar.Value;
                }

                return new LookupMapValue(from, table, fallback);
            }

            default:
                errors.Add(Error(path, node, $"{location}: değer skaler veya eşleme olmalı."));
                return null;
        }
    }

    // ------------------------------------------------------------------- tests

    private static readonly IReadOnlySet<string> TestKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "name", "input", "expect",
    };

    private static IReadOnlyList<ParserTestCase> ReadTests(
        string path, YamlMappingNode root, List<ParserSchemaError> errors)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("tests"), out var raw))
        {
            return [];
        }

        if (raw is not YamlSequenceNode sequence)
        {
            errors.Add(Error(path, raw, "`tests` bir dizi olmalı."));
            return [];
        }

        var tests = new List<ParserTestCase>(sequence.Children.Count);

        foreach (var element in sequence.Children)
        {
            if (element is not YamlMappingNode node)
            {
                errors.Add(Error(path, element, "Her test bir eşleme olmalı."));
                continue;
            }

            RejectUnknownKeys(path, node, TestKeys, "tests[]", errors);

            var name = RequireScalar(path, node, "name", errors);
            var input = RequireScalar(path, node, "input", errors);

            if (!TryGetMapping(path, node, "expect", required: true, errors, out var expectNode))
            {
                continue;
            }

            var expect = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (keyNode, valueNode) in expectNode.Children)
            {
                var key = (keyNode as YamlScalarNode)?.Value ?? string.Empty;
                expect[key] = ReadExpectedValue(valueNode);
            }

            if (name is null || input is null)
            {
                continue;
            }

            tests.Add(new ParserTestCase
            {
                Name = name,
                Input = input,
                Expect = expect,
                Line = (int)node.Start.Line,
            });
        }

        return tests;
    }

    /// <summary>
    /// YAML'ın <c>null</c> yazımları — <b>yalnızca düz (plain) skalerde</b>.
    ///
    /// <para>
    /// Tırnaklı <c>"null"</c> bilinçli olarak dışarıda: yazarın "bu alan hiç
    /// olmamalı" demesi ile "bu alanın değeri <c>null</c> metni" demesi iki
    /// ayrı beklenti ve ikisini birleştirmek, sessizce başka bir şey sınayan
    /// bir test bırakırdı (T08 raporu #6).
    /// </para>
    /// </summary>
    private static readonly HashSet<string> NullScalars =
        new(StringComparer.Ordinal) { string.Empty, "~", "null", "Null", "NULL" };

    private static object? ReadExpectedValue(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
            {
                var raw = scalar.Value;
                if (raw is null)
                {
                    return null;
                }

                if (scalar.Style != YamlDotNet.Core.ScalarStyle.Plain)
                {
                    return raw;
                }

                // `expect: core.user_name: null` → gerçek `null`, "null" metni
                // değil. `ValuesMatch` zaten `expected is null && actual is null`
                // durumunu doğru ele alıyor; eksik olan tek şey buraya kadar
                // gelen değerin `null` OLMASIYDI. Bu satır olmadan negatif alan
                // testi yazılamıyor ve katalogda yorumla açıklanıyordu.
                if (NullScalars.Contains(raw))
                {
                    return null;
                }

                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                {
                    return l;
                }

                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    return d;
                }

                if (bool.TryParse(raw, out var b))
                {
                    return b;
                }

                return raw;
            }

            case YamlSequenceNode sequence:
                return sequence.Children.Select(ReadExpectedValue).ToArray();

            default:
                return node.ToString();
        }
    }

    // ------------------------------------------------------------- yardımcılar

    private static IReadOnlySet<string> Union(IReadOnlySet<string> common, params string[] extra)
    {
        var set = new HashSet<string>(common, StringComparer.Ordinal);
        foreach (var item in extra)
        {
            set.Add(item);
        }

        return set;
    }

    private static void RejectUnknownKeys(
        string path, YamlMappingNode node, IReadOnlySet<string> allowed, string context, List<ParserSchemaError> errors)
    {
        foreach (var (keyNode, _) in node.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value;
            if (key is null || allowed.Contains(key))
            {
                continue;
            }

            var suggestion = allowed
                .Where(candidate => Levenshtein(candidate, key) <= 2)
                .OrderBy(candidate => Levenshtein(candidate, key))
                .FirstOrDefault();

            errors.Add(Error(path, keyNode,
                $"{context} içinde bilinmeyen anahtar '{key}'." +
                (suggestion is null ? string.Empty : $" '{suggestion}' mi demek istediniz?")));
        }
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static bool TryGetMapping(
        string path, YamlMappingNode parent, string key, bool required,
        List<ParserSchemaError> errors, out YamlMappingNode node)
    {
        node = null!;

        if (!parent.Children.TryGetValue(new YamlScalarNode(key), out var raw))
        {
            if (required)
            {
                errors.Add(Error(path, parent, $"`{key}` zorunlu."));
            }

            return false;
        }

        if (raw is not YamlMappingNode mapping)
        {
            errors.Add(Error(path, raw, $"`{key}` bir eşleme olmalı."));
            return false;
        }

        node = mapping;
        return true;
    }

    private static string? RequireScalar(
        string path, YamlMappingNode node, string key, List<ParserSchemaError> errors)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var raw))
        {
            errors.Add(Error(path, node, $"`{key}` zorunlu."));
            return null;
        }

        if (raw is not YamlScalarNode { Value: { } value } || value.Length == 0)
        {
            errors.Add(Error(path, raw, $"`{key}` boş olmayan bir metin olmalı."));
            return null;
        }

        return value;
    }

    private static string? OptionalScalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var raw) && raw is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static bool ReadBool(
        string path, YamlMappingNode node, string key, bool defaultValue, List<ParserSchemaError> errors)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var raw))
        {
            return defaultValue;
        }

        if (raw is YamlScalarNode { Value: { } value } && bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        errors.Add(Error(path, raw, $"`{key}` true veya false olmalı."));
        return defaultValue;
    }

    private static char ReadChar(
        string path, YamlMappingNode node, string key, char defaultValue, List<ParserSchemaError> errors)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var raw))
        {
            return defaultValue;
        }

        if (raw is YamlScalarNode { Value: { Length: 1 } value })
        {
            return value[0];
        }

        errors.Add(Error(path, raw, $"`{key}` tek karakter olmalı."));
        return defaultValue;
    }

    private static IReadOnlyList<string> ReadStringList(
        string path, YamlMappingNode node, string key, List<ParserSchemaError> errors)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var raw))
        {
            return [];
        }

        switch (raw)
        {
            case YamlScalarNode { Value: { } single }:
                return [single];

            case YamlSequenceNode sequence:
            {
                var list = new List<string>(sequence.Children.Count);
                foreach (var element in sequence.Children)
                {
                    if (element is YamlScalarNode { Value: { } value })
                    {
                        list.Add(value);
                    }
                    else
                    {
                        errors.Add(Error(path, element, $"`{key}` yalnızca metin öğeleri alabilir."));
                    }
                }

                return list;
            }

            default:
                errors.Add(Error(path, raw, $"`{key}` metin veya metin dizisi olmalı."));
                return [];
        }
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(
        string path, YamlMappingNode node, string key, List<ParserSchemaError> errors)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var raw))
        {
            return result;
        }

        if (raw is not YamlMappingNode mapping)
        {
            errors.Add(Error(path, raw, $"`{key}` bir eşleme olmalı."));
            return result;
        }

        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            var name = (keyNode as YamlScalarNode)?.Value;
            var value = (valueNode as YamlScalarNode)?.Value;

            if (name is null || value is null)
            {
                errors.Add(Error(path, keyNode, $"`{key}` yalnızca metin → metin eşlemesi alabilir."));
                continue;
            }

            result[name] = value;
        }

        return result;
    }

    private static ParserSchemaError Error(string path, YamlNode node, string message) =>
        new(path, (int)node.Start.Line, (int)node.Start.Column, message);
}
