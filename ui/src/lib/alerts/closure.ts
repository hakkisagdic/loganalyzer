import { reviewRequest, type ReviewRequestBody } from "@/lib/rca/report";

import type { AlertTrigger } from "./types";

/**
 * Alarm kapatma isteğinin gövdesi (T38).
 *
 * <p>
 * <b>RCA incelemesiyle aynı gövde ve aynı kurucu.</b> İki uç aynı dört alanı
 * alıyor (<c>verdict</c>, <c>contradicting_evidence</c>,
 * <c>actual_root_cause</c>, <c>note</c>) ve ikinci bir kurucu yazmak §9'un
 * yasakladığı kopya olurdu: alanlardan biri bir tarafta eklenip diğerinde
 * unutulursa altın küme yarısı eksik bir boyutla dolar ve ayrışma yalnızca
 * F4'ün karşılaştırmasında görünürdü.
 * </p>
 */
export type CloseRequestBody = ReviewRequestBody;

export function closeRequest(
  verdict: string,
  contradictingEvidence: string,
  actualRootCause: string,
  note = "",
): CloseRequestBody {
  return reviewRequest(verdict, contradictingEvidence, actualRootCause, note);
}

/**
 * Kapatma <b>seçilmiş bir karar</b> gerektiriyor.
 *
 * <p>
 * <b>Zorunluluk sunucuda yapısal</b> — <c>CloseAsync</c> incelemeyi parametre
 * olarak istiyor, yani incelemesiz kapatma diye bir çağrı yok. Buradaki
 * kontrol o kuralın <i>kopyası değil</i>, kullanıcıya <b>neden</b>
 * kapatamadığını söyleyen yüzü: sunucu 400 dönerdi, ekran ise düğmenin neden
 * kapalı olduğunu baştan söylüyor.
 * </p>
 *
 * <p>
 * <c>unknown</c> geçerli bir karar ve burada da öyle sayılıyor. Kaçış kapısı
 * değil bir ölçüm: doğruluk oranının paydasına girmiyor, kendi oranı ayrı bir
 * gösterge. Zorunlu bir soruda kaçış olmasaydı insanlar rastgele seçip geçerdi
 * ve altın küme, ölçülüyormuş gibi görünen gürültüyle dolardı.
 * </p>
 */
export function canClose(verdict: string | null): verdict is string {
  return verdict !== null && verdict.length > 0;
}

/**
 * Kapatmanın <b>ne kadar süreceği</b> hakkında kullanıcıya söylenecek şey.
 *
 * <p>
 * Kapatma ucu, tetiklenmenin penceresine ait kanıt paketi yoksa <b>üretimi
 * tetikliyor</b> (T38 kararı). Yani düğme ucuz değil ve bazen saniyeler sürer.
 * Ekran bunu söylemezse yavaş bir düğme <i>"takıldı"</i> diye okunur ve
 * kullanıcı ikinci kez tıklar — ikinci tık 400 alır ("zaten kapatılmış") ve
 * hata mesajı, sebebi olmayan bir arıza gibi görünür.
 * </p>
 *
 * <p>
 * Ekran paketin var olup olmadığını <b>bilemiyor</b>: tetiklenme yanıtı böyle
 * bir alan taşımıyor ve taşıması da yanlış olurdu — cevap istek anında
 * değişebilir. Bu yüzden metin bir <i>olasılık</i> söylüyor, bir vaat değil.
 * </p>
 */
export const CLOSE_COST_HINT =
  "Kapatırken kanıt paketi yoksa üretilir; bu birkaç saniye sürebilir.";

/** Kapatma sonrası, paket gerçekten üretildiyse gösterilecek metin. */
export function closeOutcomeText(bundleGenerated: boolean): string {
  return bundleGenerated
    ? "Alarm kapatıldı ve kanıt paketi bu kapatma sırasında üretildi."
    : "Alarm kapatıldı; kanıt paketi zaten vardı.";
}

/**
 * Tetiklenme kapatılabilir mi.
 *
 * <p>
 * Kapatılmış bir alarmı yeniden kapatmak altın kümeye aynı olgu için ikinci bir
 * kayıt yazardı ve doğruluk oranı, iki kez cevaplanan bir soruya iki kez
 * ağırlık verirdi. Sunucu bunu 400 ile reddediyor; ekran düğmeyi hiç
 * göstermiyor.
 * </p>
 */
export function isOpen(trigger: AlertTrigger): boolean {
  return trigger.state !== "closed";
}
