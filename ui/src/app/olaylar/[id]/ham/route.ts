import { NextResponse, type NextRequest } from "next/server";

import type { EventRaw } from "@/lib/api/client";
import { ApiError } from "@/lib/api/errors";
import { NoSessionError, apiForSession } from "@/lib/api/server";
import { readBffConfig } from "@/lib/auth/config";
import { readSessionId } from "@/lib/auth/session";
import { decodeBase64 } from "@/lib/events/raw";

/**
 * Ham baytların indirilmesi (T16).
 *
 * <p>
 * <b>Baytlar hiçbir aşamada yeniden kodlanmıyor.</b> API base64 döndürüyor,
 * burada çözülüyor ve <c>application/octet-stream</c> olarak veriliyor —
 * cihazın gönderdiğiyle birebir aynı. Metne çevirip yazmak, kodlama tespiti
 * yanlışsa düzeltilecek olan şeyi kalıcı olarak bozardı (K4).
 * </p>
 *
 * <p>
 * Ayrı bir rota olmasının sebebi <b>yetki</b>: indirme de kapsam kapısından
 * geçiyor. Kapsam dışı bir olayın baytları burada da inmiyor, çünkü istek yine
 * <c>GET /v1/events/{id}/raw</c>'a gidiyor ve orada kapsam <b>iki kez</b>
 * kontrol ediliyor (olay okunurken ve nesne anahtarındaki <c>owner_group</c>
 * üzerinden). İstemci tarafında base64 çözüp <c>Blob</c> indirmek de mümkündü
 * ama o yol token'ı ve baytları tarayıcıya taşırdı.
 * </p>
 */
export async function GET(
  request: NextRequest,
  context: { params: Promise<{ id: string }> },
): Promise<NextResponse> {
  const { id } = await context.params;
  const api = apiForSession(readSessionId(request, readBffConfig()));

  let raw: EventRaw;

  try {
    raw = (await api.get("/v1/events/{id}/raw", { path: { id } })) as EventRaw;
  } catch (error) {
    if (error instanceof NoSessionError) {
      return NextResponse.json(
        { error: "Oturum yok ya da süresi doldu.", hint: "Yeniden giriş yapın: /api/auth/login" },
        { status: 401 },
      );
    }

    if (error instanceof ApiError) {
      // Durum kodu ve ipucu aynen geçiyor: kapsam dışı olay 404 kalıyor,
      // 403'e çevrilmiyor (bilgi sızdırırdı).
      return NextResponse.json({ error: error.message, hint: error.hint }, { status: error.status });
    }

    return NextResponse.json({ error: "Ham kayıt indirilemedi." }, { status: 502 });
  }

  const bytes = decodeBase64(raw.raw_b64);

  return new NextResponse(bytes as unknown as BodyInit, {
    headers: {
      "content-type": "application/octet-stream",
      "content-length": String(bytes.length),
      // Dosya adı olay kimliğinden: kullanıcı birden fazla olayın baytlarını
      // indirdiğinde hangisi olduğu ayırt edilebilmeli.
      "content-disposition": `attachment; filename="${id}.bin"`,
      // Ham baytlar önbelleğe girmemeli: kapsam değişirse eski yanıt
      // yetkisiz bir kullanıcıya servis edilebilirdi.
      "cache-control": "no-store",
    },
  });
}
