import { ErrorState } from "@/components/ui/States";
import type { SourceItem, SourceList } from "@/lib/api/client";
import { ApiError } from "@/lib/api/errors";
import { NoSessionError, serverApi } from "@/lib/api/server";

import { ParserWorkbench } from "./ParserWorkbench";

export const dynamic = "force-dynamic";

/**
 * Parser editörü sayfası (T19).
 *
 * <p>
 * Kimlik ve <c>author</c> rol kapısı <b>layout'ta</b>; burası yalnızca
 * editörün ihtiyaç duyduğu tek sunucu verisini çekiyor: kaynak envanteri. O da
 * ham arşivden örnek satır çekerken kaynak seçebilmek için — kapsamı sunucu
 * hesaplıyor (K17), ekran onu tekrarlamıyor.
 * </p>
 *
 * <p>
 * Taslak kimliği adres çubuğundan (<c>?taslak=</c>) geliyor ve gövde
 * <b>istemcide</b> çekiliyor: taslağın YAML'ını okuyan uç
 * (<c>GET /v1/parsers/drafts/{id}</c>) T20'nin işi ve bu dalın üretilen
 * şemasında henüz yok. <c>lib/parsers/draft.ts</c> içindeki bekçi, uç indiği
 * an derlemeyi kırıp geçici çözümü bitiriyor.
 * </p>
 */
export default async function ParserEditorPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const raw = params["taslak"];
  const draftId = (Array.isArray(raw) ? raw[0] : raw) ?? "";

  let sources: readonly SourceItem[] = [];
  let sourceError: string | null = null;

  try {
    const list = (await serverApi.get("/v1/sources")) as SourceList;
    sources = list.sources;
  } catch (cause) {
    // Envanter olmadan editör ÇALIŞMAYA DEVAM ediyor: örnek satırı elle
    // yapıştırmak hâlâ mümkün. Kaynak listesini çekememek bütün ekranı
    // kapatsaydı, arızanın etkisi olduğundan büyük görünürdü.
    if (cause instanceof NoSessionError) {
      sourceError = "Oturum sona ermiş görünüyor; sayfayı yenileyin.";
    } else if (cause instanceof ApiError) {
      sourceError = [cause.problem.error, cause.hint].filter(Boolean).join(" ");
    } else {
      sourceError = "Kaynak envanteri okunamadı.";
    }
  }

  return (
    <>
      {sourceError ? (
        <ErrorState
          title="Kaynak envanteri okunamadı"
          hint={`${sourceError} Örnek satırı elle yapıştırarak devam edebilirsiniz.`}
        />
      ) : null}

      <ParserWorkbench sources={sources} draftId={draftId} />
    </>
  );
}
