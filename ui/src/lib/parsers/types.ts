import type { components } from "@/lib/api/schema";

/**
 * Parser editörünün tipleri — hepsi **üretilen şemadan**.
 *
 * <p>
 * Bu dosya tip <b>tanımlamıyor</b>, yalnızca şemadaki adlara okunabilir birer
 * takma ad veriyor. Elle yazılan bir tip API değiştiği gün sessizce yalan
 * söyler; şemadan gelen tip `npm run api:check` kapısında CI'ı kırar — T14'ün
 * var olma sebebi tam olarak bu.
 * </p>
 */
type Schemas = components["schemas"];

export type ParserTry = Schemas["ParserTryResponse"];
export type ParserGate = Schemas["ParserGateResponse"];
export type ParserDraft = Schemas["ParserDraftResponse"];
export type ParseOutcome = Schemas["ParseOutcomeResponse"];
export type ParserDispatch = Schemas["ParserDispatchResponse"];
export type ParserSchemaError = Schemas["ParserSchemaErrorResponse"];
export type ParserRedosFinding = Schemas["ParserRedosFindingResponse"];
export type ParserTestCase = Schemas["ParserTestCaseResponse"];
export type ParserExpectation = Schemas["ParserExpectationResponse"];

/**
 * Kapı aşamaları — API'nin `stage` alanının aldığı değerler.
 *
 * <p>
 * Metinler kullanıcıya <b>ne yapacağını</b> söylüyor, ne olduğunu değil:
 * "şema hatası" bir teşhis, "YAML'ın yapısı bozuk; işaretli satıra bakın" bir
 * yönerge. Kapı ancak anlaşıldığında kapı; T18 onları kurdu, bu ekranın işi
 * okunur kılmak.
 * </p>
 */
export const GATE_STAGES = ["passed", "schema", "redos", "tests"] as const;
export type GateStage = (typeof GATE_STAGES)[number];

export interface GateStageCopy {
  readonly title: string;
  readonly detail: string;
}

export const GATE_STAGE_COPY: Record<GateStage, GateStageCopy> = {
  passed: {
    title: "Bütün kapılardan geçti",
    detail: "Şema temiz, pattern'ler doğrusal motorda derleniyor ve gömülü testlerin hepsi geçiyor.",
  },
  schema: {
    title: "Şema kapısında durdu",
    detail:
      "YAML yüklenemedi ya da derlenemedi. Bu en derin sebep — ReDoS taraması ve testler hiç koşmadı, " +
      "dolayısıyla onların sessizliği bir kanıt değil.",
  },
  redos: {
    title: "ReDoS kapısında durdu",
    detail:
      "En az bir pattern doğrusal motorda derlenemiyor ve geri izlemeye düşüyor. " +
      "Geri izleyen ifade `matchTimeout` ödüyor, o da duvar saatini ölçüyor: yüklü bir makinede " +
      "sağlıklı bir satır da `failed` olur. Kataloğun sıfır GROK003 değişmezi bu yüzden var.",
  },
  tests: {
    title: "Test kapısında durdu",
    detail:
      "Gömülü `tests` bloğu ya boş ya da en az bir test düşüyor. Testsiz bir parser'ın doğru " +
      "çalıştığı hiçbir zaman gösterilemez.",
  },
};

export function gateStage(value: string): GateStage {
  return (GATE_STAGES as readonly string[]).includes(value) ? (value as GateStage) : "schema";
}

/** Dispatcher kademeleri — `tier` alanının aldığı değerler. */
export const DISPATCH_TIERS = ["inventory_bound", "candidate", "unmatched"] as const;
export type DispatchTier = (typeof DISPATCH_TIERS)[number];

export const DISPATCH_TIER_LABELS: Record<DispatchTier, string> = {
  inventory_bound: "Kademe 1 — envanter bağı",
  candidate: "Kademe 2 — literal ön filtre",
  unmatched: "Kademe 3 — eşleşme yok",
};

export function dispatchTier(value: string): DispatchTier {
  return (DISPATCH_TIERS as readonly string[]).includes(value) ? (value as DispatchTier) : "unmatched";
}

/**
 * Ayrıştırma durumunun rozet tonu.
 *
 * <p>
 * <c>timed_out</c> <b>bu fonksiyona girmiyor</b> ve girmemeli: zaman aşımı
 * "uymadı" değil "ölçülemedi" demek ve ekranda ayrı bir uyarı olarak
 * duruyor. İkisini tek bir renge indirmek, sağlıklı bir parser'ı bozuk gibi
 * gösterirdi.
 * </p>
 */
export function statusTone(status: string): "success" | "warning" | "danger" {
  if (status === "ok") return "success";
  if (status === "partial") return "warning";
  return "danger";
}

export const PARSE_STATUS_LABELS: Record<string, string> = {
  ok: "ok — satır tam ayrıştı",
  partial: "partial — satır kısmen ayrıştı",
  failed: "failed — satır ayrıştırılamadı",
};

export function parseStatusLabel(status: string): string {
  return PARSE_STATUS_LABELS[status] ?? status;
}
