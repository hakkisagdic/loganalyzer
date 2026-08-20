import type { components } from "@/lib/api/schema";

/**
 * Katalog ekranının tipleri — hepsi **üretilen şemadan**.
 *
 * <p>Bu dosya tip tanımlamıyor, şemadaki adlara okunabilir takma ad veriyor.
 * Elle yazılan bir tip API değiştiği gün sessizce yalan söyler; şemadan gelen
 * tip `npm run api:check` kapısında CI'ı kırar.</p>
 */
type Schemas = components["schemas"];

export type ParserSummary = Schemas["ParserSummaryResponse"];
export type ParserList = Schemas["ParserListResponse"];
export type ParserDetail = Schemas["ParserDetailResponse"];
export type ParserDraft = Schemas["ParserDraftResponse"];
export type ParserDraftList = Schemas["ParserDraftListResponse"];
export type ParserDraftDetail = Schemas["ParserDraftDetailResponse"];
export type PublishVerdict = Schemas["PublishVerdictResponse"];
export type ParserPublishResult = Schemas["ParserPublishResponse"];
export type CatalogCoverage = Schemas["CatalogCoverageResponse"];

/** Taslak durumları — API'nin döndürdüğü küçük harfli adlar. */
export const DRAFT_STATES = ["draft", "inreview", "published", "retired"] as const;
export type DraftState = (typeof DRAFT_STATES)[number];

export const DRAFT_STATE_LABELS: Record<DraftState, string> = {
  draft: "taslak",
  inreview: "incelemede",
  published: "yayında",
  retired: "emekli",
};

/**
 * Kapsam oranının "düşük" sayıldığı eşik.
 *
 * <p>
 * F1'de katalog <c>86/1/0</c> veriyordu, yani <c>failed</c> sıfır ve
 * <c>partial</c> tek satır. Eşik yüzde bir: bunun üstüne çıkan bir parser
 * kataloğun bilinen en iyi hâlinden geriye gitmiş demek ve ekranda
 * <b>işaretlenmesi</b> gerekiyor (T20 kabul kriteri).
 * </p>
 */
export const COVERAGE_WARN_PERCENT = 1;

/** `ok` dışındaki satırların yüzdesi. */
export function missPercent(coverage: CatalogCoverage): number {
  const total = Number(coverage.total);
  if (total === 0) return 0;

  return ((Number(coverage.partial) + Number(coverage.failed)) * 100) / total;
}
