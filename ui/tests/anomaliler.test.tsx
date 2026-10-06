import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { AppShell } from "@/components/AppShell";
import { AnomalyView } from "@/app/anomaliler/AnomalyView";

describe("Anomaliler ekranı ve durumları", () => {
  it("AppShell navigasyonunda /anomaliler bağlantısı mevcuttur", () => {
    const markup = renderToStaticMarkup(<AppShell username="testuser"><div>İçerik</div></AppShell>);
    expect(markup).toContain('href="/anomaliler"');
    expect(markup).toContain("Anomaliler");
  });

  it("AnomalyView ilk yükleme aşamasını hatasız çizer", () => {
    const markup = renderToStaticMarkup(<AnomalyView />);
    // İlk render'da iskelet / yükleniyor durumu beklenir
    expect(markup).toContain("Anomali politikaları yükleniyor");
  });
});
