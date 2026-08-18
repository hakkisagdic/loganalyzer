import { exportJWK, generateKeyPair, SignJWT, type JWK, type CryptoKey } from "jose";
import { NextRequest } from "next/server";

import { resetDiscoveryCache, useLocalJwks } from "@/lib/auth/oidc";
import { peekLoginAttempt, resetSessionStore } from "@/lib/auth/store";

/**
 * Sahte Keycloak + sahte `Bizigo.Api`.
 *
 * <p>
 * Gerçek konteyner kaldırmıyoruz: BFF'in sınadığımız davranışları — token'ın
 * tarayıcıya ulaşmaması, şeffaf yenileme, çıkışın iki oturumu da kapatması —
 * hiçbiri Keycloak'ın gerçek olmasına bağlı değil. Konteyner koşan bir test
 * bunları daha iyi göstermez, yalnızca yavaş ve kararsız gösterir (F1'in
 * duvar saati dersi).
 * </p>
 */

export const ISSUER = "http://keycloak.test/realms/bizigo";
export const CLIENT_ID = "bizigo-ui";
export const CLIENT_SECRET = "test-secret";
export const PUBLIC_URL = "http://localhost:3000";
export const API_URL = "http://api.test";

/**
 * Erişim token'ı — <b>testin aradığı iğne</b>.
 *
 * <p>Ayırt edici bir dizgi: bütün yanıtların içinde bunu arıyoruz. Rastgele
 * bir JWT olsaydı, aramanın gerçekten bir şey bulup bulmadığından emin
 * olamazdık.</p>
 */
export const ACCESS_TOKEN = "ACCESS-TOKEN-BU-DIZGI-TARAYICIYA-ASLA-ULASMAMALI";
export const REFRESH_TOKEN = "REFRESH-TOKEN-BU-DIZGI-DE-TARAYICIYA-ULASMAMALI";
export const RENEWED_ACCESS_TOKEN = "YENILENMIS-ACCESS-TOKEN-DA-ULASMAMALI";

export interface FakeIdp {
  /** Keycloak'a giden istekler. */
  readonly tokenRequests: URLSearchParams[];
  /** `Bizigo.Api`'ye giden istekler. */
  readonly apiRequests: { url: string; method: string; authorization: string | null; cookie: string | null }[];
  /** Bir sonraki token yanıtının erişim token'ı ömrü (saniye). */
  accessTokenLifetime: number;
  /** `true` ise yenileme isteği reddediliyor. */
  refreshRejected: boolean;
  /** API'nin bir sonraki yanıtı. */
  apiStatus: number;
  apiBody: unknown;
  /** Süresi dolmuş token'la gelen isteğe API 401 dönsün mü. */
  apiRejectsStaleToken: boolean;
}

let signingKey: CryptoKey;
let publicJwk: JWK;

export async function setupEnvironment(): Promise<void> {
  process.env.KEYCLOAK_ISSUER = ISSUER;
  process.env.KEYCLOAK_CLIENT_ID = CLIENT_ID;
  process.env.KEYCLOAK_CLIENT_SECRET = CLIENT_SECRET;
  process.env.BFF_PUBLIC_URL = PUBLIC_URL;
  process.env.BIZIGO_API_URL = API_URL;
  process.env.BFF_COOKIE_NAME = "bizigo.sid";
  process.env.BFF_SESSION_TTL_SECONDS = "3600";

  const pair = await generateKeyPair("RS256", { extractable: true });
  signingKey = pair.privateKey;
  publicJwk = await exportJWK(pair.publicKey);
  publicJwk.kid = "test-key";
  publicJwk.alg = "RS256";
}

const discoveryDocument = () => ({
  issuer: ISSUER,
  authorization_endpoint: `${ISSUER}/protocol/openid-connect/auth`,
  token_endpoint: `${ISSUER}/protocol/openid-connect/token`,
  jwks_uri: `${ISSUER}/protocol/openid-connect/certs`,
  end_session_endpoint: `${ISSUER}/protocol/openid-connect/logout`,
});

async function idToken(nonce: string): Promise<string> {
  return new SignJWT({ nonce, preferred_username: "analyst.core" })
    .setProtectedHeader({ alg: "RS256", kid: "test-key" })
    .setIssuer(ISSUER)
    .setAudience(CLIENT_ID)
    .setSubject("00000000-0000-0000-0000-000000000001")
    .setIssuedAt()
    .setExpirationTime("1h")
    .sign(signingKey);
}

/** Sahte sunucuları `fetch` üzerinden bağlıyor ve durumu döndürüyor. */
export function installFakes(): FakeIdp {
  resetSessionStore();
  resetDiscoveryCache();
  useLocalJwks({ keys: [publicJwk] });

  const state: FakeIdp = {
    tokenRequests: [],
    apiRequests: [],
    accessTokenLifetime: 300,
    refreshRejected: false,
    apiStatus: 200,
    apiBody: {
      subject: "00000000-0000-0000-0000-000000000001",
      username: "analyst.core",
      roles: ["analyst"],
      idp_groups: ["/network/core"],
      owner_groups: ["network/core"],
      unrestricted: false,
      sees_nothing: false,
    },
    apiRejectsStaleToken: false,
  };

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
    const method = init?.method ?? (input instanceof Request ? input.method : "GET");

    if (url.startsWith(`${ISSUER}/.well-known/openid-configuration`)) {
      return Response.json(discoveryDocument());
    }

    if (url.startsWith(discoveryDocument().token_endpoint)) {
      const body = new URLSearchParams(String(init?.body ?? ""));
      state.tokenRequests.push(body);

      if (body.get("grant_type") === "refresh_token") {
        if (state.refreshRejected) {
          return Response.json({ error: "invalid_grant" }, { status: 400 });
        }

        return Response.json({
          access_token: RENEWED_ACCESS_TOKEN,
          refresh_token: REFRESH_TOKEN,
          expires_in: 300,
          token_type: "Bearer",
        });
      }

      const attemptState = pendingState;

      return Response.json({
        access_token: ACCESS_TOKEN,
        refresh_token: REFRESH_TOKEN,
        id_token: await idToken(attemptState?.nonce ?? "yok"),
        expires_in: state.accessTokenLifetime,
        token_type: "Bearer",
      });
    }

    if (url.startsWith(API_URL)) {
      const headers = new Headers(init?.headers);
      const authorization = headers.get("authorization");

      state.apiRequests.push({
        url,
        method,
        authorization,
        cookie: headers.get("cookie"),
      });

      if (state.apiRejectsStaleToken && authorization === `Bearer ${ACCESS_TOKEN}`) {
        return Response.json({ error: "Token reddedildi." }, { status: 401 });
      }

      return Response.json(state.apiBody as object, { status: state.apiStatus });
    }

    throw new Error(`Sahte ortamda beklenmeyen adres: ${url}`);
  }) as typeof fetch;

  return state;
}

/** Sahte token ucunun doğru nonce'u üretebilmesi için okunan bekleyen giriş. */
let pendingState: { nonce: string } | undefined;

export function rememberPending(stateValue: string): void {
  pendingState = peekLoginAttempt(stateValue);
}

export function nextRequest(
  path: string,
  init: { method?: string; cookie?: string; body?: string; headers?: Record<string, string> } = {},
): NextRequest {
  const headers = new Headers(init.headers);

  if (init.cookie) {
    headers.set("cookie", init.cookie);
  }

  return new NextRequest(new Request(`${PUBLIC_URL}${path}`, {
    method: init.method ?? "GET",
    headers,
    body: init.body,
  }));
}

/**
 * Bir yanıtın <b>tamamını</b> düz metne çeviriyor: durum satırı, bütün
 * başlıklar (çerezler dâhil) ve gövde.
 *
 * <p>Yalnızca gövdeye bakmak yetmez — token bir `Set-Cookie` içinde ya da bir
 * tanılama başlığında da sızabilir. Sızıntı testi tam da bu yüzden yanıtın
 * her baytını tarıyor.</p>
 */
export async function serializeResponse(response: Response): Promise<string> {
  const parts = [String(response.status)];

  response.headers.forEach((value, name) => {
    parts.push(`${name}: ${value}`);
  });

  parts.push(await response.clone().text());

  return parts.join("\n");
}
