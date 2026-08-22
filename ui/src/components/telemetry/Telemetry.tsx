import type { ReactNode } from "react";

import { telemetryState } from "@/lib/telemetry/config";

import { TelemetryProvider } from "./TelemetryProvider";

/**
 * Telemetrinin **sunucu tarafı kapısı**. `layout.tsx` bunu çağırıyor.
 *
 * <p>
 * Ayrı bir bileşen olmasının sebebi: yapılandırma sunucu tarafında okunuyor ve
 * proje anahtarı istemciye <b>prop olarak</b> iniyor. `NEXT_PUBLIC_` ile
 * gömmek daha kısa olurdu ama `next.config.ts`'in bilinçli olarak boş
 * bıraktığı `env` girdisini açardı (bkz. `lib/telemetry/config.ts`).
 * </p>
 *
 * <p>
 * Telemetri kapalıysa <b>posthog-js hiç yüklenmiyor</b>: sağlayıcı bileşeni
 * ağaca girmiyor, dolayısıyla kütüphanenin parçası da istemciye inmiyor.
 * "Kapalı" burada bir bayrak değil, gerçekten yokluk.
 * </p>
 */
export function Telemetry({ children }: { readonly children: ReactNode }) {
  const state = telemetryState();

  if (state.status !== "ok" || !state.config.projectKey) {
    // `misconfigured` hâli de burada sessizce geçiyor — ama SESSİZ DEĞİL:
    // vekil ucu 503 ve eksik değişkenlerin adını dönüyor, yani telemetriyi
    // açtığını sanan yönetici ağ sekmesinde sebebi okuyor. Uygulamayı
    // açılmaz yapmak yanlış olurdu: telemetri, ürünün çalışması için
    // gerekli değil.
    return <>{children}</>;
  }

  return (
    <TelemetryProvider
      projectKey={state.config.projectKey}
      uiHost={state.config.uiHost}
      environment={state.config.environment}
    >
      {children}
    </TelemetryProvider>
  );
}
