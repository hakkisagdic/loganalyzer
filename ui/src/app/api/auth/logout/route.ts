import { NextResponse, type NextRequest } from "next/server";

import { clearedSessionCookie, readSessionId } from "@/lib/auth/session";
import { readBffConfig } from "@/lib/auth/config";
import { discover, endSessionUrl } from "@/lib/auth/oidc";
import { sessionStore } from "@/lib/auth/store";

export const dynamic = "force-dynamic";

/**
 * Çıkış — **iki oturum birden**.
 *
 * <p>
 * Yalnızca Next oturumunu silmek yetmez: kullanıcı Keycloak'ta hâlâ açık
 * olurdu ve bir sonraki "giriş yap" tıklaması onu hiç parola sormadan geri
 * alırdı. Kullanıcı "çıktım" sanır, ortak makinede oturum açık kalır.
 * </p>
 *
 * <p>
 * <b>POST</b>, GET değil: bir <c>&lt;img src="/api/auth/logout"&gt;</c> ile
 * kullanıcıyı sistemden atmak mümkün olmasın diye. `SameSite=Lax` çerezi
 * siteler arası POST'ta göndermiyor, yani CSRF ile çıkış da yaptırılamıyor.
 * </p>
 */
export async function POST(request: NextRequest): Promise<NextResponse> {
  const config = readBffConfig();
  const sessionId = readSessionId(request, config);

  let idToken: string | undefined;

  if (sessionId) {
    const record = await sessionStore().get(sessionId);
    idToken = record?.idToken;
    await sessionStore().delete(sessionId);
  }

  // Keycloak ayakta değilse bile yerel oturum silinmiş olmalı: kullanıcıyı
  // "çıkış yapamadınız" diyerek girmiş hâlde bırakmak, çıkışın anlamını
  // tersine çevirir.
  let redirectTo = `${config.publicUrl}/giris`;

  try {
    redirectTo = endSessionUrl(await discover(config), config, idToken);
  } catch {
    // Yerel çıkış tamam; Keycloak oturumu açık kalıyor ve bu kullanıcıya
    // gösterilmiyor — söyleyecek eyleme dönük bir şey yok.
  }

  const response = NextResponse.json({ redirectTo });

  // Çerez her hâlükârda siliniyor — oturum deposunda kayıt bulunamasa bile.
  response.cookies.set(clearedSessionCookie(config));

  return response;
}
