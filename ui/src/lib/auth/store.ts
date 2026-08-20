/**
 * Sunucu tarafı oturum deposu.
 *
 * <p>
 * <b>BFF deseninin taşıyıcı parçası burası.</b> Erişim ve yenileme token'ları
 * yalnızca bu depoda duruyor; tarayıcıya giden çerez sadece rastgele bir
 * anahtar taşıyor. Çerezin içine şifrelenmiş de olsa token koymak, token'ı
 * tarayıcıya vermek demektir — şifreleme onu ağdan ve disk yedeğinden
 * silmez.
 * </p>
 *
 * <p>
 * <b>İki uygulama var</b> ve seçim yapılandırmadan geliyor: bellek içi harita
 * (tek süreç, geliştirme) ve Redis (çok kopya, B7). Arayüz T13'te tam bu gün
 * için ayrılmıştı — uygulama değişti, sözleşme değil.
 * </p>
 *
 * <p>
 * Bellek içi hâl <b>kaldırılmadı</b> ve bu bilinçli: geliştirmede Redis zorunlu
 * olsaydı, kimsenin tek komutla çalıştıramadığı bir yerel ortam doğardı.
 * </p>
 */

/**
 * Depoya <b>ulaşılamıyor</b> — "oturum yok" ile karıştırılmaması gereken hâl.
 *
 * <p>
 * Ayrı bir tip olmasının sebebi tek bir senaryo: Redis düştüğünde
 * <c>get()</c> <c>undefined</c> dönseydi kullanıcı <b>oturumsuz</b> görünürdü,
 * girişe yönlendirilirdi, giriş yeni oturumu yazmayı denerdi, o da düşerdi —
 * ve kimse hiçbir hata görmeden <b>sonsuz döngüye</b> girerdi.
 * </p>
 *
 * <p>
 * "Herkes çıkış yapmış olur" kabul edilebilir bir sonuç; "sessizce oturumsuz
 * görünür" değil. Bu tip, ikisini ayıran şey.
 * </p>
 */
export class SessionStoreUnavailableError extends Error {
  override name = "SessionStoreUnavailableError";
}
export interface SessionRecord {
  readonly accessToken: string;
  readonly refreshToken: string | undefined;
  readonly idToken: string | undefined;
  /** Erişim token'ının bitiş anı (epoch ms). Yenileme kararı buna bakıyor. */
  readonly accessTokenExpiresAt: number;
  /** Oturumun kendi bitiş anı (epoch ms). Yenileme token'ı olsa bile aşılamıyor. */
  readonly expiresAt: number;
}

export interface SessionStore {
  get(id: string): Promise<SessionRecord | undefined>;
  set(id: string, record: SessionRecord): Promise<void>;
  delete(id: string): Promise<void>;
  /** Yalnızca testler ve tanılama için. */
  size(): number;
}

class InMemorySessionStore implements SessionStore {
  readonly #records = new Map<string, SessionRecord>();

  async get(id: string): Promise<SessionRecord | undefined> {
    const record = this.#records.get(id);

    if (!record) {
      return undefined;
    }

    if (record.expiresAt <= Date.now()) {
      // Süresi dolmuş kaydı okurken siliyoruz: ayrı bir temizlik görevi
      // kurmadan haritanın süresiz büyümesini engelliyor.
      this.#records.delete(id);
      return undefined;
    }

    return record;
  }

  async set(id: string, record: SessionRecord): Promise<void> {
    this.#records.set(id, record);
  }

  async delete(id: string): Promise<void> {
    this.#records.delete(id);
  }

  size(): number {
    return this.#records.size;
  }
}

/**
 * Modül düzeyinde tek örnek. Next geliştirme kipinde modülleri yeniden
 * yüklediği için `globalThis` üzerinde tutuluyor — yoksa her sıcak yeniden
 * yüklemede herkesin oturumu düşer ve sebebi "Keycloak bozuldu" sanılır.
 */
const globalKey = Symbol.for("bizigo.bff.sessionStore");

type StoreHolder = { [globalKey]?: SessionStore };

export function sessionStore(): SessionStore {
  const holder = globalThis as StoreHolder;
  return (holder[globalKey] ??= createConfiguredStore());
}

/**
 * Depoyu yapılandırmadan seçiyor.
 *
 * <p>
 * Redis modülü <b>tembel</b> yükleniyor: bellek içi kipte `redis` paketi hiç
 * içeri alınmıyor, dolayısıyla onu kurmayan bir geliştirme ortamı da çalışıyor.
 * </p>
 */
function createConfiguredStore(): SessionStore {
  const kind = process.env.BFF_SESSION_STORE ?? "memory";

  if (kind !== "redis") {
    return new InMemorySessionStore();
  }

  const url = process.env.BFF_REDIS_URL;

  if (!url) {
    // Sessizce belleğe düşmek, üretimde tek kopyalık bir depoyla çalıştığını
    // fark etmemek demek: ikinci kopya açıldığı gün oturumlar rastgele
    // düşmeye başlar ve sebebi Keycloak sanılır.
    throw new Error(
      "BFF_SESSION_STORE=redis ama BFF_REDIS_URL tanımlı değil. " +
        "Adres olmadan Redis deposu kurulamaz; sessizce belleğe düşmek, " +
        "çok kopyalı bir kurulumda oturumların rastgele düşmesi demek.",
    );
  }

  // Yalnızca bu dalda içeri alınıyor.
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const { createRedisClient } = require("./redis-client") as typeof import("./redis-client");
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const { RedisSessionStore } = require("./redis-store") as typeof import("./redis-store");

  return new RedisSessionStore(createRedisClient(url));
}

/** Testlerin kendi deposunu takabilmesi için. */
export function useSessionStore(store: SessionStore): void {
  (globalThis as StoreHolder)[globalKey] = store;
}

/**
 * Başlatılmış ama henüz tamamlanmamış bir giriş.
 *
 * <p>PKCE doğrulayıcısı ve nonce da <b>sunucuda</b> duruyor. Tarayıcıya giden
 * tek şey, bu kaydın anahtarı olan `state`. Doğrulayıcıyı çereze koymak PKCE'yi
 * anlamsızlaştırırdı: saldırgan yetkilendirme kodunu çalabildiği senaryoda
 * çerezi de çalabilir.</p>
 */
export interface LoginAttempt {
  readonly codeVerifier: string;
  readonly nonce: string;
  /** Giriş sonrası dönülecek uygulama içi yol. Açık yönlendirme için filtreleniyor. */
  readonly returnTo: string;
  readonly expiresAt: number;
}

const attemptKey = Symbol.for("bizigo.bff.loginAttempts");

type AttemptHolder = { [attemptKey]?: Map<string, LoginAttempt> };

function attempts(): Map<string, LoginAttempt> {
  const holder = globalThis as AttemptHolder;
  return (holder[attemptKey] ??= new Map());
}

export function rememberLoginAttempt(state: string, attempt: LoginAttempt): void {
  attempts().set(state, attempt);
}

/**
 * Kaydı okuyup <b>siliyor</b>: bir `state` yalnızca bir kez kullanılabilir.
 * Tekrar kullanılabilseydi yetkilendirme kodunun tekrar oynatılmasına
 * (replay) kapı açardı.
 */
export function consumeLoginAttempt(state: string): LoginAttempt | undefined {
  const map = attempts();
  const attempt = map.get(state);
  map.delete(state);

  if (!attempt || attempt.expiresAt <= Date.now()) {
    return undefined;
  }

  // Yarım kalan girişler birikmesin: her tüketimde süresi geçenleri at.
  const now = Date.now();
  for (const [key, value] of map) {
    if (value.expiresAt <= now) {
      map.delete(key);
    }
  }

  return attempt;
}

/**
 * Kaydı <b>silmeden</b> okuyor. Yalnızca testler için: sahte Keycloak'ın
 * doğru `nonce` ile `id_token` üretebilmesi gerekiyor ve nonce sunucuda.
 */
export function peekLoginAttempt(state: string): LoginAttempt | undefined {
  return attempts().get(state);
}

/** Testlerin birbirinden yalıtılması için. */
export function resetSessionStore(): void {
  const holder = globalThis as StoreHolder & AttemptHolder;
  holder[globalKey] = new InMemorySessionStore();
  holder[attemptKey] = new Map();
}
