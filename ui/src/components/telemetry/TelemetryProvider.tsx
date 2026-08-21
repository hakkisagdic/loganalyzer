"use client";

import { usePathname } from "next/navigation";
import posthog from "posthog-js";
import { useEffect, useRef, type ReactNode } from "react";

import { registerTelemetryClient, trackScreen } from "@/lib/telemetry/client";
import { DENIED_AUTOMATIC_PROPERTIES, scrubUrl } from "@/lib/telemetry/scrub";

/**
 * posthog-js'i **kapalı bir kutu olarak** kuran bileşen.
 *
 * <p>
 * Kütüphanenin varsayılanları bir pazarlama sitesi için tasarlanmış: her tıkı
 * yakala, her URL'yi gönder, oturumu videoya çek. Bu ürünün ekranında duran
 * şey <b>müşterinin log satırları</b> — o varsayılanların hepsi burada
 * yanlış. Aşağıdaki her kapatma ayrı ayrı yazıldı ve her birinin yanında
 * neden kapatıldığı duruyor.
 * </p>
 */

export interface TelemetryProviderProps {
  /** PostHog proje (yazma) anahtarı. Sunucu bileşeninden prop olarak geliyor. */
  readonly projectKey: string;
  /** "PostHog'da aç" bağlantılarının kökü. */
  readonly uiHost: string;
  /** Dağıtım etiketi — `dev` / `stage` / `prod`. */
  readonly environment: string;
  readonly children: ReactNode;
}

export function TelemetryProvider({
  projectKey,
  uiHost,
  environment,
  children,
}: TelemetryProviderProps) {
  const pathname = usePathname();
  const started = useRef(false);

  useEffect(() => {
    if (started.current) {
      return;
    }

    started.current = true;

    posthog.init(projectKey, {
      // Tarayıcı PostHog'a DOĞRUDAN konuşmuyor. `/api/telemetry` Next
      // sunucusundaki vekil (bkz. app/api/telemetry/[...path]/route.ts).
      api_host: "/api/telemetry",
      ui_host: uiHost,

      // Otomatik yakalama KAPALI. Açık olsaydı her tıklanan öğenin metni
      // olaya girerdi — ve bu ekranlarda tıklanan metin bir ana bilgisayar
      // adı, bir kullanıcı adı ya da bir log satırının kendisi.
      autocapture: false,

      // Kütüphanenin sayfa görüntülemesi kapalı; ham URL taşıyor. Yerine
      // `trackScreen` gidiyor — kalıba indirilmiş yolla, sorgu dizesi olmadan.
      capture_pageview: false,
      capture_pageleave: false,

      // OTURUM KAYDI. Bu ürünün ekranı müşterinin log'u; kaydı videoya almak
      // o log'u PostHog'a kopyalamak olurdu. Vekilde de `/s` ucu kapalı —
      // iki kapı, çünkü bu seçenek bir sürüm yükseltmesinde varsayılanını
      // değiştirebilir, vekildeki liste değiştirmez.
      disable_session_recording: true,
      disable_surveys: true,

      // Isı haritası, ölü tık ve öfke tıkı: hepsi konum + öğe metni topluyor.
      capture_heatmaps: false,
      capture_dead_clicks: false,

      // İstisna yakalama kapalı: yığın izi ve hata mesajı, işlenen verinin
      // parçalarını taşıyabiliyor. Hatalar `error_shown` olayıyla
      // SINIFLANDIRILMIŞ hâlde gidiyor (bkz. events.ts).
      capture_exceptions: false,

      // Uzaktan betik indirme kapalı: kaydedici/anket paketleri hiç
      // kullanılmıyor, indirilmelerinin de sebebi yok.
      disable_external_dependency_loading: true,

      // Çerez değil `localStorage`: telemetri kimliğinin oturum çerezinin
      // yanında durmasının, her isteğe binmesinin bir sebebi yok.
      persistence: "localStorage",

      property_denylist: [...DENIED_AUTOMATIC_PROPERTIES],

      // İKİNCİ savunma hattı. Birincisi `lib/telemetry/client.ts`'teki
      // `track` — orası saf fonksiyonlarla süzüyor ve test edilebiliyor.
      // Burası kütüphanenin KENDİ eklediği alanları yakalıyor; ona
      // güvenilmiyor ama boş da bırakılmıyor.
      sanitize_properties: (properties) => {
        const safe: Record<string, unknown> = { ...properties };

        for (const key of DENIED_AUTOMATIC_PROPERTIES) {
          delete safe[key];
        }

        // Kütüphanenin sildiğimiz `$current_url`'ünün yerine kalıba
        // indirilmiş hâlini koyuyoruz: sayfa kırılımı olmadan pano işe
        // yaramaz, ham URL ise gitmemeli.
        if (typeof properties.$current_url === "string") {
          safe.route = scrubUrl(properties.$current_url);
        }

        safe.environment = environment;

        return safe;
      },

      loaded: (instance) => {
        registerTelemetryClient(instance);
      },
    });

    return () => {
      registerTelemetryClient(undefined);
    };
  }, [projectKey, uiHost, environment]);

  useEffect(() => {
    if (pathname) {
      trackScreen(pathname);
    }
  }, [pathname]);

  return <>{children}</>;
}
