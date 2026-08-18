import type { NextRequest } from "next/server";

import { cookieSecure, readBffConfig, type BffConfig } from "./config";
import { accessTokenExpiry, discover, OidcError, randomToken, refresh } from "./oidc";
import { sessionStore, type SessionRecord } from "./store";

/**
 * Oturumun tarayıcı tarafı: **yalnızca bir çerez, yalnızca bir anahtar**.
 *
 * <p>
 * Çerezin değeri rastgele bir dizgi. İçinde token yok, kullanıcı adı yok, rol
 * yok — çözülebilecek hiçbir şey yok. "Erişim token'ı tarayıcıya hiç ulaşmıyor"
 * kabul kriterinin dayanağı bu: sızdıracak bir şey yok, çünkü gönderilen şey
 * bir anahtardan ibaret.
 * </p>
 */

export interface SessionCookieOptions {
  readonly name: string;
  readonly value: string;
  readonly httpOnly: true;
  readonly secure: boolean;
  readonly sameSite: "lax";
  readonly path: "/";
  readonly maxAge: number;
}

export function sessionCookieOptions(config: BffConfig, value: string, maxAge: number): SessionCookieOptions {
  return {
    name: config.cookieName,
    value,
    // Üçü de pazarlık dışı:
    // - HttpOnly: JavaScript çerezi okuyamıyor, yani XSS oturumu çalamıyor.
    // - SameSite=Lax: siteler arası POST'la oturum kullanılamıyor (CSRF).
    // - Secure: adres HTTPS ise çerez düz HTTP'ye hiç gitmiyor.
    httpOnly: true,
    secure: cookieSecure(config),
    sameSite: "lax",
    path: "/",
    maxAge,
  };
}

export function newSessionId(): string {
  return randomToken(32);
}

export function readSessionId(request: NextRequest, config = readBffConfig()): string | undefined {
  return request.cookies.get(config.cookieName)?.value;
}

/**
 * Yenilemenin ne kadar erken tetikleneceği.
 *
 * <p>Sıfır olsaydı, "henüz geçerli" bir token'la yola çıkıp API'ye vardığında
 * süresi dolmuş olabilirdi — kullanıcı sebepsiz bir 401 görürdü. Otuz saniye,
 * ağ gecikmesi ve saat kaymasına karşı payı olan pratik bir değer.</p>
 */
const REFRESH_SKEW_MS = 30_000;

export interface ResolvedSession {
  readonly id: string;
  readonly record: SessionRecord;
  /** Bu istekte token yenilendiyse `true`. Testler bunu okuyor. */
  readonly refreshed: boolean;
}

/**
 * Oturumu çözüyor ve gerekiyorsa erişim token'ını **kullanıcıya hissettirmeden**
 * yeniliyor.
 *
 * <p>Yenileme başarısız olursa oturum siliniyor ve `undefined` dönüyor: elde
 * kullanılamaz bir token'la devam etmek, kullanıcıya art arda 401 göstermek
 * demek olurdu.</p>
 */
export async function resolveSession(
  sessionId: string | undefined,
  config = readBffConfig(),
  now = Date.now(),
): Promise<ResolvedSession | undefined> {
  if (!sessionId) {
    return undefined;
  }

  const store = sessionStore();
  const record = await store.get(sessionId);

  if (!record) {
    return undefined;
  }

  if (record.accessTokenExpiresAt - REFRESH_SKEW_MS > now) {
    return { id: sessionId, record, refreshed: false };
  }

  if (!record.refreshToken) {
    await store.delete(sessionId);
    return undefined;
  }

  try {
    const discovery = await discover(config);
    const tokens = await refresh(discovery, config, record.refreshToken);

    const renewed: SessionRecord = {
      accessToken: tokens.access_token,
      // Keycloak dönen yanıtta yenileme token'ını döndürmeyebilir; o durumda
      // eldeki geçerli kalıyor.
      refreshToken: tokens.refresh_token ?? record.refreshToken,
      idToken: tokens.id_token ?? record.idToken,
      accessTokenExpiresAt: accessTokenExpiry(tokens, now),
      expiresAt: record.expiresAt,
    };

    await store.set(sessionId, renewed);
    return { id: sessionId, record: renewed, refreshed: true };
  } catch (error) {
    if (!(error instanceof OidcError)) {
      throw error;
    }

    // Yenileme token'ı da düşmüş (kullanıcı Keycloak'tan çıkmış, realm yeniden
    // import edilmiş, oturum idle timeout'a takılmış). Doğru davranış yeniden
    // giriş istemek.
    await store.delete(sessionId);
    return undefined;
  }
}

export function clearedSessionCookie(config: BffConfig): SessionCookieOptions {
  return sessionCookieOptions(config, "", 0);
}
