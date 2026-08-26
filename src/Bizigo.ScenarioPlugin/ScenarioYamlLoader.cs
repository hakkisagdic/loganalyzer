using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Bizigo.ScenarioPlugin;

/// <summary>
/// Senaryo plugin'inin <b>yükleme anı</b> kapısı (F4 plugin formatı §6.1).
///
/// <para>
/// Doğruladığı şeyler bir liste değil bir <b>sınır</b>: zarf. Sağlayıcı adı
/// kayıtlı mı, blok iyi biçimli mi, her adımın kısıtı ya da gerekçeli muafiyeti
/// var mı. Kanıt bloğunun <b>içeriği</b> bu kapının arkasından geçiyor ve
/// doğrulaması sağlayıcıya ait — çekirdeğin şekli bilmesi gerekmiyor.
/// </para>
///
/// <para>
/// Kapının varlık sebebi §3.2: bugünkü formatta <c>constraint</c> satırı
/// yazılmazsa senaryo <b>sessizce geçerli</b> sayılıyordu. Yani format, tek
/// gerçek güvencesinin uygulandığı senaryoyu uygulanmadığından ayırt edemiyordu.
/// </para>
/// </summary>
public static class ScenarioYamlLoader
{
    public const string ExpectedApiVersion = "bizigo.dev/v1";
    public const string ExpectedKind = "Scenario";

    private static readonly IReadOnlySet<string> RootKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "apiVersion", "kind", "metadata", "spec",
    };

    private static readonly IReadOnlySet<string> MetadataKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "id", "version", "owner", "description",
    };

    private static readonly IReadOnlySet<string> SpecKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "trigger", "subject", "evidence", "steps", "publish",
    };

    private static readonly IReadOnlySet<string> TriggerKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "on",
    };

    private static readonly IReadOnlySet<string> SubjectKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "kind",
    };

    private static readonly IReadOnlySet<string> PublishKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "requires_review", "target",
    };

    private static readonly IReadOnlySet<string> StepKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "id", "task", "input", "output",
    };

    private static readonly IReadOnlySet<string> OutputKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "schema", "constraints", "constraints_waived", "max_items",
    };

    public static ScenarioLoadResult LoadFile(
        string path,
        IScenarioProviderRegistry registry,
        IReadOnlySet<string>? triggers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Load(File.ReadAllText(path), registry, path, triggers);
    }

    public static ScenarioLoadResult Load(
        string yaml,
        IScenarioProviderRegistry registry,
        string path = "<inline>",
        IReadOnlySet<string>? triggers = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var vocabulary = triggers ?? ScenarioTriggers.Known;
        var errors = new List<ScenarioSchemaError>();

        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yaml ?? string.Empty);
            stream.Load(reader);

            if (stream.Documents.Count == 0)
            {
                errors.Add(new ScenarioSchemaError(path, 1, 1, "YAML dosyası boş."));
                return new ScenarioLoadResult(null, errors);
            }

            if (stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                errors.Add(Error(path, stream.Documents[0].RootNode, "Kök düğüm bir eşleme (mapping) olmalı."));
                return new ScenarioLoadResult(null, errors);
            }

            root = mapping;
        }
        catch (YamlException ex)
        {
            errors.Add(new ScenarioSchemaError(
                path, (int)ex.Start.Line, (int)ex.Start.Column, "YAML söz dizimi hatası: " + ex.Message));
            return new ScenarioLoadResult(null, errors);
        }
        catch (InvalidOperationException ex)
        {
            // ÖLÇÜLDÜ: YamlDotNet 18.1 her bozuk girdide `YamlException`
            // fırlatmıyor — kapanmamış bir akış dizisi (`[a\nb`) tarayıcıdan
            // çıplak `InvalidOperationException` olarak sızıyor. Yalnızca
            // `YamlException` yakalansaydı bozuk bir senaryo dosyası, hata
            // listesi yerine çağıranın yüzüne patlardı: dosya adı da satır da
            // kaybolurdu. Konum bilgisi bu yolda yok, o yüzden 1:1.
            errors.Add(new ScenarioSchemaError(path, 1, 1, "YAML söz dizimi hatası: " + ex.Message));
            return new ScenarioLoadResult(null, errors);
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

        if (!TryGetMapping(path, root, "spec", required: true, errors, out var spec))
        {
            return new ScenarioLoadResult(null, errors);
        }

        RejectUnknownKeys(path, spec, SpecKeys, "spec", errors);

        var triggerValues = ReadTriggers(path, spec, vocabulary, errors);
        var subject = ReadSubject(path, spec, errors);
        var evidence = ReadEvidence(path, spec, registry, errors);
        var steps = ReadSteps(path, spec, errors);
        var publish = ReadPublish(path, spec, errors);

        if (errors.Count > 0 || metadata is null || evidence is null || publish is null)
        {
            return new ScenarioLoadResult(null, errors);
        }

        var definition = new ScenarioDefinition
        {
            ApiVersion = apiVersion!,
            Metadata = metadata,
            Triggers = triggerValues,
            Evidence = evidence,
            Steps = steps,
            Publish = publish,
            Subject = subject,
            SourcePath = path,
        };

        return new ScenarioLoadResult(definition, errors);
    }

    // ---------------------------------------------------------------- metadata

    private static ScenarioMetadata? ReadMetadata(
        string path, YamlMappingNode root, List<ScenarioSchemaError> errors)
    {
        if (!TryGetMapping(path, root, "metadata", required: true, errors, out var node))
        {
            return null;
        }

        RejectUnknownKeys(path, node, MetadataKeys, "metadata", errors);

        var id = RequireScalar(path, node, "id", errors);
        var version = RequireScalar(path, node, "version", errors);
        var owner = RequireScalar(path, node, "owner", errors);

        if (id is not null && !IsValidScenarioId(id))
        {
            errors.Add(Error(path, node["id"],
                $"Geçersiz senaryo id '{id}'. Beklenen biçim: nokta ile ayrılmış küçük harf, örn. builtin.rca.network."));
        }

        if (version is not null && !System.Version.TryParse(version, out _))
        {
            errors.Add(Error(path, node["version"],
                $"Geçersiz sürüm '{version}'. Semantik sürüm bekleniyor, örn. 1.0.0."));
        }

        if (id is null || version is null || owner is null)
        {
            return null;
        }

        return new ScenarioMetadata
        {
            Id = id,
            Version = version,
            Owner = owner,
            Description = OptionalScalar(node, "description") ?? string.Empty,
        };
    }

    private static bool IsValidScenarioId(string id)
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

    // ----------------------------------------------------------------- trigger

    private static IReadOnlyList<string> ReadTriggers(
        string path, YamlMappingNode spec, IReadOnlySet<string> vocabulary, List<ScenarioSchemaError> errors)
    {
        if (!TryGetMapping(path, spec, "trigger", required: true, errors, out var node))
        {
            return [];
        }

        RejectUnknownKeys(path, node, TriggerKeys, "trigger", errors);

        if (!node.Children.TryGetValue(new YamlScalarNode("on"), out var raw))
        {
            // Tetikleyicisiz bir senaryo hiç koşamaz. Sessizce kabul etmek,
            // yazanın "kurdum" sandığı ölü bir dosya bırakırdı.
            errors.Add(Error(path, node, "`trigger.on` zorunlu — tetikleyicisiz senaryo hiç koşamaz."));
            return [];
        }

        var values = ReadStringList(path, node, "on", errors);

        if (values.Count == 0)
        {
            errors.Add(Error(path, raw, "`trigger.on` boş olamaz."));
            return values;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                errors.Add(Error(path, raw, $"`trigger.on` içinde '{value}' iki kez geçiyor."));
                continue;
            }

            if (!vocabulary.Contains(value))
            {
                errors.Add(Error(path, raw,
                    $"Bilinmeyen tetikleyici '{value}'. Küme KAPALI (K20); bilinenler: " +
                    string.Join(", ", vocabulary.Order(StringComparer.Ordinal)) + ". " +
                    "Yeni tetikleyici eklemek bir çekirdek kararı, plugin kararı değil."));
            }
        }

        return values;
    }

    // ----------------------------------------------------------------- subject

    private static ScenarioSubject? ReadSubject(
        string path, YamlMappingNode spec, List<ScenarioSchemaError> errors)
    {
        if (!spec.Children.ContainsKey(new YamlScalarNode("subject")))
        {
            return null;
        }

        if (!TryGetMapping(path, spec, "subject", required: false, errors, out var node))
        {
            return null;
        }

        RejectUnknownKeys(path, node, SubjectKeys, "subject", errors);

        var kind = RequireScalar(path, node, "kind", errors);
        return kind is null ? null : new ScenarioSubject { Kind = kind };
    }

    // ---------------------------------------------------------------- evidence

    private static ScenarioEvidenceBlock? ReadEvidence(
        string path, YamlMappingNode spec, IScenarioProviderRegistry registry, List<ScenarioSchemaError> errors)
    {
        // "Blok iyi biçimli mi" — zarfın ilk sorusu. Bir dizi ya da skaler
        // `evidence`, sağlayıcıya hiç ulaşmadan burada duruyor.
        if (!TryGetMapping(path, spec, "evidence", required: true, errors, out var node))
        {
            return null;
        }

        if (!node.Children.TryGetValue(new YamlScalarNode("providers"), out var providersNode))
        {
            errors.Add(Error(path, node, "`evidence.providers` zorunlu."));
            return null;
        }

        var providers = ReadStringList(path, node, "providers", errors);

        if (providers.Count == 0)
        {
            errors.Add(Error(path, providersNode, "`evidence.providers` boş olamaz."));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (!seen.Add(provider))
            {
                errors.Add(Error(path, providersNode, $"`evidence.providers` içinde '{provider}' iki kez geçiyor."));
                continue;
            }

            if (!registry.IsRegistered(provider))
            {
                // YÜKLEME anında. Koşum anına ertelenseydi bozuk bir senaryo
                // kayıtlı görünür, arızası ilk tetiklendiğinde çıkardı.
                errors.Add(Error(path, providersNode,
                    $"Kayıtlı olmayan kanıt sağlayıcısı '{provider}'. Kayıtlı olanlar: " +
                    string.Join(", ", registry.ProviderIds.Order(StringComparer.Ordinal)) + "."));
            }
        }

        // `providers` dışındaki her şey içerik: çekirdek onu taşıyor, okumuyor.
        // Bilinmeyen anahtar REDDEDİLMİYOR ve bu bilinçli — `window`, `horizon`,
        // `sample` üç ayrı meşru şekil, ve dördüncüsünü yasaklamak uzantı
        // noktasını kapatmak olurdu (§5.2).
        var content = new Dictionary<string, ScenarioValue>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in node.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value;
            if (key is null || string.Equals(key, "providers", StringComparison.Ordinal))
            {
                continue;
            }

            content[key] = Convert(valueNode);
        }

        return new ScenarioEvidenceBlock
        {
            Providers = providers,
            Content = new ScenarioValue.Mapping(content),
        };
    }

    private static ScenarioValue Convert(YamlNode node) => node switch
    {
        YamlScalarNode scalar => new ScenarioValue.Scalar(scalar.Value ?? string.Empty),
        YamlSequenceNode sequence => new ScenarioValue.Sequence([.. sequence.Children.Select(Convert)]),
        YamlMappingNode mapping => new ScenarioValue.Mapping(
            mapping.Children
                .Where(pair => pair.Key is YamlScalarNode { Value: not null })
                .ToDictionary(
                    pair => ((YamlScalarNode)pair.Key).Value!,
                    pair => Convert(pair.Value),
                    StringComparer.Ordinal)),
        _ => new ScenarioValue.Scalar(string.Empty),
    };

    // ------------------------------------------------------------------- steps

    private static IReadOnlyList<ScenarioStep> ReadSteps(
        string path, YamlMappingNode spec, List<ScenarioSchemaError> errors)
    {
        if (!spec.Children.TryGetValue(new YamlScalarNode("steps"), out var raw))
        {
            errors.Add(Error(path, spec, "`steps` zorunlu."));
            return [];
        }

        if (raw is not YamlSequenceNode sequence)
        {
            errors.Add(Error(path, raw, "`steps` sıralı bir dizi olmalı."));
            return [];
        }

        if (sequence.Children.Count == 0)
        {
            errors.Add(Error(path, raw, "`steps` boş olamaz — en az bir adım zorunlu."));
            return [];
        }

        var steps = new List<ScenarioStep>(sequence.Children.Count);
        var defined = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in sequence.Children)
        {
            if (element is not YamlMappingNode node)
            {
                errors.Add(Error(path, element, "Her `steps` öğesi bir eşleme olmalı."));
                continue;
            }

            RejectUnknownKeys(path, node, StepKeys, "steps öğesi", errors);

            var id = RequireScalar(path, node, "id", errors);
            var task = RequireScalar(path, node, "task", errors);

            if (id is not null && !IsValidStepId(id))
            {
                errors.Add(Error(path, node["id"],
                    $"Geçersiz adım id '{id}'. Beklenen biçim: küçük harf ve tire, örn. bind-evidence."));
                id = null;
            }

            if (id is not null && !defined.Add(id))
            {
                errors.Add(Error(path, node["id"], $"Adım id '{id}' iki kez tanımlanmış."));
                id = null;
            }

            var input = ReadInput(path, node, defined, id, errors);
            var output = ReadOutput(path, node, id, errors);

            if (id is null || task is null || output is null)
            {
                continue;
            }

            steps.Add(new ScenarioStep { Id = id, Task = task, Input = input, Output = output });
        }

        return steps;
    }

    private static bool IsValidStepId(string id)
    {
        if (id.Length == 0 || id.StartsWith('-') || id.EndsWith('-'))
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Adım grafiği: <c>evidence.&lt;yol&gt;</c> ya da <c>steps.&lt;id&gt;</c>, ve
    /// <c>steps.</c> atıfları yalnızca <b>daha önce tanımlanmış</b> adımlara.
    ///
    /// <para>
    /// İleri atıf yasağı bir söz dizimi titizliği değil: kısıt doğrulaması
    /// <b>adımın gördüğü</b> kanıta karşı yapılıyor (§6). Henüz koşmamış bir
    /// adımın çıktısına atıf yapan adım, görmediği bir şeye dayanmış olurdu.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ReadInput(
        string path, YamlMappingNode node, IReadOnlySet<string> defined, string? selfId,
        List<ScenarioSchemaError> errors)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode("input"), out var raw))
        {
            errors.Add(Error(path, node, "`input` zorunlu — girdisi yazılmamış adım neye baktığını söylemiyor."));
            return [];
        }

        var values = ReadStringList(path, node, "input", errors);

        if (values.Count == 0)
        {
            errors.Add(Error(path, raw, "`input` boş olamaz."));
            return values;
        }

        foreach (var value in values)
        {
            if (value.StartsWith("evidence.", StringComparison.Ordinal) && value.Length > "evidence.".Length)
            {
                continue;
            }

            if (value.StartsWith("steps.", StringComparison.Ordinal) && value.Length > "steps.".Length)
            {
                var referenced = value["steps.".Length..];

                if (string.Equals(referenced, selfId, StringComparison.Ordinal))
                {
                    errors.Add(Error(path, raw, $"`input` kendi adımına atıf yapıyor: '{value}'."));
                }
                else if (!defined.Contains(referenced))
                {
                    errors.Add(Error(path, raw,
                        $"`input` tanımlanmamış ya da daha sonra gelen bir adıma atıf yapıyor: '{value}'. " +
                        "Adımlar sıralı; bir adım yalnızca kendinden ÖNCEKİ adımların çıktısını görebilir."));
                }

                continue;
            }

            errors.Add(Error(path, raw,
                $"Geçersiz `input` atfı '{value}'. Beklenen: `evidence.<yol>` ya da `steps.<id>`."));
        }

        return values;
    }

    /// <summary>
    /// §3.2'nin kapattığı boşluk. Kısıt listesi <b>ya dolu</b> ya da
    /// <b>gerekçesiyle</b> boş; üçüncü hâl yok.
    /// </summary>
    private static ScenarioOutput? ReadOutput(
        string path, YamlMappingNode node, string? stepId, List<ScenarioSchemaError> errors)
    {
        if (!TryGetMapping(path, node, "output", required: true, errors, out var output))
        {
            return null;
        }

        var label = stepId is null ? "adım" : $"'{stepId}' adımı";

        // Tekil `constraint` formatın eski hâli ve iki belgede hâlâ örnekli.
        // Levenshtein önerisine bırakmak yerine adı konmuş bir hata: okuyan
        // neyin değiştiğini de öğreniyor.
        if (output.Children.ContainsKey(new YamlScalarNode("constraint")))
        {
            errors.Add(Error(path, output,
                $"{label}: `constraint` (tekil) kaldırıldı. `constraints` listesi kullanın — " +
                "tek adımda iki kısıt gerekebiliyor (F4 plugin formatı §5.1(a))."));
        }

        RejectUnknownKeys(path, output, OutputKeys, "output", errors);

        var schema = RequireScalar(path, output, "schema", errors);
        var constraints = ReadStringList(path, output, "constraints", errors);
        var hasWaiverKey = output.Children.TryGetValue(new YamlScalarNode("constraints_waived"), out var waiverNode);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var constraint in constraints)
        {
            if (string.IsNullOrWhiteSpace(constraint))
            {
                errors.Add(Error(path, output["constraints"], $"{label}: boş kısıt adı."));
            }
            else if (!seen.Add(constraint))
            {
                errors.Add(Error(path, output["constraints"], $"{label}: '{constraint}' iki kez yazılmış."));
            }
        }

        string? waived = null;

        if (hasWaiverKey)
        {
            waived = (waiverNode as YamlScalarNode)?.Value;

            if (waiverNode is not YamlScalarNode)
            {
                errors.Add(Error(path, waiverNode!, $"{label}: `constraints_waived` bir metin olmalı."));
                waived = null;
            }
            else if (string.IsNullOrWhiteSpace(waived))
            {
                // Boş dize, muafiyeti tek harekete indirirdi: anahtarı yazmak
                // yeterdi. Gerekçe yazmak ile gerekçe yazmış GÖRÜNMEK arasındaki
                // farkı kapatan satır bu.
                errors.Add(Error(path, waiverNode,
                    $"{label}: `constraints_waived` boş olamaz — muafiyet gerekçesini dosyada TAŞIMAK zorunda."));
                waived = null;
            }
        }

        if (constraints.Count > 0 && hasWaiverKey)
        {
            errors.Add(Error(path, output,
                $"{label}: dolu bir `constraints` listesiyle `constraints_waived` birlikte yazılamaz. " +
                "Muafiyet, kısıtın OLMADIĞI hâlin adı; ikisi birlikte, uygulanan bir kapıyı muaf gösterirdi."));
        }
        else if (constraints.Count == 0 && !hasWaiverKey)
        {
            // §3.2'nin tamamı bu dal. Eskiden burası sessizce geçiyordu.
            errors.Add(Error(path, output,
                $"{label}: `constraints` ya da `constraints_waived` zorunlu. " +
                "Kısıtsız bir adım geçerli olabilir ama ADI OLMAK ZORUNDA — kapının uygulanmadığı hâl, " +
                "uygulandığı hâlden ayırt edilebilir olmalı (F4 plugin formatı §3.2)."));
        }

        var maxItems = ReadOptionalInt(path, output, "max_items", errors);

        if (schema is null)
        {
            return null;
        }

        return new ScenarioOutput
        {
            Schema = schema,
            Constraints = constraints,
            ConstraintsWaived = waived,
            MaxItems = maxItems,
        };
    }

    // ----------------------------------------------------------------- publish

    private static ScenarioPublish? ReadPublish(
        string path, YamlMappingNode spec, List<ScenarioSchemaError> errors)
    {
        if (!TryGetMapping(path, spec, "publish", required: true, errors, out var node))
        {
            return null;
        }

        RejectUnknownKeys(path, node, PublishKeys, "publish", errors);

        if (!node.Children.TryGetValue(new YamlScalarNode("requires_review"), out var raw))
        {
            // Varsayılan vermek K16'yı sessizce gevşetirdi: alanı yazmayı unutan
            // bir senaryo, aksiyon alsa bile onaysız yayınlanırdı.
            errors.Add(Error(path, node, "`publish.requires_review` zorunlu — varsayılanı YOK (K16)."));
            return null;
        }

        if (raw is not YamlScalarNode { Value: { } value } || !bool.TryParse(value, out var requiresReview))
        {
            errors.Add(Error(path, raw, "`publish.requires_review` true veya false olmalı."));
            return null;
        }

        return new ScenarioPublish
        {
            RequiresReview = requiresReview,
            Target = OptionalScalar(node, "target"),
        };
    }

    // ---------------------------------------------------------------- yardımcı

    private static void RejectUnknownKeys(
        string path, YamlMappingNode node, IReadOnlySet<string> allowed, string context,
        List<ScenarioSchemaError> errors)
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
        List<ScenarioSchemaError> errors, out YamlMappingNode node)
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
        string path, YamlMappingNode node, string key, List<ScenarioSchemaError> errors)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var raw))
        {
            errors.Add(Error(path, node, $"`{key}` zorunlu."));
            return null;
        }

        if (raw is not YamlScalarNode { Value: { } value } || value.Trim().Length == 0)
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

    private static int? ReadOptionalInt(
        string path, YamlMappingNode node, string key, List<ScenarioSchemaError> errors)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var raw))
        {
            return null;
        }

        if (raw is YamlScalarNode { Value: { } value } &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        errors.Add(Error(path, raw, $"`{key}` bir tam sayı olmalı."));
        return null;
    }

    private static IReadOnlyList<string> ReadStringList(
        string path, YamlMappingNode node, string key, List<ScenarioSchemaError> errors)
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
                errors.Add(Error(path, raw, $"`{key}` metin ya da metin dizisi olmalı."));
                return [];
        }
    }

    private static ScenarioSchemaError Error(string path, YamlNode node, string message) =>
        new(path, (int)node.Start.Line, (int)node.Start.Column, message);
}
