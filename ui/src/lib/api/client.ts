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
 * `Bizigo.Api` istemcisi — **ince** ve **üretilen tiplerden** beslenen.
 *
 * <p>
 * Yol ve yöntem adları elle yazılmıyor: <c>schema.d.ts</c> OpenAPI belgesinden
 * üretiliyor ve bu dosya yalnızca onun üstüne biniyor. API'ye yeni bir uç
 * eklenip tipler yeniden üretilmezse CI düşüyor
 * (`ui/scripts/generate-api-types.sh --check`).
 * </p>
 *
 * <p>
 * Tarayıcı <c>Bizigo.Api</c>'ye doğrudan konuşmuyor. Bütün istekler
 * <c>/api/bff/…</c> vekilinden geçiyor; oradaki oturum çerezi sunucuda
 * <c>Authorization: Bearer</c>'a çevriliyor. Sunucu bileşenlerinin karşılığı
 * <c>server.ts</c> — o vekile hiç uğramıyor.
 * </p>
 */

export type { ExcludedPath, PathsWith, RequestOptions } from "./paths";

/** Vekilin kökü. Mutlak API adresi burada bilinçli olarak YOK. */
const PROXY_PREFIX = "/api/bff";

export async function request<TMethod extends HttpMethod, P extends PathsWith<TMethod>>(
  method: TMethod,
  path: P,
  options: RequestOptions<Operation<P, TMethod>> = {},
): Promise<JsonResponse<Operation<P, TMethod>>> {
  const url = PROXY_PREFIX + buildRelativeUrl(
    path as string,
    options.path as Record<string, unknown> | undefined,
    options.query as Record<string, unknown> | undefined,
  );

  let response: Response;

  try {
    response = await fetch(url, {
      method: method.toUpperCase(),
      headers: options.body === undefined ? { accept: "application/json" } : {
        accept: "application/json",
        "content-type": "application/json",
      },
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      // Oturum çerezi aynı kaynağa gidiyor; `omit` olsaydı vekil kimliği hiç
      // görmezdi.
      credentials: "same-origin",
      signal: options.signal,
      cache: "no-store",
    });
  } catch (cause) {
    throw asTransportError(cause);
  }

  const body = await readBody(response);

  if (!response.ok) {
    throw toApiError(response.status, body);
  }

  return body as JsonResponse<Operation<P, TMethod>>;
}

export const api = {
  get: <P extends PathsWith<"get">>(path: P, options?: RequestOptions<Operation<P, "get">>) =>
    request("get", path, options),
  post: <P extends PathsWith<"post">>(path: P, options?: RequestOptions<Operation<P, "post">>) =>
    request("post", path, options),
  put: <P extends PathsWith<"put">>(path: P, options?: RequestOptions<Operation<P, "put">>) =>
    request("put", path, options),
  patch: <P extends PathsWith<"patch">>(path: P, options?: RequestOptions<Operation<P, "patch">>) =>
    request("patch", path, options),
  delete: <P extends PathsWith<"delete">>(path: P, options?: RequestOptions<Operation<P, "delete">>) =>
    request("delete", path, options),
};

/** `/auth/me` gövdesi — BFF'in ve ekranların kimlik/kapsam kaynağı. */
export type AuthMe = JsonResponse<Operation<"/auth/me", "get">>;

/** `POST /v1/events/search` gövdeleri — arama ekranının sözleşmesi (T15). */
export type EventSearchBody = NonNullable<
  RequestOptions<Operation<"/v1/events/search", "post">>["body"]
>;
export type EventSearchResult = JsonResponse<Operation<"/v1/events/search", "post">>;
export type EventSummary = EventSearchResult["events"][number];

/** `GET /v1/events/{id}` — olay detayı (T16). */
export type EventDetail = JsonResponse<Operation<"/v1/events/{id}", "get">>;
export type EventFieldView = EventDetail["ocsf"][number];

/** `GET /v1/events/{id}/raw` — ham baytlar (T16). */
export type EventRaw = JsonResponse<Operation<"/v1/events/{id}/raw", "get">>;

/** `GET /v1/sources` — arama ekranının kaynak filtresi (T15). */
export type SourceList = JsonResponse<Operation<"/v1/sources", "get">>;
export type SourceItem = SourceList["sources"][number];
