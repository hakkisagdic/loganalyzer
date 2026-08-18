import { NextResponse, type NextRequest } from "next/server";

import { readBffConfig } from "@/lib/auth/config";
import { accessTokenExpiry, discover, exchangeCode, OidcError, verifyIdToken } from "@/lib/auth/oidc";
import { newSessionId, sessionCookieOptions } from "@/lib/auth/session";
import { consumeLoginAttempt, sessionStore } from "@/lib/auth/store";

export const dynamic = "force-dynamic";

/**
 * OIDC dönüş ucu.
 *
 * <p>
 * Yol <b>sabit</b>: Keycloak realm'inde `bizigo-ui` istemcisinin
 * `redirectUris` listesinde `http://localhost:3000/signin-oidc` birebir yazılı.
 * Buradaki dizin adı değişirse `deploy/keycloak/realm-bizigo.json` da
 * değişmeli.
 * </p>
 *
 * <p>
 * Bu işleyici token'ları alıyor ve <b>doğrudan sunucu deposuna</b> yazıyor.
 * Tarayıcıya dönen yanıtta yalnızca oturum çerezi ve bir yönlendirme var.
 * </p>
 */
export async function GET(request: NextRequest): Promise<NextResponse> {
  const config = readBffConfig();
  const params = request.nextUrl.searchParams;

  const error = params.get("error");

  if (error) {
    return failure(
      request,
      `Keycloak girişi reddetti: ${error}`,
      params.get("error_description") ?? undefined,
    );
  }

  const code = params.get("code");
  const state = params.get("state");

  if (!code || !state) {
    return failure(request, "Dönüş adresinde `code` ya da `state` yok.");
  }

  const attempt = consumeLoginAttempt(state);

  if (!attempt) {
    // `state` bilinmiyor: ya CSRF denemesi, ya sekme çok bekledi, ya da Next
    // sunucusu bu arada yeniden başladı (bellek içi depo).
    return failure(
      request,
      "Giriş denemesi bulunamadı.",
      "Bağlantı çok eski olabilir ya da sunucu yeniden başlatılmış olabilir. Baştan deneyin.",
    );
  }

  try {
    const discovery = await discover(config);
    const tokens = await exchangeCode(discovery, config, { code, codeVerifier: attempt.codeVerifier });

    if (!tokens.id_token) {
      return failure(request, "Keycloak `id_token` döndürmedi.", "İstemcinin `openid` kapsamını kontrol edin.");
    }

    await verifyIdToken(discovery, config, tokens.id_token, attempt.nonce);

    const now = Date.now();
    const sessionId = newSessionId();

    await sessionStore().set(sessionId, {
      accessToken: tokens.access_token,
      refreshToken: tokens.refresh_token,
      idToken: tokens.id_token,
      accessTokenExpiresAt: accessTokenExpiry(tokens, now),
      expiresAt: now + config.sessionTtlSeconds * 1000,
    });

    const response = NextResponse.redirect(new URL(attempt.returnTo, config.publicUrl));
    response.cookies.set(sessionCookieOptions(config, sessionId, config.sessionTtlSeconds));

    return response;
  } catch (cause) {
    if (cause instanceof OidcError) {
      return failure(request, cause.message, cause.detail);
    }

    throw cause;
  }
}

/**
 * Hatayı **giriş sayfasına** taşıyor, ham bir 500 göstermek yerine.
 *
 * <p>Mesaj sorgu dizesinde gidiyor; içinde token yok, yalnızca akışın nerede
 * kırıldığını söyleyen metin var.</p>
 */
function failure(request: NextRequest, message: string, hint?: string): NextResponse {
  const url = new URL("/giris", request.nextUrl.origin);
  url.searchParams.set("hata", message);

  if (hint) {
    url.searchParams.set("ipucu", hint);
  }

  return NextResponse.redirect(url);
}
