import { beforeAll, beforeEach, describe, expect, it } from "vitest";

import { RedisSessionStore, type RedisClient } from "@/lib/auth/redis-store";
import { useSessionStore } from "@/lib/auth/store";

import {
  ACCESS_TOKEN,
  installFakes,
  nextRequest,
  REFRESH_TOKEN,
  rememberPending,
  serializeResponse,
  setupEnvironment,
  type FakeIdp,
} from "./harness";

/**
 * **Depo değişiyor, sözleşme değil** (B7).
 *
 * <p>
 * <c>token-isolation.test.ts</c> BFF deseninin kanıtı ve bellek içi depoyla
 * koşuyor. Bu dosya <b>aynı akışı</b> Redis destekli depoyla koşturuyor:
 * oturum çerezi hâlâ opak rastgele bir anahtar, token'lar hâlâ tarayıcıya
 * ulaşmıyor, ve depoya yazılan şey hâlâ yalnızca sunucuda duruyor.
 * </p>
 *
 * <p>
 * Bu ayrı bir dosya çünkü depo <b>giriş akışı başlamadan önce</b> takılmak
 * zorunda; <c>installFakes()</c> her testte bellek içi depoya sıfırlıyor.
 * </p>
 */

/** Sahte Redis: belleğe yazıyor, düşürülebiliyor. Gerçek sunucu gerekmiyor. */
class FakeRedis implements RedisClient {
  readonly entries = new Map<string, { value: string; ttlSeconds: number }>();
  ready = true;

  async get(key: string): Promise<string | null> {
    return this.entries.get(key)?.value ?? null;
  }

  async set(key: string, value: string, ttlSeconds: number): Promise<void> {
    this.entries.set(key, { value, ttlSeconds });
  }

  async delete(key: string): Promise<void> {
    this.entries.delete(key);
  }

  isReady(): boolean {
    return this.ready;
  }
}

let fake: FakeIdp;
let redis: FakeRedis;
let login: typeof import("@/app/api/auth/login/route").GET;
let callback: typeof import("@/app/signin-oidc/route").GET;
let proxy: typeof import("@/app/api/bff/[...path]/route").GET;

beforeAll(async () => {
  await setupEnvironment();

  // Modüller ölçülen gövdenin dışında yükleniyor: Next modüllerinin dönüşüm
  // maliyeti testin duvar saati bütçesine yazılmasın (F1'in dersi).
  ({ GET: login } = await import("@/app/api/auth/login/route"));
  ({ GET: callback } = await import("@/app/signin-oidc/route"));
  ({ GET: proxy } = await import("@/app/api/bff/[...path]/route"));
});

beforeEach(() => {
  fake = installFakes();
  redis = new FakeRedis();
  useSessionStore(new RedisSessionStore(redis));
});

async function signIn(): Promise<{ cookie: string; responses: string[] }> {
  const responses: string[] = [];

  const loginResponse = await login(nextRequest("/api/auth/login?returnTo=%2F"));
  responses.push(await serializeResponse(loginResponse));

  const state = new URL(loginResponse.headers.get("location")!).searchParams.get("state")!;
  rememberPending(state);

  const callbackResponse = await callback(nextRequest(`/signin-oidc?code=kod-123&state=${state}`));
  responses.push(await serializeResponse(callbackResponse));

  return {
    cookie: `bizigo.sid=${callbackResponse.cookies.get("bizigo.sid")!.value}`,
    responses,
  };
}

describe("Redis deposuyla da token tarayıcıya ulaşmıyor", () => {
  it("giriş akışının hiçbir yanıtında token geçmiyor", async () => {
    const { responses } = await signIn();

    for (const response of responses) {
      expect(response).not.toContain(ACCESS_TOKEN);
      expect(response).not.toContain(REFRESH_TOKEN);
    }
  });

  it("çerez hâlâ opak bir anahtar — içinde nokta bile yok", async () => {
    // Depo değişti; çerezin ANLAMI değişmedi. Çerezde çözülebilecek bir şey
    // olsaydı (JWT, base64 JSON) token'ı tarayıcıya vermiş olurduk.
    const loginResponse = await login(nextRequest("/api/auth/login"));
    const state = new URL(loginResponse.headers.get("location")!).searchParams.get("state")!;
    rememberPending(state);

    const response = await callback(nextRequest(`/signin-oidc?code=kod-123&state=${state}`));
    const cookie = response.cookies.get("bizigo.sid")!;

    expect(cookie.value).not.toContain(ACCESS_TOKEN);
    expect(cookie.value).not.toContain(".");
    expect(cookie.value.length).toBeGreaterThanOrEqual(32);
    expect(cookie.httpOnly).toBe(true);
    expect(cookie.sameSite).toBe("lax");
  });

  it("token depoda, yanıtta değil", async () => {
    // Kanıtın diğer yarısı: token GERÇEKTEN saklanıyor. Bu satır olmadan
    // yukarıdaki iddia, hiç oturum yazılmadığı için de geçerdi.
    const { cookie } = await signIn();

    const stored = [...redis.entries.values()].map((entry) => entry.value).join("\n");

    expect(stored).toContain(ACCESS_TOKEN);
    expect(cookie).not.toContain(ACCESS_TOKEN);
  });

  it("vekil isteği token'ı yukarı akışa taşıyor, aşağı akışa değil", async () => {
    const { cookie } = await signIn();

    const response = await proxy(nextRequest("/api/bff/auth/me", { cookie }), {
      params: Promise.resolve({ path: ["auth", "me"] }),
    });

    expect(response.status).toBe(200);
    expect(await serializeResponse(response)).not.toContain(ACCESS_TOKEN);
    expect(fake.apiRequests[0]!.authorization).toBe(`Bearer ${ACCESS_TOKEN}`);
  });
});

describe("Redis düştüğünde kullanıcı döngüye girmiyor", () => {
  /**
   * Tasarımın can alıcı noktası. Depo ulaşılamazken vekil <b>401 dönmüyor</b>:
   * 401 istemciye "yeniden giriş yap" dedirtir, giriş de aynı depoya yazmayı
   * dener ve düşer — kullanıcı hiçbir hata görmeden döngüye girer.
   */
  it("vekil 503 dönüyor, 401 değil", async () => {
    const { cookie } = await signIn();
    redis.ready = false;

    const response = await proxy(nextRequest("/api/bff/auth/me", { cookie }), {
      params: Promise.resolve({ path: ["auth", "me"] }),
    });

    expect(response.status).toBe(503);

    const body = (await response.json()) as { error: string; hint?: string };
    expect(body.hint).toContain("Yeniden giriş yapmak düzeltmiyor");
  });

  it("API'ye hiç istek gitmiyor", async () => {
    // Oturum çözülemediği için yukarı akışa gidilmiyor; token'sız bir istek
    // API'de 401 üretir ve hata yanlış yeri işaret ederdi.
    const { cookie } = await signIn();
    const before = fake.apiRequests.length;
    redis.ready = false;

    await proxy(nextRequest("/api/bff/auth/me", { cookie }), {
      params: Promise.resolve({ path: ["auth", "me"] }),
    });

    expect(fake.apiRequests).toHaveLength(before);
  });

  it("depo geri geldiğinde oturum çalışmaya devam ediyor", async () => {
    // "Herkes çıkış yapmış olur" kabul edilebilir bir sonuç ama gereksiz:
    // kayıt Redis'te duruyorsa oturum kesinti sonrası ayakta kalıyor.
    const { cookie } = await signIn();

    redis.ready = false;
    await proxy(nextRequest("/api/bff/auth/me", { cookie }), {
      params: Promise.resolve({ path: ["auth", "me"] }),
    });

    redis.ready = true;
    const response = await proxy(nextRequest("/api/bff/auth/me", { cookie }), {
      params: Promise.resolve({ path: ["auth", "me"] }),
    });

    expect(response.status).toBe(200);
  });
});
