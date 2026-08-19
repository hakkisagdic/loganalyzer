import { Badge, type BadgeTone } from "@/components/ui/Field";

/**
 * <c>time_source</c> — olayın zamanı <b>nereden</b> geldi.
 *
 * <p>
 * Bunu görmeden "olay saat 14:03'te oldu" cümlesi kurulamıyor: değer cihazın
 * kendi yazdığı zaman da olabilir, collector'ın gördüğü an da, bizim aldığımız
 * an da. Aradaki fark ağ gecikmesi + tampon süresi kadar, yani dakikalara
 * çıkabiliyor — ve RCA'nın korelasyon penceresi buna bağlı (F1).
 * </p>
 *
 * <p>
 * Rozet renkle <b>ve</b> metinle anlatıyor: <c>observed</c> ile <c>parsed</c>
 * arasındaki farkı yalnızca renge bırakmak, renk körü bir kullanıcı için o
 * bilgiyi tamamen yok etmek olurdu (WCAG 1.4.1).
 * </p>
 */

interface Presentation {
  readonly label: string;
  readonly tone: BadgeTone;
  readonly explanation: string;
}

const PRESENTATION: Record<string, Presentation> = {
  parsed: {
    label: "cihaz saati",
    tone: "success",
    explanation: "Zaman satırın kendisinden çözüldü — tek gerçekten güvenilir kaynak.",
  },
  observed: {
    label: "gözlem saati",
    tone: "warning",
    explanation:
      "Satırda tarih yoktu; bu, collector'ın satırı gördüğü an. Cihazdaki gerçek zamandan sapabilir.",
  },
  received: {
    label: "alınma saati",
    tone: "danger",
    explanation:
      "Son çare: bizim aldığımız an. Ağ gecikmesi ve tampon süresi kadar sapma taşıyor.",
  },
};

/** Kolon eklenmeden önce yazılmış satırlar. */
const UNKNOWN: Presentation = {
  label: "bilinmiyor",
  tone: "neutral",
  explanation:
    "Bu satır `time_source` kolonu eklenmeden önce yazıldı; zamanın kaynağı kayıtlı değil.",
};

export function describeTimeSource(value: string): Presentation {
  return PRESENTATION[value] ?? UNKNOWN;
}

export function TimeSourceBadge({ value }: { value: string }) {
  const presentation = describeTimeSource(value);

  return (
    <span title={presentation.explanation}>
      <Badge tone={presentation.tone}>{presentation.label}</Badge>
    </span>
  );
}
