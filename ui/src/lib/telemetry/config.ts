/**
 * Telemetri yapılandırması. Hepsi **sunucu tarafı** ortam değişkeni.
 *
 * <p>
 * `NEXT_PUBLIC_` önekli tek bir değişken bile yok, ve bu bilinçli:
 * `next.config.ts` "tarayıcıya inen paketin içinde hiçbir sunucu sırrı
 * olmamalı" diyor. Proje anahtarı zaten <b>yazma anahtarı</b>, yani sır değil —
 * ama onu `NEXT_PUBLIC_` ile gömmek, yarın yanına konacak bir kişisel
 * erişim token'ı için açılmış bir kapı olurdu. Anahtar sunucu bileşeninde
 * okunup istemci bileşenine <b>prop olarak</b> geçiyor; kapı hiç açılmıyor.
 * </p>
 *
 * <p>
 * Değerler ilk kullanımda okunuyor, modül yüklenirken değil — `auth/config.ts`
 * ile aynı gerekçe: testler ortamı kurup içeri aktarma sırasına bağlı kalmasın.
 * </p>
 */
export interface TelemetryConfig {
  /**
   * Telemetri açık mı. **Varsayılan kapalı**, ve bu pazarlığa açık değil.
   *
   * <p>Bu ürün müşterinin log'unu okuyor. Kurulumun kendiliğinden dışarı
   * konuşmaya başlaması, ürünü değerlendiren bir güvenlik ekibinin ilk
   * gününde bulacağı ve bir daha kapatamayacağı bir şey. Açmak <b>bilinçli
   * bir hareket</b> olmak zorunda.</p>
   */
  readonly enabled: boolean;
  /** PostHog proje (yazma) anahtarı — `phc_…`. */
  readonly projectKey: string | undefined;
  /**
   * Olayların gideceği PostHog adresi. Tarayıcı buraya **hiç** konuşmuyor;
   * yalnızca Next sunucusu (`/api/telemetry` vekili).
   */
  readonly host: string;
  /**
   * PostHog'un statik varlık adresi. Bulut kurulumunda ayrı bir alan adı,
   * self-host'ta çoğu zaman `host` ile aynı.
   */
  readonly assetHost: string;
  /**
   * Ekranlardaki "PostHog'da aç" bağlantılarının kökü. Vekil adresi değil,
   * insanın tarayıcıda açacağı adres.
   */
  readonly uiHost: string;
  /**
   * Olaylar oturum açmış kullanıcıya <b>bağlansın mı</b>.
   *
   * <p>Varsayılan <b>kapalı</b>. Kapalıyken posthog-js'in kendi ürettiği
   * rastgele tarayıcı kimliği kullanılıyor: "kaç farklı tarayıcı bu ekranı
   * açtı" sorusu cevaplanabiliyor, "hangi kullanıcı" sorusu
   * cevaplanamıyor — ve çoğu ürün kararı için ilki yetiyor.</p>
   *
   * <p>Açıldığında bile giden şey ham `sub` değil, tuzlanmış özeti
   * (`identity.ts`). Açmak, cevabı takma adla da olsa kişiye bağlamak
   * demek; o yüzden ayrı bir anahtar ve ayrı bir karar.</p>
   */
  readonly identifyUsers: boolean;
  /**
   * `distinct_id` üretilirken kullanılan tuz. Yalnızca `identifyUsers` açıkken
   * anlamlı, ve o hâlde <b>zorunlu</b>.
   *
   * <p>Keycloak `sub`'ı ham hâlde göndermek, telemetri veritabanını kimlik
   * veritabanına <b>birleştirilebilir</b> yapardı. Tuzlanmış özet, aynı
   * kullanıcıyı oturumlar arasında saymayı sürdürüyor ama geri
   * çözülemiyor.</p>
   */
  readonly identitySalt: string | undefined;
  /** Olaylara eklenen dağıtım etiketi — `dev`, `stage`, `prod`. */
  readonly environment: string;
}

/**
 * Yapılandırmanın **üç durumu** var, iki değil.
 *
 * <p>"Kapalı" ile "açık ama eksik yapılandırılmış" ayrılmak zorunda:
 * ikisini birden sessizce kapalıya düşürmek, telemetriyi açtığını sanan bir
 * yöneticinin haftalarca boş bir panoya bakması demek. `currentUser`'daki
 * üçüncü durumun aynı gerekçesi.</p>
 */
export type TelemetryState =
  | { readonly status: "disabled" }
  | { readonly status: "misconfigured"; readonly missing: readonly string[] }
  | { readonly status: "ok"; readonly config: TelemetryConfig };

function trimSlash(value: string): string {
  return value.replace(/\/+$/, "");
}

/**
 * Bir ortam değişkeni "açık" mı.
 *
 * <p>
 * Kapalıya düşen her değer: boş, `false`, `0`, `off`, ve yazım hatası taşıyan
 * her şey. Yalnızca açık bir `true`/`1`/`on`/`yes` açıyor — hatalı bir değerin
 * telemetriyi <b>AÇMASI</b> yanlış yöndeki hata olurdu.
 * </p>
 *
 * <p>
 * <c>toLowerCase()</c> KULLANILMIYOR, bilerek: Türkçe yerelde `I` → `ı`
 * dönüyor ve `"TRUE"` değeri `"trıe"` olup eşleşmeyi kaçırırdı. Düzenli
 * ifadenin `i` bayrağı ASCII üstünde yerelden bağımsız çalışıyor
 * (<c>ui-consistency</c> bekçisi bu sınıfı denetliyor).
 * </p>
 */
function isTruthy(value: string | undefined): boolean {
  return /^(true|1|on|yes)$/i.test((value ?? "").trim());
}

export function readTelemetryConfig(): TelemetryConfig {
  const host = trimSlash(process.env.TELEMETRY_HOST ?? "https://eu.i.posthog.com");

  return {
    enabled: isTruthy(process.env.TELEMETRY_ENABLED),
    projectKey: process.env.TELEMETRY_PROJECT_KEY?.trim() || undefined,
    identifyUsers: isTruthy(process.env.TELEMETRY_IDENTIFY_USERS),
    host,
    assetHost: trimSlash(process.env.TELEMETRY_ASSET_HOST ?? host),
    uiHost: trimSlash(process.env.TELEMETRY_UI_HOST ?? "https://eu.posthog.com"),
    identitySalt: process.env.TELEMETRY_IDENTITY_SALT?.trim() || undefined,
    environment: process.env.TELEMETRY_ENVIRONMENT ?? "dev",
  };
}

export function telemetryState(config: TelemetryConfig = readTelemetryConfig()): TelemetryState {
  if (!config.enabled) {
    return { status: "disabled" };
  }

  const missing: string[] = [];

  if (!config.projectKey) {
    missing.push("TELEMETRY_PROJECT_KEY");
  }

  // Tuz yalnızca kimlik bağlama AÇIKKEN zorunlu — ama o hâlde gerçekten
  // zorunlu: tuzsuz devam etmek ham `sub`'ı göndermek olurdu ve bu, kimsenin
  // vermediği bir karar olurdu. "Açık ama tuzsuz" hâli sessizce anonime
  // düşmüyor, yapılandırma hatası olarak duruyor.
  if (config.identifyUsers && !config.identitySalt) {
    missing.push("TELEMETRY_IDENTITY_SALT");
  }

  return missing.length > 0 ? { status: "misconfigured", missing } : { status: "ok", config };
}
