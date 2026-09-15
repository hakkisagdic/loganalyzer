import { describe, expect, it, vi } from "vitest";

import { RedisSessionStore, type RedisClient } from "@/lib/auth/redis-store";
import { resetSessionStore, sessionStore, useSessionStore } from "@/lib/auth/store";
import { defaultProbes, readiness, type ReadinessProbes } from "@/lib/health/readiness";

/**
 * <b>BFF hazırlık ucu</b> (T62) — ve bu paketin taşıdığı iddia bir davranış
 * değil, bir <b>görüş alanı</b>.
 *
 * <h3>Ölçülen sessizlik</h3>
 *
 * <p>
 * T49'un container sağlık kontrolü kök sayfayı yokluyor ve ölçütü
 * <c>&lt; 500</c>. Sonda çerez taşımadığı için istek oturum deposuna
 * <b>hiç uğramıyor</b>: <c>resolveSession</c> kimlik yoksa depoya gitmeden
 * dönüyor, kök sayfa 307 ile girişe yönlendiriyor, 307 &lt; 500 ve sağlık
 * kontrolü <b>yeşil</b>. Yani <c>redis-session</c> tamamen kırıkken de
 * <c>docker compose up -d --wait</c> yeşil dönüyor ve arıza ilk giriş
 * denemesinde çıkıyor.
 * </p>
 *
 * <p>
 * Aşağıdaki testler o üç kör bağımlılığın (oturum deposu, API, kimlik
 * sağlayıcısı) <b>ayrı ayrı</b> kırılabildiğini ve her birinde ucun kırmızı
 * yandığını tutuyor. <b>Konteyner yok</b> (§2): bağımlılıklar
 * <c>ReadinessProbes</c> ile değiştiriliyor — kırık bir bağımlılığı ölçmek onu
 * kırabilmeyi gerektiriyor ve konteynerle kırmak koordinatörün tarafı.
 * </p>
 */

const HAZIR: ReadinessProbes = {
  sessionStore: async () => ({ kind: "memory", reachable: true }),
  api: async () => true,
  identityProvider: async () => true,
};

function ile(overrides: Partial<ReadinessProbes>): ReadinessProbes {
  return { ...HAZIR, ...overrides };
}

describe("hazırlık ucu", () => {
  it("her bağımlılık hazırken hazır diyor", async () => {
    const report = await readiness(HAZIR);

    expect(report.ready).toBe(true);
    expect(report.checks.map((c) => c.name).sort()).toEqual([
      "api",
      "identity_provider",
      "session_store",
    ]);
  });

  /**
   * Üç kör bağımlılık, üç ayrı test. Tek bir "hepsi kırık" testi yazmak
   * <b>yetmez</b>: kapının yalnızca birine baktığı hâlde de geçerdi.
   */
  it.each([
    ["session_store", { sessionStore: async () => ({ kind: "redis" as const, reachable: false }) }],
    ["api", { api: async () => false }],
    ["identity_provider", { identityProvider: async () => false }],
  ])("%s kırıkken hazır DEĞİL", async (name, override) => {
    const report = await readiness(ile(override));

    expect(report.ready).toBe(false);
    expect(report.checks.find((c) => c.name === name)?.state).toBe("unreachable");

    // Ve yalnızca kırık olan suçlanıyor: hepsini kırmızı yakan bir uç
    // operatörü yanlış yere gönderir.
    expect(report.checks.filter((c) => c.state === "unreachable")).toHaveLength(1);
  });

  /**
   * <b>Yapılandırılmış depo</b> raporda adıyla duruyor.
   *
   * <p>
   * Sınırın kendisi: varsayılan <c>memory</c> ve orada Redis <b>yok</b>. Uç
   * <i>"Redis kırık"</i> diyemez — söyleyebileceği tek doğru şey hangi deponun
   * yoklandığı. Bu ayrım kaçırılsa uç yerel geliştirmede sürekli kırmızı yanar
   * ve ilk gün kapatılır.
   * </p>
   */
  it("hangi deponun yoklandığını söylüyor", async () => {
    const bellek = await readiness(HAZIR);
    expect(bellek.checks.find((c) => c.name === "session_store")?.kind).toBe("memory");

    const redis = await readiness(
      ile({ sessionStore: async () => ({ kind: "redis", reachable: true }) }),
    );
    expect(redis.checks.find((c) => c.name === "session_store")?.kind).toBe("redis");
    expect(redis.ready).toBe(true);
  });

  /**
   * <b>Fırlatan bir yoklama, hazır sayılmıyor.</b> Sessizce yeşile düşmek bu
   * ucun kapattığı hatanın kendisi olurdu.
   */
  it("yoklama fırlatırsa hazır değil", async () => {
    const report = await readiness(
      ile({
        api: async () => {
          throw new Error("ağ");
        },
      }),
    );

    expect(report.ready).toBe(false);
    expect(report.checks.find((c) => c.name === "api")?.state).toBe("unreachable");
  });

  /**
   * <b>Yük topoloji taşımıyor</b> — ve bu bir güvenlik kararı, üslup değil.
   *
   * <p>
   * Uç kimlik doğrulaması istemiyor (onu çağıran container sağlık kontrolü) ve
   * <c>ui</c> servisi 3000'i dışarıya veriyor. O yüzden yükte adres, ana makine
   * adı, hata metni ve sürüm <b>yok</b>; yalnızca sayılabilir durumlar var.
   * Kalıp telemetri süzgecinin aynısı.
   * </p>
   */
  it("yükte adres, ana makine adı ya da hata metni geçmiyor", async () => {
    const report = await readiness(
      ile({
        api: async () => {
          throw new Error("connect ECONNREFUSED 10.0.4.17:8080 (api.internal)");
        },
        sessionStore: async () => {
          throw new Error("redis://redis-session:6379 unreachable");
        },
      }),
    );

    const payload = JSON.stringify(report);

    for (const leak of ["redis://", "http://", "ECONNREFUSED", "6379", "8080", "internal"]) {
      expect(payload).not.toContain(leak);
    }

    // Ve yük hâlâ işe yarıyor: iki bağımlılık kırık olarak GÖRÜNÜYOR.
    expect(report.ready).toBe(false);
    expect(report.checks.filter((c) => c.state === "unreachable")).toHaveLength(2);
  });
});

/**
 * <b>Depo yüzeyinin kendisi</b> — sağlık ucunun dayandığı sözleşme.
 *
 * <p>
 * <c>probe()</c> arayüzde <b>zorunlu</b>: isteğe bağlı olsaydı yeni bir depo
 * uygulaması onu yazmayı unutabilir ve uç o depo için sessizce yeşil kalırdı.
 * </p>
 */
describe("oturum deposu yoklaması", () => {
  it("bellek içi depo yapısı gereği erişilebilir", async () => {
    resetSessionStore();

    await expect(sessionStore().probe()).resolves.toEqual({
      kind: "memory",
      reachable: true,
    });
  });

  /**
   * Redis deposunun yoklaması <b>bağlantı durumunu</b> okuyor ve
   * <c>get()</c>'in ayrımını tekrar etmiyor: sağlık sondasının elinde geçerli
   * bir oturum kimliği yok, dolayısıyla <c>get(rastgele)</c> ulaşılabilir bir
   * depoda da <c>undefined</c> dönerdi — "bulunamadı" ile "ulaşılamadı" ayrımı
   * sonda tarafında kaybolurdu.
   */
  it("Redis deposu bağlantı durumunu bildiriyor", async () => {
    const client = fakeRedis();
    const store = new RedisSessionStore(client);

    await expect(store.probe()).resolves.toEqual({ kind: "redis", reachable: true });

    client.ready = false;

    await expect(store.probe()).resolves.toEqual({ kind: "redis", reachable: false });
  });

  /**
   * <b>Soğuk açılış beklemesi yoklamada da geçerli.</b> Bir kez hazır olmadan
   * önce yoklanan depo, bekleme penceresi içinde hazır olursa
   * <c>reachable</c> dönüyor — aksi hâlde açılışın ilk saniyelerinde sağlık
   * ucu kırmızı yanar, servis hiç <c>healthy</c> olmaz ve <c>--wait</c> zaman
   * aşımına düşer.
   */
  it("soğuk açılışta hazır olmayı bekliyor", async () => {
    const client = fakeRedis();
    client.ready = false;
    client.becomesReady = true;

    const store = new RedisSessionStore(client);

    await expect(store.probe()).resolves.toEqual({ kind: "redis", reachable: true });
  });
});

/**
 * <b>Bağlanmışlık</b> — T50'nin ayrımı: <i>var olmak ile bağlı olmak</i>.
 *
 * <p>
 * Yukarıdaki testlerin tamamı yoklamaları <b>enjekte ediyor</b> ve tek başına
 * yanıltıcı olurdu: mantık kusursuz çalışırken üretimin yoklamaları başka bir
 * şeye bakıyor olabilir. Bu bölüm iki halkayı kapatıyor — üretimin yoklaması
 * gerçekten <b>yapılandırılmış depoya</b> soruyor mu, ve rotanın durum kodu
 * rapora bağlı mı.
 * </p>
 */
describe("bağlanmışlık", () => {
  it("üretimin yoklaması yapılandırılmış depoya soruyor", async () => {
    let sorulan = 0;

    useSessionStore({
      async get() {
        return undefined;
      },
      async set() {},
      async delete() {},
      size() {
        return 0;
      },
      async probe() {
        sorulan += 1;
        return { kind: "redis", reachable: false };
      },
    });

    const probes = defaultProbes(sahteYapilandirma());

    await expect(probes.sessionStore()).resolves.toEqual({ kind: "redis", reachable: false });
    expect(sorulan).toBe(1);

    resetSessionStore();
  });

  /**
   * <b>Durum kodu ölçütün kendisi.</b> Container sağlık kontrolü `r.ok`
   * okuyor; rota hazır olmayan raporu 200 ile dönerse uç yazılmış ama
   * <b>okunmamış</b> olur — T49'un sağlık kontrolündeki hatanın birebir aynısı.
   */
  it("rota hazır değilken 503 dönüyor", async () => {
    vi.resetModules();
    vi.doMock("@/lib/health/readiness", () => ({
      readiness: async () => ({
        ready: false,
        checks: [{ name: "api", state: "unreachable" }],
      }),
    }));

    const { GET } = await import("@/app/api/health/ready/route");
    const response = await GET();

    expect(response.status).toBe(503);
    await expect(response.json()).resolves.toMatchObject({ ready: false });

    vi.doUnmock("@/lib/health/readiness");
    vi.resetModules();
  });

  it("rota hazırken 200 dönüyor", async () => {
    vi.resetModules();
    vi.doMock("@/lib/health/readiness", () => ({
      readiness: async () => ({ ready: true, checks: [] }),
    }));

    const { GET } = await import("@/app/api/health/ready/route");
    const response = await GET();

    expect(response.status).toBe(200);

    vi.doUnmock("@/lib/health/readiness");
    vi.resetModules();
  });
});

function sahteYapilandirma() {
  return {
    issuer: "http://localhost:8180/realms/bizigo",
    metadataUrl: "http://localhost:8180/realms/bizigo/.well-known/openid-configuration",
    clientId: "bizigo-ui",
    clientSecret: "sır",
    publicUrl: "http://localhost:3000",
    apiBaseUrl: "http://localhost:5080",
    cookieName: "bizigo.sid",
    sessionTtlSeconds: 60,
    sessionStore: "redis" as const,
    redisUrl: "redis://localhost:6379",
  };
}

interface FakeRedisClient extends RedisClient {
  ready: boolean;
  becomesReady: boolean;
}
function fakeRedis(): FakeRedisClient {
  const client: FakeRedisClient = {
    ready: true,
    becomesReady: false,
    async get() {
      return null;
    },
    async set() {},
    async delete() {},
    isReady() {
      return client.ready;
    },
    async waitUntilReady() {
      if (client.becomesReady) {
        client.ready = true;
        return true;
      }

      return false;
    },
    async close() {},
  };

  return client;
}
