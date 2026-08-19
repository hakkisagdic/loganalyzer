import { cookies } from "next/headers";

import { readBffConfig } from "@/lib/auth/config";
import { resolveSession } from "@/lib/auth/session";

import { toApiError } from "./errors";
import {
  asTransportError,
  buildRelativeUrl,
  readBody,
  type HttpMethod,
  type JsonResponse,
  type Operation,
  type PathsWith,
  type RequestOptions,
} from "./paths";

/**
 * Sunucu tarafı `Bizigo.Api` istemcisi.
 *
 * <p>
 * Tarayıcıdaki istemciden (<c>client.ts</c>) tek farkı <b>nereye</b> bağlandığı:
 * burası vekile uğramıyor, API'ye doğrudan <c>Authorization: Bearer</c> ile
 * gidiyor. Token bu modülün içinde doğuyor ve içinde ölüyor — sayfaya hiçbir
 * biçimde geçmiyor, dolayısıyla React'in istemciye serileştirdiği hiçbir prop
 * onu taşıyamıyor.
 * </p>
 *
 * <p>
 * Ekranların sunucuda veri çekmesi bilinçli: arama sonuçları HTML olarak
 * iniyor, tarayıcıda <c>fetch</c> yok, ve "token tarayıcıya ulaşmıyor"
 * özelliği ekstra bir önlem gerektirmeden korunuyor.
 * </p>
 *
 * <p>
 * İki giriş var. <c>serverApi</c> oturumu <c>next/headers</c>'tan okuyor ve
 * sunucu bileşenleri için; <c>apiForSession</c> ise oturum kimliğini
 * <b>açıkça</b> alıyor ve rota işleyicileri için — bir rota işleyicisinin
 * elinde zaten istek var, örtük bağlam kullanmak onu test edilemez kılardı.
 * </p>
 */

/** Oturum yoksa fırlatılan hata — çağıran bunu girişe ya da 401'e bağlıyor. */
export class NoSessionError extends Error {
  override name = "NoSessionError";
}

async function tokenFor(sessionId: string | undefined): Promise<string> {
  const session = await resolveSession(sessionId, readBffConfig());

  if (!session) {
    throw new NoSessionError("Oturum yok ya da süresi doldu.");
  }

  return session.record.accessToken;
}

async function call<TMethod extends HttpMethod, P extends PathsWith<TMethod>>(
  token: string,
  method: TMethod,
  path: P,
  options: RequestOptions<Operation<P, TMethod>>,
): Promise<JsonResponse<Operation<P, TMethod>>> {
  const config = readBffConfig();

  const url = config.apiBaseUrl + buildRelativeUrl(
    path as string,
    options.path as Record<string, unknown> | undefined,
    options.query as Record<string, unknown> | undefined,
  );

  const headers: Record<string, string> = {
    accept: "application/json",
    authorization: `Bearer ${token}`,
  };

  if (options.body !== undefined) {
    headers["content-type"] = "application/json";
  }

  let response: Response;

  try {
    response = await fetch(url, {
      method: method.toUpperCase(),
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: options.signal,
      cache: "no-store",
    });
  } catch (cause) {
    throw asTransportError(cause);
  }

  const body = await readBody(response);

  if (!response.ok) {
    // 401 burada `SessionExpiredError` olarak çıkıyor. Vekildeki şeffaf
    // yenileme bu yolda YOK: `resolveSession` zaten önden yeniliyor, buraya
    // düşmek güven zincirinin kırıldığı anlamına geliyor (realm yeniden import
    // edilmiş ve imzalama anahtarı değişmiş gibi).
    throw toApiError(response.status, body);
  }

  return body as JsonResponse<Operation<P, TMethod>>;
}

/** Oturum kimliği açıkça verilen istemci — rota işleyicileri için. */
export function apiForSession(sessionId: string | undefined) {
  return {
    get: async <P extends PathsWith<"get">>(
      path: P,
      options: RequestOptions<Operation<P, "get">> = {},
    ) => call(await tokenFor(sessionId), "get", path, options),
    post: async <P extends PathsWith<"post">>(
      path: P,
      options: RequestOptions<Operation<P, "post">> = {},
    ) => call(await tokenFor(sessionId), "post", path, options),
  };
}

async function sessionIdFromHeaders(): Promise<string | undefined> {
  const jar = await cookies();
  return jar.get(readBffConfig().cookieName)?.value;
}

/** Sunucu bileşenlerinin istemcisi; oturumu istek bağlamından okuyor. */
export const serverApi = {
  get: async <P extends PathsWith<"get">>(
    path: P,
    options: RequestOptions<Operation<P, "get">> = {},
  ) => call(await tokenFor(await sessionIdFromHeaders()), "get", path, options),
  post: async <P extends PathsWith<"post">>(
    path: P,
    options: RequestOptions<Operation<P, "post">> = {},
  ) => call(await tokenFor(await sessionIdFromHeaders()), "post", path, options),
};
