/**
 * `describeError` artık ortak katmanda (`@/lib/api/errors`).
 *
 * <p>
 * T23'te burada doğmuştu; T19 ikinci bir ekran getirince ortaklaştırıldı —
 * ikinci bir kopya, aynı 403'ü iki ekranda iki farklı sebep gibi gösterirdi.
 * Bu dosya alarm ekranlarının içeri aktarımlarını kırmamak için duruyor.
 * </p>
 */
export { describeError } from "@/lib/api/errors";
