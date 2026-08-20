import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { RedisSessionStore, type RedisClient } from "@/lib/auth/redis-store";
import {
  SessionStoreUnavailableError,
  resetSessionStore,
  sessionStore,
  type SessionRecord,
  type SessionStore,
} from "@/lib/auth/store";

/**
 * Oturum deposu sözleşmesi (B7).
 *
 * <p>
 * İki uygulama var ve <b>aynı sözleşmeyi</b> taşımak zorundalar: bellek içi
 * harita ile Redis. Aynı testler ikisine de koşuyor — ayrıştıkları gün, ayrıldığı
 * yer burada görünüyor.
 * </p>
 *
 * <p>
 * <b>Gerçek Redis yok.</b> Depo mantığı dört metotluk dar bir arayüzün üstünde
 * duruyor (<c>RedisClient</c>) ve testler onun sahte bir uygulamasını veriyor.
 * Konteyner gerekmiyor, dolayısıyla bu testler her makinede koşuyor. Gerçek
 * sunucuya karşı koşan test ayrı ve <c>Skip</c> ile duruyor.
 * </p>
 */

/**
 * Sahte Redis — belleğe yazan, <b>düşürülebilen</b> bir istemci.
 *
 * <p>TTL'i saklıyor ama kendiliğinden silmiyor: süre dolmasını testler saati
 * ilerleterek sınıyor, duvar saatiyle bekleyerek değil (F1'in dersi).</p>
 */
class FakeRedis implements RedisClient {
  readonly entries = new Map<string, { value: string; ttlSeconds: number }>();

  /** `false` ise bütün çağrılar "ulaşılamıyor" davranışı üretiyor. */
  ready = true;

  /** `true` ise bağlantı hazır görünüyor ama çağrı fırlatıyor — yarı ölü hâl. */
  throwOnCall = false;

  async get(key: string): Promise<string | null> {
    this.#check();
    return this.entries.get(key)?.value ?? null;
  }

  async set(key: string, value: string, ttlSeconds: number): Promise<void> {
    this.#check();
    this.entries.set(key, { value, ttlSeconds });
  }

  async delete(key: string): Promise<void> {
    this.#check();
    this.entries.delete(key);
  }

  isReady(): boolean {
    return this.ready;
  }

  async close(): Promise<void> {
    // Sahtede kapatılacak bir şey yok; arayüz zorunlu kıldığı için var.
    this.ready = false;
  }

  #check(): void {
    if (this.throwOnCall) {
      throw new Error("bağlantı sıfırlandı");
    }
  }
}

const HOUR = 60 * 60 * 1000;

function record(overrides: Partial<SessionRecord> = {}): SessionRecord {
  return {
    accessToken: "ACCESS",
    refreshToken: "REFRESH",
    idToken: "ID",
    accessTokenExpiresAt: Date.now() + 5 * 60 * 1000,
    expiresAt: Date.now() + 8 * HOUR,
    ...overrides,
  };
}

let redis: FakeRedis;

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date("2026-08-20T12:00:00Z"));
  resetSessionStore();
  redis = new FakeRedis();
});

afterEach(() => {
  vi.useRealTimers();
});

/** İki uygulamanın da geçmesi gereken sözleşme. */
function contract(name: string, create: () => SessionStore) {
  describe(`${name} — ortak sözleşme`, () => {
    it("yazılan kayıt geri okunuyor", async () => {
      const store = create();
      const value = record();

      await store.set("abc", value);

      expect(await store.get("abc")).toEqual(value);
    });

    it("olmayan anahtar `undefined` — hata değil", async () => {
      expect(await create().get("yok")).toBeUndefined();
    });

    it("silinen kayıt dönmüyor", async () => {
      const store = create();
      await store.set("abc", record());
      await store.delete("abc");

      expect(await store.get("abc")).toBeUndefined();
    });

    it("süresi dolmuş kayıt okunmuyor", async () => {
      const store = create();
      await store.set("abc", record({ expiresAt: Date.now() + HOUR }));

      vi.setSystemTime(new Date(Date.now() + 2 * HOUR));

      expect(await store.get("abc")).toBeUndefined();
    });

    it("yenileme token'ı olmayan kayıt da saklanıyor", async () => {
      // Keycloak yenileme token'ı döndürmeyebiliyor; kayıt yine geçerli.
      const store = create();
      const value = record({ refreshToken: undefined, idToken: undefined });

      await store.set("abc", value);

      expect(await store.get("abc")).toEqual(value);
    });
  });
}

contract("bellek içi", () => {
  resetSessionStore();
  return sessionStore();
});

contract("redis", () => new RedisSessionStore(redis));

// --------------------------------------------------------------- TTL tek kaynak

describe("TTL tek yerden türüyor", () => {
  /**
   * Redis'in <c>EXPIRE</c>'ı kaydın kendi <c>expiresAt</c>'inden hesaplanıyor;
   * o da giriş anında <c>BFF_SESSION_TTL_SECONDS</c>'ten geliyor. İkinci bir
   * ömür değeri yazılsaydı ikisi ayrışırdı: oturum ya erken ölürdü ya Redis'te
   * sızıntı olarak kalırdı, ve ikisi de sessiz.
   */
  it("`EXPIRE`, kaydın kalan ömrü kadar", async () => {
    const store = new RedisSessionStore(redis);

    await store.set("abc", record({ expiresAt: Date.now() + 8 * HOUR }));

    expect(redis.entries.get("bizigo:bff:session:abc")?.ttlSeconds).toBe(8 * 60 * 60);
  });

  it("kısalan oturum kısalan TTL veriyor", async () => {
    const store = new RedisSessionStore(redis);

    await store.set("abc", record({ expiresAt: Date.now() + 90 * 1000 }));

    expect(redis.entries.get("bizigo:bff:session:abc")?.ttlSeconds).toBe(90);
  });

  it("süresi geçmiş kayıt yazılmıyor — siliniyor", async () => {
    // Negatif TTL'i Redis "süresiz" sayar ve kayıt sonsuza kadar kalırdı.
    const store = new RedisSessionStore(redis);
    await store.set("abc", record());

    await store.set("abc", record({ expiresAt: Date.now() - 1000 }));

    expect(redis.entries.has("bizigo:bff:session:abc")).toBe(false);
  });
});

// --------------------------------------------------- ulaşılamıyor ≠ oturum yok

describe("depo ulaşılamazken", () => {
  /**
   * Bu bloğun tamamı tek bir cümleyi savunuyor: <b>"herkes çıkış yapmış olur"
   * kabul edilebilir, "sessizce oturumsuz görünür" kabul edilemez.</b>
   *
   * <p>İkincisi olsaydı kullanıcı girişe yönlendirilirdi, giriş yeni oturumu
   * yazmayı denerdi, o da düşerdi — ve hiçbir hata görünmeden sonsuz döngü
   * oluşurdu.</p>
   */
  it("bağlantı hazır değilken okuma `undefined` DEĞİL, hata veriyor", async () => {
    const store = new RedisSessionStore(redis);
    redis.ready = false;

    await expect(store.get("abc")).rejects.toBeInstanceOf(SessionStoreUnavailableError);
  });

  it("yarı ölü bağlantı da hata veriyor", async () => {
    // `isReady()` doğru diyor ama çağrı patlıyor: ağ kesintisinin en sinsi hâli.
    const store = new RedisSessionStore(redis);
    redis.throwOnCall = true;

    await expect(store.get("abc")).rejects.toBeInstanceOf(SessionStoreUnavailableError);
  });

  it("yazma ve silme de aynı hatayı veriyor", async () => {
    const store = new RedisSessionStore(redis);
    redis.ready = false;

    await expect(store.set("abc", record())).rejects.toBeInstanceOf(SessionStoreUnavailableError);
    await expect(store.delete("abc")).rejects.toBeInstanceOf(SessionStoreUnavailableError);
  });

  it("hata mesajı hangi işlemin düştüğünü söylüyor", async () => {
    const store = new RedisSessionStore(redis);
    redis.ready = false;

    await expect(store.get("abc")).rejects.toThrow(/okuma/);
    await expect(store.set("abc", record())).rejects.toThrow(/yazma/);
  });

  it("özgün hata `cause` olarak korunuyor", async () => {
    // Tanı için: "bağlantı sıfırlandı" ile "kimlik reddedildi" farklı sorunlar.
    const store = new RedisSessionStore(redis);
    redis.throwOnCall = true;

    const error = await store.get("abc").catch((e: unknown) => e);

    expect((error as Error).cause).toBeInstanceOf(Error);
  });
});

// --------------------------------------------------------- bozuk kayıt

describe("depodaki değer çözülemezse", () => {
  it("bozuk JSON kaydı yok sayılıyor ve siliniyor", async () => {
    const store = new RedisSessionStore(redis);
    redis.entries.set("bizigo:bff:session:abc", { value: "{bozuk", ttlSeconds: 60 });

    expect(await store.get("abc")).toBeUndefined();
    expect(redis.entries.has("bizigo:bff:session:abc")).toBe(false);
  });

  it("alanları eksik kayıt yok sayılıyor", async () => {
    // Sürüm yükseltmesi ya da aynı öneki kullanan başka bir yazılım. Çözemediğimiz
    // bir kaydı kullanmaya çalışmak, tanımsız davranış demek.
    const store = new RedisSessionStore(redis);
    redis.entries.set("bizigo:bff:session:abc", {
      value: JSON.stringify({ accessToken: "A" }),
      ttlSeconds: 60,
    });

    expect(await store.get("abc")).toBeUndefined();
  });
});
