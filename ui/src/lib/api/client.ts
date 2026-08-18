import { toApiError, TransportError } from "./errors";
import type { paths } from "./schema";

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
 * <c>Authorization: Bearer</c>'a çevriliyor.
 * </p>
 */

/**
 * `/v1/logs` istemciden **dışarıda**.
 *
 * <p>Şemada var ama UI'ın işi değil — collector'ın ingest ucu. Tip düzeyinde
 * dışlamak, bir ekranın yanlışlıkla oraya yazmasını derleme zamanında
 * engelliyor.</p>
 */
export type ExcludedPath = "/v1/logs";

type HttpMethod = "get" | "post" | "put" | "delete" | "patch";

type AvailablePaths = Exclude<keyof paths, ExcludedPath>;

/** Verilen yöntemi destekleyen yollar. */
export type PathsWith<TMethod extends HttpMethod> = {
  [P in AvailablePaths]: paths[P] extends { [K in TMethod]: infer TOperation }
    ? TOperation extends undefined
      ? never
      : P
    : never;
}[AvailablePaths];

type Operation<P extends AvailablePaths, TMethod extends HttpMethod> = paths[P] extends {
  [K in TMethod]: infer TOperation;
}
  ? TOperation
  : never;

type JsonBody<TOperation> = TOperation extends {
  requestBody?: { content: { "application/json": infer TBody } };
}
  ? TBody
  : never;

/**
 * 200 yanıtının gövde tipi.
 *
 * <p>Ucun uç tanımında <c>Produces&lt;T&gt;</c> yoksa şemada gövde tipi de yok
 * ve burası <c>unknown</c> kalıyor. Bu bilinçli: uydurulmuş bir tip, olmayan
 * bir güvence verirdi. Bir ekran belirli bir ucun gövdesine ihtiyaç duyduğunda
 * çözüm API tarafına <c>Produces&lt;T&gt;</c> eklemek — burada elle tip yazmak
 * değil.</p>
 */
type JsonResponse<TOperation> = TOperation extends {
  responses: { 200: { content: { "application/json": infer TResult } } };
}
  ? TResult
  : unknown;

type PathParams<TOperation> = TOperation extends { parameters: { path: infer TPath } }
  ? TPath extends Record<string, unknown>
    ? TPath
    : never
  : never;

type QueryParams<TOperation> = TOperation extends { parameters: { query?: infer TQuery } }
  ? TQuery extends Record<string, unknown>
    ? TQuery
    : never
  : never;

export interface RequestOptions<TOperation> {
  readonly path?: PathParams<TOperation>;
  readonly query?: QueryParams<TOperation>;
  readonly body?: JsonBody<TOperation>;
  readonly signal?: AbortSignal;
}

/** Vekilin kökü. Mutlak API adresi burada bilinçli olarak YOK. */
const PROXY_PREFIX = "/api/bff";

function buildUrl(
  template: string,
  pathParams: Record<string, unknown> | undefined,
  query: Record<string, unknown> | undefined,
): string {
  const filled = template.replace(/\{([^}]+)\}/g, (_, name: string) => {
    const value = pathParams?.[name];

    if (value === undefined || value === null) {
      throw new TypeError(`Yol parametresi eksik: ${name}`);
    }

    return encodeURIComponent(String(value));
  });

  const search = new URLSearchParams();

  for (const [key, value] of Object.entries(query ?? {})) {
    if (value === undefined || value === null) {
      continue;
    }

    if (Array.isArray(value)) {
      // ASP.NET aynı adı tekrarlayan parametreleri diziye topluyor.
      for (const item of value) {
        search.append(key, String(item));
      }
      continue;
    }

    search.set(key, String(value));
  }

  const suffix = search.size > 0 ? `?${search.toString()}` : "";
  return `${PROXY_PREFIX}${filled}${suffix}`;
}

async function readBody(response: Response): Promise<unknown> {
  if (response.status === 204 || response.headers.get("content-length") === "0") {
    return undefined;
  }

  const text = await response.text();

  if (text.length === 0) {
    return undefined;
  }

  try {
    return JSON.parse(text);
  } catch {
    // JSON bekleyip HTML alıyorsak arada bir vekil ya da hata sayfası var.
    return { error: "Sunucudan beklenmeyen bir yanıt geldi.", hint: text.slice(0, 200) };
  }
}

export async function request<TMethod extends HttpMethod, P extends PathsWith<TMethod>>(
  method: TMethod,
  path: P,
  options: RequestOptions<Operation<P, TMethod>> = {},
): Promise<JsonResponse<Operation<P, TMethod>>> {
  const url = buildUrl(
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
    throw new TransportError("Sunucuya ulaşılamadı.", { cause });
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
