import type { TelemetryProperties } from "./scrub";

/**
 * **Olay kataloğu.** Gönderilebilecek olayların tamamı burada.
 *
 * <p>
 * Serbest metinle olay adı üretmek — `capture("parser_" + kind)` gibi — üç ay
 * sonra kimsenin ne anlama geldiğini bilmediği yüzlerce olay adı demek.
 * Katalog, olay eklemeyi bir <b>kod değişikliği</b> yapıyor: gözden geçirilir,
 * tartışılır, ve yanına hangi alanların gideceği aynı satırda yazılır.
 * </p>
 *
 * <p>
 * `properties` bir <b>beyaz liste</b>: `scrubProperties` bu listenin dışındaki
 * her alanı atıyor. Yani bir ekranda yanlışlıkla `{ sorgu: kullaniciMetni }`
 * geçirmek olayı bozmuyor, alanı sessizce düşürüyor — ve düşürdüğünün testi
 * var.
 * </p>
 *
 * <p>
 * Adlandırma `snake_case`, CLAUDE.md §8 ile aynı gerekçe: iki adlandırma
 * politikası bir arada yaşayamıyor.
 * </p>
 */
export interface EventDefinition {
  /** PostHog'da görünecek olay adı. */
  readonly name: string;
  /** Bu olayla gidebilecek alanlar. Dışındaki her şey atılıyor. */
  readonly properties: readonly string[];
  /** Olayın neyi ölçtüğü. Panoyu kuran kişinin okuduğu tek belge burası. */
  readonly describes: string;
}

function event<const T extends EventDefinition>(definition: T): T {
  return definition;
}

export const EVENTS = {
  /**
   * Sayfa görüntüleme. posthog-js'in kendi `capture_pageview`'ı **kapalı**;
   * onun yerine bu olay gidiyor, çünkü kütüphaneninki ham URL'yi taşıyor
   * (bkz. `scrub.ts`).
   */
  screen_viewed: event({
    name: "screen_viewed",
    properties: ["route", "scope_kind"],
    describes: "Hangi ekran açıldı. `route` kalıba indirilmiş yol (`/kaynaklar/:id`).",
  }),

  /**
   * Olay araması koştu. **Sorgunun kendisi gitmiyor** — yalnızca şekli:
   * kaç ölçüt, hangi zaman aralığı, kaç sonuç.
   */
  event_search_run: event({
    name: "event_search_run",
    properties: ["criteria_count", "range_hours", "result_count", "duration_ms", "timed_out"],
    describes: "Olay araması. Aranan metin ASLA gitmiyor; yalnızca ölçüt sayısı ve süre.",
  }),

  /** Parser taslağı derlendi. Derleme başarısı ürünün en önemli sağlık sinyali. */
  parser_compiled: event({
    name: "parser_compiled",
    properties: ["parser_kind", "succeeded", "error_kind", "duration_ms"],
    describes: "Parser derleme sonucu. `error_kind` sınıflandırılmış hata tipi, mesajın kendisi değil.",
  }),

  /** Parser sürümü yayımlandı. */
  parser_published: event({
    name: "parser_published",
    properties: ["parser_kind", "from_draft"],
    describes: "Bir parser sürümü yayına alındı.",
  }),

  /** Alarm ölçütü kaydedildi. */
  alert_saved: event({
    name: "alert_saved",
    properties: ["criteria_count", "is_new", "has_threshold"],
    describes: "Alarm kuralı kaydı. Kuralın metni gitmiyor, şekli gidiyor.",
  }),

  /** Kök neden analizi çalıştırıldı. */
  rca_run: event({
    name: "rca_run",
    properties: ["signal_count", "duration_ms", "quality_band"],
    describes: "RCA koşumu ve kalite bandı.",
  }),

  /**
   * Kullanıcıya bir hata gösterildi.
   *
   * <p>`error_kind` <b>sınıflandırılmış</b> bir değer — sunucudan gelen
   * mesajın kendisi değil. Mesaj bir dosya yolu, bir ana bilgisayar adı ya
   * da bir log satırı taşıyabilir.</p>
   */
  error_shown: event({
    name: "error_shown",
    properties: ["route", "status", "error_kind"],
    describes: "Ekranda gösterilen hata. Hata METNİ değil, sınıfı gidiyor.",
  }),
} as const;

export type EventName = keyof typeof EVENTS;

export type EventPayload<TName extends EventName> = Readonly<
  Partial<Record<(typeof EVENTS)[TName]["properties"][number], unknown>>
>;

/** Katalogda tanımlı olan alan listesi. `scrubProperties` bunu kullanıyor. */
export function allowedProperties(name: EventName): readonly string[] {
  return EVENTS[name].properties;
}

export type ScrubbedEvent = {
  readonly name: string;
  readonly properties: TelemetryProperties;
};
