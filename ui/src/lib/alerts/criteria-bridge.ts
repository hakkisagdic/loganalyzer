import { PARAM } from "@/lib/events/criteria";

/**
 * Arama ölçütünün alarm kuralına çevrilme tablosu.
 *
 * <p>
 * <b>Bu dosya bir özellik değil, bir taahhüt.</b> "Bir arama" bugün iki yerde
 * temsil ediliyor: T15'in adres çubuğu parametreleri (`criteria.ts`) ve alarm
 * motorunun `AlertSearch`/`FieldFilter`'ı. Ayrışırlarsa ekrandan kurulan bir
 * alarm, ekranda görülen sonuçtan başkasını izler — ve bu **hiçbir yerde
 * kırmızı yanmaz**, çünkü iki taraf da kendi içinde tutarlı kalır.
 * </p>
 *
 * <p>
 * Alan filtresi arayüzü henüz yok (alarm formu `filters: []` gönderiyor), yani
 * bugün sessizce düşen bir alan da yok. Düzeltilecek bir kusur değil,
 * <b>önlenecek</b> bir kusur var: tablo, yeni bir ölçüt eklendiğinde onun
 * alarma nasıl çevrileceğinin karara bağlanmasını zorunlu kılıyor.
 * <c>alert-criteria-bridge.test.ts</c> eşlemesi olmayan bir ölçüt görürse
 * kırmızı yanıyor.
 * </p>
 */

/** Kuralın olay filtresine (`FieldFilter`) çevrilen ölçütler. */
export interface FilterMapping {
  readonly kind: "filter";
  /** `EventReader.FilterableColumns` içindeki kolon adı. */
  readonly column: string;
  /** `FieldFilter` operatörünün kısa adı. */
  readonly op: "eq" | "ne" | "in" | "gt" | "lt" | "contains" | "startswith";
  readonly note?: string;
}

/** Kuralın kendi alanına giden ölçütler — filtre değil. */
export interface DirectMapping {
  readonly kind: "direct";
  /** `AlertRuleRequest` üzerindeki alan. */
  readonly field: string;
  readonly note?: string;
}

/** Alarma çevrilemeyen ölçütler; sebebi yazılı olmak zorunda. */
export interface UnmappedCriterion {
  readonly kind: "unmapped";
  readonly reason: string;
}

export type CriterionMapping = FilterMapping | DirectMapping | UnmappedCriterion;

/**
 * `PARAM`'ın her anahtarı burada karşılığını bulmak zorunda.
 *
 * <p>Anahtarlar `PARAM`'ın <b>anahtarları</b>, değerleri değil: parametre adı
 * değişse bile eşleme kaybolmasın.</p>
 */
export const CRITERION_BRIDGE: Record<keyof typeof PARAM, CriterionMapping> = {
  fullText: {
    kind: "direct",
    field: "fullText",
    note: "Kuralın tam metin alanı. F1 ölçümü: indeks ~10-11 karakterden sonra seçici ve kural periyodik koştuğu için bedel katlanıyor.",
  },
  sourceId: { kind: "direct", field: "sourceIds" },
  ownerGroup: {
    kind: "direct",
    field: "ownerGroups",
    note: "Kuralın KAPSAMI oluyor, filtresi değil: kapsam daraltması kapsamı genişletemez (K17).",
  },
  parseStatus: { kind: "direct", field: "parseStatuses" },

  vendor: { kind: "filter", column: "vendor", op: "eq" },
  proto: { kind: "filter", column: "proto", op: "eq" },
  action: { kind: "filter", column: "action", op: "eq" },

  severityMin: {
    kind: "filter",
    column: "severity_num",
    op: "gt",
    // Ekrandaki ölçüt "n ve üzeri", operatör kümesinde ise `gte` YOK.
    // Çeviri bu yüzden n-1'e `gt`: sessizce `gt n` yazmak, kullanıcının
    // istediğinden bir kademe dar bir alarm üretirdi.
    note: "Ekranda 'n ve üzeri'; operatör kümesinde `gte` olmadığı için değer n-1'e düşürülüp `gt` kullanılır.",
  },

  from: {
    kind: "unmapped",
    reason:
      "Arama mutlak aralık kullanıyor, alarm kayan pencere. Karşılığı `windowSeconds` ama birebir çeviri değil: kullanıcı pencere uzunluğunu kendisi seçmeli.",
  },
  to: {
    kind: "unmapped",
    reason: "Aynı sebep — alarmın bitişi daima 'şimdi'.",
  },
  limit: {
    kind: "unmapped",
    reason: "Alarm sayıyor, sayfalamıyor; satır sınırının karşılığı yok.",
  },
  afterTimestamp: {
    kind: "unmapped",
    reason: "Keyset imleci; tek bir sayfanın konumu, kuralın değil.",
  },
  afterEventId: {
    kind: "unmapped",
    reason: "Aynı imlecin ikinci yarısı.",
  },
  force: {
    kind: "unmapped",
    reason:
      "Kısa tam metin aramasını zorlamak için ekrana özel bayrak; kuralda karşılığı olsaydı periyodik tam tarama demek olurdu.",
  },
};

/** Bir ölçütün alarma çevrilip çevrilemediği. */
export function isTranslatable(key: keyof typeof PARAM): boolean {
  return CRITERION_BRIDGE[key].kind !== "unmapped";
}

/** Eşlemenin hedeflediği olay kolonları — C# tarafındaki izin listesiyle karşılaştırılıyor. */
export function mappedColumns(): readonly string[] {
  return Object.values(CRITERION_BRIDGE)
    .filter((mapping): mapping is FilterMapping => mapping.kind === "filter")
    .map((mapping) => mapping.column)
    .sort();
}
