import { afterAll, beforeAll, describe, expect, it } from "vitest";

import { RedisSessionStore, type RedisClient } from "@/lib/auth/redis-store";
import { createRedisClient } from "@/lib/auth/redis-client";
import type { SessionRecord } from "@/lib/auth/store";

/**
 * **Gerçek Redis'e karşı** oturum deposu (B7).
 *
 * <p>
 * <b>Bu dosya varsayılan koşumda atlanıyor</b> ve sebebi yazılı: ajanlar
 * Docker'a dokunmuyor (protokol §2), makine 16 GB ve paralel konteyner koşumu
 * onu swap'e sürüklüyor. Testi <b>yazmak</b> ajanın işi, <b>koşturmak</b>
 * koordinatörün.
 * </p>
 *
 * <p>
 * Atlama <b>sessiz değil</b>: koşum çıktısında "skipped" olarak görünüyor ve
 * neden atlandığı burada duruyor. Sessizce atlayan bir bekçi, bekçinin
 * kendisinden tehlikeli — bu depoda o desenin bedeli üç kez ödendi.
 * </p>
 *
 * <h3>Koşturmak için</h3>
 *
 * <pre>
 * docker compose -f deploy/docker-compose.yml up -d redis-session
 * BFF_REDIS_URL=redis://localhost:6379 npx vitest run tests/redis-live.test.ts
 * </pre>
 *
 * <p>
 * Servis adı <b>`redis-session`</b>, düz `redis` değil: compose'da iki Redis
 * örneği var ve `redis` sidecar'ın Drain3 durumunu tutuyor — kalıcılığı açık.
 * Oturum deposunu oraya bağlamak token'ları diske yazmak olurdu.
 * </p>
 *
 * <p>
 * <c>describe.skip</c> yerine adres değişkenine bakmıyor olmamız bilinçli:
 * değişkene bağlansaydı, değişken unutulduğunda test <b>sessizce</b> hiç
 * koşmazdı ve yeşil görünürdü.
 * </p>
 *
 * <h3>Koşturulduğunda ne kanıtlıyor</h3>
 *
 * <p>
 * Sahte istemcinin kanıtlayamadığı tek şey: <b>Redis'in kendi <c>EXPIRE</c>'ı
 * gerçekten uyguluyor mu.</b> Sahte, TTL'i saklıyor ama uygulamıyor —
 * dolayısıyla "kayıt süresi dolunca gerçekten kayboluyor mu" sorusu ancak
 * burada cevaplanıyor. İkinci kanıt: iki ayrı istemci örneği (çok kopyalı
 * kurulumun karşılığı) aynı oturumu görüyor.
 * </p>
 */

const URL = process.env.BFF_REDIS_URL ?? "redis://localhost:6379";

describe.skip("gerçek Redis (koordinatör koşturur)", () => {
  let store: RedisSessionStore;
  let second: RedisSessionStore;
  let clients: RedisClient[];

  beforeAll(() => {
    // İKİ bağlantı: uygulamanın iki kopyası. Paylaşılan depoya geçmenin bütün
    // sebebi bu — birinin yazdığını öbürü görmeli.
    clients = [createRedisClient(URL), createRedisClient(URL)];
    store = new RedisSessionStore(clients[0]!);
    second = new RedisSessionStore(clients[1]!);
  });

  afterAll(async () => {
    // Protokol §3: açtığın bağlantıyı kapat — hata alsan bile. `Promise.all`
    // yerine tek tek: biri patlarsa öbürü yine kapanmalı, yoksa koşum bitince
    // asılı bir soket kalır.
    for (const client of clients) {
      await client.close().catch(() => undefined);
    }
  });

  function record(ttlMs: number): SessionRecord {
    return {
      accessToken: "ACCESS-CANLI",
      refreshToken: "REFRESH-CANLI",
      idToken: undefined,
      accessTokenExpiresAt: Date.now() + ttlMs,
      expiresAt: Date.now() + ttlMs,
    };
  }

  it("bir kopyanın yazdığını öbür kopya okuyor", async () => {
    await store.set("paylasilan", record(60_000));

    expect(await second.get("paylasilan")).toMatchObject({ accessToken: "ACCESS-CANLI" });
  });

  it("`EXPIRE` gerçekten uygulanıyor", async () => {
    // Sahte istemcinin kanıtlayamadığı tek şey bu: TTL'i saklıyor ama
    // uygulamıyor. Kaydın Redis tarafından SİLİNDİĞİ ancak burada görülüyor.
    await store.set("kisa", record(1_000));

    await new Promise((resolve) => setTimeout(resolve, 1_500));

    expect(await second.get("kisa")).toBeUndefined();
  });

  it("silinen oturum iki kopyada da yok", async () => {
    await store.set("silinecek", record(60_000));
    await store.delete("silinecek");

    expect(await second.get("silinecek")).toBeUndefined();
  });
});
