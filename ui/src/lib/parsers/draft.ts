import { api } from "@/lib/api/client";
import type { ParserDraftDetail } from "./types";

/**
 * Kaydedilmiş bir taslağın gövdesini okuma (T19 ↔ T20).
 *
 * <p>
 * Editörün bir taslağı yeniden açabilmesi <c>GET /v1/parsers/drafts/{id}</c>'ye
 * bağlı; o ucu T20 yazdı (fark görünümü onun kabul kriteri) ve iki ajanın aynı
 * ucu eklemesi çakışma üretirdi.
 * </p>
 *
 * <p>
 * <b>Burada bir geçici çözüm vardı ve kendini yok etti.</b> Uç bu dalın üretilen
 * şemasında yokken gövde <c>unknown</c> alınıp çalışma zamanında
 * doğrulanıyordu; elle tip yazmamak için. Geçici çözümün kalıcılaşmaması bir
 * yoruma bırakılmamıştı — <c>"/v1/parsers/drafts/{id}" extends PathsWith&lt;"get"&gt;</c>
 * koşulu bir <c>false</c> atamasına bağlıydı ve uç indiği an derlemeyi kırdı.
 * Kırdığı için bu dosya artık üretilen tipi tüketiyor: <c>fetch</c> yok, el
 * yazması alan adı yok, tipli istemci var.
 * </p>
 */

/** Editörün taslaktan ihtiyaç duyduğu tek şey — gövdenin kendisi. */
export interface DraftDocument {
  readonly id: string;
  readonly yaml: string;
  readonly parserId: string;
  readonly version: string;
  readonly state: string;
}

/**
 * Taslağı tipli istemciyle çekiyor.
 *
 * <p>
 * Yanıt <c>previous_version</c> ve <c>previous_yaml</c> da taşıyor; onlar
 * T20'nin fark görünümünün alanları ve editör onları <b>yok sayıyor</b>. Aynı
 * yanıtta olmaları doğru: ayrı istekle çekilseydi, inceleme sırasında araya bir
 * yayın girdiğinde inceleyen artık var olmayan bir sürümle karşılaştırma
 * yapardı.
 * </p>
 */
export async function fetchDraft(id: string, signal?: AbortSignal): Promise<DraftDocument> {
  const draft = (await api.get("/v1/parsers/drafts/{id}", {
    path: { id },
    signal,
  })) as ParserDraftDetail;

  return {
    id: draft.id,
    yaml: draft.yaml,
    parserId: draft.parser_id,
    version: draft.version,
    state: draft.state,
  };
}
