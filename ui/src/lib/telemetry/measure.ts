import type { AlertRuleRequest } from "@/lib/alerts/types";
import type { SearchCriteria, QueryVerdict } from "@/lib/events/criteria";
import type { RcaReport } from "@/lib/rca/report";

import { errorKind, trustBand } from "./classify";
import type { EventPayload } from "./events";

/**
 * Ekranın elindeki **zengin** nesneden telemetriye giden **fakir** şekli
 * türetiyor.
 *
 * <p>
 * Bu dönüşümün ayrı bir modülde olmasının sebebi nerede yaşadığı değil, kimin
 * kararı olduğu. "Bu alan gönderilebilir mi" sorusu telemetri modülünün
 * sorusu; ekranın değil. Ekranların içine dağılsaydı, sekizinci ekranı yazan
 * kişi <c>criteria</c> nesnesini olduğu gibi geçirir ve <c>fullText</c> —
 * yani müşterinin aradığı metin — telemetriye giderdi. Süzgeç onu düşürürdü
 * (beyaz liste), ama düşürdüğünü kimse okumazdı.
 * </p>
 *
 * <p>
 * Buradaki her fonksiyon <b>saf</b>: girdi bir nesne, çıktı sayılar ve
 * numaralandırmalar. Testi tarayıcı da sunucu da gerektirmiyor.
 * </p>
 */

/**
 * Aramanın **şekli** — içeriği değil.
 *
 * <p>
 * <c>fullText</c>'ten giden tek şey <b>var mı</b> bilgisi. Uzunluğu bile
 * gitmiyor: uzunluk tek başına zararsız görünüyor ama bir IP adresi (15),
 * bir UUID (36) ya da bir e-posta ile bir kelimeyi ayırt etmeye yetiyor, ve
 * dar bir kümede uzunluk artı zaman aralığı bir satırı tanımlayabiliyor.
 * </p>
 *
 * <p>
 * <c>criteria_count</c> kaç filtre kullanıldığını sayıyor — hangi filtreler
 * olduğunu değil. "İnsanlar kaç filtre kullanıyor" ürün sorusu;
 * "hangi vendor'ı arıyor" müşterinin envanteri.
 * </p>
 */
export function searchShape(
  criteria: SearchCriteria,
  verdict: QueryVerdict,
  measured: { readonly durationMs: number; readonly resultCount?: number },
): EventPayload<"event_search_run"> {
  const filters = [
    criteria.sourceId,
    criteria.ownerGroup,
    criteria.vendor,
    criteria.proto,
    criteria.action,
  ].filter((value) => value.length > 0).length;

  const counted =
    filters +
    criteria.parseStatuses.length +
    (criteria.severityMin === undefined ? 0 : 1) +
    (criteria.fullText.length > 0 ? 1 : 0);

  return {
    criteria_count: counted,
    range_hours: rangeHours(criteria.from, criteria.to),
    duration_ms: Math.round(measured.durationMs),
    result_count: measured.resultCount,
    has_full_text: criteria.fullText.length > 0,
    query_verdict: verdict.kind,
    paginated: criteria.cursor !== undefined,
    page_size: criteria.limit,
    // Kaynak filtresi verilmiş mi. F1'de ölçülen fark burada: filtresiz derin
    // sayfa 1M satır okuyor, kaynak filtresiyle 57k. Bu alan olmadan
    // "aramalar neden yavaş" sorusunun cevabı veride yok.
    scoped: criteria.sourceId.length > 0,
  };
}

/**
 * Zaman aralığının **saat** cinsinden genişliği.
 *
 * <p>Sınırların kendisi (`from`/`to`) gitmiyor: mutlak zaman damgaları bir
 * olayın ne zaman olduğunu söyler ve dar bir aralık tek bir olaya işaret
 * edebilir. Genişlik ise davranış — "insanlar son bir saate mi bakıyor, son
 * bir aya mı".</p>
 *
 * <p>Ayrıştırılamayan ya da ters aralıkta <c>undefined</c> dönüyor; sıfır
 * dönmek "aralık yok" ile "aralık sıfır" arasını siler.</p>
 */
export function rangeHours(from: string, to: string): number | undefined {
  if (from.length === 0 || to.length === 0) {
    return undefined;
  }

  const start = Date.parse(from);
  const end = Date.parse(to);

  if (Number.isNaN(start) || Number.isNaN(end) || end < start) {
    return undefined;
  }

  return Math.round(((end - start) / 3_600_000) * 10) / 10;
}

/** YAML'ın **satır sayısı**. İçeriğinden hiçbir şey türemiyor. */
export function yamlLines(yaml: string): number {
  return yaml.length === 0 ? 0 : yaml.split("\n").length;
}

/**
 * RCA koşumunun **şekli** — raporun içeriği değil.
 *
 * <p>
 * <c>searchShape</c> ile aynı gerekçe ve aynı yer: "bu alan gönderilebilir mi"
 * sorusu telemetri modülünün sorusu, ekranın değil. Ekranın elinde
 * <c>RcaReport</c> var ve o nesne <b>müşterinin log'unun kendisi</b>:
 * <c>findings[].summary</c> bir olay cümlesi, <c>findings[].payload</c> ham
 * alan sözlüğü, <c>window.source_ids</c> / <c>window.owner_groups</c> müşteri
 * envanteri, <c>bundle_id</c> ise tek bir koşumu adresleyen kimlik. Raporu
 * <c>track</c>'e verip beyaz listeye güvenmek "süzgeç tutar" demek olurdu —
 * tutardı, ama tuttuğunu kimse okumazdı.
 * </p>
 *
 * <p>
 * Geriye giden üç şey: <b>bir sayı</b>, <b>bir süre</b>, <b>bir bant</b>.
 * </p>
 *
 * @param report Sunucudan dönen rapor.
 * @param measured Ölçülen süre — <b>kullanıcının beklediği</b> süre.
 */
export function rcaShape(
  report: RcaReport,
  measured: { readonly durationMs: number },
): EventPayload<"rca_run"> {
  return {
    // Bulguların SAYISI. `timeline` de bulgu taşıyor ama sunucuda aynı
    // `ranked` listesinin zamana göre dizilişi — "ayrı bir veri değil"
    // (`DeterministicReport.From`). İkisini toplamak aynı şeyi iki kez
    // saymak olurdu.
    signal_count: report.findings.length,
    duration_ms: Math.round(measured.durationMs),
    trust_band: trustBand(report.trust),
    succeeded: true,
  };
}

/**
 * **Düşen** RCA koşumunun şekli.
 *
 * <p>
 * Ayrı bir fonksiyon çünkü ayrı bir bilgi kümesi: elde rapor yok, dolayısıyla
 * <c>signal_count</c> ve <c>trust_band</c> <b>bilinmiyor</b> — sıfır değil.
 * İkisini de dışarıda bırakmak zorunlu: <c>signal_count: 0</c> "koşum hiçbir
 * şey bulamadı" der, "koşum patladı" demez, ve bu ikisini tek kovaya koymak
 * bu deponun en pahalı hata sınıfı (§7).
 * </p>
 *
 * <p>
 * Süre yine gidiyor ve bilerek: bir RCA'nın <b>ne kadar sonra</b> düştüğü
 * anlamlı — hemen dönen bir doğrulama hatası ile zaman aşımına giden bir koşum
 * aynı şey değil.
 * </p>
 */
export function rcaFailureShape(
  cause: unknown,
  measured: { readonly durationMs: number },
): EventPayload<"rca_run"> {
  return {
    duration_ms: Math.round(measured.durationMs),
    succeeded: false,
    error_kind: errorKind(cause),
  };
}

/**
 * Alarm kuralının **şekli** — kuralın kendisi değil.
 *
 * <p>
 * Kuralın adı, açıklaması, aradığı tam metin ve izlediği kaynak kimlikleri
 * <b>gitmiyor</b>. Üçü de müşterinin envanterinden birer satır: kaynak kimliği
 * cihazın adı, tam metin analistin aradığı şey, kural adı çoğu zaman ikisini
 * birden taşıyor ("golden segmentte deny patlaması").
 * </p>
 *
 * <p>
 * <c>criteria_count</c> <b>gönderilen gövdeden</b> sayılıyor, formun
 * alanlarından değil. Sebep: form yarın yeni bir ölçüt kazandığında sayı
 * kendiliğinden doğru kalıyor. Sayımı ekranın içine yazsaydık o alanı ekleyen
 * kişinin ayrıca burayı hatırlaması gerekirdi — ve hatırlamadığı gün sayı
 * <b>sessizce</b> eksik kalırdı, hiçbir kapı kırmızı yanmadan.
 * </p>
 *
 * <p>
 * Çok değerli bir ölçüt <b>değer başına</b> sayılıyor (iki kaynak = iki),
 * çünkü <c>searchShape</c> aynı alanı aynı şekilde sayıyor
 * (<c>parseStatuses.length</c>) ve <c>criteria_count</c> iki olayda da
 * <b>aynı alan</b>. İki farklı sayma kuralı, iki olayı yan yana koyan panoyu
 * sessizce yalancı yapardı.
 * </p>
 *
 * <p>
 * <b>Kapsam (<c>ownerGroups</c>) ölçüt sayılmıyor.</b> O bir daraltma değil
 * kuralın sahipliği, ve form en az bir grup seçilmeden kaydetmiyor — sayıya
 * katmak her kurala sabit bir artı eklemek, yani hiçbir şey söylememek olurdu.
 * </p>
 *
 * <p>
 * Sayı <b>isteğin</b> şekli, motorun yorumu değil: sessizlik kuralında tam
 * metin sunucuda okunmuyor ama gönderildiyse burada sayılıyor. Aksini yapmak,
 * hangi kural tipinin hangi ölçütü okuduğunu tarayıcıda <b>ikinci kez</b>
 * tarif etmek olurdu — ve o kopya, motor değiştiği gün sessizce ayrışırdı.
 * </p>
 */
export function alertShape(request: AlertRuleRequest, isNew: boolean): EventPayload<"alert_saved"> {
  // `fullText` şemada `null` da olabiliyor; uzunluğa bakmadan önce tek şekle
  // indiriyoruz — `null.length` çalışma zamanında patlar, ve bu fonksiyonun
  // patlaması kaydetme akışını bozardı.
  const fullText = request.fullText ?? "";

  return {
    criteria_count:
      (fullText.length > 0 ? 1 : 0) +
      (request.sourceIds?.length ?? 0) +
      (request.filters?.length ?? 0),
    is_new: isNew,
    // "Eşiği olan kural" = tetiklenmesi bir eşiğe BAĞLI kural. Sessizlik
    // kuralı verinin YOKLUĞUNDA tetikleniyor ve onu yöneten sayı
    // `silenceSeconds`; gövdedeki `threshold` orada formun varsayılanından
    // kalma bir artık, motor hiç okumuyor (`AlertEvaluator.EvaluateSilenceAsync`
    // yalnızca `SilenceSeconds` kullanıyor).
    //
    // Bu alan "kullanıcı eşiği ELLEDİ mi" sorusunu cevaplamıyor ve
    // cevaplayamaz: form 100 ile açılıyor, yani bilerek 100 yazan kullanıcı
    // ile alana hiç dokunmayan birebir aynı gövdeyi üretiyor. O ayrımı
    // uydurmak panoya sessiz bir yalan koymak olurdu.
    has_threshold: (request.ruleType ?? "threshold") !== "silence",
  };
}
