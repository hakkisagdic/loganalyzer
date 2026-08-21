import type { ErrorKind } from "./classify";
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
 * <b>Ama "yalnızca bu liste gider" DEĞİL, ve fark önemli.</b> Süzgeç
 * <i>ekranın geçirdiği</i> alanları buna göre kesiyor; sink'ler bunun üstüne
 * kendi alanlarını ekliyor: her iki taraf da <c>environment</c>, posthog-js
 * <c>$lib</c>/<c>$lib_version</c> gibi kütüphane alanları, posthog-node ayrıca
 * <c>$is_server</c> ve <c>$geoip_disable</c>. Kütüphanenin kendi eklediği
 * alanlar `DENIED_AUTOMATIC_PROPERTIES` kara listesinden geçiyor
 * (bkz. `scrub.ts`) — yani iki ayrı mekanizma, iki ayrı küme.
 * </p>
 *
 * <p>
 * Bu satır bir ayrıntı değil bir <b>düzeltme</b>: buradaki açıklamayı
 * "allowlist dışında hiçbir şey gitmiyor" diye okuyan biri, gerçekte giden
 * alanları saymadan güvenlik yorumu yapardı. Giden şeyin tamamını görmek
 * için <c>ui/scripts/telemetry-echo.mjs</c> ham paylodu basıyor.
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
    properties: [
      "criteria_count",
      "range_hours",
      "result_count",
      "duration_ms",
      "has_full_text",
      "query_verdict",
      "paginated",
      "page_size",
      "scoped",
    ],
    describes:
      "Olay araması. Aranan metin ASLA gitmiyor; `has_full_text` yalnızca VAR MI diyor, " +
      "`query_verdict` kısa-sorgu kapısının kararı (ready/forced/too-short), `scoped` " +
      "kaynak filtresi verilmiş mi — F1'de ölçülen derin sayfalama maliyeti bununla ilgili.",
  }),

  /** Parser taslağı derlendi. Derleme başarısı ürünün en önemli sağlık sinyali. */
  parser_compiled: event({
    name: "parser_compiled",
    properties: [
      "succeeded",
      "error_kind",
      "duration_ms",
      "schema_error_count",
      "test_failure_count",
      "redos_count",
      "yaml_lines",
      "gate_ok",
      "gate_stage",
    ],
    describes:
      "Parser derleme sonucu. `error_kind` SINIFLANDIRILMIŞ hata tipi (bkz. classify.ts), " +
      "mesajın kendisi değil — mesaj bir pattern adı ya da bir log satırı taşıyabilir. " +
      "`yaml_lines` taslağın BOYUTU; içeriğinden hiçbir şey gitmiyor. `gate_stage` " +
      "kapının DURDUĞU aşama — sınırlı bir sözlük, ve \"insanlar nerede takılıyor\" " +
      "sorusunun tek cevaplanabilir hâli.",
  }),

  /** Parser sürümü yayımlandı. */
  parser_submitted: event({
    name: "parser_submitted",
    properties: ["succeeded", "error_kind", "gate_ok_before_submit"],
    describes:
      "Taslak incelemeye gönderildi. `gate_ok_before_submit` ekranın gönderim ANINDA ne " +
      "sandığı: kapı sunucuda YENİDEN koşuyor, yani ekranın tahmini ile sonucun ayrıştığı " +
      "oran ölçülebilir bir şey ve ürün için anlamlı.",
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

/**
 * **Her alanın tipi.** Katalog neyin gidebileceğini söylüyordu; bu tablo
 * <b>ne şekilde</b> gidebileceğini söylüyor.
 *
 * <p>
 * İnceleme bunu istedi ve gerekçesi haklıydı: paylod `unknown` iken bir ekran
 * `error_kind: identity.message` yazabiliyordu — yani sunucunun cümlesini
 * doğrudan telemetriye koyabiliyordu — ve derleyici susuyordu. Kaynağı tarayan
 * bir bekçi bunu yakalayamaz, çünkü orada bir metin sabiti yok, bir ifade var.
 * </p>
 *
 * <p>
 * Bu tabloyla o hata artık <b>derlenmiyor</b>. Bir tarayıcının kaçırabildiği
 * her şeyi tip sistemi kaçırmıyor, ve kapalı sözlükler (`ErrorKind`,
 * `query_verdict`) buradan dayatılıyor.
 * </p>
 */
export interface EventFieldTypes {
  /** Kalıba indirilmiş yol — `/kaynaklar/:id`. Ham URL DEĞİL. */
  route: string;
  scope_kind: string;
  /** Sınıflandırılmış hata tipi. `classify.ts`'in kapalı sözlüğü. */
  error_kind: ErrorKind;
  status: number;

  criteria_count: number;
  range_hours: number;
  result_count: number;
  duration_ms: number;
  has_full_text: boolean;
  /** `judgeQuery`'nin kararı; serbest metin değil. */
  query_verdict: "ready" | "forced" | "too-short";
  paginated: boolean;
  page_size: number;
  scoped: boolean;

  succeeded: boolean;
  schema_error_count: number;
  test_failure_count: number;
  redos_count: number;
  yaml_lines: number;
  gate_ok: boolean;
  /** Kapının durduğu aşama. API'nin döndürdüğü sınırlı sözlük. */
  gate_stage: string;
  gate_ok_before_submit: boolean;

  is_new: boolean;
  has_threshold: boolean;
  signal_count: number;
  quality_band: string;
}

/**
 * Bir olayın taşıyabileceği paylod: katalogdaki alanlar, `EventFieldTypes`'taki
 * tipleriyle. Katalogda olmayan bir alan adı derlenmiyor; yanlış tipte bir
 * değer de derlenmiyor.
 */
export type EventPayload<TName extends EventName> = Readonly<
  Partial<Pick<EventFieldTypes, Extract<(typeof EVENTS)[TName]["properties"][number], keyof EventFieldTypes>>>
>;

/** Katalogda tanımlı olan alan listesi. `scrubProperties` bunu kullanıyor. */
export function allowedProperties(name: EventName): readonly string[] {
  return EVENTS[name].properties;
}

export type ScrubbedEvent = {
  readonly name: string;
  readonly properties: TelemetryProperties;
};
