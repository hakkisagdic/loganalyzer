import type { components } from "@/lib/api/schema";

export type RcaReport = components["schemas"]["RcaReportResponse"];
export type RcaFinding = components["schemas"]["RcaFindingResponse"];
export type RcaSlice = components["schemas"]["RcaSliceResponse"];
export type RcaTrust = components["schemas"]["RcaTrustResponse"];
export type RcaBundleSummary = components["schemas"]["RcaBundleSummaryResponse"];
export type RcaReview = components["schemas"]["RcaReviewResponse"];

/**
 * Raporun ekranda okunan hâli — <b>dört durumun ayrı kaldığı yer</b> (T37).
 *
 * <p>
 * Bu dosyanın varlık sebebi tek bir hata: <c>empty</c>, <c>never_fed</c>,
 * <c>unavailable</c>/<c>failed</c> ve <c>not_registered</c> ekranda tek bir
 * "veri yok" kutusuna düşerse, T34 ve T36'nın kurduğu her şey tek satırda geri
 * alınır — ve <b>hiçbir şey haber vermez</b>. En pahalısı <c>never_fed</c>:
 * "değişiklik akışı hiç beslenmemiş" cümlesi "değişiklik olmadı" diye
 * okunursa kullanıcı bir sinyalin <b>yokluğunu</b> bulgu sanar ve kök nedeni
 * başka yerde aramaya başlar.
 * </p>
 */

/** Bir sağlayıcı durumunun ekrandaki karşılığı. */
export interface StatusPresentation {
  /** Rozet metni — <b>dördü de farklı</b>. */
  readonly label: string;
  /** Kullanıcıya ne demek olduğu; durum etiketi tek başına yetmiyor. */
  readonly meaning: string;
  /** Rozet rengi sınıfı. */
  readonly tone: "ok" | "warn" | "bad" | "dim";
}

/**
 * <b>Tek eşleme tablosu.</b> Ekranın her yeri buradan okuyor; ikinci bir yerde
 * dallanmak, iki görünümün ayrışabileceği bir kapı açardı.
 *
 * <p>
 * <c>never_fed</c>'in cümlesi bilerek uzun ve bilerek olumsuz: kısa bir "veri
 * yok" tam olarak yanlış anlaşılan hâli.
 * </p>
 */
export const STATUS_PRESENTATION: Readonly<Record<string, StatusPresentation>> = {
  gathered: {
    label: "kanıt bulundu",
    meaning: "Sağlayıcı koştu ve kanıt üretti.",
    tone: "ok",
  },
  empty: {
    label: "bakıldı, yok",
    meaning: "Sağlayıcı koştu; bu pencerede eşleşme yok. Bu bir kanıt: 'bu pencerede bir şey olmadı' denebilir.",
    tone: "dim",
  },
  never_fed: {
    label: "besleme yok",
    meaning:
      "Kaynak hiç beslenmemiş — bu 'değişiklik olmadı' DEĞİL. Ölçümün yokluğu; bu türde bir şey olup olmadığı bilinmiyor.",
    tone: "warn",
  },
  unavailable: {
    label: "kapalı",
    meaning: "Sağlayıcı kayıtlı ama koşamıyor. Bu türe bakılmadı.",
    tone: "warn",
  },
  failed: {
    label: "hata",
    meaning: "Sağlayıcı koştu ve patladı. Kanıt eksik.",
    tone: "bad",
  },
  not_registered: {
    label: "sağlayıcı yok",
    meaning: "Bu kanıt türü için sağlayıcı yok (F5). Bu türe hiç bakılmadı.",
    tone: "dim",
  },
};

/**
 * Bilinmeyen bir durum <b>gizlenmiyor</b>.
 *
 * <p>
 * Sunucu bir gün yeni bir durum eklerse ve ekran onu tanımazsa, sessizce
 * "veri yok"a düşürmek tam da bu dosyanın engellemeye çalıştığı şey olurdu.
 * Tanınmayan durum, tanınmadığını söyleyerek görünüyor.
 * </p>
 */
export function presentStatus(status: string): StatusPresentation {
  return (
    STATUS_PRESENTATION[status] ?? {
      label: status,
      meaning: "Bu durumu arayüz tanımıyor — sunucu yeni bir değer döndürmüş olabilir.",
      tone: "warn",
    }
  );
}

/** Raporun en üstünde duran uyarılar. */
export interface HonestyLine {
  readonly id: "out_of_scope" | "trust_unmeasured" | "trust_unreliable" | "partial";
  readonly text: string;
}

/**
 * Üç dürüstlük satırı — <b>en üstte</b>.
 *
 * <p>
 * Yerleri bilinçli: raporun sonunda duran bir kısıt okunmuyor, ve okunmayan bir
 * kısıt hiç yazılmamış gibi. Sunucunun Markdown'ı da aynı üç satırı aynı yere
 * koyuyor; ikisi aynı kaynaktan beslenmese ekranda görünen bir uyarı export'ta
 * kaybolabilirdi.
 * </p>
 *
 * <p>
 * <b>Uyarısı olmayan rapor uyarı göstermiyor.</b> Her raporda duran bir uyarı
 * hiçbir şey söylemez.
 * </p>
 */
export function honestyLines(report: RcaReport): readonly HonestyLine[] {
  const lines: HonestyLine[] = [];

  if (Number(report.out_of_scope_count) > 0) {
    // Sayı veriliyor, içerik verilmiyor — grup adı da bir sızıntı (K17).
    lines.push({
      id: "out_of_scope",
      text: `Kapsamınız dışında ${report.out_of_scope_count} ilişkili kayıt var. Tam analiz için ilgili grubun sahibiyle görüşün.`,
    });
  }

  if (!report.trust.measured) {
    lines.push({
      id: "trust_unmeasured",
      text: "Pencerenin zaman güvenilirliği ölçülemedi — bilinmiyor, 'sorun yok' değil.",
    });
  } else if (Number(report.trust.unreliable_time_events) > 0) {
    const ratio = report.trust.unreliable_ratio;
    const suffix = typeof ratio === "number" ? ` (%${(ratio * 100).toFixed(1)})` : "";

    lines.push({
      id: "trust_unreliable",
      text:
        `Penceredeki ${report.trust.unreliable_time_events} / ${report.trust.total_events} olayın zamanı ` +
        `cihazdan gelmiyor${suffix}; yayılma sırası ve korelasyon penceresi kaymış olabilir.`,
    });
  }

  if (report.is_partial) {
    lines.push({
      id: "partial",
      text: "Kanıt eksik: bir sağlayıcı patladı, bütçeye takıldı ya da liste kırpıldı.",
    });
  }

  return lines;
}

/**
 * İnceleme kararları — sunucudaki `ReviewVerdict` ile **birebir**.
 *
 * `unknown` bir kaçış kapısı değil bir ölçüm: seçenek olmasaydı gerçekten
 * bilmeyen kişi rastgele birini seçerdi ve altın küme sessizce gürültüyle
 * dolardı — ölçülemez olmaktan kötü, çünkü ölçülüyormuş gibi görünürdü.
 * Doğruluk oranının **paydasına girmiyor**; kendi oranı ayrı bir gösterge.
 */
export const REVIEW_STATES = [
  { value: "correct", label: "Doğru" },
  { value: "incomplete", label: "Eksik" },
  { value: "wrong", label: "Yanlış" },
  { value: "unknown", label: "Bilmiyorum" },
] as const;

/**
 * Çelişen kanıt hakkında <b>ayrı</b> karar — sunucudaki
 * `ContradictingEvidenceVerdict` ile birebir (RCA riski #5).
 *
 * <p>
 * <b>Neden ayrı bir soru ve neden "yanlış/eksik" seçilince açılan bir alt soru
 * DEĞİL:</b> tiyatronun en tehlikeli hâli, raporun <b>bütün olarak doğru</b>
 * olduğu hâl. Model çelişen kanıt alanını doldurmak için önemsiz bir şey
 * uydurmuş olabilir ve rapor yine de "doğru" kararını alır. Soruyu karara
 * bağlasaydık, ölçüm tam da görmesi gereken durumu <b>sistematik olarak</b>
 * hiç örneklemezdi — kendi en kötü durumunu göremeyen bir ölçüm, ölçüm değil.
 * </p>
 *
 * <p>
 * <b>Neden ikinci bir düğme grubu da değil:</b> kararı iki tıka çıkarmak
 * inceleme yorgunluğunu (RCA riski #2) büyütürdü ve altın küme, kazandığı
 * boyuttan çok kaybettiği satırdan zarar görürdü. Seçim tek tıkı bozmadan
 * yanında duruyor: hangi karar düğmesine basılırsa basılsın bu değer onunla
 * birlikte gidiyor.
 * </p>
 *
 * <p>
 * <b>Varsayılan <c>unknown</c>, <c>not_present</c> değil.</b> Bugün F3'ün
 * deterministik raporunda çelişen kanıt bölümü <b>yok</b> (o F4'ün), yani
 * <c>not_present</c> çoğu rapor için doğru cevap. Ama ekran bunu
 * <b>bilemiyor</b>: yanıt böyle bir alan taşımıyor ve ekranın kullanıcı adına
 * çıkarım yapması, F4 bölümü eklediğinde birinin hatırlamasına kadar sessizce
 * yanlış iddia etmeye devam etmek olurdu. İddia, raporu gerçekten gören
 * kişide kalıyor.
 * </p>
 */
export const CONTRADICTING_CHOICES = [
  { value: "unknown", label: "Bilmiyorum" },
  { value: "not_present", label: "Bölüm yoktu" },
  { value: "sound", label: "Vardı, yerindeydi" },
  { value: "trivial", label: "Vardı, önemsizdi" },
] as const;

export interface ReviewRequestBody {
  readonly verdict: string;
  readonly contradicting_evidence: string;
  readonly actual_root_cause: string;
  readonly note: string;
}

/**
 * İnceleme isteğinin gövdesi — <b>tek yerde</b>.
 *
 * <p>
 * Ayrı bir fonksiyon olmasının sebebi sınanabilirlik: düğmeye tıklamayı
 * sınamak DOM ortamı isterdi, oysa asıl soru "hangi gövde gidiyor". Aynı kalıp
 * <c>changeWriteRequest</c>'te de var.
 * </p>
 *
 * <p>
 * <c>reviewer</c> gövdede <b>yok</b>: sunucu onu token'dan alıyor. İstemciden
 * göndermek, herkesin başkasının adına oy yazabilmesi demek olurdu.
 * </p>
 */
export function reviewRequest(
  verdict: string,
  contradictingEvidence: string,
  actualRootCause: string,
  note = "",
): ReviewRequestBody {
  return {
    verdict,
    contradicting_evidence: contradictingEvidence,
    actual_root_cause: actualRootCause.trim(),
    note,
  };
}
