import { describe, expect, it } from "vitest";

import { safeReturnTo } from "@/lib/auth/redirects";

/**
 * Açık yönlendirme (open redirect) koruması.
 *
 * <p>Giriş bağlantısı kullanıcıyı istediği yere döndürüyor; filtresiz
 * bırakılırsa saldırgan <c>/api/auth/login?returnTo=https://sahte.site</c>
 * bağlantısını gerçek alan adıyla paylaşıp kullanıcıyı girişten sonra kendi
 * sayfasına düşürebilir.</p>
 */
describe("safeReturnTo", () => {
  it("uygulama içi göreli yolu koruyor", () => {
    expect(safeReturnTo("/olaylar?kaynak=fw-core-01")).toBe("/olaylar?kaynak=fw-core-01");
  });

  const reddedilenler: [value: string | null, neden: string][] = [
    ["https://sahte.site", "mutlak adres"],
    ["//sahte.site", "şema-göreli adres — tek eğik çizgi sanılıp gözden kaçıyor"],
    ["http://localhost:3000/", "kendi kökümüz bile olsa mutlak"],
    ["javascript:alert(1)", "betik şeması"],
    ["olaylar", "başında eğik çizgi yok"],
    ["", "boş"],
    [null, "hiç verilmemiş"],
  ];

  it.each(reddedilenler)("%s reddediliyor (%s)", (value) => {
    expect(safeReturnTo(value)).toBe("/");
  });
});
