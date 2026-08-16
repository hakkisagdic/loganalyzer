using System.Text;

namespace Bizigo.Parsing.Schema;

/// <summary>
/// Şema hatası. <see cref="Line"/>/<see cref="Column"/> zorunlu tutuldu: parser
/// YAML'ını yazan kişi editörde o satıra gidebilmeli, yoksa hata mesajı
/// "bir yerde bir şey yanlış"a dönüşür.
/// </summary>
public sealed record ParserSchemaError(string Path, int Line, int Column, string Message)
{
    public override string ToString() => $"{Path} ({Line}:{Column}): {Message}";
}

public sealed record ParserLoadResult(ParserDefinition? Definition, IReadOnlyList<ParserSchemaError> Errors)
{
    public bool Ok => Errors.Count == 0 && Definition is not null;

    public ParserDefinition Value => Definition
        ?? throw new InvalidOperationException("Parser yüklenemedi: " + Describe());

    public string Describe()
    {
        if (Errors.Count == 0)
        {
            return "hata yok";
        }

        var builder = new StringBuilder();
        foreach (var error in Errors)
        {
            builder.AppendLine(error.ToString());
        }

        return builder.ToString().TrimEnd();
    }
}
