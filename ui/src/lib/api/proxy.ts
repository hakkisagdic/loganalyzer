import { NextResponse, type NextRequest } from "next/server";

import { readBffConfig } from "@/lib/auth/config";
import { discover, OidcError, refresh, accessTokenExpiry } from "@/lib/auth/oidc";
import { readSessionId, resolveSession } from "@/lib/auth/session";
import { sessionStore } from "@/lib/auth/store";

/**
 * Tarayıcı → Next → `Bizigo.Api` vekili.
 *
 * <p>
 * Tarayıcı `Bizigo.Api`'ye <b>doğrudan konuşmuyor</b>. İstek buraya oturum
 * çereziyle geliyor, buradan API'ye <c>Authorization: Bearer</c> ile gidiyor.
 * Token bu fonksiyonun içinde doğuyor ve içinde ölüyor.
 * </p>
 */

/** API'ye taşınan istek başlıkları. Liste **beyaz**: sayılmayan hiçbir şey geçmiyor. */
const FORWARDED_REQUEST_HEADERS = ["content-type", "accept", "accept-language"];

/**
 * Tarayıcıya taşınan yanıt başlıkları — bu da beyaz liste.
 *
 * <p>API'nin ürettiği her başlığı geçirmek, ileride eklenecek bir tanılama
 * başlığının (ya da bir `Set-Cookie`'nin) sessizce tarayıcıya ulaşması demek.
 * Beyaz liste, "yeni başlık eklendi" kararını bilinçli hâle getiriyor.</p>
 */
const FORWARDED_RESPONSE_HEADERS = ["content-type", "content-language"];

export interface ApiErrorBody {
  readonly error: string;
  readonly hint?: string;
}

export function apiError(status: number, error: string, hint?: string): NextResponse {
  // F1'de yerleşen gövde biçimi: `{ error, hint }`. UI tarafındaki tek tipli
  // hata ele alışı (T14) bunu bekliyor.
  return NextResponse.json<ApiErrorBody>({ error, hint }, { status });
}

function forwardedRequestHeaders(request: NextRequest, accessToken: string): Headers {
  const headers = new Headers();

  for (const name of FORWARDED_REQUEST_HEADERS) {
    const value = request.headers.get(name);

    if (value) {
      headers.set(name, value);
    }
  }

  // Oturum çerezi API'ye GİTMİYOR. API'de artık cookie işleyicisi yok (K31);
  // göndermek, orada bir gün yeniden açılacak bir kapıya davetiye olurdu.
  headers.set("authorization", `Bearer ${accessToken}`);

  return headers;
}

function forwardedResponse(upstream: Response, body: ArrayBuffer): NextResponse {
  const headers = new Headers();

  for (const name of FORWARDED_RESPONSE_HEADERS) {
    const value = upstream.headers.get(name);

    if (value) {
      headers.set(name, value);
    }
  }

  return new NextResponse(body, { status: upstream.status, headers });
}

async function callApi(
  request: NextRequest,
  targetUrl: string,
  accessToken: string,
  body: ArrayBuffer | undefined,
): Promise<Response> {
  return fetch(targetUrl, {
    method: request.method,
    headers: forwardedRequestHeaders(request, accessToken),
    body,
    redirect: "manual",
    cache: "no-store",
  });
}

/**
 * Süresi dolmuş erişim token'ını **istek sırasında** yeniliyor.
 *
 * <p><c>resolveSession</c> zaten önden yeniliyor; bu ikinci kapı, token'ın
 * sunucu tarafında hâlâ geçerli görünüp API tarafında reddedildiği durumu
 * kapatıyor — realm yeniden import edildiğinde imzalama anahtarı değiştiği için
 * tam olarak bu oluyor.</p>
 */
async function refreshOnce(sessionId: string, refreshToken: string): Promise<string | undefined> {
  const config = readBffConfig();

  try {
    const discovery = await discover(config);
    const tokens = await refresh(discovery, config, refreshToken);
    const store = sessionStore();
    const current = await store.get(sessionId);

    if (!current) {
      return undefined;
    }

    await store.set(sessionId, {
      accessToken: tokens.access_token,
      refreshToken: tokens.refresh_token ?? current.refreshToken,
      idToken: tokens.id_token ?? current.idToken,
      accessTokenExpiresAt: accessTokenExpiry(tokens),
      expiresAt: current.expiresAt,
    });

    return tokens.access_token;
  } catch (error) {
    if (!(error instanceof OidcError)) {
      throw error;
    }

    await sessionStore().delete(sessionId);
    return undefined;
  }
}

export async function proxyToApi(request: NextRequest, apiPath: string): Promise<NextResponse> {
  const config = readBffConfig();
  const sessionId = readSessionId(request, config);
  const session = await resolveSession(sessionId, config);

  if (!session) {
    // Yönlendirme DEĞİL, 401. Bu ucu çağıran taraf `fetch`; 302 dönmek
    // Keycloak'ın giriş sayfasının HTML'ini bir JSON ayrıştırıcısına
    // yedirmek olurdu.
    return apiError(401, "Oturum yok ya da süresi doldu.", "Yeniden giriş yapın: /api/auth/login");
  }

  const query = request.nextUrl.search;
  const targetUrl = `${config.apiBaseUrl}/${apiPath}${query}`;

  // Gövde iki kez okunamıyor; 401 sonrası tekrar denemek için tamponluyoruz.
  const body =
    request.method === "GET" || request.method === "HEAD"
      ? undefined
      : await request.arrayBuffer();

  let upstream = await callApi(request, targetUrl, session.record.accessToken, body);

  if (upstream.status === 401 && session.record.refreshToken) {
    const renewed = await refreshOnce(session.id, session.record.refreshToken);

    if (!renewed) {
      return apiError(401, "Oturum yenilenemedi.", "Yeniden giriş yapın: /api/auth/login");
    }

    upstream = await callApi(request, targetUrl, renewed, body);
  }

  return forwardedResponse(upstream, await upstream.arrayBuffer());
}
