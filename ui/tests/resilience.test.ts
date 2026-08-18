import { beforeAll, beforeEach, describe, expect, it } from "vitest";

import { installFakes, nextRequest, setupEnvironment } from "./harness";

/**
 * Keycloak ya da API ayakta değilken BFF ne yapıyor.
 *
 * <p>
 * İki başarısızlık biçimi ayrı ayrı tehlikeli: biri kullanıcıyı sonsuz
 * yönlendirme döngüsüne sokuyor, diğeri onu "çıkış yaptım" sanırken girmiş
 * hâlde bırakıyor. İkisi de yalnızca bağımlılık düştüğünde ortaya çıkıyor,
 * yani mutlu yolu koşan bir testte hiç görünmüyorlar.
 * </p>
 */

beforeAll(setupEnvironment);

beforeEach(() => {
  installFakes();
});

/** Bütün dış çağrıları düşüren bir ağ. */
function networkDown(): void {
  globalThis.fetch = (async () => {
    throw new TypeError("fetch failed");
  }) as typeof fetch;
}

describe("Keycloak ulaşılamazken", () => {
  it("giriş ham 500 yerine sebebi söyleyen sayfaya gidiyor", async () => {
    const { GET } = await import("@/app/api/auth/login/route");
    networkDown();

    const response = await GET(nextRequest("/api/auth/login"));
    const location = new URL(response.headers.get("location")!);

    expect(response.status).toBe(307);
    expect(location.pathname).toBe("/giris");
    expect(location.searchParams.get("hata")).toBeTruthy();
    // İpucu adresi söylemeli: "Keycloak'a ulaşılamıyor" tek başına hangi
    // adrese bakılacağını söylemiyor.
    expect(location.searchParams.get("ipucu")).toContain("realms/bizigo");
  });

  it("çıkış yine de yerel oturumu siliyor", async () => {
    const { GET: login } = await import("@/app/api/auth/login/route");
    const { GET: callback } = await import("@/app/signin-oidc/route");
    const { POST: logout } = await import("@/app/api/auth/logout/route");
    const { rememberPending } = await import("./harness");

    const loginResponse = await login(nextRequest("/api/auth/login"));
    const state = new URL(loginResponse.headers.get("location")!).searchParams.get("state")!;
    rememberPending(state);

    const callbackResponse = await callback(nextRequest(`/signin-oidc?code=k&state=${state}`));
    const cookie = `bizigo.sid=${callbackResponse.cookies.get("bizigo.sid")!.value}`;

    // Keşif belgesi giriş sırasında önbelleğe alındı; onu da düşürüyoruz ki
    // gerçekten yedek yola girelim. (Önbellek sıcakken çıkış adresi zaten
    // üretilebiliyor — o da doğru davranış.)
    const { resetDiscoveryCache } = await import("@/lib/auth/oidc");
    resetDiscoveryCache();
    networkDown();

    const response = await logout(nextRequest("/api/auth/logout", { method: "POST", cookie }));
    const body = (await response.json()) as { redirectTo: string };

    expect(response.cookies.get("bizigo.sid")!.maxAge).toBe(0);
    // Keycloak'a gidilemiyor; kullanıcı en azından giriş sayfasına düşüyor.
    expect(body.redirectTo).toBe("http://localhost:3000/giris");

    const { GET: proxy } = await import("@/app/api/bff/[...path]/route");
    const after = await proxy(nextRequest("/api/bff/auth/me", { cookie }), {
      params: Promise.resolve({ path: ["auth", "me"] }),
    });

    expect(after.status).toBe(401);
  });
});
