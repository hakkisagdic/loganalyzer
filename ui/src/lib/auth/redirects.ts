/**
 * Açık yönlendirme (open redirect) koruması: yalnızca uygulama içi göreli
 * yollar kabul ediliyor.
 *
 * <p>`//baska.site` de bir mutlak adres — başında tek eğik çizgi olduğu için
 * gözden kaçıyor ve tarayıcı onu şema-göreli URL olarak çözüyor. İkinci koşul
 * bunun için var.</p>
 */
export function safeReturnTo(value: string | null | undefined): string {
  return value && value.startsWith("/") && !value.startsWith("//") ? value : "/";
}
