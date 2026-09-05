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

let discoveryCache: { key: string; document: DiscoveryDocument } | undefined;

/**
 * Bir ucu **tarayıcının görebildiği** kökene taşır.
 *
 * <p>
 * Keşif belgesi container ağından iniyor (<c>metadataUrl</c>) ve içindeki bazı
 * uçlar o ağın adresini taşıyabiliyor — <c>keycloak:8080</c>, ki tarayıcı onu
 * <b>çözemez</b>. Ama belgedeki her uç böyle taşınamaz: yalnızca kullanıcının
 * tarayıcısıyla gittiği <b>ön kanal</b> uçları taşınmalı, sunucudan sunucuya
 * konuşulan arka kanal uçları (token, JWKS) container adresinde <i>kalmalı</i>.
 * </p>
 *
 * <p>
 * Keycloak bu ayrımı <c>KC_HOSTNAME_BACKCHANNEL_DYNAMIC</c> ile zaten yapıyor
 * ve doğru yapıyorsa buradaki dönüşüm <b>kimliktir</b> — hiçbir şeyi
 * değiştirmez. Yine de duruyor, çünkü iddia bizim tarafımızda yazılı olmalı:
 * "tarayıcının gideceği uç, tarayıcının görebildiği kökende" cümlesi bir dış
 * bileşenin yapılandırmasına bırakılırsa, o yapılandırma değiştiği gün kırılan
 * şey <b>giriş akışı</b> olur ve belirtisi Keycloak'ın çözülemeyen bir adrese
 * yönlendirmesidir — sebebini söylemeyen bir hata.
 * </p>
 */
function onPublicOrigin(endpoint: string, issuer: string): string {
  const target = new URL(endpoint);
  const publicOrigin = new URL(issuer).origin;

  if (target.origin === publicOrigin) {
    return endpoint;
  }

  return new URL(`${target.pathname}${target.search}`, publicOrigin).toString();
}

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
  // Önbellek anahtarı İKİ değeri birden taşıyor: belge nereden indi ve hangi
  // issuer'a göre doğrulandı. Yalnız issuer'la anahtarlansaydı, metadata adresi
  // değişen bir kurulum eski belgeyi kullanmaya devam ederdi.
  const key = `${config.metadataUrl}\n${config.issuer}`;

  if (discoveryCache?.key === key) {
    return discoveryCache.document;
  }

  const url = config.metadataUrl;
  const response = await fetch(url, { cache: "no-store" });

  if (!response.ok) {
    throw new OidcError(
      `Keycloak keşif belgesi okunamadı (${response.status}).`,
      502,
      `Adres: ${url}`,
    );
  }

  const raw = (await response.json()) as DiscoveryDocument;

  // Bu karşılaştırma metadata adresi ayrıldıktan sonra DAHA değerli: artık
  // belgeyi başka bir adresten indiriyoruz ve "indirdiğim yer, güvendiğim
  // issuer'ı veriyor mu" sorusunu soran tek yer burası.
  //
  // F1'de ölçülen tuzak: `KC_HOSTNAME` ayarlı değilse Keycloak issuer'ı
  // isteğin host'undan türetiyor ve API'nin beklediğiyle uyuşmuyor. Sonuç
  // her istekte 401 ve hiçbir yerde sebebini söyleyen bir mesaj yok.
  if (raw.issuer !== config.issuer) {
    throw new OidcError(
      "Keycloak issuer beklenenden farklı.",
      502,
      `Beklenen ${config.issuer}, gelen ${raw.issuer}. Belge ${url} adresinden indi. ` +
        "deploy/keycloak/README.md — KC_HOSTNAME.",
    );
  }

  const document: DiscoveryDocument = {
    ...raw,

    // Ön kanal: tarayıcı gidiyor.
    authorization_endpoint: onPublicOrigin(raw.authorization_endpoint, config.issuer),
    end_session_endpoint: raw.end_session_endpoint
      ? onPublicOrigin(raw.end_session_endpoint, config.issuer)
      : undefined,

    // Arka kanal (`token_endpoint`, `jwks_uri`) BİLEREK dokunulmadan geçiyor:
    // onlara Next sunucusu gidiyor ve container ağında doğru adres zaten
    // belgeden geleni.
  };

  discoveryCache = { key, document };
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
