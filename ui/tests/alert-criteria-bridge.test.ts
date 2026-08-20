import { describe, expect, it } from "vitest";

import { CRITERION_BRIDGE, isTranslatable, mappedColumns } from "@/lib/alerts/criteria-bridge";
import { PARAM } from "@/lib/events/criteria";

/**
 * "Bir arama" iki yerde temsil ediliyor: T15'in adres çubuğu ölçütleri ve alarm
 * motorunun `AlertSearch`/`FieldFilter`'ı. **Ayrıştıkları gün hiçbir yerde
 * kırmızı yanmaz** — iki taraf da kendi içinde tutarlı kalır, yalnızca
 * ekrandan kurulan alarm ekranda görülenden başkasını izler.
 *
 * Bu bekçi o sessizliği bozuyor: `criteria.ts`'in URL'ye koyabildiği her ölçüt
 * ya eşlemede bir karşılık taşıyor ya da **açıkça** "çevrilemez" diye
 * gerekçesiyle listelenmiş. Üçüncü bir seçenek — hiç bahsedilmemek — yok.
 */
describe("arama ölçütü → alarm kuralı köprüsü", () => {
  it("her ölçütün bir karşılığı var", () => {
    const missing = Object.keys(PARAM).filter((key) => !(key in CRITERION_BRIDGE));

    expect(
      missing,
      `Eşlemesi olmayan ölçüt: ${missing.join(", ")}. ` +
        "criteria-bridge.ts'e ya çeviriyi ya da 'çevrilemez' gerekçesini ekleyin.",
    ).toEqual([]);
  });

  it("köprüde fazladan anahtar yok", () => {
    // Ters yön de önemli: kaldırılmış bir ölçütün eşlemesi kalırsa tablo
    // gerçeği anlatmayı bırakır.
    const extra = Object.keys(CRITERION_BRIDGE).filter((key) => !(key in PARAM));

    expect(extra, `PARAM'da olmayan eşleme: ${extra.join(", ")}`).toEqual([]);
  });

  it("çevrilemez sayılan her ölçüt gerekçe taşıyor", () => {
    for (const [key, mapping] of Object.entries(CRITERION_BRIDGE)) {
      if (mapping.kind === "unmapped") {
        expect(mapping.reason.length, `${key} gerekçesiz 'çevrilemez'`).toBeGreaterThan(20);
      }
    }
  });

  it("severity_min çevirisi operatör kümesinin sınırını kabul ediyor", () => {
    const mapping = CRITERION_BRIDGE.severityMin;

    // Ekranda "n ve üzeri", operatör kümesinde `gte` yok. Çeviri bunu
    // gizlemiyor: `gt` kullanıyor ve notunda değerin düşürüldüğünü söylüyor.
    expect(mapping.kind).toBe("filter");
    if (mapping.kind === "filter") {
      expect(mapping.column).toBe("severity_num");
      expect(mapping.op).toBe("gt");
      expect(mapping.note).toContain("gte");
    }
  });

  it("sayfalama ölçütleri alarma çevrilmiyor", () => {
    expect(isTranslatable("afterTimestamp")).toBe(false);
    expect(isTranslatable("afterEventId")).toBe(false);
    expect(isTranslatable("limit")).toBe(false);
  });

  it("kapsam ölçütü filtre değil kuralın kapsamı oluyor", () => {
    // Filtre olarak çevrilseydi kapsam ikinci bir yerden gelirdi — K17'nin
    // kaçındığı dağılma.
    expect(CRITERION_BRIDGE.ownerGroup.kind).toBe("direct");
  });

  it("hedeflenen kolonlar C# izin listesindekilerle aynı isimde", () => {
    // Bu liste `EventReader.FilterableColumns`'ta da sınanıyor
    // (`AlertCriteriaBridgeTests`); iki taraf ayrışırsa biri kırmızı yanar.
    expect(mappedColumns()).toEqual(["action", "proto", "severity_num", "vendor"]);
  });
});
