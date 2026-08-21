import {
  ApiError,
  ForbiddenError,
  NotFoundError,
  RateLimitedError,
  SessionExpiredError,
  TransportError,
} from "@/lib/api/errors";

/**
 * Bir hatayı **sınıfına** indiriyor. Mesajına değil.
 *
 * <p>
 * Bu modülün varlık sebebi `describeError` ile arasındaki fark. O fonksiyon
 * <b>sunucunun cümlesini</b> döndürüyor ve döndürmesi doğru — kullanıcının
 * ekranında "hangi grup kapsam dışında", "hangi sınır aşıldı" yazması
 * gerekiyor. Ama o cümle bir grup adı, bir dosya yolu, bir sınır değeri, hatta
 * bir log satırı taşıyabilir. Telemetriye giden şey <b>o cümle olamaz</b>.
 * </p>
 *
 * <p>
 * Buradaki sözlük <b>kapalı</b>: dönebilecek değerler sayılı. Yeni bir hata
 * tipi eklendiğinde `unknown`'a düşüyor — sessizce ham metin sızdırmıyor.
 * Yanlış yöndeki hata bu olurdu.
 * </p>
 */
export type ErrorKind =
  /**
   * Kimlik katmanı düştü — API çağrısı değil.
   *
   * <p>Ayrı bir üye çünkü ayrı bir arıza: `currentUser` üç durumlu ve
   * "API cevap vermiyor" hâli hiçbir HTTP durumu taşımıyor. `unknown`'a
   * düşürmek onu ekranların ürettiği her sınıflandırılamayan hatayla aynı
   * kovaya koyardı ve "kimlik katmanı mı bozuk, bir uç mu" sorusu veride
   * cevaplanamaz olurdu. Sözlüğe girdi, yani kapalı kalmaya devam ediyor.</p>
   */
  | "identity"
  | "session_expired"
  | "forbidden"
  | "not_found"
  | "rate_limited"
  | "transport"
  | `http_${number}`
  | "unknown";

export function errorKind(cause: unknown): ErrorKind {
  // Sıra önemli: hepsi `ApiError`'dan türüyor, en özel olan önce sorulmalı.
  if (cause instanceof SessionExpiredError) {
    return "session_expired";
  }

  if (cause instanceof ForbiddenError) {
    return "forbidden";
  }

  if (cause instanceof NotFoundError) {
    return "not_found";
  }

  if (cause instanceof RateLimitedError) {
    return "rate_limited";
  }

  if (cause instanceof ApiError) {
    // Durum kodu bir SAYI, sunucunun cümlesi değil. `http_500` "sunucu
    // düştü" diyor ve içinde hiçbir şey taşımıyor.
    return `http_${cause.status}`;
  }

  if (cause instanceof TransportError) {
    return "transport";
  }

  return "unknown";
}

/**
 * Bir hatanın HTTP durumu — biliniyorsa.
 *
 * <p>Ayrı bir fonksiyon çünkü `error_shown` olayı ikisini de taşıyor ve
 * `errorKind`'ın dönüş tipini sayıya bulaştırmak istemedik.</p>
 */
export function errorStatus(cause: unknown): number | undefined {
  return cause instanceof ApiError ? cause.status : undefined;
}
