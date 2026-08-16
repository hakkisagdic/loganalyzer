using System.Collections.Concurrent;
using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Schema;

namespace Bizigo.Parsing.Engine;

public sealed record ParserCompilationResult(
    CompiledParser? Parser,
    IReadOnlyList<ParserSchemaError> Errors)
{
    public bool Ok => Errors.Count == 0 && Parser is not null;

    public CompiledParser Value => Parser
        ?? throw new InvalidOperationException("Parser derlenemedi: " +
            string.Join(Environment.NewLine, Errors.Select(static e => e.ToString())));
}

/// <summary>
/// <see cref="ParserDefinition"/> → <see cref="CompiledParser"/>.
///
/// <para>
/// Derlenmiş sonuç <c>parser_id@version</c> anahtarıyla önbelleklenir (T05).
/// Anahtarda sürümün olması şart: replay sırasında aynı id'nin iki sürümü
/// aynı süreçte koşar, önbellek sürümü unutursa düzeltilmiş parser eski
/// pattern'lerle çalışır ve kimse fark etmez.
/// </para>
/// </summary>
public sealed class ParserCompiler
{
    private readonly GrokCompiler _grok;
    private readonly MappingTableCatalog _tables;
    private readonly ConcurrentDictionary<string, CompiledParser> _cache = new(StringComparer.Ordinal);

    public ParserCompiler(GrokCompiler grok, MappingTableCatalog? tables = null)
    {
        _grok = grok ?? throw new ArgumentNullException(nameof(grok));
        _tables = tables ?? MappingTableCatalog.Empty;
    }

    public MappingTableCatalog Tables => _tables;

    public ParserCompilationResult Compile(ParserDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (_cache.TryGetValue(definition.CacheKey, out var cached))
        {
            return new ParserCompilationResult(cached, []);
        }

        var errors = new List<ParserSchemaError>();
        var steps = new List<ICompiledStep>(definition.Pipeline.Count);
        var parserGrok = _grok.With(definition.PatternDefinitions);

        foreach (var step in definition.Pipeline)
        {
            switch (step)
            {
                case GrokStep grokStep:
                {
                    var compiler = parserGrok.With(grokStep.PatternDefinitions);
                    var patterns = new List<CompiledGrok>(grokStep.Patterns.Count);

                    foreach (var pattern in grokStep.Patterns)
                    {
                        try
                        {
                            patterns.Add(compiler.Compile(pattern));
                        }
                        catch (GrokCompilationException ex)
                        {
                            errors.Add(new ParserSchemaError(
                                definition.SourcePath, step.Line, 1, ex.Message));
                        }
                    }

                    steps.Add(new CompiledGrokStep(grokStep, patterns));
                    break;
                }

                case KvStep kvStep:
                    steps.Add(new CompiledKvStep(kvStep));
                    break;

                case JsonStep jsonStep:
                    steps.Add(new CompiledJsonStep(jsonStep));
                    break;

                case CsvStep csvStep:
                    steps.Add(new CompiledCsvStep(csvStep));
                    break;

                case DateStep dateStep:
                    steps.Add(new CompiledDateStep(dateStep));
                    break;

                case ConvertStep convertStep:
                    steps.Add(new CompiledConvertStep(convertStep));
                    break;

                case DropStep dropStep:
                    steps.Add(new CompiledDropStep(dropStep));
                    break;

                default:
                    errors.Add(new ParserSchemaError(
                        definition.SourcePath, step.Line, 1, $"Derlenemeyen adım tipi: {step.Type}"));
                    break;
            }
        }

        // Eşleme tablosu eksikse bu bir şema hatasıdır, çalışma anı sürprizi değil.
        foreach (var (section, values) in new[]
                 {
                     ("core", definition.Map.Core),
                     ("ocsf", definition.Map.Ocsf),
                     ("otel", definition.Map.Otel),
                 })
        {
            foreach (var (key, value) in values)
            {
                if (value is LookupMapValue lookup && !_tables.Contains(lookup.Table))
                {
                    errors.Add(new ParserSchemaError(
                        definition.SourcePath, 0, 0,
                        $"map.{section}.{key}: bilinmeyen eşleme tablosu '{lookup.Table}'. " +
                        $"Mevcut tablolar: {string.Join(", ", _tables.TableNames.Order(StringComparer.Ordinal))}."));
                }
            }
        }

        if (errors.Count > 0)
        {
            return new ParserCompilationResult(null, errors);
        }

        var parser = new CompiledParser(definition, steps, _tables);
        _cache[definition.CacheKey] = parser;
        return new ParserCompilationResult(parser, errors);
    }

    /// <summary>Yükleme + derleme tek adımda — CLI ve testlerin kullandığı yol.</summary>
    public ParserCompilationResult CompileFile(string path)
    {
        var loaded = ParserYamlLoader.LoadFile(path);
        return loaded.Ok
            ? Compile(loaded.Value)
            : new ParserCompilationResult(null, loaded.Errors);
    }
}
