namespace Bizigo.Rca.Models;

/// <param name="Text">Modelin ürettiği metin. Reddedilen koşumda boş.</param>
/// <param name="PromptTokens">
/// Ölçülen giriş belirteci. Uç bildirmezse <see langword="null"/> — <b>0 değil</b>.
/// Sıfır "ölçüldü ve sıfır" demek; bildirilmemiş bir sayıyı sıfır yazmak
/// token bütçesini (T46) sessizce yanıltırdı.
/// </param>
public sealed record ModelCompletion(
    string Text,
    int? PromptTokens,
    int? CompletionTokens,
    TimeSpan Duration,
    string? Failure)
{
    public bool Ok => Failure is null;
}

/// <summary>
/// Model sağlayıcısı — yerel ve kurum içi uzak uçlar <b>tek arayüz</b> arkasında
/// (K6).
///
/// <para>
/// <b>Girdisi <see cref="ModelRequest"/>, <c>string</c> değil.</b> Arayüzün en
/// önemli satırı bu: redaksiyon kapısı (T41) ve K6 kapısı isteğin <i>tipinde</i>
/// duruyor, dolayısıyla ikisini de atlayan bir çağrı yazılamıyor.
/// </para>
///
/// <para>
/// <b>Token bütçesi burada uygulanmıyor</b> — o T46'nın alanı. Buradaki
/// taahhüt yalnızca ölçülen sayıyı <b>bildirmek</b>
/// (<see cref="ModelCompletion.PromptTokens"/>); kısıtı kuyruk uyguluyor.
/// İkisi aynı yerde olsaydı aynı kısıt iki kez yazılır ve biri sessizce
/// ayrışırdı.
/// </para>
/// </summary>
public interface IModelProvider
{
    /// <summary>Sağlayıcının adı — kayıtta ve hata mesajında görünen.</summary>
    string Name { get; }

    ValueTask<ModelCompletion> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}
