"use client";

import { EVENTS, allowedProperties, type EventName, type EventPayload } from "./events";
import { scrubPathname, scrubProperties, type TelemetryProperties } from "./scrub";

/**
 * Tarayıcı tarafındaki **tek** olay kapısı.
 *
 * <p>
 * `posthog.capture` hiçbir ekrandan doğrudan çağrılmıyor; hepsi buradan
 * geçiyor. Sebep tek bir cümle: süzgeç yalnızca herkesin geçtiği yerdeyse
 * süzgeçtir. İki giriş kapısı olsaydı ikincisinin bir gün süzgeçsiz kalması
 * kaçınılmazdı ve o gün kimse fark etmezdi.
 * </p>
 *
 * <p>
 * Telemetri kapalıyken `track` <b>hiçbir şey yapmıyor ve hata da atmıyor</b>.
 * Ekranların "telemetri açık mı" diye sorması gerekseydi o soru bir yerde
 * unutulurdu; unutulduğunda da ekran çökerdi. Sessizlik burada doğru
 * davranış çünkü kapalı olmak <b>beklenen</b> hâl, bir arıza değil.
 * </p>
 */

/**
 * İhtiyacımız olan **tek** yüzey.
 *
 * <p>posthog-js'in kendi `PostHog` tipini almak, bu modülü kütüphanenin
 * yetmiş küsur üyelik yüzeyine bağlardı — ve `loaded` kancasının verdiği
 * nesne zaten o tipin tamamı değil. Bir metotluk arayüz hem derleniyor hem
 * de testte sahtelenebiliyor.</p>
 */
export interface TelemetrySink {
  capture(event: string, properties?: Record<string, unknown>): unknown;
}

let client: TelemetrySink | undefined;

/** Sağlayıcı bileşeni hazır olan istemciyi buraya veriyor. */
export function registerTelemetryClient(instance: TelemetrySink | undefined): void {
  client = instance;
}

/** Yalnızca test için: kayıtlı istemciyi temizler. */
export function resetTelemetryClient(): void {
  client = undefined;
}

/**
 * Bir olayı süzüp gönderiyor.
 *
 * <p>Dönüş değeri <b>gönderilen hâl</b> — testin ölçtüğü şey bu. `undefined`
 * dönüyorsa olay gitmedi (telemetri kapalı ya da olay katalogda yok).</p>
 */
export function track<TName extends EventName>(
  name: TName,
  payload?: EventPayload<TName>,
): TelemetryProperties | undefined {
  const definition = EVENTS[name];

  if (!definition) {
    return undefined;
  }

  const properties = scrubProperties(
    (payload ?? {}) as Record<string, unknown>,
    allowedProperties(name),
  );

  if (!client) {
    return undefined;
  }

  client.capture(definition.name, { ...properties });

  return properties;
}

/**
 * Sayfa görüntüleme. posthog-js'in kendi `capture_pageview`'ı kapalı olduğu
 * için tek kaynak burası.
 */
export function trackScreen(pathname: string, scopeKind?: string): void {
  track("screen_viewed", {
    route: scrubPathname(pathname),
    ...(scopeKind === undefined ? {} : { scope_kind: scopeKind }),
  } as EventPayload<"screen_viewed">);
}
