/**
 * API hatalarının tek tipli karşılığı.
 *
 * <p>
 * Gövde biçimi F1'de yerleşti: <c>{ error, hint }</c>. `error` ne olduğunu,
 * `hint` ne yapılacağını söylüyor — ikisi ayrı taşınıyor çünkü arayüzde ayrı
 * yerlere gidiyorlar (bkz. `ErrorState`).
 * </p>
 */
export interface ApiProblem {
  readonly error: string;
  readonly hint?: string;
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: ApiProblem,
  ) {
    super(problem.error);
    this.name = "ApiError";
  }

  get hint(): string | undefined {
    return this.problem.hint;
  }
}

/**
 * 401 — BFF oturumu düşmüş.
 *
 * <p>Erişim token'ının süresi dolduğu için değil: BFF onu şeffaf yeniliyor.
 * Buraya düşmek, yenilemenin de başarısız olduğu anlamına geliyor (kullanıcı
 * Keycloak'tan çıkmış ya da realm yeniden import edilmiş). Tek doğru cevap
 * yeniden giriş.</p>
 */
export class SessionExpiredError extends ApiError {
  override name = "SessionExpiredError";
}

/** 403 — kimlik doğru, yetki yok. Kullanıcıya "yetkiniz yok" gösteriliyor. */
export class ForbiddenError extends ApiError {
  override name = "ForbiddenError";
}

/**
 * 404 — bulunamadı.
 *
 * <p><b>Kapsam dışı bir olay da 404 dönüyor, 403 değil</b> (F1). 403 "böyle bir
 * olay var ama sizin değil" bilgisini sızdırırdı; bir ekibin başka bir ekibin
 * olay kimliğini deneyerek envanter çıkarmasına yeterdi. Bu yüzden istemci
 * tarafında ikisi ayrı ele alınıyor ve 404 mesajı "yetkiniz yok" demiyor.</p>
 */
export class NotFoundError extends ApiError {
  override name = "NotFoundError";
}

export class RateLimitedError extends ApiError {
  override name = "RateLimitedError";
}

/** Ağ kesintisi ya da JSON olmayan yanıt. */
export class TransportError extends Error {
  override name = "TransportError";
}

/**
 * HTTP durumunu tipli hataya çeviriyor.
 *
 * <p>Gövde `{ error, hint }` değilse durum koduna göre okunabilir bir metin
 * uyduruluyor: ham "500 Internal Server Error" kullanıcıya hiçbir şey
 * söylemiyor.</p>
 */
export function toApiError(status: number, body: unknown): ApiError {
  const problem = normalizeProblem(status, body);

  switch (status) {
    case 401:
      return new SessionExpiredError(status, problem);
    case 403:
      return new ForbiddenError(status, problem);
    case 404:
      return new NotFoundError(status, problem);
    case 429:
      return new RateLimitedError(status, problem);
    default:
      return new ApiError(status, problem);
  }
}

const fallbackMessages: Record<number, ApiProblem> = {
  401: { error: "Oturum sona erdi.", hint: "Yeniden giriş yapmanız gerekiyor." },
  403: { error: "Bu işlem için yetkiniz yok.", hint: "Gerekli rol için yöneticinize başvurun." },
  404: { error: "Kayıt bulunamadı.", hint: "Adres değişmiş ya da kayıt kapsamınız dışında olabilir." },
  429: { error: "Çok fazla istek.", hint: "Sorgu bitmeden yenisini başlatmayı deneyin." },
};

/**
 * Bir hatayı kullanıcıya gösterilebilecek metne çeviriyor.
 *
 * <p>
 * <c>ApiError</c> zaten sunucunun cümlesini taşıyor; onu yeniden yazmak,
 * sunucudaki gerekçeyi (hangi grup kapsam dışında, hangi sınır aşıldı) atıp
 * yerine genel bir cümle koymak olurdu. Yalnızca <b>tanınmayan</b> hata
 * biçimleri için genel bir metin üretiliyor — çıplak <c>String(cause)</c> çoğu
 * zaman <c>[object Object]</c> basar.
 * </p>
 *
 * <p>
 * T23'te alarm ekranının içinde doğdu; T19'da buraya taşındı. Ekranların
 * hatayı <b>aynı</b> biçimde göstermesi bir tutarlılık tercihi değil,
 * doğruluk meselesi: iki ekranın iki farklı özeti, aynı 403'ü iki farklı
 * sebep gibi gösterirdi (T28 bunu denetleyecek).
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

function normalizeProblem(status: number, body: unknown): ApiProblem {
  if (body && typeof body === "object" && "error" in body && typeof body.error === "string") {
    const hint = "hint" in body && typeof body.hint === "string" ? body.hint : undefined;
    return { error: body.error, hint };
  }

  return fallbackMessages[status] ?? { error: `İstek başarısız (HTTP ${status}).` };
}
