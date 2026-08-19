import type { EventSearchBody } from "@/lib/api/client";

/**
 * Arama ekranının sorgu durumu — **URL'de** yaşıyor.
 *
 * <p>
 * Durumu bileşen state'inde tutmak yerine adres çubuğunda tutmanın üç somut
 * karşılığı var: aramalar paylaşılabiliyor, tarayıcının geri düğmesi çalışıyor
 * ve T21'in alarm ekranı bu ekrana <b>derin bağlantı</b> verebiliyor. "Kayıtlı
 * arama" da bu yüzden bir URL'den ibaret.
 * </p>
 */

/**
 * Tam metin kutusunun **alt sınırı**.
 *
 * <p>
 * F1'de ölçüldü: <c>sparseGrams</c> indeksi ~10-11 karakterden sonra seçici.
 * <c>kullanıcı</c> (9 karakter) indeksten faydalanmıyor ve 1M satırda
 * <b>tam tarama</b> yapıyor; <c>用户登录失败，请检查凭据</c> (12) atlıyor. Yani
 * bu bir Türkçe/CJK sorunu değil, uzunluk sorunu — sınır bu yüzden alfabeye
 * göre değişmiyor.
 * </p>
 *
 * <p>11 seçildi çünkü 9 ölçülerek "atlamıyor", 12 ölçülerek "atlıyor" çıktı;
 * aradaki belirsiz bandın güvenli tarafı.</p>
 */
export const MIN_FULL_TEXT_LENGTH = 11;

/** Sayfa başına satır. API üst sınırı 1000. */
export const PAGE_SIZES = [50, 100, 200, 500] as const;

export const PARSE_STATUSES = ["ok", "partial", "failed"] as const;

export interface Cursor {
  readonly afterTimestamp: string;
  readonly afterEventId: string;
}

export interface SearchCriteria {
  readonly fullText: string;
  readonly sourceId: string;
  readonly ownerGroup: string;
  readonly vendor: string;
  readonly parseStatuses: readonly string[];
  /** <c>severity_num</c> alt sınırı (1–6). 0 "belirtilmemiş" demek, dışarıda kalıyor. */
  readonly severityMin: number | undefined;
  readonly proto: string;
  readonly action: string;
  readonly from: string;
  readonly to: string;
  readonly limit: number;
  readonly cursor: Cursor | undefined;
  /** Kullanıcı kısa sorgu uyarısını görüp yine de aramayı seçti. */
  readonly force: boolean;
}

/** Adres çubuğundaki parametre adları. Tek yerde, iki yönde de aynı. */
export const PARAM = {
  fullText: "q",
  sourceId: "source_id",
  ownerGroup: "owner_group",
  vendor: "vendor",
  parseStatus: "parse_status",
  severityMin: "severity_min",
  proto: "proto",
  action: "action",
  from: "from",
  to: "to",
  limit: "limit",
  afterTimestamp: "after_ts",
  afterEventId: "after_id",
  force: "force",
} as const;

type RawParams = Record<string, string | string[] | undefined>;

function one(params: RawParams, key: string): string {
  const value = params[key];
  return (Array.isArray(value) ? value[0] : value)?.trim() ?? "";
}

function many(params: RawParams, key: string): string[] {
  const value = params[key];

  if (value === undefined) {
    return [];
  }

  return (Array.isArray(value) ? value : [value])
    .flatMap((entry) => entry.split(","))
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0);
}

export function readCriteria(params: RawParams): SearchCriteria {
  const limit = Number.parseInt(one(params, PARAM.limit), 10);
  const severity = Number.parseInt(one(params, PARAM.severityMin), 10);

  const afterTimestamp = one(params, PARAM.afterTimestamp);
  const afterEventId = one(params, PARAM.afterEventId);

  return {
    fullText: one(params, PARAM.fullText),
    sourceId: one(params, PARAM.sourceId),
    ownerGroup: one(params, PARAM.ownerGroup),
    vendor: one(params, PARAM.vendor),
    parseStatuses: many(params, PARAM.parseStatus).filter((status) =>
      (PARSE_STATUSES as readonly string[]).includes(status),
    ),
    severityMin: Number.isInteger(severity) && severity >= 1 && severity <= 6 ? severity : undefined,
    proto: one(params, PARAM.proto),
    action: one(params, PARAM.action),
    from: one(params, PARAM.from),
    to: one(params, PARAM.to),
    limit: (PAGE_SIZES as readonly number[]).includes(limit) ? limit : 100,
    // Yarım imleç sessizce ilk sayfayı tekrarlardı: kullanıcı sayfaladığını
    // sanarken aynı satırları görür. API de aynı gerekçeyle reddediyor.
    cursor: afterTimestamp && afterEventId ? { afterTimestamp, afterEventId } : undefined,
    force: one(params, PARAM.force) === "1",
  };
}

/** Ölçütleri adres çubuğu parametrelerine çeviriyor (boşlar düşüyor). */
export function toSearchParams(criteria: SearchCriteria): URLSearchParams {
  const params = new URLSearchParams();

  const setIf = (key: string, value: string) => {
    if (value.length > 0) {
      params.set(key, value);
    }
  };

  setIf(PARAM.fullText, criteria.fullText);
  setIf(PARAM.sourceId, criteria.sourceId);
  setIf(PARAM.ownerGroup, criteria.ownerGroup);
  setIf(PARAM.vendor, criteria.vendor);
  setIf(PARAM.proto, criteria.proto);
  setIf(PARAM.action, criteria.action);
  setIf(PARAM.from, criteria.from);
  setIf(PARAM.to, criteria.to);

  for (const status of criteria.parseStatuses) {
    params.append(PARAM.parseStatus, status);
  }

  if (criteria.severityMin !== undefined) {
    params.set(PARAM.severityMin, String(criteria.severityMin));
  }

  if (criteria.limit !== 100) {
    params.set(PARAM.limit, String(criteria.limit));
  }

  if (criteria.cursor) {
    params.set(PARAM.afterTimestamp, criteria.cursor.afterTimestamp);
    params.set(PARAM.afterEventId, criteria.cursor.afterEventId);
  }

  if (criteria.force) {
    params.set(PARAM.force, "1");
  }

  return params;
}

/**
 * Tam metin uzunluğu **kod noktası** olarak sayılıyor.
 *
 * <p><c>String.length</c> UTF-16 birimi sayıyor; emoji ya da BMP dışı bir
 * karakter tek başına 2 çıkardı ve sınır o sorguda yanlış yerde olurdu.</p>
 */
export function fullTextLength(value: string): number {
  return Array.from(value).length;
}

export type QueryVerdict =
  /** Sorgu koşulabilir. */
  | { readonly kind: "ready" }
  /** Kısa sorgu — koşulmuyor, kullanıcıya sebebi söyleniyor. */
  | { readonly kind: "too-short"; readonly length: number }
  /** Kısa sorgu ama kullanıcı bilerek ısrar etti — koşuluyor. */
  | { readonly kind: "forced"; readonly length: number };

/**
 * Kısa sorgu kuralı.
 *
 * <p>
 * <b>Sessizce kabul etmek yasak</b> (T15 kabul kriteri): kullanıcının yazdığı
 * her kısa kelime 1M satırlık tam tarama demek. Varsayılan davranış sorguyu
 * <b>koşmamak</b>; ısrar açık bir eylem (<c>force=1</c>) gerektiriyor, yani
 * maliyet bilinerek ödeniyor.
 * </p>
 */
export function judgeQuery(criteria: SearchCriteria): QueryVerdict {
  const length = fullTextLength(criteria.fullText);

  if (length === 0 || length >= MIN_FULL_TEXT_LENGTH) {
    return { kind: "ready" };
  }

  return criteria.force ? { kind: "forced", length } : { kind: "too-short", length };
}

/**
 * Keyset sayfalamanın kaynak filtresi gereksinimi.
 *
 * <p>
 * F1'de ölçüldü (1M satır): filtresiz derin sayfa <b>1M satır</b> okuyor,
 * <c>owner_group</c> ile 286k, <c>owner_group</c> + <c>source_id</c> ile
 * <b>57k</b> — ve sayfa 1 ile derin sayfa aynı süreye iniyor. Sıralama
 * anahtarının tam öneki verilmeden keyset sabit süreli değil.
 * </p>
 *
 * <p>
 * Ekran bunu <b>dayatmıyor</b> ama yönlendiriyor; ve sayfalama başladığında
 * uyarı sertleşiyor, çünkü bedeli asıl orada ödeniyor.
 * </p>
 */
export type PaginationAdvice = "none" | "suggest" | "warn";

export function advisePagination(criteria: SearchCriteria): PaginationAdvice {
  if (criteria.sourceId.length > 0) {
    return "none";
  }

  return criteria.cursor ? "warn" : "suggest";
}

/** Ölçütleri `POST /v1/events/search` gövdesine çeviriyor. */
export function toSearchBody(criteria: SearchCriteria): EventSearchBody {
  const filters: NonNullable<EventSearchBody["filters"]> = [];

  if (criteria.vendor.length > 0) {
    filters.push({ field: "vendor", op: "eq", values: [criteria.vendor] });
  }

  if (criteria.proto.length > 0) {
    filters.push({ field: "proto", op: "eq", values: [criteria.proto] });
  }

  if (criteria.action.length > 0) {
    filters.push({ field: "action", op: "eq", values: [criteria.action] });
  }

  if (criteria.severityMin !== undefined) {
    // API'de `>=` operatörü yok; `gt` ile bir eksiği veriliyor. `severity_num`
    // tamsayı olduğu için ikisi denk — ve 0 ("belirtilmemiş") dışarıda kalıyor,
    // çünkü "belirtilmemiş" ile "düşük" aynı şey değil.
    filters.push({
      field: "severity_num",
      op: "gt",
      values: [String(criteria.severityMin - 1)],
    });
  }

  return {
    // Boş bırakılırsa API son 24 saati alıyor; burada `undefined` bırakmak o
    // varsayılanı tek yerde tutuyor.
    from: criteria.from.length > 0 ? new Date(criteria.from).toISOString() : undefined,
    to: criteria.to.length > 0 ? new Date(criteria.to).toISOString() : undefined,
    full_text: criteria.fullText.length > 0 ? criteria.fullText : undefined,
    filters,
    owner_groups: criteria.ownerGroup.length > 0 ? [criteria.ownerGroup] : [],
    source_ids: criteria.sourceId.length > 0 ? [criteria.sourceId] : [],
    parse_statuses: [...criteria.parseStatuses],
    after_timestamp: criteria.cursor?.afterTimestamp,
    after_event_id: criteria.cursor?.afterEventId,
    limit: criteria.limit,
    ascending: false,
  };
}
