import { beforeAll, beforeEach, describe, expect, it } from "vitest";

import {
  ACCESS_TOKEN,
  API_URL,
  ISSUER,
  installFakes,
  nextRequest,
  rememberPending,
  REFRESH_TOKEN,
  RENEWED_ACCESS_TOKEN,
  serializeResponse,
  setupEnvironment,
  type FakeIdp,
} from "./harness";

/**
 * **BFF deseninin tek kanıtı** (T13 kabul kriteri).
 *
 * <p>
 * "Token sunucuda kalıyor" bir niyet beyanı; bu dosya onu ölçüye çeviriyor.
 * Giriş akışı uçtan uca koşuyor ve tarayıcıya dönen <b>her yanıtın her baytı</b>
 * — durum satırı, bütün başlıklar, çerezler ve gövde — erişim ve yenileme
 * token'ı dizgileri için taranıyor.
 * </p>
 *
 * <p>
 * Testin kırmızı yanabildiği doğrulandı: <c>signin-oidc</c> işleyicisinde
 * çerez değeri <c>sessionId</c> yerine <c>tokens.access_token</c> yapıldığında
 * "oturum çerezi" iddiası düşüyor; <c>proxyToApi</c>'deki yanıt başlığı beyaz
 * listesi kaldırılıp bütün başlıklar geçirildiğinde "vekil yanıtı" iddiası
 * düşüyor.
 * </p>
 */

let fake: FakeIdp;

beforeAll(setupEnvironment);

beforeEach(() => {
  fake = installFakes();
});

/** Giriş akışını uçtan uca koşup toplanan bütün yanıtları döndürüyor. */
async function signIn(): Promise<{ cookie: string; responses: string[] }> {
  const { GET: login } = await import("@/app/api/auth/login/route");
  const { GET: callback } = await import("@/app/signin-oidc/route");

  const responses: string[] = [];

  const loginResponse = await login(nextRequest("/api/auth/login?returnTo=%2F"));
  responses.push(await serializeResponse(loginResponse));

  const authorizeUrl = new URL(loginResponse.headers.get("location")!);
  const state = authorizeUrl.searchParams.get("state")!;
  rememberPending(state);

  const callbackResponse = await callback(nextRequest(`/signin-oidc?code=kod-123&state=${state}`));
  responses.push(await serializeResponse(callbackResponse));

  const sessionCookie = callbackResponse.cookies.get("bizigo.sid")!;

  return { cookie: `bizigo.sid=${sessionCookie.value}`, responses };
}

describe("erişim token'ı tarayıcıya hiç ulaşmıyor", () => {
  it("giriş akışının hiçbir yanıtında token geçmiyor", async () => {
    const { responses } = await signIn();

    for (const response of responses) {
      expect(response).not.toContain(ACCESS_TOKEN);
      expect(response).not.toContain(REFRESH_TOKEN);
    }
  });

  it("oturum çerezi token değil, yalnızca bir anahtar", async () => {
    const { GET: login } = await import("@/app/api/auth/login/route");
    const { GET: callback } = await import("@/app/signin-oidc/route");

    const loginResponse = await login(nextRequest("/api/auth/login"));
    const state = new URL(loginResponse.headers.get("location")!).searchParams.get("state")!;
    rememberPending(state);

    const response = await callback(nextRequest(`/signin-oidc?code=kod-123&state=${state}`));
    const cookie = response.cookies.get("bizigo.sid")!;

    expect(cookie.value).not.toContain(ACCESS_TOKEN);
    expect(cookie.value).not.toContain(REFRESH_TOKEN);
    // Çerezin içinde çözülebilecek bir şey olmamalı: JWT değil, base64 JSON
    // değil — rastgele bir anahtar.
    expect(cookie.value).not.toContain(".");
    expect(cookie.value.length).toBeGreaterThanOrEqual(32);

    // Üç bayrak da pazarlık dışı.
    expect(cookie.httpOnly).toBe(true);
    expect(cookie.sameSite).toBe("lax");
    // Yerel geliştirme düz HTTP; `Secure` adresin şemasından türüyor.
    expect(cookie.secure).toBe(false);
  });

  it("HTTPS adreste çerez Secure oluyor", async () => {
    const previous = process.env.BFF_PUBLIC_URL;
    process.env.BFF_PUBLIC_URL = "https://loglar.ornek.com";

    try {
      const { readBffConfig } = await import("@/lib/auth/config");
      const { sessionCookieOptions } = await import("@/lib/auth/session");

      expect(sessionCookieOptions(readBffConfig(), "abc", 60).secure).toBe(true);
    } finally {
      process.env.BFF_PUBLIC_URL = previous;
    }
  });

  it("vekil yanıtında token yok, API isteğinde var", async () => {
    const { cookie } = await signIn();
    const { GET } = await import("@/app/api/bff/[...path]/route");

    const response = await GET(nextRequest("/api/bff/auth/me", { cookie }), {
      params: Promise.resolve({ path: ["auth", "me"] }),
    });

    const serialized = await serializeResponse(response);

    expect(response.status).toBe(200);
    expect(serialized).not.toContain(ACCESS_TOKEN);
    expect(serialized).not.toContain(REFRESH_TOKEN);

    // Kanıtın diğer yarısı: token GERÇEKTEN kullanılıyor. Bu satır olmadan
    // yukarıdaki iddia, "hiç token üretilmediği" için de geçerdi.
    expect(fake.apiRequests).toHaveLength(1);
    expect(fake.apiRequests[0]!.authorization).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(fake.apiRequests[0]!.url).toBe(`${API_URL}/auth/me`);
  });

  it("oturum çerezi API'ye iletilmiyor", async () => {
    const { cookie } = await signIn();
    const { GET } = await import("@/app/api/bff/[...path]/route");

    await GET(nextRequest("/api/bff/auth/me", { cookie }), {
      params: Promise.resolve({ path: ["auth", "me"] }),
    });

    // API'de artık cookie işleyicisi yok (K31); çerezi göndermek, orada bir gün
    // yeniden açılacak ikinci bir kimlik yoluna davetiye olurdu.
    expect(fake.apiRequests[0]!.cookie).toBeNull();
  });

  it("API'nin gönderdiği başlıklar tarayıcıya olduğu gibi geçmiyor", async () => {
    const { cookie } = await signIn();

    // API bir gün tanılama başlığı ya da çerez döndürmeye başlarsa, vekil onu
    // sessizce tarayıcıya taşımamalı.
    const previousFetch = globalThis.fetch;
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;

      if (url.startsWith(API_URL)) {
        return new Response(JSON.stringify({ ok: true }), {
          headers: {
            "content-type": "application/json",
            "set-cookie": `api.session=${ACCESS_TOKEN}`,
            "x-upstream-token": ACCESS_TOKEN,
          },
        });
      }

      return previousFetch(input, init);
    }) as typeof fetch;

    try {
      const { GET } = await import("@/app/api/bff/[...path]/route");
      const response = await GET(nextRequest("/api/bff/v1/sources", { cookie }), {
        params: Promise.resolve({ path: ["v1", "sources"] }),
      });

      const serialized = await serializeResponse(response);

      expect(serialized).not.toContain(ACCESS_TOKEN);
      expect(response.headers.get("set-cookie")).toBeNull();
      expect(response.headers.get("x-upstream-token")).toBeNull();
    } finally {
      globalThis.fetch = previousFetch;
    }
  });

  it("yetkilendirme adresinde sır yok", async () => {
    const { GET: login } = await import("@/app/api/auth/login/route");

    const response = await login(nextRequest("/api/auth/login"));
    const url = new URL(response.headers.get("location")!);

    expect(url.origin + url.pathname).toBe(`${ISSUER}/protocol/openid-connect/auth`);
    expect(url.toString()).not.toContain(process.env.KEYCLOAK_CLIENT_SECRET!);

    // PKCE doğrulayıcısı sunucuda kalıyor; adrese yalnızca S256 özeti gidiyor.
    expect(url.searchParams.get("code_challenge_method")).toBe("S256");
    expect(url.searchParams.get("code_challenge")).toBeTruthy();
    expect(url.searchParams.get("code_verifier")).toBeNull();

    // Yalnızca `openid`. Realm'de yerleşik scope'lar hiç oluşmadığı için
    // `profile`/`email` istemek `invalid_scope` ile düşer.
    expect(url.searchParams.get("scope")).toBe("openid");
  });

  it("gizli anahtar gövdede değil Basic başlığında gidiyor", async () => {
    await signIn();

    for (const body of fake.tokenRequests) {
      expect(body.get("client_secret")).toBeNull();
    }
  });
});

describe("süresi dolan token şeffaf yenileniyor", () => {
  it("kullanıcı hiçbir şey fark etmeden yeni token'la devam ediyor", async () => {
    // Token daha doğarken ölü: bir sonraki istek yenileme tetiklemeli.
    fake.accessTokenLifetime = 1;

    const { cookie } = await signIn();
    const { GET } = await import("@/app/api/bff/[...path]/route");

    const response = await GET(nextRequest("/api/bff/auth/me", { cookie }), {
      params: Promise.resolve({ path: ["auth", "me"] }),
    });

    expect(response.status).toBe(200);

    const refreshCalls = fake.tokenRequests.filter((b) => b.get("grant_type") === "refresh_token");
    expect(refreshCalls).toHaveLength(1);
    expect(refreshCalls[0]!.get("refresh_token")).toBe(REFRESH_TOKEN);

    // API yeni token'ı gördü, kullanıcı hiçbir yönlendirme ya da hata görmedi.
    expect(fake.apiRequests[0]!.authorization).toBe(`Bearer ${RENEWED_ACCESS_TOKEN}`);
    expect(await serializeResponse(response)).not.toContain(RENEWED_ACCESS_TOKEN);
  });

  it("API token'ı reddederse bir kez yenileyip yeniden deniyor", async () => {
    const { cookie } = await signIn();
    fake.apiRejectsStaleToken = true;

    const { GET } = await import("@/app/api/bff/[...path]/route");
    const response = await GET(nextRequest("/api/bff/auth/me", { cookie }), {
      params: Promise.resolve({ path: ["auth", "me"] }),
    });

    expect(response.status).toBe(200);
    expect(fake.apiRequests).toHaveLength(2);
    expect(fake.apiRequests[1]!.authorization).toBe(`Bearer ${RENEWED_ACCESS_TOKEN}`);
  });

  it("yenileme de düşerse 401 dönüyor, yönlendirme değil", async () => {
    fake.accessTokenLifetime = 1;
    const { cookie } = await signIn();
    fake.refreshRejected = true;

    const { GET } = await import("@/app/api/bff/[...path]/route");
    const response = await GET(nextRequest("/api/bff/auth/me", { cookie }), {
      params: Promise.resolve({ path: ["auth", "me"] }),
    });

    // 302 dönmek, Keycloak'ın giriş sayfasının HTML'ini bir JSON
    // ayrıştırıcısına yedirmek olurdu.
    expect(response.status).toBe(401);
    expect(await response.json()).toMatchObject({ error: expect.any(String) });
  });
});

describe("çıkış iki oturumu da kapatıyor", () => {
  it("Next oturumu siliniyor ve Keycloak çıkış adresi dönüyor", async () => {
    const { cookie } = await signIn();
    const { POST } = await import("@/app/api/auth/logout/route");

    const response = await POST(nextRequest("/api/auth/logout", { method: "POST", cookie }));
    const body = (await response.json()) as { redirectTo: string };

    const cleared = response.cookies.get("bizigo.sid")!;
    expect(cleared.value).toBe("");
    expect(cleared.maxAge).toBe(0);

    const logoutUrl = new URL(body.redirectTo);
    expect(logoutUrl.origin + logoutUrl.pathname).toBe(`${ISSUER}/protocol/openid-connect/logout`);
    // `id_token_hint` olmadan Keycloak onay ekranı gösteriyor ve akış yarıda kalıyor.
    expect(logoutUrl.searchParams.get("id_token_hint")).toBeTruthy();
    // Realm'deki `post.logout.redirect.uris` ile eşleşmeli.
    expect(logoutUrl.searchParams.get("post_logout_redirect_uri")).toBe("http://localhost:3000/");

    // Oturum gerçekten silinmiş: aynı çerezle vekil artık 401 veriyor.
    const { GET } = await import("@/app/api/bff/[...path]/route");
    const after = await GET(nextRequest("/api/bff/auth/me", { cookie }), {
      params: Promise.resolve({ path: ["auth", "me"] }),
    });

    expect(after.status).toBe(401);
  });

  it("çıkış yanıtında token yok", async () => {
    const { cookie } = await signIn();
    const { POST } = await import("@/app/api/auth/logout/route");

    const response = await POST(nextRequest("/api/auth/logout", { method: "POST", cookie }));
    const serialized = await serializeResponse(response);

    // `id_token_hint` yanıtta geçiyor — o kasıtlı ve zararsız: id_token bir
    // kimlik belgesi, API'ye erişim vermiyor. Erişim ve yenileme token'ları ise
    // burada da görünmemeli.
    expect(serialized).not.toContain(ACCESS_TOKEN);
    expect(serialized).not.toContain(REFRESH_TOKEN);
  });
});

describe("oturum yoksa", () => {
  it("vekil 401 ve ipucu dönüyor", async () => {
    const { GET } = await import("@/app/api/bff/[...path]/route");

    const response = await GET(nextRequest("/api/bff/auth/me"), {
      params: Promise.resolve({ path: ["auth", "me"] }),
    });

    expect(response.status).toBe(401);
    expect(await response.json()).toMatchObject({ hint: expect.stringContaining("/api/auth/login") });
    expect(fake.apiRequests).toHaveLength(0);
  });

  it("bilinmeyen state ile dönen çağrı oturum açmıyor", async () => {
    const { GET: callback } = await import("@/app/signin-oidc/route");

    const response = await callback(nextRequest("/signin-oidc?code=kod&state=uydurma"));

    expect(response.status).toBe(307);
    expect(response.headers.get("location")).toContain("/giris?hata=");
    expect(response.cookies.get("bizigo.sid")).toBeUndefined();
  });
});
