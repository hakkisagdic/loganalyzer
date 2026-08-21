/**
 * Telemetriye giren her değerin geçtiği süzgeç.
 *
 * <p>
 * Bu modülün varlık sebebi ürünün ne olduğu: Bizigo <b>müşterinin log'unu</b>
 * okuyor. Bir arama kutusuna yazılan metin bir IP, bir kullanıcı adı, bir
 * oturum kimliği ya da bir sır olabilir; bir URL'deki kimlik müşterinin
 * kaynak envanterinden bir satır. Ürün analitiği aracı bunların hiçbirini
 * görmemeli, ve "görmemeli"nin tek güvenilir hâli <b>beyaz liste</b>.
 * </p>
 *
 * <p>
 * `api/proxy.ts`'deki başlık listeleriyle aynı gerekçe: kara liste, "yarın
 * eklenen alan sızar" demek. Beyaz liste, yeni alan eklemeyi <b>bilinçli bir
 * hareket</b> yapıyor.
 * </p>
 *
 * <p>
 * Buradaki her şey <b>saf fonksiyon</b>. posthog-js'in kendi
 * <c>sanitize_properties</c> kancası da bağlı ama ona <b>güvenilmiyor</b>:
 * bir kanca adının sürüm yükseltmesinde değişmesi, süzgecin sessizce devre
 * dışı kalması demek — ve sessiz yanlış davranış bu depodaki en pahalı hata
 * sınıfı (CLAUDE.md §7). Saf fonksiyon test edilebiliyor; kanca ediliyorsa
 * ikinci savunma hattı.
 * </p>
 */

/** Bir yol parçasının kimlik olup olmadığı. */
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const HEX = /^[0-9a-f]{12,}$/i;
const DIGITS = /^\d+$/;

/**
 * Bir yol parçası kimlik mi?
 *
 * <p>Üç şekil: UUID, uzun onaltılık, ve saf sayı. Dördüncü bir şekil çıkarsa
 * buraya eklenir — ve eklenmediği sürece o kimlik <b>ham hâlde</b> gider.
 * Bunun bilinerek duruyor olması için `scrubPathname` çıktısındaki her
 * parçanın testi var.</p>
 */
function isIdentifier(segment: string): boolean {
  return UUID.test(segment) || HEX.test(segment) || DIGITS.test(segment);
}

/**
 * Yolu **kalıbına** indiriyor: `/kaynaklar/9f2c…/olaylar` → `/kaynaklar/:id/olaylar`.
 *
 * <p>Ham yol gönderilseydi PostHog'daki sayfa listesi müşterinin kaynak
 * kimliklerinin envanteri olurdu — ve hiç kimse o envanteri oraya koymaya
 * karar vermemiş olurdu.</p>
 */
export function scrubPathname(pathname: string): string {
  if (!pathname.startsWith("/")) {
    return "/";
  }

  return (
    "/" +
    pathname
      .split("/")
      .filter((segment) => segment.length > 0)
      .map((segment) => (isIdentifier(segment) ? ":id" : segment))
      .join("/")
  );
}

/**
 * Tam URL'den yalnızca **kalıba indirilmiş yolu** çıkarıyor.
 *
 * <p>Sorgu dizesi ve çapa <b>tamamen düşüyor</b>, süzülmüyor. Bu ekranda
 * sorgu dizesi kullanıcının log arama ölçütü — yani aranan şeyin kendisi.
 * Onun bir alanını kurtarmaya çalışmak, kalanının bir gün sızmasına açık
 * kapı bırakmak olurdu.</p>
 */
export function scrubUrl(url: string): string {
  try {
    return scrubPathname(new URL(url, "http://local").pathname);
  } catch {
    return "/";
  }
}

/**
 * Bir olay özelliğinin taşıyabileceği değerler.
 *
 * <p>Yalnızca ilkel tipler ve ilkel dizileri. İç içe nesne <b>yok</b>: bir
 * nesneyi olduğu gibi geçirmek, içine yarın konacak her alanı geçirmek
 * demek — ve o alan bir log satırı olabilir.</p>
 */
export type TelemetryValue = string | number | boolean | null | readonly (string | number)[];

export type TelemetryProperties = Readonly<Record<string, TelemetryValue>>;

/** Serbest metnin kesildiği sınır. */
export const MAX_STRING_LENGTH = 120;

/**
 * Verilen alan listesinin **dışındaki her şeyi** atıyor ve kalanları
 * taşınabilir bir şekle indiriyor.
 *
 * @param properties Ham özellikler.
 * @param allowed İzin verilen alan adları — olay kataloğundan geliyor.
 */
export function scrubProperties(
  properties: Readonly<Record<string, unknown>>,
  allowed: readonly string[],
): TelemetryProperties {
  const permitted = new Set(allowed);
  const result: Record<string, TelemetryValue> = {};

  for (const [key, value] of Object.entries(properties)) {
    if (!permitted.has(key)) {
      continue;
    }

    const scrubbed = scrubValue(value);

    // `undefined` "bu değeri taşıyamıyorum" demek. Atlamak, taşınamayan bir
    // değerin `"[object Object]"` olarak gitmesinden iyi.
    if (scrubbed !== undefined) {
      result[key] = scrubbed;
    }
  }

  return result;
}

function scrubValue(value: unknown): TelemetryValue | undefined {
  if (value === null || typeof value === "boolean") {
    return value;
  }

  if (typeof value === "number") {
    // `NaN`/`Infinity` JSON'da `null`'a düşüyor; sayı sanılan bir alanın
    // sessizce `null` olması yerine hiç gitmemesi daha dürüst.
    return Number.isFinite(value) ? value : undefined;
  }

  if (typeof value === "string") {
    return value.slice(0, MAX_STRING_LENGTH);
  }

  if (Array.isArray(value)) {
    const items = value
      .filter((item): item is string | number => typeof item === "string" || typeof item === "number")
      .slice(0, 20)
      .map((item) => (typeof item === "string" ? item.slice(0, MAX_STRING_LENGTH) : item));

    return items;
  }

  return undefined;
}

/**
 * posthog-js'in kendiliğinden eklediği özelliklerden **atılanlar**.
 *
 * <p>Bunlar `scrubProperties`'ten geçmiyor çünkü kütüphane onları olay
 * gövdesine sonradan koyuyor. Kara liste olmalarının sebebi bu: burada
 * denetlediğimiz küme bizim değil kütüphanenin.</p>
 *
 * <p>`$current_url` ve `$referrer` ham yol + sorgu taşıyor; `$ip` sunucu
 * tarafında zaten çözülüyor ve müşteri ağının adresi bizim analitiğimizde
 * işi yok.</p>
 */
export const DENIED_AUTOMATIC_PROPERTIES: readonly string[] = [
  "$current_url",
  "$referrer",
  "$referring_domain",
  "$initial_current_url",
  "$initial_referrer",
  "$initial_referring_domain",
  "$ip",
  "$host",
  "$pathname",
];
