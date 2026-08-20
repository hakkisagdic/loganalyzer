import { cookies } from "next/headers";

import type { AuthMe } from "@/lib/api/client";

import { readBffConfig } from "./config";
import { resolveSession } from "./session";
import { SessionStoreUnavailableError } from "./store";

/**
 * Sunucu bileşenlerinin kimlik sorgusu.
 *
 * <p>
 * Kapsamı BFF <b>kendisi hesaplamıyor</b>: `/auth/me`'yi API'ye soruyor.
 * `AccessScopeResolver` kontrol düzlemindeki eşleme tablosunu okuyor ve o
 * yorumu iki yerde tutmak, iki yerde ayrışabilen bir kapsam demek olurdu —
 * F1'de tam olarak bu risk ölçüldü.
 * </p>
 *
 * <p>
 * Sonuç <b>üç durumlu</b>, iki değil. "Oturum yok" ile "API cevap vermiyor"
 * ayrılmak zorunda: ikisini birden giriş yönlendirmesine bağlamak sonsuz
 * döngü üretiyor — kullanıcı giriş yapıyor, sayfaya dönüyor, API hâlâ
 * ulaşılamaz olduğu için tekrar girişe yollanıyor ve tarayıcı bunu durdurana
 * kadar dönüyor. Yalnızca gerçekten oturumsuz olan hâl girişe gidiyor.
 * </p>
 */
export type IdentityResult =
  | { readonly status: "anonymous" }
  | { readonly status: "ok"; readonly user: AuthMe }
  | { readonly status: "error"; readonly message: string; readonly hint?: string };

export async function currentUser(): Promise<IdentityResult> {
  const config = readBffConfig();
  const jar = await cookies();

  let session: Awaited<ReturnType<typeof resolveSession>>;

  try {
    session = await resolveSession(jar.get(config.cookieName)?.value, config);
  } catch (error) {
    if (!(error instanceof SessionStoreUnavailableError)) {
      throw error;
    }

    // ÜÇÜNCÜ DURUM, ve varlık sebebi tam olarak bu: depo ulaşılamazken
    // "anonymous" dönmek kullanıcıyı girişe yollardı, giriş yeni oturumu
    // yazmayı denerdi, o da düşerdi — ve kimse bir hata görmeden sonsuz
    // döngüye girerdi (B7).
    return {
      status: "error",
      message: "Oturum deposuna ulaşılamıyor.",
      hint: "Paylaşılan oturum deposu (Redis) yanıt vermiyor; yönetici kontrol etmeli. Yeniden giriş yapmak bu durumu düzeltmiyor.",
    };
  }

  if (!session) {
    return { status: "anonymous" };
  }

  let response: Response;

  try {
    response = await fetch(`${config.apiBaseUrl}/auth/me`, {
      headers: {
        authorization: `Bearer ${session.record.accessToken}`,
        accept: "application/json",
      },
      cache: "no-store",
    });
  } catch {
    return {
      status: "error",
      message: "API'ye ulaşılamıyor.",
      hint: `Bizigo.Api ${config.apiBaseUrl} adresinde çalışıyor mu?`,
    };
  }

  if (response.status === 401) {
    // BFF token'ı zaten yeniledi; API yine de reddediyorsa sorun oturumda
    // değil güven zincirinde — realm yeniden import edilmiş ve imzalama
    // anahtarı değişmiş olabilir (deploy/keycloak/README.md).
    return {
      status: "error",
      message: "API kimliği tanımıyor.",
      hint: "Keycloak realm'i yeniden import edildiyse imzalama anahtarları değişmiştir; çıkış yapıp yeniden girin.",
    };
  }

  if (!response.ok) {
    return {
      status: "error",
      message: `Kimlik sorgusu başarısız (HTTP ${response.status}).`,
    };
  }

  return { status: "ok", user: (await response.json()) as AuthMe };
}
