import { createClient, type RedisClientType } from "redis";

import type { RedisClient } from "./redis-store";

/**
 * `node-redis` adaptörü — <c>RedisClient</c>'ın tek gerçek uygulaması.
 *
 * <p>
 * Kütüphaneye bağlanan tek dosya burası. Depo mantığı (<c>redis-store.ts</c>)
 * dört metotluk dar arayüzü görüyor, dolayısıyla birim testleri kütüphaneye de
 * konteynere de dokunmadan koşuyor.
 * </p>
 *
 * <p>
 * <b>Bağlantı arka planda kuruluyor ve hatası yutulmuyor.</b> `node-redis`
 * yeniden bağlanmayı kendisi deniyor; bizim işimiz, o denerken gelen isteklere
 * <b>yanlış cevap vermemek</b>. Hazır olmayan istemci
 * <c>SessionStoreUnavailableError</c> üretiyor ve bu, "oturum yok"tan farklı
 * bir yola gidiyor.
 * </p>
 */

/**
 * Yeniden bağlanma bekleme süresi. Üst sınır var çünkü sınırsız üstel geri
 * çekilme, Redis geri geldikten sonra dakikalarca ölü kalmak demek.
 */
const RECONNECT_CEILING_MS = 5_000;

export function createRedisClient(url: string): RedisClient {
  const client: RedisClientType = createClient({
    url,
    socket: {
      reconnectStrategy: (retries) => Math.min(retries * 200, RECONNECT_CEILING_MS),
    },
  });

  // Dinleyici olmadan `error` olayı Node'da sürecin tamamını düşürüyor.
  // Sessizce yutmuyoruz da: hatanın kendisi `isReady()` üzerinden zaten
  // görünür hâle geliyor, ama günlüğe düşmesi tanı için gerekiyor.
  client.on("error", (error: unknown) => {
    console.error("[bff] Redis bağlantı hatası:", error);
  });

  // Bağlantı arka planda kuruluyor: modül yüklenirken beklemek, Redis kapalıyken
  // uygulamanın hiç açılmaması demek olurdu. Açılsın, ve isteğe düzgün hata
  // versin.
  void client.connect().catch((error: unknown) => {
    console.error("[bff] Redis'e ilk bağlantı kurulamadı:", error);
  });

  return {
    async get(key) {
      return client.get(key);
    },
    async set(key, value, ttlSeconds) {
      await client.set(key, value, { expiration: { type: "EX", value: ttlSeconds } });
    },
    async delete(key) {
      await client.del(key);
    },
    isReady() {
      return client.isReady;
    },
    async close() {
      // `destroy()` değil `close()`: bekleyen komutların bitmesine izin veriyor.
      // Kapalı bir istemciyi tekrar kapatmak hata veriyor, o yüzden korumalı.
      if (client.isOpen) {
        await client.close();
      }
    },
  };
}
