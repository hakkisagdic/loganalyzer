import { readBffConfig, type BffConfig } from "@/lib/auth/config";
import { discover } from "@/lib/auth/oidc";
import { sessionStore, type SessionStoreReadiness } from "@/lib/auth/store";

/**
 * <b>BFF'in hazırlık ucu</b> (T62) — ve var olma sebebi ölçülmüş bir
 * sessizlik.
 *
 * <h3>Neden var: `--wait` yeşil dönüyordu</h3>
 *
 * <p>
 * T49'un container sağlık kontrolü kök sayfayı yokluyordu ve ölçütü
 * <c>&lt; 500</c>'dü. Ölçülen kod yolu şu: sonda <b>çerez taşımıyor</b>, yani
 * <c>resolveSession</c> oturum deposuna <b>hiç gitmeden</b> dönüyor
 * (<c>session.ts</c>), kimlik anonim oluyor ve kök sayfa 307 ile giriş akışına
 * yönlendiriyor. 307 &lt; 500, yani sağlık kontrolü <b>yeşil</b>.
 * </p>
 *
 * <p>
 * Sonuç: <c>redis-session</c> tamamen kırıkken de <c>ui</c> servisi
 * <b>healthy</b> görünüyor, <c>docker compose up -d --wait</c> yeşil dönüyor —
 * yani yığın <i>"kalktı"</i> diyor — ve arıza <b>ilk giriş denemesinde</b>
 * çıkıyor. Bir kapının varlığı, neye baktığını söylemiyor.
 * </p>
 *
 * <h3>Hazırlık, canlılık DEĞİL — ve gerekçesi ölçüldü</h3>
 *
 * <p>
 * İki ayrı soru var: <i>süreç ayakta mı</i> (canlılık) ve <i>bağımlılıkları
 * hazır mı</i> (hazırlık). Bu uç <b>ikincisini</b> cevaplıyor, çünkü tüketicisi
 * <c>compose</c> sağlık kontrolü ve onun beslediği <c>--wait</c>'in verdiği söz
 * <i>"yığın kullanılabilir"</i>.
 * </p>
 *
 * <p>
 * <b>Canlılık ucu yazılmadı ve sebebi tüketicisizlik:</b> <c>deploy/docker-compose.yml</c>
 * içinde <c>ui</c> servisinin <b>hiçbir <c>restart</c> politikası yok</b>
 * (ölçüldü — dosyadaki iki <c>restart</c> satırı başka servislere ait) ve
 * ortada bir orkestratör de yok. Yani "beni yeniden başlat" cevabını okuyacak
 * kimse yok; §8'in <i>tüketicisi olmayan bir tip tahmindir</i> kuralı.
 * </p>
 *
 * <p>
 * <b>Kilitlenme riski ölçüldü ve yok:</b> <c>ui</c> servisi
 * <c>api</c> · <c>keycloak</c> · <c>redis-session</c> üçünü de
 * <c>service_healthy</c> ile bekliyor, yani bu uç koştuğunda üçü zaten hazır.
 * Ters yönde bekleyen bir servis de yok, dolayısıyla bağımlılıkları yoklamak
 * kalkış sırasını kilitleyemiyor. <b>Kalan bedel</b> yazılı: bağımlılıklardan
 * biri çalışırken düşerse <c>ui</c> <c>unhealthy</c> oluyor — ve bugün bunu
 * okuyan bir yeniden başlatma politikası olmadığı için sonucu, gerçeği
 * yansıtan kırmızı bir sağlık kontrolü.
 * </p>
 *
 * <h3>Yükte ne YOK — ve bu bir güvenlik kararı</h3>
 *
 * <p>
 * Uç <b>kimlik doğrulaması istemiyor</b> ve istemesi imkânsız: onu çağıran şey
 * container'ın sağlık kontrolü. Ama <c>ui</c> servisi 3000'i <b>dışarıya</b>
 * veriyor, yani bu yük kuruma dışarıdan da görünebilir. O yüzden yük
 * <b>durum</b> taşıyor, <b>topoloji taşımıyor</b>: adres yok, ana makine adı
 * yok, hata metni yok, sürüm yok. Kalıp telemetri süzgecinin aynısı
 * (<c>ui/src/lib/telemetry</c>): sayılabilir durumlar gidiyor, serbest metin
 * gitmiyor.
 * </p>
 */

/** Tek bir bağımlılığın hâli. İki değer, çünkü üçüncüsü karar gerektirirdi. */
export type ReadinessState = "ready" | "unreachable";

export interface ReadinessCheck {
  /** Sabit ad — istemci buna göre okuyor. */
  readonly name: "session_store" | "api" | "identity_provider";
  readonly state: ReadinessState;
  /**
   * Yalnızca <c>session_store</c> için: <c>memory</c> ya da <c>redis</c>.
   * <b>Hangi</b> deponun yoklandığını söylemek zorunlu — "erişilemiyor" tek
   * başına yerel geliştirmede yanlış okunur.
   */
  readonly kind?: SessionStoreReadiness["kind"];
}

export interface ReadinessReport {
  readonly ready: boolean;
  readonly checks: readonly ReadinessCheck[];
}

/**
 * Yapılandırma okunamadığında dönen rapor.
 *
 * <p>
 * <b>Hangi değişkenin eksik olduğu YAZILMIYOR</b> — kimliksiz bir uçta eksik
 * ortam değişkenlerinin adını saymak, kurulumun şeklini dışarıya anlatmak
 * olurdu. Gerçek hata sunucu günlüğüne düşüyor; orayı okuyan operatör.
 * </p>
 */
const CONFIGURATION_INVALID: ReadinessReport = {
  ready: false,
  checks: [
    { name: "session_store", state: "unreachable" },
    { name: "api", state: "unreachable" },
    { name: "identity_provider", state: "unreachable" },
  ],
};

/**
 * Her yoklamanın kendi zaman aşımı.
 *
 * <p>
 * Container sağlık kontrolünün <c>timeout</c>'u 5 sn ve yoklamalar
 * <b>paralel</b> koşuyor, yani toplam bütçe ≈ bu değer. Zaman aşımı olmasa
 * cevabı hiç dönmeyen bir bağımlılık sağlık kontrolünü kendi süresinde
 * düşürürdü ve çıktı <b>hangi bağımlılığın</b> düştüğünü söylemezdi — yani
 * bu ucun kapattığı sessizliğin yerine ikinci bir sessizlik gelirdi.
 * </p>
 */
const PROBE_TIMEOUT_MS = 2_000;

export interface ReadinessProbes {
  sessionStore(): Promise<SessionStoreReadiness>;
  api(): Promise<boolean>;
  identityProvider(): Promise<boolean>;
}

/**
 * Üretimin yoklamaları. Ayrı bir tip olmasının sebebi testte değiştirilebilir
 * olmaları: kırık bir bağımlılığı ölçmek için onu <b>kırabilmek</b> gerekiyor
 * ve konteynerle kırmak bu paketin işi değil (§2).
 */
export function defaultProbes(config: BffConfig): ReadinessProbes {
  return {
    sessionStore: () => sessionStore().probe(),

    // `/healthz` — API'nin kendi sağlık ucu (`Program.cs`).
    // Sunucudan sunucuya: tarayıcı bu adrese hiç konuşmuyor.
    api: () => reachable(`${config.apiBaseUrl}/healthz`),

    // Keşif belgesi. Giriş akışının ilk adımı bu belgeye bağlı; inmiyorsa
    // ekran açılıyor ama kimse giriş yapamıyor.
    //
    // `discover()` çağrılıyor, ham `fetch` DEĞİL: ölçülmesi gereken şey
    // belgenin inip inmediği değil, ÜRÜNÜN kullandığı yolun çalıştığı —
    // container'da o yol arka kanal adresini kullanıyor (T49) ve ham bir
    // `fetch` o ayrımı atlardı.
    identityProvider: async () => {
      try {
        await discover(config);
        return true;
      } catch {
        return false;
      }
    },
  };
}

async function reachable(url: string): Promise<boolean> {
  try {
    const response = await fetch(url, {
      signal: AbortSignal.timeout(PROBE_TIMEOUT_MS),
      cache: "no-store",
    });

    return response.ok;
  } catch {
    return false;
  }
}

/**
 * Hazırlık raporunu kurar. <b>Fırlatmıyor:</b> bir bağımlılığın kırık olması
 * bu ucun hata hâli değil, cevabının kendisi.
 */
export async function readiness(probes?: ReadinessProbes): Promise<ReadinessReport> {
  let resolved = probes;

  if (!resolved) {
    let config: BffConfig;

    try {
      config = readBffConfig();
    } catch (error) {
      // Sunucu günlüğüne düşüyor; yüke girmiyor.
      console.error("[bff] Hazırlık ucu yapılandırmayı okuyamadı:", error);
      return CONFIGURATION_INVALID;
    }

    resolved = defaultProbes(config);
  }

  // PARALEL ve `allSettled`: biri fırlatırsa diğerlerinin cevabı kaybolmamalı.
  // Sıralı koşsalardı toplam süre üç zaman aşımının toplamı olurdu ve sağlık
  // kontrolü kendi süresinde düşerdi.
  const [store, api, idp] = await Promise.allSettled([
    withTimeout(resolved.sessionStore()),
    withTimeout(resolved.api()),
    withTimeout(resolved.identityProvider()),
  ]);

  const storeReadiness: SessionStoreReadiness =
    store.status === "fulfilled" && store.value
      ? store.value
      : // Yoklama fırlattıysa depo hakkında hiçbir şey bilinmiyor. `kind`
        // yapılandırmadan okunabilirdi ama okunmuyor: yoklamanın kendisi
        // düştüyse hangi depo olduğu da bir tahmindir.
        { kind: "memory", reachable: false };

  const checks: ReadinessCheck[] = [
    {
      name: "session_store",
      state: storeReadiness.reachable ? "ready" : "unreachable",
      kind: storeReadiness.kind,
    },
    { name: "api", state: settled(api) ? "ready" : "unreachable" },
    { name: "identity_provider", state: settled(idp) ? "ready" : "unreachable" },
  ];

  return {
    ready: checks.every((check) => check.state === "ready"),
    checks,
  };
}

function settled(result: PromiseSettledResult<boolean | undefined>): boolean {
  return result.status === "fulfilled" && result.value === true;
}

/**
 * Yoklamanın kendi zaman aşımı. <c>AbortSignal.timeout</c> yalnızca
 * <c>fetch</c>'i kesiyor; oturum deposunun beklemesi gibi ağ dışı bir gecikmeyi
 * kesen şey bu sarmalayıcı.
 */
async function withTimeout<T>(work: Promise<T>): Promise<T | undefined> {
  let timer: ReturnType<typeof setTimeout> | undefined;

  const guard = new Promise<undefined>((resolve) => {
    timer = setTimeout(() => resolve(undefined), PROBE_TIMEOUT_MS);
    // Süreci ayakta TUTMAMALI (protokol §3): kapanırken bekleyen bir
    // zamanlayıcı yüzünden asılı kalan bir proses.
    timer.unref?.();
  });

  try {
    return await Promise.race([work, guard]);
  } finally {
    clearTimeout(timer);
  }
}
