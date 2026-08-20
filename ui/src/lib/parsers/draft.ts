import { toApiError } from "@/lib/api/errors";
import type { PathsWith } from "@/lib/api/paths";

/**
 * Kaydedilmiş bir taslağın gövdesini okuma (T19 ↔ T20).
 *
 * <p>
 * <b>Bu dosya geçici ve kendini yok edecek biçimde yazıldı.</b> Editörün bir
 * taslağı yeniden açabilmesi için <c>GET /v1/parsers/drafts/{id}</c> gerekiyor;
 * o ucu T20 yazıyor (fark görünümü onun kabul kriteri) ve iki ajanın aynı ucu
 * eklemesi çakışma üretirdi. Sözleşme koordinatörde çivilendi:
 * <c>{ id, parser_id, version, state, owner, yaml, verdict, updated_at, … }</c>.
 * </p>
 *
 * <p>
 * Uç henüz bu daldaki <c>schema.d.ts</c>'te olmadığı için tipli istemci
 * kullanılamıyor. Elle tip <b>yazılmadı</b>: aşağıdaki <c>fetch</c> gövdeyi
 * <c>unknown</c> alıyor ve çalışma zamanında <b>tek bir alan</b> için
 * doğrulanıyor. Alternatif — şemadaki tipin kopyasını yazmak — T14'ün var olma
 * sebebine aykırıydı; kopya, uç değiştiği gün sessizce yalan söylerdi.
 * </p>
 */

/**
 * <b>Bekçi.</b> T20'nin <c>GET</c> ucu <c>schema.d.ts</c>'e indiği an
 * <c>DraftGetLanded</c> <c>true</c> olur, aşağıdaki atama derlemeyi kırar ve
 * bu dosyanın geçici çözümü <b>fark edilmek zorunda</b> kalır.
 *
 * <p>
 * Yorum bırakmak yetmezdi: "sonra düzeltiriz" notları düzeltilmiyor.
 * Derlemeyi kıran bir bekçi, geçici çözümün kalıcılaşmasını imkânsız kılıyor —
 * bu depoda bekçilerin kırmızı yanabilmesi zaten kural.
 * </p>
 *
 * <p>
 * Yolun <b>varlığına</b> değil <c>GET</c> yöntemine bakılıyor:
 * <c>/v1/parsers/drafts/{id}</c> anahtarı <c>PUT</c> yüzünden zaten var ve
 * anahtarı sınayan bir bekçi ilk günden yeşil yanardı — yani hiçbir şey
 * ölçmezdi.
 * </p>
 */
type DraftGetLanded = "/v1/parsers/drafts/{id}" extends PathsWith<"get"> ? true : false;

const DRAFT_ENDPOINT_STILL_MISSING: DraftGetLanded = false;

/** Editörün taslaktan ihtiyaç duyduğu tek şey — gövdenin kendisi. */
export interface DraftDocument {
  readonly id: string;
  readonly yaml: string;
  readonly parserId: string;
  readonly version: string;
  readonly state: string;
}

/**
 * Gövdeyi çalışma zamanında doğruluyor.
 *
 * <p>Eksik bir alan <b>hata</b>: sessizce boş bir editör açmak, kullanıcının
 * taslağını kaybettiğini sandırırdı — oysa taslak yerinde, okunamayan biziz.</p>
 */
function readDraft(body: unknown): DraftDocument {
  if (!body || typeof body !== "object") {
    throw new TypeError("Taslak yanıtı beklenen biçimde değil.");
  }

  const record = body as Record<string, unknown>;

  if (typeof record["yaml"] !== "string") {
    throw new TypeError("Taslak yanıtında `yaml` alanı yok.");
  }

  return {
    id: typeof record["id"] === "string" ? record["id"] : "",
    yaml: record["yaml"],
    parserId: typeof record["parser_id"] === "string" ? record["parser_id"] : "",
    version: typeof record["version"] === "string" ? record["version"] : "",
    state: typeof record["state"] === "string" ? record["state"] : "",
  };
}

/**
 * Taslağı BFF vekilinden çekiyor.
 *
 * <p>Tipli istemci (<c>api.get</c>) kullanılamıyor — yol şemada yok. Vekil
 * yolu ise tanım gereği tipsiz; token yine tarayıcıya hiç ulaşmıyor.</p>
 */
export async function fetchDraft(id: string, signal?: AbortSignal): Promise<DraftDocument> {
  const response = await fetch(`/api/bff/v1/parsers/drafts/${encodeURIComponent(id)}`, {
    headers: { accept: "application/json" },
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });

  const body: unknown = response.status === 204 ? undefined : await response.json().catch(() => undefined);

  if (!response.ok) {
    throw toApiError(response.status, body);
  }

  return readDraft(body);
}

/**
 * Bekçi dışa aktarılıyor: kullanılmayan bir yerel değişkeni `noUnusedLocals`
 * eler ve bekçi sessizce kaybolurdu.
 */
export const DRAFT_ENDPOINT_PENDING = DRAFT_ENDPOINT_STILL_MISSING;
