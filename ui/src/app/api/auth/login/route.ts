import { NextResponse, type NextRequest } from "next/server";

import { readBffConfig } from "@/lib/auth/config";
import { authorizationUrl, discover, OidcError, pkcePair, randomToken } from "@/lib/auth/oidc";
import { safeReturnTo } from "@/lib/auth/redirects";
import { rememberLoginAttempt } from "@/lib/auth/store";

export const dynamic = "force-dynamic";

/** Giriş denemesinin ömrü. Kullanıcı Keycloak ekranında bu kadar oyalanabilir. */
const ATTEMPT_TTL_MS = 10 * 60 * 1000;

/**
 * Girişi başlatıyor: PKCE üretiliyor, doğrulayıcı **sunucuda** saklanıyor,
 * tarayıcı Keycloak'a yönlendiriliyor.
 */
export async function GET(request: NextRequest): Promise<NextResponse> {
  const config = readBffConfig();

  let discovery: Awaited<ReturnType<typeof discover>>;

  try {
    discovery = await discover(config);
  } catch (cause) {
    // Keycloak ayakta değilse ham bir 500 yerine sebebi söyleyen bir sayfa.
    // F1'in en pahalı hatalarının ortak yanı, mesajın yanlış yeri işaret
    // etmesiydi; burada adres ve gerekçe birlikte gidiyor.
    const failure = new URL("/giris", request.nextUrl.origin);
    failure.searchParams.set(
      "hata",
      cause instanceof OidcError ? cause.message : "Keycloak'a ulaşılamıyor.",
    );
    failure.searchParams.set(
      "ipucu",
      cause instanceof OidcError && cause.detail
        ? cause.detail
        : `Realm adresi: ${config.issuer}`,
    );

    return NextResponse.redirect(failure);
  }

  const state = randomToken(32);
  const nonce = randomToken(32);
  const { verifier, challenge } = await pkcePair();

  rememberLoginAttempt(state, {
    codeVerifier: verifier,
    nonce,
    returnTo: safeReturnTo(request.nextUrl.searchParams.get("returnTo")),
    expiresAt: Date.now() + ATTEMPT_TTL_MS,
  });

  return NextResponse.redirect(authorizationUrl(discovery, config, { state, nonce, challenge }));
}
