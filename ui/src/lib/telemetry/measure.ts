import type { SearchCriteria, QueryVerdict } from "@/lib/events/criteria";

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
