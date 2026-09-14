using System.Diagnostics.CodeAnalysis;

namespace Bizigo.Commands;

/// <summary>
/// Bir komutun neden yürümediği — <b>kapalı küme</b>.
///
/// <para>
/// <b>Çıkış kodu değil sebep.</b> Bugünkü CLI <c>1</c> döndürüp <c>stderr</c>'e
/// yazıyor; MCP tarafının ihtiyacı olan şey bir kod değil, tel üzerinde
/// ayrıştırılabilir bir <b>sınıf</b>. Çekirdek sebebi taşıyor, iki sunum kendi
/// karşılığına çeviriyor: CLI bir çıkış koduna, MCP bir
/// <c>McpToolError</c>'a.
/// </para>
///
/// <para>
/// Değerler MCP'nin hata kodlarıyla <b>bilerek</b> örtüşüyor. Örtüşmeseydi
/// aradaki eşleme elle yazılırdı ve elle yazılan eşleme bu depoda bir kez
/// sessizce kırıldı (<c>idp_groups</c> → <c>idpGroups</c>).
/// </para>
/// </summary>
public enum CommandFailureKind
{
    /// <summary>Girdi yanlış — çağıran düzeltebilir.</summary>
    InvalidArgument = 0,

    /// <summary>İstenen şey yok. Dosya, katalog, kural.</summary>
    NotFound = 1,

    /// <summary>
    /// Ortam hazır değil — ClickHouse kapalı, bağlantı dizgisi verilmemiş.
    /// <b><see cref="InvalidArgument"/>'tan ayrı</b>: çağıran doğru sordu, cevap
    /// verilemedi. İkisi tek değere inseydi <i>"yanlış yazdım"</i> ile
    /// <i>"altyapı yok"</i> aynı cevabı alırdı.
    /// </summary>
    Unavailable = 2,
}

/// <param name="Kind">Sebep sınıfı.</param>
/// <param name="Message">İnsan okunur açıklama.</param>
public sealed record CommandFailure(CommandFailureKind Kind, string Message);

/// <summary>
/// Bir komutun sonucu — <b>bir değer, bir yan etki değil</b>.
///
/// <para>
/// <b>M02'nin taşıyıcı kararı bu tip.</b> Ölçüldü: hesap katmanında
/// (<c>Fields/</c>, <c>Seeding/</c>, <c>Bizigo.Parsing</c>) <c>Console</c>
/// çağrısı <b>sıfır</b>, sunum katmanında <b>117</b>. Yani ortaklaştırılacak
/// şey iş mantığı değildi — o zaten ortaktı ve zaten saftı. Ortaklaşamayan şey
/// <b>sonucun kendisiydi</b>: bir komutun çıktısı bir değer değil bir yan
/// etkiydi, ve bir MCP aracı yan etkiyi tüketemez.
/// </para>
///
/// <para>
/// Kalıp icat edilmedi: <c>SigmaSyncCommandHandler.Plan</c> / <c>RunAsync</c>
/// ayrımı bu depoda zaten duruyordu ve burada <b>genelleniyor</b> — ikinci bir
/// kopya değil, birincinin genellemesi (§9).
/// </para>
/// </summary>
public sealed record CommandOutcome<TPayload>
    where TPayload : class
{
    private CommandOutcome(TPayload? payload, CommandFailure? failure)
    {
        Payload = payload;
        Failure = failure;
    }

    public TPayload? Payload { get; }

    public CommandFailure? Failure { get; }

    [MemberNotNullWhen(true, nameof(Payload))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool Ok => Failure is null && Payload is not null;

    public static CommandOutcome<TPayload> Success(TPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new CommandOutcome<TPayload>(payload, null);
    }

    public static CommandOutcome<TPayload> Failed(CommandFailureKind kind, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new CommandOutcome<TPayload>(null, new CommandFailure(kind, message));
    }
}
