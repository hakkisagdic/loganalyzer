import { ApiError, TransportError } from "@/lib/api/errors";

/**
 * Bir hatayı kullanıcıya gösterilebilecek metne çeviriyor.
 *
 * <p>
 * <c>ApiError</c> zaten sunucunun cümlesini taşıyor; onu yeniden yazmak,
 * sunucudaki gerekçeyi (hangi grup kapsam dışında, hangi sınır aşıldı) atıp
 * yerine genel bir cümle koymak olurdu. Yalnızca <b>tanınmayan</b> hata
 * biçimleri için genel bir metin üretiliyor — çıplak `String(cause)` çoğu
 * zaman `[object Object]` basar.
 * </p>
 */
export function describeError(cause: unknown): string {
  if (cause instanceof ApiError) {
    return [cause.problem.error, cause.hint].filter(Boolean).join(" ");
  }

  if (cause instanceof TransportError) {
    return cause.message;
  }

  return cause instanceof Error ? cause.message : "Beklenmeyen bir hata oluştu.";
}
