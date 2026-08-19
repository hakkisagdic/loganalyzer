import { TransportError } from "./errors";
import type { paths } from "./schema";

/**
 * Yol ve yöntem tiplerinin **tek kaynağı**.
 *
 * <p>
 * İki istemci var — tarayıcıdaki (<c>client.ts</c>, BFF vekilinden geçen) ve
 * sunucu bileşenlerinin kullandığı (<c>server.ts</c>, doğrudan
 * <c>Authorization: Bearer</c> ile). İkisi aynı üretilen şemadan besleniyor;
 * tip makinesinin iki kopyası olsaydı biri güncellenirken diğeri sessizce
 * eskirdi.
 * </p>
 */

/**
 * `/v1/logs` istemcilerden **dışarıda**.
 *
 * <p>Şemada var ama UI'ın işi değil — collector'ın ingest ucu. Tip düzeyinde
 * dışlamak, bir ekranın yanlışlıkla oraya yazmasını derleme zamanında
 * engelliyor.</p>
 */
export type ExcludedPath = "/v1/logs";

export type HttpMethod = "get" | "post" | "put" | "delete" | "patch";

export type AvailablePaths = Exclude<keyof paths, ExcludedPath>;

/** Verilen yöntemi destekleyen yollar. */
export type PathsWith<TMethod extends HttpMethod> = {
  [P in AvailablePaths]: paths[P] extends { [K in TMethod]: infer TOperation }
    ? TOperation extends undefined
      ? never
      : P
    : never;
}[AvailablePaths];

export type Operation<P extends AvailablePaths, TMethod extends HttpMethod> = paths[P] extends {
  [K in TMethod]: infer TOperation;
}
  ? TOperation
  : never;

export type JsonBody<TOperation> = TOperation extends {
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
 * değil. Hangi uçların açıkta kaldığı
 * <c>tests/Bizigo.UnitTests/ProducesContractTests.cs</c> içindeki izin
 * listesinde duruyor.</p>
 */
export type JsonResponse<TOperation> = TOperation extends {
  responses: { 200: { content: { "application/json": infer TResult } } };
}
  ? TResult
  : unknown;

export type PathParams<TOperation> = TOperation extends { parameters: { path: infer TPath } }
  ? TPath extends Record<string, unknown>
    ? TPath
    : never
  : never;

export type QueryParams<TOperation> = TOperation extends { parameters: { query?: infer TQuery } }
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

/** Yol şablonunu doldurup sorgu dizesini ekliyor. Ön ek **yok** — çağıran koyuyor. */
export function buildRelativeUrl(
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
  return `${filled}${suffix}`;
}

/** Yanıt gövdesini JSON'a çeviriyor; boş gövde `undefined`. */
export async function readBody(response: Response): Promise<unknown> {
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

/** `fetch`in fırlattığı ağ hatasını tek tipe çeviriyor. */
export function asTransportError(cause: unknown): TransportError {
  return new TransportError("Sunucuya ulaşılamadı.", { cause });
}
