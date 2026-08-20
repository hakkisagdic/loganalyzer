import type { components } from "@/lib/api/schema";

/**
 * Alarm ekranlarının tipleri — hepsi **üretilen şemadan**.
 *
 * <p>
 * Bu dosya tip <b>tanımlamıyor</b>, yalnızca şemadaki adlara okunabilir birer
 * takma ad veriyor. Elle yazılan bir tip, API değiştiği gün sessizce yalan
 * söyler; şemadan gelen tip `npm run api:check` kapısında CI'ı kırar. F1'in
 * dersi ("doğrulanmamış her katman kırıktı") arayüz tarafında tam olarak bu
 * ayrımda karşılığını buluyor.
 * </p>
 */
type Schemas = components["schemas"];

export type AlertRule = Schemas["AlertRuleResponse"];
export type AlertRuleList = Schemas["AlertRuleListResponse"];
export type AlertRuleDetail = Schemas["AlertRuleDetailResponse"];
export type AlertPreview = Schemas["AlertPreviewResponse"];
export type PreviewPoint = Schemas["PreviewPointResponse"];
export type PreviewSource = Schemas["PreviewSourceResponse"];
export type AlertTrigger = Schemas["AlertTriggerResponse"];
export type AlertTriggerList = Schemas["AlertTriggerListResponse"];
export type AlertDelivery = Schemas["AlertDeliveryResponse"];
export type MaintenanceWindow = Schemas["MaintenanceWindowResponse"];
export type MaintenanceWindowList = Schemas["MaintenanceWindowListResponse"];
export type NotificationChannel = Schemas["NotificationChannelResponse"];
export type NotificationChannelList = Schemas["NotificationChannelListResponse"];
export type ChannelTest = Schemas["ChannelTestResponse"];
export type AlertRuleRequest = Schemas["AlertRuleRequest"];
export type NotificationChannelRequest = Schemas["NotificationChannelRequest"];

/** Kural tipleri — API'nin kabul ettiği kısa adlar. */
export const RULE_TYPES = ["threshold", "ratio", "silence"] as const;
export type RuleType = (typeof RULE_TYPES)[number];

export const RULE_TYPE_LABELS: Record<RuleType, string> = {
  threshold: "Eşik — sayı bir sınırı aştı mı",
  ratio: "Oran — değişim hızlandı mı",
  silence: "Sessizlik — beklenen veri gelmedi mi",
};

export const COMPARISONS = ["gt", "gte", "lt", "lte"] as const;
export type Comparison = (typeof COMPARISONS)[number];

export const COMPARISON_LABELS: Record<Comparison, string> = {
  gt: "büyükse (>)",
  gte: "büyük veya eşitse (≥)",
  lt: "küçükse (<)",
  lte: "küçük veya eşitse (≤)",
};

export const CHANNEL_TYPES = ["slack", "teams", "email", "webhook"] as const;
export type ChannelType = (typeof CHANNEL_TYPES)[number];

export const CHANNEL_TYPE_LABELS: Record<ChannelType, string> = {
  slack: "Slack",
  teams: "Microsoft Teams",
  email: "E-posta (SMTP)",
  webhook: "Genel webhook",
};

/**
 * Şemadaki sayısal alanı `number`'a çeviriyor.
 *
 * <p>
 * .NET 10'un OpenAPI üreticisi `long` ve `double` alanları
 * `type: [integer, string]` olarak yazıyor — JSON'da dizge kodlanmış sayıları
 * da kabul etmek için. Üretilen TypeScript tipi bu yüzden `number | string`.
 * </p>
 *
 * <p>
 * Dönüşüm <b>tek yerde</b>: her kullanım yerinde `Number(...)` yazmak, birinde
 * unutulduğu gün `"12" > 9` gibi dizge karşılaştırmasına düşmek demekti — ve o
 * hata eşik karşılaştırmasında sessizce yanlış tetiklenme sayısı üretirdi.
 * </p>
 */
export function toNumber(value: number | string | null | undefined): number {
  if (typeof value === "number") return value;
  if (value === null || value === undefined) return 0;

  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

/**
 * Saniyeyi insanın okuyacağı hâle getiriyor.
 *
 * <p>Motorun `AlertEvaluator.Describe`'ıyla aynı eşikler — bildirim metnindeki
 * "30 dk" ile ekrandaki "30 dk" ayrışırsa kullanıcı ikisinin farklı şeyler
 * ölçtüğünü sanır.</p>
 */
export function describeSeconds(seconds: number | string): string {
  const value = Math.round(toNumber(seconds));

  if (value < 60) return `${value} sn`;
  if (value < 3600) return `${Math.round(value / 60)} dk`;
  if (value < 86400) return `${Math.round(value / 3600)} sa`;
  return `${Math.round(value / 86400)} gün`;
}

/** Tarihi yerel okunabilir biçime çeviriyor; geçersiz girdi sessizce boş dönüyor. */
export function formatInstant(value: string | null | undefined): string {
  if (!value) return "—";

  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? "—"
    : date.toLocaleString("tr-TR", { dateStyle: "short", timeStyle: "medium" });
}
