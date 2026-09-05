/**
 * BFF'in yapılandırması. Hepsi **sunucu tarafı** ortam değişkeni — hiçbiri
 * `NEXT_PUBLIC_` önekli değil, dolayısıyla hiçbiri tarayıcı paketine girmiyor.
 *
 * Değerler ilk kullanımda okunuyor, modül yüklenirken değil: testler ortamı
 * kurup içeri aktarma sırasına bağlı kalmasın diye.
 */
export interface BffConfig {
  /**
   * Keycloak realm adresi — issuer. API'nin `Auth:Authority` değeriyle aynı
   * olmak zorunda.
   *
   * <p><b>Aynı zamanda tarayıcının gördüğü adres.</b> Token'ın `iss` alanı bu,
   * ve kullanıcı giriş için buraya yönlendiriliyor. İkisi tek değer çünkü
   * Keycloak'ın `KC_HOSTNAME`'i ikisini birden sabitliyor.</p>
   */
  readonly issuer: string;
  /**
   * Keşif belgesinin **indirileceği** adres.
   *
   * <p>
   * API'deki <c>Auth:MetadataAddress</c>'in birebir karşılığı ve aynı sebeple
   * var: container içinde <c>localhost:8180</c> Keycloak değil,
   * <b>container'ın kendisi</b>. Anahtarın <i>nereden indirileceği</i> ile
   * <i>kime güvenildiği</i> ayrı sorular; <see cref="issuer"/> ikincisini
   * cevaplıyor, bu birincisini.
   * </p>
   *
   * <p>
   * Verilmezse issuer'dan türüyor, yani makinede koşan kurulum bu alanın
   * varlığını hiç fark etmiyor — container'lı hâl varsayılanı değiştirmiyor.
   * </p>
   */
  readonly metadataUrl: string;
  readonly clientId: string;
  readonly clientSecret: string;
  /** Next uygulamasının dışarıdan görünen kökü. Yönlendirme adresleri buradan türüyor. */
  readonly publicUrl: string;
  /** `Bizigo.Api`'nin sunucudan sunucuya adresi. Tarayıcı buraya hiç konuşmuyor. */
  readonly apiBaseUrl: string;
  /** Oturum çerezinin adı. */
  readonly cookieName: string;
  /** Oturumun sunucudaki ömrü. **TTL'in tek kaynağı** — Redis `EXPIRE` bundan türüyor. */
  readonly sessionTtlSeconds: number;
  /**
   * Oturum deposu: `memory` (tek süreç) ya da `redis` (çok kopya).
   *
   * <p>Bellek içi hâl geliştirmenin varsayılanı ve kaldırılmadı: Redis zorunlu
   * olsaydı yerel ortam tek komutla ayağa kalkmazdı.</p>
   */
  readonly sessionStore: "memory" | "redis";
  /** `sessionStore === "redis"` iken zorunlu. */
  readonly redisUrl: string | undefined;
}

function required(name: string): string {
  const value = process.env[name];

  if (!value) {
    // Eksik yapılandırmayla sessizce çalışmak, giriş akışının ilk kullanıcıda
    // anlaşılmaz bir Keycloak hatasıyla düşmesi demek.
    throw new Error(`Ortam değişkeni tanımlı değil: ${name}`);
  }

  return value;
}

export function readBffConfig(): BffConfig {
  const publicUrl = (process.env.BFF_PUBLIC_URL ?? "http://localhost:3000").replace(/\/+$/, "");
  const issuer = (process.env.KEYCLOAK_ISSUER ?? "http://localhost:8180/realms/bizigo").replace(/\/+$/, "");

  return {
    issuer,
    metadataUrl:
      process.env.KEYCLOAK_METADATA_URL ?? `${issuer}/.well-known/openid-configuration`,
    clientId: process.env.KEYCLOAK_CLIENT_ID ?? "bizigo-ui",
    clientSecret: required("KEYCLOAK_CLIENT_SECRET"),
    publicUrl,
    apiBaseUrl: (process.env.BIZIGO_API_URL ?? "http://localhost:5080").replace(/\/+$/, ""),
    cookieName: process.env.BFF_COOKIE_NAME ?? "bizigo.sid",
    sessionTtlSeconds: Number(process.env.BFF_SESSION_TTL_SECONDS ?? 60 * 60 * 8),
    sessionStore: process.env.BFF_SESSION_STORE === "redis" ? "redis" : "memory",
    redisUrl: process.env.BFF_REDIS_URL,
  };
}

/**
 * Keycloak realm'inde `redirectUris` içinde **birebir** bu adres yazılı
 * (`deploy/keycloak/realm-bizigo.json`). Yol değişirse orası da değişmeli.
 */
export function redirectUri(config: BffConfig): string {
  return `${config.publicUrl}/signin-oidc`;
}

/**
 * Çerez `Secure` bayrağı, dışarıdan görünen adresin şemasından türüyor.
 *
 * <p>Sabit `true` yazmak yerel geliştirmeyi (düz HTTP) sessizce kırardı:
 * tarayıcı çerezi hiç saklamaz, giriş sonsuz döngüye girer ve hiçbir hata
 * mesajı çıkmaz. Sabit `false` yazmak ise üretimde oturum çerezini açık ağa
 * verirdi. Şemadan türetmek ikisini de engelliyor.</p>
 */
export function cookieSecure(config: BffConfig): boolean {
  return config.publicUrl.startsWith("https://");
}
