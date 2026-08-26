namespace Bizigo.ScenarioPlugin;

/// <summary>
/// Yükleme anında bulunan tek bir kusur. Satır/sütun taşınıyor çünkü senaryo
/// dosyaları elle yazılıyor ve <c>"bir yerde bir hata var"</c> diyen bir yükleyici
/// yazanı dosyada arattırır.
/// </summary>
public sealed record ScenarioSchemaError(string Path, int Line, int Column, string Message)
{
    public override string ToString() => $"{Path} ({Line}:{Column}): {Message}";
}

/// <summary>
/// Yükleme sonucu. <b>Kısmi başarı yok:</b> tek bir kusur bile varsa
/// <see cref="Definition"/> null kalıyor.
///
/// <para>
/// Yarım yüklenen bir senaryo, bu deponun en pahalı hata sınıfının kendisi
/// olurdu: koşum başlar, adımlardan biri eksiktir, ve rapor "üretildi" der.
/// </para>
/// </summary>
public sealed record ScenarioLoadResult(ScenarioDefinition? Definition, IReadOnlyList<ScenarioSchemaError> Errors)
{
    public bool Ok => Errors.Count == 0 && Definition is not null;

    public ScenarioDefinition Value => Definition
        ?? throw new InvalidOperationException("Senaryo yüklenemedi: " + Describe());

    public string Describe() =>
        Errors.Count == 0 ? "hata yok" : string.Join(Environment.NewLine, Errors.Select(e => e.ToString()));
}
