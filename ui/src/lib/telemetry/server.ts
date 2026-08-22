import "server-only";

import { PostHog } from "posthog-node";

import { readTelemetryConfig, telemetryState, type TelemetryConfig } from "./config";
import { allowedProperties, type EventName, type EventPayload } from "./events";
import { pseudonymousId } from "./identity";
import { scrubProperties, type TelemetryProperties } from "./scrub";

/**
 * **Sunucu tarafı olay kapısı.**
 *
 * <p>
 * İkinci bir sink'in varlık sebebi mimari, tercih değil: bu üründe en değerli
 * ölçümler <b>sunucuda</b> doğuyor. Log araması bir sunucu bileşeninde koşuyor
 * (<c>olaylar/page.tsx</c>); sorgunun süresi, dönen satır sayısı ve hatanın
 * sınıfı orada biliniyor. Bunları tarayıcıya taşıyıp oradan göndermek iki
 * şeyi birden bozardı: ölçüm <b>tarayıcının söylediğine</b> güvenirdi, ve
 * ölçüm için taşınan veri arama ölçütünü tarayıcıya indirirdi.
 * </p>
 *
 * <p>
 * <b>Süzgeç ikinci kez yazılmadı.</b> <c>scrub.ts</c> ve <c>events.ts</c> iki
 * sink arasında paylaşılıyor — §9'un yasakladığı ikinci kopya olurdu, ve
 * ayrıştıkları gün ayrıştıkları hiçbir yerde görünmezdi.
 * </p>
 *
 * <h3>Vekil neden burada yok</h3>
 *
 * <p>
 * <c>/api/telemetry</c> vekili <b>tarayıcıyı</b> üçüncü bir alan adından uzak
 * tutmak için var. Burada tarayıcı yok; istek zaten Next sunucusundan çıkıyor.
 * Kendi vekilimize döngü yapmak hiçbir şey kazandırmaz, yalnızca bir zıplama
 * ekler ve hata yüzeyini büyütür.
 * </p>
 */

let client: PostHog | undefined;
let clientKey: string | undefined;

function sink(config: TelemetryConfig): PostHog | undefined {
  if (!config.projectKey) {
    return undefined;
  }

  // Yapılandırma değiştiyse (testlerde ortam değişkeni değişiyor) istemci
  // yeniden kuruluyor. Anahtarı önbelleğe almadan bunu anlamanın yolu yok.
  const key = `${config.projectKey}@${config.host}`;

  if (client && clientKey === key) {
    return client;
  }

  client = new PostHog(config.projectKey, {
    host: config.host,
    // İSTEK BAŞINA gönderim. Sunucu bileşeni istekten sonra yaşamıyor;
    // tamponlanan bir olay hiç gitmezdi. Hacim arttığında burası bir kuyruğa
    // dönüşmeli — ama önce hacmin var olduğu ölçülmeli.
    flushAt: 1,
    flushInterval: 0,
    // Coğrafi çözümleme KAPALI. Yukarı akış istek IP'sini görüyor ve o IP
    // müşterinin değil BİZİM sunucumuz; ondan türeyen "ülke" alanı doğru
    // görünen yanlış bir veri olurdu.
    disableGeoip: true,
  });

  clientKey = key;

  return client;
}

/** Yalnızca test için: önbelleğe alınmış istemciyi bırakır. */
export async function resetServerTelemetry(): Promise<void> {
  const current = client;

  client = undefined;
  clientKey = undefined;

  await current?.shutdown(1000).catch(() => undefined);
}

/**
 * Sunucu tarafındaki olayın kimliği.
 *
 * <p>
 * Kimlik bağlama <b>kapalıyken</b> sabit bir <c>anonymous</c> kimliği ve
 * <c>$process_person_profile: false</c> gidiyor: PostHog olayı kaydediyor ama
 * kişi profili oluşturmuyor. Sayılar, süreler ve hata oranları çalışmaya devam
 * ediyor; <b>"kaç farklı kullanıcı" sorusu cevaplanamıyor</b>. Bilinçli takas
 * ve tek satırla geri alınabilir (<c>TELEMETRY_IDENTIFY_USERS</c>).
 * </p>
 *
 * <p>
 * Rastgele bir kimlik üretmek daha "doğru" görünürdü ve <b>daha kötü</b>
 * olurdu: her olay ayrı bir kişi sayılır, kullanıcı sayısı olay sayısına eşit
 * çıkar, ve kimse o sayının anlamsız olduğunu fark etmez.
 * </p>
 */
function identify(
  config: TelemetryConfig,
  subject: string | undefined,
): { distinctId: string; anonymous: boolean } {
  if (config.identifyUsers && config.identitySalt && subject) {
    return { distinctId: pseudonymousId(subject, config.identitySalt), anonymous: false };
  }

  return { distinctId: "anonymous", anonymous: true };
}

export interface ServerEventContext {
  /** Keycloak `sub`. Ham hâlde ASLA gönderilmiyor; yalnızca özeti. */
  readonly subject?: string;
}

/**
 * Bir olayı süzüp sunucudan gönderiyor.
 *
 * <p>
 * <b>Hiçbir koşulda atmıyor.</b> Telemetrinin ulaşılamaz olması ürünü
 * bozmamalı — bir log arama ekranının PostHog düştüğü için beyaz sayfa
 * göstermesi, ölçmeye çalıştığımız şeyin kendisini bozmak olurdu.
 * </p>
 *
 * @returns Gönderilen özellikler; gönderilmediyse `undefined`. Testin ölçtüğü
 *   şey bu — "ne gitti" sorusunun cevabı.
 */
export async function trackServer<TName extends EventName>(
  name: TName,
  payload?: EventPayload<TName>,
  context: ServerEventContext = {},
): Promise<TelemetryProperties | undefined> {
  const state = telemetryState();

  if (state.status !== "ok") {
    return undefined;
  }

  const config = readTelemetryConfig();
  const target = sink(config);

  if (!target) {
    return undefined;
  }

  const properties = scrubProperties(
    (payload ?? {}) as Record<string, unknown>,
    allowedProperties(name),
  );

  const { distinctId, anonymous } = identify(config, context.subject);

  try {
    // `captureImmediate` — `capture` DEĞİL. `capture` kuyruğa koyup arka planda
    // boşaltıyor; sunucu bileşeni yanıtı yazıp bittiğinde o kuyruk hiç
    // boşalmayabiliyor. Beklenebilir gönderim, olayın gerçekten gittiğini
    // doğrulanabilir de yapıyor.
    await target.captureImmediate({
      distinctId,
      event: name,
      properties: {
        ...properties,
        environment: config.environment,
        ...(anonymous ? { $process_person_profile: false } : {}),
      },
    });
  } catch {
    // Yutuluyor ve bu bilinçli — yukarıdaki gerekçe. Sessizliğin bedeli
    // "telemetri gitmiyor ve kimse bilmiyor"; alternatifin bedeli "ürün
    // çalışmıyor". İkincisi daha pahalı.
    return undefined;
  }

  return properties;
}
