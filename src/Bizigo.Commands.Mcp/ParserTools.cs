using System.Text.Json;
using System.Text.Json.Serialization;

using Bizigo.Contracts;
using Bizigo.Mcp;
using Bizigo.Parsing.Grok;

namespace Bizigo.Commands.Mcp;

/// <summary><c>parser.lint</c> — şema doğrulaması + ReDoS taraması.</summary>
public sealed class ParserLintTool(McpSurface surface, ParserToolbox toolbox)
    : CommandTool(surface, "parser.lint")
{
    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "files": {
              "type": "array",
              "items": { "type": "string" },
              "minItems": 1
            }
          },
          "required": ["files"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "ok": { "type": "boolean" },
            "files": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "path":          { "type": "string" },
                  "parser_id":     { "type": ["string", "null"] },
                  "schema_errors": { "type": "array", "items": { "type": "string" } },
                  "redos": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "severity": { "type": "string", "enum": ["error", "warning", "info"] },
                        "code":     { "type": "string" },
                        "message":  { "type": "string" }
                      },
                      "required": ["severity", "code", "message"],
                      "additionalProperties": false
                    }
                  }
                },
                "required": ["path", "parser_id", "schema_errors", "redos"],
                "additionalProperties": false
              }
            }
          },
          "required": ["ok", "files"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected override ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var files = invocation.Required<string[]>("files");
        var outcome = ParserCommands.Lint(files, toolbox);

        return ValueTask.FromResult(outcome.Ok
            ? McpToolResult.Structured(Shape(outcome.Payload))
            : Failure(outcome.Failure));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Örnek GERÇEK şekillendirmeyi koşturuyor: sabit bir domain nesnesi
        // `Shape`'ten geçiyor. Elle yazılmış bir JSON sabiti şemayı değil
        // kendini doğrulardı.
        return ValueTask.FromResult(McpToolResult.Structured(Shape(
            new ParserLintOutcome(
            [
                new Parsing.Testing.ParserLintReport("catalog/parsers/ornek.yaml", "ornek.v1", [], []),
            ]))));
    }

    private static Payload Shape(ParserLintOutcome outcome) => new(
        !outcome.HasErrors,
        [
            .. outcome.Files.Select(file => new FilePayload(
                file.Path,
                file.ParserId,
                [.. file.SchemaErrors.Select(e => e.ToString()!)],
                [
                    .. file.RedosFindings.Select(f => new RedosPayload(
                        f.Severity switch
                        {
                            RedosSeverity.Error => "error",
                            RedosSeverity.Warning => "warning",
                            _ => "info",
                        },
                        f.Code,
                        f.Message)),
                ])),
        ]);

    /// <summary>
    /// Yanıt tipi — <b>domain kaydı olduğu gibi tele konmuyor</b> (§8).
    /// <c>ParserLintReport</c>'a bir alan eklendiğinde o alan kimse karar
    /// vermeden modelin bağlamına girmemeli.
    /// </summary>
    private sealed record Payload(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("files")] IReadOnlyList<FilePayload> Files);

    private sealed record FilePayload(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("parser_id")] string? ParserId,
        [property: JsonPropertyName("schema_errors")] IReadOnlyList<string> SchemaErrors,
        [property: JsonPropertyName("redos")] IReadOnlyList<RedosPayload> Redos);

    private sealed record RedosPayload(
        [property: JsonPropertyName("severity")] string Severity,
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message);
}

/// <summary><c>parser.test</c> — gömülü <c>tests</c> bloğunu koşturur.</summary>
public sealed class ParserTestTool(McpSurface surface, ParserToolbox toolbox)
    : CommandTool(surface, "parser.test")
{
    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "files": {
              "type": "array",
              "items": { "type": "string" },
              "minItems": 1
            }
          },
          "required": ["files"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "ok":         { "type": "boolean" },
            "pass_count": { "type": "integer", "minimum": 0 },
            "fail_count": { "type": "integer", "minimum": 0 },
            "files": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "path":           { "type": "string" },
                  "parser_id":      { "type": ["string", "null"] },
                  "compile_errors": { "type": "array", "items": { "type": "string" } },
                  "failed_tests": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "name": { "type": "string" },
                        "line": { "type": "integer" }
                      },
                      "required": ["name", "line"],
                      "additionalProperties": false
                    }
                  }
                },
                "required": ["path", "parser_id", "compile_errors", "failed_tests"],
                "additionalProperties": false
              }
            }
          },
          "required": ["ok", "pass_count", "fail_count", "files"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected override ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var outcome = ParserCommands.Test(invocation.Required<string[]>("files"), toolbox);

        return ValueTask.FromResult(outcome.Ok
            ? McpToolResult.Structured(Shape(outcome.Payload))
            : Failure(outcome.Failure));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(McpToolResult.Structured(Shape(
            new ParserTestOutcome(
            [
                new ParserTestFileOutcome("catalog/parsers/ornek.yaml", ["derlenemedi"], null),
            ]))));
    }

    private static Payload Shape(ParserTestOutcome outcome) => new(
        !outcome.HasFailures,
        outcome.PassCount,
        outcome.FailCount,
        [
            .. outcome.Files.Select(file => new FilePayload(
                file.Path,
                file.Report?.ParserId,
                file.CompileErrors,
                [
                    .. (file.Report?.Tests ?? [])
                        .Where(static t => !t.Passed)
                        .Select(t => new FailedTestPayload(t.Name, t.Line)),
                ])),
        ]);

    private sealed record Payload(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("pass_count")] int PassCount,
        [property: JsonPropertyName("fail_count")] int FailCount,
        [property: JsonPropertyName("files")] IReadOnlyList<FilePayload> Files);

    private sealed record FilePayload(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("parser_id")] string? ParserId,
        [property: JsonPropertyName("compile_errors")] IReadOnlyList<string> CompileErrors,
        [property: JsonPropertyName("failed_tests")] IReadOnlyList<FailedTestPayload> FailedTests);

    private sealed record FailedTestPayload(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("line")] int Line);
}

/// <summary><c>parser.coverage</c> — katalogdaki altın örneklerin çözülme oranı.</summary>
public sealed class ParserCoverageTool(McpSurface surface, ParserToolbox toolbox)
    : CommandTool(surface, "parser.coverage")
{
    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "directory": { "type": "string" },
            "allowed_failed_percent": {
              "type": "number", "minimum": 0, "maximum": 100, "default": 0
            }
          },
          "required": ["directory"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "within_budget": { "type": "boolean" },
            "total":          { "type": "integer", "minimum": 0 },
            "ok":             { "type": "integer", "minimum": 0 },
            "partial":        { "type": "integer", "minimum": 0 },
            "failed":         { "type": "integer", "minimum": 0 },
            "failed_percent": { "type": "number" },
            "files": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "path":           { "type": "string" },
                  "total":          { "type": "integer", "minimum": 0 },
                  "ok_percent":     { "type": "number" },
                  "failed_percent": { "type": "number" }
                },
                "required": ["path", "total", "ok_percent", "failed_percent"],
                "additionalProperties": false
              }
            }
          },
          "required": ["within_budget", "total", "ok", "partial", "failed", "failed_percent", "files"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected override ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var outcome = ParserCommands.Coverage(
            invocation.Required<string>("directory"),
            invocation.Optional("allowed_failed_percent", 0d),
            toolbox);

        return ValueTask.FromResult(outcome.Ok
            ? McpToolResult.Structured(Shape(outcome.Payload))
            : Failure(outcome.Failure));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(McpToolResult.Structured(Shape(
            new ParserCoverageOutcome(
                new Parsing.Testing.SampleCoverageReport(
                [
                    new Parsing.Testing.SampleFileCoverage(
                        "catalog/parsers/ornek/samples/a.log", 8, 1, 1,
                        new Dictionary<string, int>(StringComparer.Ordinal) { ["ornek.v1"] = 9 }),
                ]),
                AllowedFailedPercent: 10d))));
    }

    private static Payload Shape(ParserCoverageOutcome outcome) => new(
        outcome.WithinBudget,
        outcome.Report.Total,
        outcome.Report.Ok,
        outcome.Report.Partial,
        outcome.Report.Failed,
        outcome.Report.FailedPercent,
        [
            .. outcome.Report.Files.Select(file => new FilePayload(
                file.Path, file.Total, file.OkPercent, file.FailedPercent)),
        ]);

    private sealed record Payload(
        [property: JsonPropertyName("within_budget")] bool WithinBudget,
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("ok")] int Ok,
        [property: JsonPropertyName("partial")] int Partial,
        [property: JsonPropertyName("failed")] int Failed,
        [property: JsonPropertyName("failed_percent")] double FailedPercent,
        [property: JsonPropertyName("files")] IReadOnlyList<FilePayload> Files);

    private sealed record FilePayload(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("ok_percent")] double OkPercent,
        [property: JsonPropertyName("failed_percent")] double FailedPercent);
}

/// <summary>
/// <c>parser.try</c> — tek satırı parser'dan geçirir.
///
/// <h3>ÇÖZÜLEN DEĞERLER DÖNMÜYOR, ALAN ADLARI DÖNÜYOR</h3>
///
/// <para>
/// CLI aynı komutta <c>core</c>/<c>ocsf</c>/<c>fields</c> <b>değerlerini</b>
/// yazdırıyor. Araç yazdırmıyor ve bu bilinçli bir daraltma: bir araç
/// sonucundaki her şey <b>doğrudan modelin bağlamına</b> giriyor, ve çözülmüş
/// alan değerleri log içeriğidir. Log içeriğinin menteşesi
/// <see cref="McpLogText"/> ve o menteşeyi <b>M06 takacak</b> (redaksiyon ve K6
/// kapısı).
/// </para>
///
/// <para>
/// Yani seçenek "kapıyı beklemek" ile "kapısız bir yüzey açıp sonra kapatmak"
/// arasındaydı. İkincisi, arada bir sürüm boyunca redaksiyonsuz bir log yolu
/// bırakırdı — ve MCP ticket'ının kendi cümlesi bunu yasaklıyor: <i>araçlar
/// yazılır, kapı takılır, ikisi birlikte açılır.</i>
/// </para>
///
/// <para>
/// Daraltmanın bedeli ölçülü: araç <i>"parser bu satırı çözdü mü, hangi
/// alanları doldurdu"</i> sorusunu hâlâ cevaplıyor — <i>"ne yazıyordu"</i>
/// sorusunu cevaplamıyor. Asimetri M02 raporunda yazılı.
/// </para>
/// </summary>
public sealed class ParserTryTool(McpSurface surface, ParserToolbox toolbox)
    : CommandTool(surface, "parser.try")
{
    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "file":  { "type": "string" },
            "lines": {
              "type": "array",
              "items": { "type": "string" },
              "minItems": 1
            }
          },
          "required": ["file", "lines"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "parser_id": { "type": "string" },
            "lines": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "status":            { "type": "string", "enum": ["ok", "partial", "failed"] },
                  "resolved_fields":   { "type": "array", "items": { "type": "string" } },
                  "issues":            { "type": "array", "items": { "type": "string" } }
                },
                "required": ["status", "resolved_fields", "issues"],
                "additionalProperties": false
              }
            }
          },
          "required": ["parser_id", "lines"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected override ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var outcome = ParserCommands.Try(
            invocation.Required<string>("file"),
            invocation.Required<string[]>("lines"),
            toolbox);

        return ValueTask.FromResult(outcome.Ok
            ? McpToolResult.Structured(Shape(outcome.Payload))
            : Failure(outcome.Failure));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(McpToolResult.Structured(Shape(
            new ParserTryOutcome("ornek.v1", []))));
    }

    private static Payload Shape(ParserTryOutcome outcome) => new(
        outcome.ParserId,
        [
            .. outcome.Lines.Select(line => new LinePayload(
                line.Result.Status switch
                {
                    ParseStatus.Ok => "ok",
                    ParseStatus.Partial => "partial",
                    _ => "failed",
                },

                // ADLAR, DEĞERLER DEĞİL. Değerler log içeriği ve menteşesi
                // M06'nın.
                [
                    .. line.Result.Core.Keys
                        .Concat(line.Result.Ocsf.Keys)
                        .Concat(line.Result.Otel.Keys)
                        .Concat(line.Result.Fields.Keys)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal),
                ],

                // Sorunlar ADIM ADI taşıyor, satırın kendisini değil.
                [.. line.Result.Issues.Select(i => $"{i.Step}: {i.Message}")])),
        ]);

    private sealed record Payload(
        [property: JsonPropertyName("parser_id")] string ParserId,
        [property: JsonPropertyName("lines")] IReadOnlyList<LinePayload> Lines);

    private sealed record LinePayload(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("resolved_fields")] IReadOnlyList<string> ResolvedFields,
        [property: JsonPropertyName("issues")] IReadOnlyList<string> Issues);
}
