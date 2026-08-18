import { createLocalJWKSet, createRemoteJWKSet, decodeJwt, jwtVerify } from "jose";

import type { BffConfig } from "./config";
import { redirectUri } from "./config";

/**
 * Keycloak ile konuşan katman: keşif, authorization code + PKCE, token
 * yenileme ve çıkış.
 *
 * <p>
 * Tarayıcı bu modülün ürettiği hiçbir değeri görmüyor — yalnızca Keycloak'a
 * giden yetkilendirme adresini (ki içinde sır yok) ve dönüşteki oturum
 * çerezini.
 * </p>
 */

export interface DiscoveryDocument {
  readonly issuer: string;
  readonly authorization_endpoint: string;
  readonly token_endpoint: string;
  readonly jwks_uri: string;
  readonly end_session_endpoint?: string;
}

export interface TokenResponse {
  readonly access_token: string;
  readonly refresh_token?: string;
  readonly id_token?: string;
  readonly expires_in?: number;
  readonly token_type?: string;
}

/** Keycloak'ın hata gövdesi: `{ error, error_description }`. */
export class OidcError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly detail?: string,
  ) {
    super(message);
    this.name = "OidcError";
  }
}

let discoveryCache: { issuer: string; document: DiscoveryDocument } | undefined;

/**
 * Keşif belgesi süreç ömrü boyunca önbellekleniyor.
 *
 * <p><b>Realm yeniden import edilirse imzalama anahtarları değişiyor</b>
 * (`deploy/keycloak/README.md`). Bu bir hata değil, beklenen davranış:
 * geliştirme sırasında oturumlar geçersizleşir ve Next sunucusunun yeniden
 * başlatılması gerekir. JWKS ayrı önbellekte ve `jose` anahtar bulunamadığında
 * kendisi yeniden çekiyor, yani anahtar dönüşü tek başına sorun değil.</p>
 */
export async function discover(config: BffConfig): Promise<DiscoveryDocument> {
  if (discoveryCache?.issuer === config.issuer) {
    return discoveryCache.document;
  }

  const url = `${config.issuer}/.well-known/openid-configuration`;
  const response = await fetch(url, { cache: "no-store" });

  if (!response.ok) {
    throw new OidcError(
      `Keycloak keşif belgesi okunamadı (${response.status}).`,
      502,
      `Adres: ${url}`,
    );
  }

  const document = (await response.json()) as DiscoveryDocument;

  if (document.issuer !== config.issuer) {
    // F1'de ölçülen tuzak: `KC_HOSTNAME` ayarlı değilse Keycloak issuer'ı
    // isteğin host'undan türetiyor ve API'nin beklediğiyle uyuşmuyor. Sonuç
    // her istekte 401 ve hiçbir yerde sebebini söyleyen bir mesaj yok.
    throw new OidcError(
      "Keycloak issuer beklenenden farklı.",
      502,
      `Beklenen ${config.issuer}, gelen ${document.issuer}. deploy/keycloak/README.md — KC_HOSTNAME.`,
    );
  }

  discoveryCache = { issuer: config.issuer, document };
  return document;
}

/** Testlerin önbelleği taşımaması için. */
export function resetDiscoveryCache(): void {
  discoveryCache = undefined;
  jwksCache = undefined;
}

function base64Url(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("base64url");
}

export function randomToken(bytes = 32): string {
  return base64Url(crypto.getRandomValues(new Uint8Array(bytes)));
}

export async function pkcePair(): Promise<{ verifier: string; challenge: string }> {
  const verifier = randomToken(48);
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));

  return { verifier, challenge: base64Url(new Uint8Array(digest)) };
}

/**
 * Yetkilendirme adresi.
 *
 * <p>
 * <b>Yalnızca `openid` kapsamı isteniyor</b> — `profile`/`email` DEĞİL. Realm
 * dosyasında `clientScopes` dizisi verildiği için Keycloak yerleşik scope'ları
 * hiç oluşturmuyor; olmayan bir scope istemek `invalid_scope` ile düşer.
 * İhtiyacımız olan claim'lerin tamamı zaten `bizigo-claims` içinde ve o,
 * istemcinin varsayılan scope'u (`deploy/keycloak/README.md` — claim sözleşmesi).
 * </p>
 */
export function authorizationUrl(
  discovery: DiscoveryDocument,
  config: BffConfig,
  params: { state: string; nonce: string; challenge: string },
): string {
  const url = new URL(discovery.authorization_endpoint);

  url.searchParams.set("client_id", config.clientId);
  url.searchParams.set("redirect_uri", redirectUri(config));
  url.searchParams.set("response_type", "code");
  url.searchParams.set("scope", "openid");
  url.searchParams.set("state", params.state);
  url.searchParams.set("nonce", params.nonce);
  url.searchParams.set("code_challenge", params.challenge);
  url.searchParams.set("code_challenge_method", "S256");

  return url.toString();
}

async function postToken(
  discovery: DiscoveryDocument,
  config: BffConfig,
  body: Record<string, string>,
): Promise<TokenResponse> {
  const response = await fetch(discovery.token_endpoint, {
    method: "POST",
    headers: {
      "content-type": "application/x-www-form-urlencoded",
      // İstemci kimlik doğrulaması gövdede DEĞİL, `Basic` başlığında: gizli
      // anahtar böylece istek gövdesi kaydeden aracıların loglarına düşmüyor.
      authorization: `Basic ${Buffer.from(`${config.clientId}:${config.clientSecret}`).toString("base64")}`,
    },
    body: new URLSearchParams(body).toString(),
    cache: "no-store",
  });

  const text = await response.text();

  if (!response.ok) {
    let detail = text;

    try {
      const parsed = JSON.parse(text) as { error?: string; error_description?: string };
      detail = parsed.error_description ?? parsed.error ?? text;
    } catch {
      // Gövde JSON değilse ham metni taşıyoruz.
    }

    throw new OidcError("Keycloak token isteği reddetti.", response.status, detail);
  }

  return JSON.parse(text) as TokenResponse;
}

export function exchangeCode(
  discovery: DiscoveryDocument,
  config: BffConfig,
  params: { code: string; codeVerifier: string },
): Promise<TokenResponse> {
  return postToken(discovery, config, {
    grant_type: "authorization_code",
    code: params.code,
    redirect_uri: redirectUri(config),
    code_verifier: params.codeVerifier,
  });
}

export function refresh(
  discovery: DiscoveryDocument,
  config: BffConfig,
  refreshToken: string,
): Promise<TokenResponse> {
  return postToken(discovery, config, {
    grant_type: "refresh_token",
    refresh_token: refreshToken,
  });
}

let jwksCache: ReturnType<typeof createRemoteJWKSet> | undefined;

/**
 * `id_token`'ı doğruluyor: imza, issuer, audience ve nonce.
 *
 * <p>Token'ı doğrudan token ucundan, istemci kimliğiyle aldığımız için imza
 * doğrulaması OIDC'de zorunlu değil. Yine de yapıyoruz: <b>nonce</b>
 * karşılaştırması bunu gerektiriyor ve nonce olmadan bu oturumun bu giriş
 * denemesine ait olduğunu gösteren bir bağ kalmıyor.</p>
 */
export async function verifyIdToken(
  discovery: DiscoveryDocument,
  config: BffConfig,
  idToken: string,
  expectedNonce: string,
): Promise<void> {
  jwksCache ??= createRemoteJWKSet(new URL(discovery.jwks_uri));

  const { payload } = await jwtVerify(idToken, jwksCache, {
    issuer: config.issuer,
    audience: config.clientId,
  });

  if (payload.nonce !== expectedNonce) {
    throw new OidcError("id_token nonce eşleşmiyor.", 400);
  }
}

/** Testlerin sabit bir anahtar setiyle koşabilmesi için. */
export function useLocalJwks(jwks: Parameters<typeof createLocalJWKSet>[0]): void {
  jwksCache = createLocalJWKSet(jwks) as unknown as ReturnType<typeof createRemoteJWKSet>;
}

/**
 * Erişim token'ının bitiş anı (epoch ms).
 *
 * <p>Öncelik <c>expires_in</c>'de: token ucunun söylediği süre, token'ın
 * içindeki <c>exp</c>'ten daha güvenilir çünkü saat kayması içermiyor.
 * <c>expires_in</c> yoksa token çözülüyor — <b>doğrulanmıyor</b>, çünkü
 * erişim token'ının doğrulaması API'nin işi ve BFF onu yalnızca taşıyor.</p>
 */
export function accessTokenExpiry(tokens: TokenResponse, now = Date.now()): number {
  if (typeof tokens.expires_in === "number" && Number.isFinite(tokens.expires_in)) {
    return now + tokens.expires_in * 1000;
  }

  try {
    const exp = decodeJwt(tokens.access_token).exp;

    if (typeof exp === "number") {
      return exp * 1000;
    }
  } catch {
    // Çözülemeyen token'ı süresi dolmuş sayıyoruz: bir sonraki istekte
    // yenilenir. Sessizce "sonsuz geçerli" saymak, süresi geçmiş bir token'la
    // ısrarla 401 almak demek olurdu.
  }

  return now;
}

export function endSessionUrl(
  discovery: DiscoveryDocument,
  config: BffConfig,
  idToken: string | undefined,
): string {
  // Keycloak realm'inde `post.logout.redirect.uris` içinde
  // `http://localhost:3000/*` yazılı; buraya onunla eşleşen bir adres gitmezse
  // Keycloak "Invalid redirect uri" sayfası gösterir.
  const fallback = `${config.publicUrl}/`;

  if (!discovery.end_session_endpoint) {
    return fallback;
  }

  const url = new URL(discovery.end_session_endpoint);
  url.searchParams.set("post_logout_redirect_uri", fallback);
  url.searchParams.set("client_id", config.clientId);

  if (idToken) {
    // `id_token_hint` olmadan Keycloak kullanıcıya "çıkmak istediğinize emin
    // misiniz" ekranı gösteriyor; akış sessizce yarıda kalıyor.
    url.searchParams.set("id_token_hint", idToken);
  }

  return url.toString();
}
