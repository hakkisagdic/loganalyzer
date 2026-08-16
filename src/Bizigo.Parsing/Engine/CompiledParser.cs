using Bizigo.Contracts;
using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Schema;

namespace Bizigo.Parsing.Engine;

/// <summary>
/// Çalıştırılabilir parser. <see cref="ParserCompiler"/> üretir;
/// <c>parser_id + version</c> anahtarıyla önbelleklenir.
/// </summary>
public sealed class CompiledParser
{
    private readonly MappingTableCatalog _tables;

    internal CompiledParser(
        ParserDefinition definition,
        IReadOnlyList<ICompiledStep> steps,
        MappingTableCatalog tables)
    {
        Definition = definition;
        Steps = steps;
        _tables = tables;
    }

    public ParserDefinition Definition { get; }

    public IReadOnlyList<ICompiledStep> Steps { get; }

    public string Id => Definition.Metadata.Id;

    public string Version => Definition.Metadata.Version;

    /// <summary>Derlenmiş grok'ların tamamı — linter ve <c>parser lint</c> için.</summary>
    public IEnumerable<CompiledGrok> Groks =>
        Steps.OfType<CompiledGrokStep>().SelectMany(step => step.Patterns);

    public ParseResult Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var context = new ParseContext(input);

        foreach (var step in Steps)
        {
            if (step.Execute(context, out var reason))
            {
                continue;
            }

            var definition = step.Definition;
            switch (definition.OnFailure)
            {
                case OnFailure.Fail:
                    context.Abort(definition.Type, reason);
                    return Build(context);

                case OnFailure.Continue:
                    context.Degrade(definition.Type, reason);
                    break;

                case OnFailure.Tag:
                    context.Degrade(definition.Type, reason);
                    context.AddTag(definition.Tag ?? $"_{definition.Type}_failure");
                    break;

                default:
                    throw new InvalidOperationException($"Bilinmeyen on_failure: {definition.OnFailure}");
            }
        }

        return Build(context);
    }

    private ParseResult Build(ParseContext context)
    {
        var status = context.Status;

        var core = status == ParseStatus.Failed
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : Evaluate(Definition.Map.Core, context);

        var ocsf = status == ParseStatus.Failed
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : Evaluate(Definition.Map.Ocsf, context);

        var otel = status == ParseStatus.Failed
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : Evaluate(Definition.Map.Otel, context);

        var timestamp = context.Fields.TryGetValue(ParseContext.TimestampField, out var ts) && ts is DateTimeOffset value
            ? value
            : (DateTimeOffset?)null;

        return new ParseResult
        {
            ParserId = Id,
            ParserVersion = Version,
            Status = status,
            Fields = context.Fields,
            Core = core,
            Ocsf = ocsf,
            Otel = otel,
            Tags = context.Tags,
            Issues = context.Issues,
            Timestamp = timestamp,
            TimedOut = context.TimedOut,
        };
    }

    private Dictionary<string, object?> Evaluate(
        IReadOnlyDictionary<string, MapValue> section, ParseContext context)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in section)
        {
            switch (value)
            {
                case LiteralMapValue literal:
                    result[key] = literal.Value;
                    break;

                case TemplateMapValue template:
                    // Çözülemeyen şablon atanmaz — boş string yazmak, olayda
                    // "kaynak IP boş" gibi görünüp sorguları sessizce kirletir.
                    if (TemplateRenderer.TryRender(template.Template, context.Fields, out var rendered))
                    {
                        result[key] = rendered;
                    }

                    break;

                case LookupMapValue lookup:
                {
                    if (context.TryGetString(lookup.From, out var raw) &&
                        _tables.TryLookup(lookup.Table, raw, out var mapped))
                    {
                        result[key] = mapped;
                    }
                    else if (lookup.Default is not null)
                    {
                        result[key] = lookup.Default;
                    }

                    break;
                }

                default:
                    throw new InvalidOperationException($"Bilinmeyen map değeri: {value.GetType().Name}");
            }
        }

        return result;
    }
}
