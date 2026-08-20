import { SessionStoreUnavailableError, type SessionRecord, type SessionStore } from "./store";

/**
 * Paylaşılan oturum deposu (B7).
 *
 * <p>
 * T13'te bellek içi harita bilinçli bir borç olarak bırakılmıştı: tek süreçte
 * yeterli, çok kopyada değil. Arayüz o gün için ayrılmıştı ve bu dosya onun
 * karşılığı — <b>uygulama değişiyor, sözleşme değil</b>.
 * </p>
 *
 * <p>
 * Oturum çerezinin anlamı aynı kalıyor: tarayıcıya giden şey hâlâ <b>opak
 * rastgele bir anahtar</b>. Depoya taşınan tek şey o anahtarın arkasındaki
 * kayıt; token'lar yine tarayıcıya hiç ulaşmıyor.
 * </p>
 */

/**
 * İhtiyacımız olan Redis yüzeyi — <b>dört metot</b>.
 *
 * <p>
 * İstemci kütüphanesinin tamamına bağlanmak yerine bu dar arayüz duruyor, ve
 * sebebi test edilebilirlik: birim testleri sahte bir uygulamayla koşuyor,
 * konteyner gerekmiyor. Kütüphane değişirse değişecek tek yer adaptör.
 * </p>
 */
export interface RedisClient {
  get(key: string): Promise<string | null>;
  /** `ttlSeconds` **saniye**; Redis `SET key value EX ttl` karşılığı. */
  set(key: string, value: string, ttlSeconds: number): Promise<void>;
  delete(key: string): Promise<void>;
  /** Bağlantının canlı olup olmadığı. Sağlık için değil, <b>hata ayırt etmek</b> için. */
  isReady(): boolean;
}

/** Anahtar öneki: aynı Redis'i paylaşan başka bir şey varsa çarpışmasın. */
const KEY_PREFIX = "bizigo:bff:session:";

export class RedisSessionStore implements SessionStore {
  readonly #client: RedisClient;

  constructor(client: RedisClient) {
    this.#client = client;
  }

  /**
   * <p>
   * <b>Bulunamadı ile ulaşılamadı ayrı.</b> Bu ayrım bu sınıfın en önemli
   * özelliği: Redis düştüğünde <c>undefined</c> dönmek, herkesi <b>oturumsuz</b>
   * göstermek demek. Oturumsuz kullanıcı girişe yönlendirilir, giriş yeni bir
   * oturum yazmayı dener, o da düşer — ve kullanıcı hiçbir hata görmeden
   * <b>sonsuz yönlendirme döngüsüne</b> girer.
   * </p>
   *
   * <p>
   * Aynı ayrım <c>currentUser()</c>'ın üç durumlu olmasının da sebebiydi:
   * "oturum yok" ile "API cevap vermiyor" ayrılmazsa döngü oluşuyor. Depo
   * katmanında da aynı kural geçerli — bu yüzden ulaşılamama bir istisna,
   * bir <c>undefined</c> değil.
   * </p>
   */
  async get(id: string): Promise<SessionRecord | undefined> {
    const raw = await this.#guard(() => this.#client.get(KEY_PREFIX + id), "okuma");

    if (raw === null) {
      return undefined;
    }

    const record = parse(raw);

    if (!record) {
      // Bozuk ya da eski biçimli kayıt: silinip yok sayılıyor. Kullanıcı
      // yeniden giriş yapıyor — kabul edilebilir. Çözemediğimiz bir kaydı
      // kullanmaya çalışmak kabul edilemez.
      await this.delete(id);
      return undefined;
    }

    if (record.expiresAt <= Date.now()) {
      await this.delete(id);
      return undefined;
    }

    return record;
  }

  /**
   * <p>
   * <b>TTL tek yerden türüyor.</b> Redis'in <c>EXPIRE</c>'ı kaydın kendi
   * <c>expiresAt</c>'inden hesaplanıyor; o da giriş anında
   * <c>BFF_SESSION_TTL_SECONDS</c>'ten geliyor. İkinci bir ömür değeri
   * yazılsaydı ikisi ayrışırdı ve oturum ya erken ölürdü ya Redis'te sızıntı
   * olarak kalırdı — ikisi de sessiz.
   * </p>
   */
  async set(id: string, record: SessionRecord): Promise<void> {
    const ttl = Math.ceil((record.expiresAt - Date.now()) / 1000);

    if (ttl <= 0) {
      // Süresi çoktan geçmiş bir kaydı yazmak yerine siliyoruz: Redis negatif
      // TTL'i "süresiz" sayar ve kayıt sonsuza kadar kalırdı.
      await this.delete(id);
      return;
    }

    await this.#guard(
      () => this.#client.set(KEY_PREFIX + id, JSON.stringify(record), ttl),
      "yazma",
    );
  }

  async delete(id: string): Promise<void> {
    await this.#guard(() => this.#client.delete(KEY_PREFIX + id), "silme");
  }

  /**
   * Bellek içi depodaki tanılama sayacının Redis karşılığı yok: <c>KEYS</c>
   * taraması üretimde yasak sayılır. Sayı yalnızca testlerde anlamlıydı.
   */
  size(): number {
    return -1;
  }

  /** Her çağrıyı aynı hata sözleşmesine bağlıyor. */
  async #guard<T>(operation: () => Promise<T>, what: string): Promise<T> {
    if (!this.#client.isReady()) {
      throw new SessionStoreUnavailableError(`Oturum deposuna bağlanılamıyor (${what}).`);
    }

    try {
      return await operation();
    } catch (cause) {
      throw new SessionStoreUnavailableError(`Oturum deposu ${what} sırasında hata verdi.`, {
        cause,
      });
    }
  }
}

/**
 * Kaydı çözüyor. <b>Alan alan doğruluyor</b> çünkü depodaki bir dizgi
 * yapımızın tipini garanti etmiyor: sürüm yükseltmesi, elle müdahale ya da
 * aynı anahtar önekini kullanan başka bir yazılım.
 */
function parse(raw: string): SessionRecord | undefined {
  let value: unknown;

  try {
    value = JSON.parse(raw);
  } catch {
    return undefined;
  }

  if (typeof value !== "object" || value === null) {
    return undefined;
  }

  const candidate = value as Record<string, unknown>;

  if (
    typeof candidate.accessToken !== "string" ||
    typeof candidate.accessTokenExpiresAt !== "number" ||
    typeof candidate.expiresAt !== "number"
  ) {
    return undefined;
  }

  return {
    accessToken: candidate.accessToken,
    refreshToken: typeof candidate.refreshToken === "string" ? candidate.refreshToken : undefined,
    idToken: typeof candidate.idToken === "string" ? candidate.idToken : undefined,
    accessTokenExpiresAt: candidate.accessTokenExpiresAt,
    expiresAt: candidate.expiresAt,
  };
}
