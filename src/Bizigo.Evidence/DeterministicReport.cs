using System.Globalization;
using System.Text;

namespace Bizigo.Evidence;

/// <summary>
/// Kanıt paketinin <b>LLM'siz</b> raporu (T36, K22).
///
/// <para>
/// K22'nin ayrımı burada sınanıyor: <i>kanıt F3'te, akıl F4'te</i>. Bu raporun
/// tek kabul kriteri, <b>model kapalıyken okunabilir ve işe yarar</b> olması —
/// yani kullanıcı hiçbir model koşmadan "pencerede ilk kez şu imzalar göründü,
/// öncesinde şu config değişti, şu cihazlar sustu" cümlesini alabilmeli.
/// </para>
///
/// <para>
/// <b>Hipotez üretmiyor.</b> "Şu değişiklik şunu bozdu" cümlesini kuran taraf
/// F4; burada yalnızca gözlemler, kaynaklarıyla ve sırasıyla duruyor. Aradaki
/// fark bu ürünün tamamı: kanıt yanlış olamaz, yorum olabilir.
/// </para>
///
/// <para>
/// <b>Raporun dürüstlük satırları — üçü de bilinçli:</b> bakılmayan kanıt
/// türleri görünüyor, kapsam dışında kaç olay olduğu söyleniyor, ve pencerenin
/// zamanına ne kadar güvenilebileceği yazıyor. Üçü de okuyanın körü körüne
/// güvenmesini zorlaştırmak için (RCA §6).
/// </para>
/// </summary>
public sealed record DeterministicReport
{
    public required Guid BundleId { get; init; }
    public required string ContentHash { get; init; }
    public required RcaWindow Window { get; init; }
    public required DateTimeOffset GatheredAt { get; init; }

    /// <summary>Sıralanmış kanıt satırları — <see cref="EvidenceRanking"/>.</summary>
    public required IReadOnlyList<RankedEvidence> Findings { get; init; }

    /// <summary>Zaman sıralı olaylar; ham loga inen yol kanıt satırında.</summary>
    public required IReadOnlyList<RankedEvidence> Timeline { get; init; }

    /// <summary>
    /// Bakılmayan kanıt türleri ve <b>neden</b> bakılmadığı. Boş liste "her şeye
    /// bakıldı" demek — sessizce atlanan bir tür, eksik kanıta tam kanıt
    /// muamelesi yapılmasının en kolay yolu.
    /// </summary>
    public required IReadOnlyList<EvidenceSlice> NotConsulted { get; init; }

    /// <summary>
    /// Kanıt bulamayan ama <b>koşan</b> sağlayıcılar. "Baktık, bir şey yok" bir
    /// kanıt ve raporda görünmek zorunda: okuyan, o sinyalin sessiz kalmasını
    /// bir bilgi olarak kullanıyor.
    /// </summary>
    public required IReadOnlyList<EvidenceSlice> Silent { get; init; }

    public required WindowTrust Trust { get; init; }

    public long OutOfScopeCount { get; init; }

    public bool IsPartial { get; init; }

    public static DeterministicReport From(EvidenceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var ranked = EvidenceRanking.RankAll(bundle.Slices);

        return new DeterministicReport
        {
            BundleId = bundle.Id,
            ContentHash = bundle.ContentHash,
            Window = bundle.Window,
            GatheredAt = bundle.GatheredAt,
            Findings = ranked,

            // Zaman çizelgesi aynı satırların **zamana göre** dizilişi, ayrı bir
            // veri değil. İkinci bir liste üretmek, iki görünümün ayrışabileceği
            // bir yer açardı; burada tek kaynak var, iki sıralama.
            Timeline =
            [
                .. ranked
                    .OrderBy(r => r.Item.Timestamp)
                    .ThenByDescending(r => r.Score)
                    .ThenBy(r => r.Item.Id, StringComparer.Ordinal)
            ],
            NotConsulted = bundle.NotConsulted,
            Silent = [.. bundle.Slices.Where(s => s.Status == EvidenceStatus.Empty)],
            Trust = bundle.Trust,
            OutOfScopeCount = bundle.OutOfScopeCount,
            IsPartial = bundle.IsPartial,
        };
    }

    /// <summary>
    /// Markdown çıktısı — raporun insan okuduğu hâli.
    ///
    /// <para>
    /// Ekran ve export T37'nin işi; burada üretilen metin o ekranın kaynağı
    /// <b>ve</b> model kapalıyken tek başına yeterli olan çıktı. Biçim olarak
    /// Markdown seçildi çünkü hem ekranda hem ticket'ta hem terminalde okunuyor
    /// ve hiçbir çalışma zamanı gerektirmiyor.
    /// </para>
    /// </summary>
    public string ToMarkdown()
    {
        var text = new StringBuilder();

        text.AppendLine("# RCA kanıt paketi");
        text.AppendLine();
        text.AppendLine(Row("Paket", BundleId.ToString()));
        text.AppendLine(Row("İçerik hash", ContentHash));
        text.AppendLine(Row("Toplandı", Iso(GatheredAt)));
        text.AppendLine(Row("Olay penceresi", $"{Iso(Window.From)} → {Iso(Window.To)}"));
        text.AppendLine(Row("Taban penceresi", $"{Iso(Window.BaselineFrom)} → {Iso(Window.BaselineTo)}"));
        text.AppendLine();

        AppendHonesty(text);
        AppendFindings(text);
        AppendTimeline(text);
        AppendSilent(text);
        AppendNotConsulted(text);

        text.AppendLine();
        text.AppendLine("_Bu rapor deterministik: model kullanılmadı, aynı paket her zaman aynı metni üretir._");

        return text.ToString();
    }

    /// <summary>
    /// Raporun en üstündeki uyarılar. <b>En üstte olmaları bilinçli:</b> bir
    /// kısıt raporun sonunda dururken okunmuyor, ve okunmayan bir kısıt hiç
    /// yazılmamış gibi.
    /// </summary>
    private void AppendHonesty(StringBuilder text)
    {
        var lines = new List<string>();

        if (OutOfScopeCount > 0)
        {
            // RCA §3.2 — sayı veriliyor, içerik verilmiyor. Kök neden başka
            // grubun cihazındaysa rapor bunu BİLMEDEN yanlış sonuca varırdı.
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"⚠ Kapsamınız dışında **{OutOfScopeCount}** ilişkili kayıt var. Tam analiz için ilgili grubun sahibiyle görüşün."));
        }

        if (!Trust.Measured)
        {
            lines.Add("⚠ Pencerenin zaman güvenilirliği **ölçülemedi** — bilinmiyor, 'sorun yok' değil.");
        }
        else if (Trust.HasUnreliableTime)
        {
            var ratio = Trust.UnreliableRatio is { } r
                ? string.Create(CultureInfo.InvariantCulture, $" (%{r * 100:0.#})")
                : string.Empty;

            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"⚠ Penceredeki **{Trust.UnreliableTimeEvents}** / {Trust.TotalEvents} olayın zamanı cihazdan " +
                $"gelmiyor{ratio}; yayılma sırası ve korelasyon penceresi kaymış olabilir."));
        }

        if (IsPartial)
        {
            lines.Add("⚠ Kanıt **eksik**: bir sağlayıcı patladı, bütçeye takıldı ya da liste kırpıldı.");
        }

        if (lines.Count == 0)
        {
            return;
        }

        text.AppendLine("## Bu raporu okurken");
        text.AppendLine();
        foreach (var line in lines)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {line}");
        }

        text.AppendLine();
    }

    private void AppendFindings(StringBuilder text)
    {
        text.AppendLine("## Bulgular");
        text.AppendLine();

        if (Findings.Count == 0)
        {
            // "Hiçbir sinyal bir şey bulmadı" da bir sonuç — ve boş bir bölüm
            // bırakmak, okuyanın raporun yarım kaldığını sanmasına yol açardı.
            text.AppendLine("Hiçbir sinyal bu pencerede kanıt üretmedi.");
            text.AppendLine();
            return;
        }

        text.AppendLine("| # | Sinyal | Bulgu | Zaman |");
        text.AppendLine("| --- | --- | --- | --- |");

        var index = 1;
        foreach (var finding in Findings)
        {
            text.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {index++} | `{finding.Item.ProviderId}` | {Escape(finding.Item.Summary)} " +
                $"| {Iso(finding.Item.Timestamp)} |");
        }

        text.AppendLine();
    }

    private void AppendTimeline(StringBuilder text)
    {
        if (Timeline.Count == 0)
        {
            return;
        }

        text.AppendLine("## Zaman çizelgesi");
        text.AppendLine();
        text.AppendLine("| Zaman | Tür | Olay |");
        text.AppendLine("| --- | --- | --- |");

        foreach (var entry in Timeline)
        {
            text.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {Iso(entry.Item.Timestamp)} | `{entry.Item.ProviderId}` | {Escape(entry.Item.Summary)} |");
        }

        text.AppendLine();
    }

    /// <summary>
    /// Koşan ama bir şey bulamayan sağlayıcılar. Bu bölüm olmadan rapor,
    /// sessizliği "bakılmadı" ile karıştırılabilir hâlde bırakırdı.
    /// </summary>
    private void AppendSilent(StringBuilder text)
    {
        if (Silent.Count == 0)
        {
            return;
        }

        text.AppendLine("## Bakıldı, kanıt çıkmadı");
        text.AppendLine();

        foreach (var slice in Silent.OrderBy(s => s.ProviderId, StringComparer.Ordinal))
        {
            var detail = slice.Detail.Length > 0 ? slice.Detail : "Bu pencerede eşleşme yok.";
            text.AppendLine(CultureInfo.InvariantCulture, $"- `{slice.ProviderId}` — {Escape(detail)}");
        }

        text.AppendLine();
    }

    /// <summary>
    /// <b>Bakılmayanlar.</b> Raporun en çok yanlış güven üreten yeri burası
    /// olurdu: kapalı bir sağlayıcı sessizce atlandığında okuyan, o kanıt
    /// türünde bir şey olmadığını sanır. Özellikle <c>NeverFed</c> — "değişiklik
    /// akışı hiç beslenmemiş" ile "değişiklik olmamış" aynı cümle değil.
    /// </summary>
    private void AppendNotConsulted(StringBuilder text)
    {
        if (NotConsulted.Count == 0)
        {
            return;
        }

        text.AppendLine("## Bakılmayanlar");
        text.AppendLine();

        foreach (var slice in NotConsulted.OrderBy(s => s.ProviderId, StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- `{slice.ProviderId}` ({Label(slice.Status)}) — {Escape(slice.Detail)}");
        }

        text.AppendLine();
    }

    /// <summary>
    /// Durumların insan karşılığı. <see cref="EvidenceStatus.NeverFed"/> ile
    /// <see cref="EvidenceStatus.Empty"/> ayrımı raporun ta kendisi ve
    /// <c>Empty</c> buraya hiç gelmiyor: o "bakıldı" tarafında.
    /// </summary>
    private static string Label(EvidenceStatus status) => status switch
    {
        EvidenceStatus.NeverFed => "besleme yok",
        EvidenceStatus.Unavailable => "kapalı",
        EvidenceStatus.Failed => "hata",
        EvidenceStatus.NotRegistered => "sağlayıcı yok",
        EvidenceStatus.Empty => "boş",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static string Row(string label, string value) => $"- **{label}:** {value}";

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Markdown tablo hücresi. Boru işareti kaçırılmazsa <b>log gövdesinden
    /// gelen tek bir <c>|</c></b> tabloyu bozuyor — ve kanıt özetleri ham log
    /// satırı taşıyor, yani bu teorik bir risk değil.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
             .Replace("\r", " ", StringComparison.Ordinal)
             .Replace("\n", " ", StringComparison.Ordinal);
}
