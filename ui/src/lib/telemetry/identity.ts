import { createHmac } from "node:crypto";

/**
 * Telemetrideki kullanıcı kimliği — **takma ad**, kimlik değil.
 *
 * <p>
 * Keycloak `sub`'ını ham hâlde göndermek, telemetri veritabanını kimlik
 * veritabanına <b>birleştirilebilir</b> yapardı: iki döküm yan yana konduğunda
 * hangi olayın hangi kişiye ait olduğu çıkar. E-posta göndermek zaten
 * doğrudan kişisel veri.
 * </p>
 *
 * <p>
 * HMAC-SHA256 + kuruluma özel tuz: aynı kullanıcı oturumlar arasında aynı
 * kimlikle sayılmayı sürdürüyor (analitiğin ihtiyacı olan tek şey bu), ama
 * özet geri çözülemiyor ve <b>iki farklı kurulumun</b> özetleri birbirine
 * bağlanamıyor.
 * </p>
 *
 * <p>
 * Düz `sha256(sub)` yetmezdi: `sub` bir UUID, yani arama uzayı sonlu değil ama
 * hedefli bir saldırgan için elindeki kullanıcı listesini tek tek özetleyip
 * eşleştirmek serbest olurdu. Tuz bunu kapatıyor.
 * </p>
 *
 * @param subject Keycloak `sub` iddiası.
 * @param salt `TELEMETRY_IDENTITY_SALT`. Eksikse telemetri hiç açılmıyor
 *   (bkz. `telemetryState`) — tuzsuz devam etmek ham `sub` göndermek olurdu.
 */
export function pseudonymousId(subject: string, salt: string): string {
  return createHmac("sha256", salt).update(subject).digest("hex").slice(0, 32);
}
