import { describe, expect, it } from "vitest";

import { diffLines, MAX_DIFF_CELLS, splitLines } from "@/lib/parsers/diff";

/**
 * Fark görünümünün bekçileri (T20 kabul kriteri: "YAML'ı satır satır
 * karşılaştırıyor").
 *
 * <p>Hepsi saf fonksiyon üzerinde — hiçbiri render etmiyor. Hesap bileşenin
 * içinde olsaydı bu testler yavaş, kırılgan ve okunmaz olurdu.</p>
 */
describe("YAML fark görünümü", () => {
  it("değişmemiş dosyada fark yok", () => {
    const yaml = "id: fortigate\nversion: 1.0.0\n";
    const result = diffLines(yaml, yaml);

    expect(result.added).toBe(0);
    expect(result.removed).toBe(0);
    expect(result.lines.every((line) => line.kind === "same")).toBe(true);
  });

  it("tek satır değişikliğini eklenen + silinen olarak gösteriyor", () => {
    const result = diffLines("id: fw\nversion: 1.0.0\nvendor: fortinet\n", "id: fw\nversion: 1.1.0\nvendor: fortinet\n");

    expect(result.added).toBe(1);
    expect(result.removed).toBe(1);

    const removed = result.lines.find((line) => line.kind === "removed");
    const added = result.lines.find((line) => line.kind === "added");

    expect(removed?.text).toBe("version: 1.0.0");
    expect(added?.text).toBe("version: 1.1.0");

    // Satır numaraları iki tarafta ayrı: eklenen satırın solda karşılığı yok.
    expect(removed?.rightNumber).toBeUndefined();
    expect(added?.leftNumber).toBeUndefined();
  });

  it("eklenen satır önceki satırları kaydırmıyor", () => {
    const result = diffLines("a\nb\n", "a\nyeni\nb\n");

    expect(result.added).toBe(1);
    expect(result.removed).toBe(0);

    const moved = result.lines.find((line) => line.text === "b");
    expect(moved?.leftNumber).toBe(2);
    expect(moved?.rightNumber).toBe(3);
  });

  it("boş dosyadan içerik eklemeyi tamamen 'eklenen' sayıyor", () => {
    const result = diffLines("", "id: fw\nversion: 1.0.0\n");

    expect(result.removed).toBe(0);
    expect(result.added).toBeGreaterThan(0);
  });

  /**
   * F2'nin açık riski: gövdeler Türkçe, Arapça ve Çince geliyor. Fark satırları
   * **karakter değil bayt** sayarsa bu dillerde kayar.
   */
  describe("çok dilli gövdeler", () => {
    it("CJK değerindeki değişikliği yakalıyor", () => {
      const before = "description: 用户登录失败\nvendor: cisco\n";
      const after = "description: 用户登录成功\nvendor: cisco\n";

      const result = diffLines(before, after);

      expect(result.added).toBe(1);
      expect(result.removed).toBe(1);
      expect(result.lines.find((line) => line.kind === "added")?.text).toBe("description: 用户登录成功");
    });

    it("Arapça değeri değişmemişse fark üretmiyor", () => {
      // Sağdan sola metin ve birleşen harfler; bayt uzunluğu karakter
      // sayısından farklı.
      const yaml = "description: فشل تسجيل الدخول\nvendor: fortinet\n";

      expect(diffLines(yaml, yaml).added).toBe(0);
    });

    it("Türkçe İ/ı içeren satırı doğru eşliyor", () => {
      const yaml = "description: KULLANICI GİRİŞİ BAŞARISIZ\n";

      // Kültür duyarlı bir karşılaştırma `İ`yi `i` sanabilir; eşitlik ordinal.
      expect(diffLines(yaml, yaml).added).toBe(0);
      expect(diffLines(yaml, "description: kullanici girisi basarisiz\n").added).toBe(1);
    });

    it("aynı görünen farklı Unicode bileşimini fark saymıyor", () => {
      // `é` tek kod noktası (NFC) ya da `e` + birleşik aksan (NFD). Ürün
      // ingest'te NFC'ye normalize ediyor; ekranın boru hattının sildiği bir
      // farkı raporlaması kafa karıştırırdı.
      const composed = "description: café\n";
      const decomposed = "description: café\n";

      expect(diffLines(composed, decomposed).added).toBe(0);
    });

    it("emoji taşıyan satırı bölmüyor", () => {
      const yaml = "description: kritik 🔥 uyarı\n";

      expect(diffLines(yaml, yaml).added).toBe(0);
      expect(diffLines(yaml, "description: kritik uyarı\n").added).toBe(1);
    });
  });

  describe("satır sonu ve sınırlar", () => {
    it("CRLF satır sonlarını LF gibi okuyor", () => {
      // Windows'ta düzenlenmiş bir YAML aksi hâlde HER satırı değişmiş
      // gösterirdi.
      expect(diffLines("a\nb\n", "a\r\nb\r\n").added).toBe(0);
    });

    it("çok büyük fark hesaplanmıyor ve bunu söylüyor", () => {
      // Ortak ön/son ek kırpıldıktan sonra bile sınırı aşan bir çift.
      const size = Math.ceil(Math.sqrt(MAX_DIFF_CELLS)) + 10;
      const left = Array.from({ length: size }, (_, index) => `sol-${index}`).join("\n");
      const right = Array.from({ length: size }, (_, index) => `sag-${index}`).join("\n");

      const result = diffLines(left, right);

      expect(result.tooLarge).toBe(true);
      expect(result.lines).toHaveLength(0);
    });

    it("ortak ön ve son ek kırpıldığı için büyük ama benzer dosyalar hesaplanıyor", () => {
      const shared = Array.from({ length: 5_000 }, (_, index) => `satir-${index}`);
      const left = shared.join("\n");
      const right = [...shared.slice(0, 2_500), "araya-giren", ...shared.slice(2_500)].join("\n");

      const result = diffLines(left, right);

      // Kırpma olmasaydı 25 milyon hücre denenirdi.
      expect(result.tooLarge).toBe(false);
      expect(result.added).toBe(1);
      expect(result.removed).toBe(0);
    });
  });

  it("satır bölme boş metinde boş dizi veriyor", () => {
    expect(splitLines("")).toEqual([]);
    expect(splitLines("tek")).toEqual(["tek"]);
  });

  it("dosya sonundaki satır sonu hayalet satır üretmiyor", () => {
    // Aksi hâlde düzgün biten her YAML'ın farkı sonda boş bir satır gösterirdi.
    expect(splitLines("a\nb\n")).toEqual(["a", "b"]);
    expect(splitLines("a\nb")).toEqual(["a", "b"]);

    // İçerideki boş satır korunuyor — o gerçek bir satır.
    expect(splitLines("a\n\nb\n")).toEqual(["a", "", "b"]);
  });
});
