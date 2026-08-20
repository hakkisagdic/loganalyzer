import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import defaultConfig from "../vitest.config";
import liveConfig from "../vitest.live.config";
import { LIVE_TESTS } from "../vitest.live-tests";

/**
 * **Test yapılandırmasının kendi tutarlılığı** (T27).
 *
 * <p>
 * Bu dosya ürün kodunu sınamıyor; <b>hangi testin koştuğunu</b> sınıyor. Var
 * olma sebebi ölçülmüş bir olay: bir tur önce ekran görüntüsü bekçisi için
 * raporda "varsayılan pakete koymadım" yazılmıştı, oysa <c>include</c> deseni
 * dosyayı topluyordu. Niyet bir yerde, gerçek başka yerdeydi ve ikisinin
 * ayrıştığını kimse okumadı — üstelik ölçüm her koşumda ekrandaydı (vitest
 * dosya sayısını basıyor).
 * </p>
 *
 * <p>
 * Ders şuydu: <b>ihlali bir kişi değil yapılandırma yapıyor.</b> Dolayısıyla
 * bekçi de yapılandırmayı okumalı, yorumu değil.
 * </p>
 */

const root = fileURLToPath(new URL("..", import.meta.url));

/** Bir vitest yapılandırmasının `test` bloğu — tip gymnastics olmadan. */
function testBlock(config: unknown): { include?: string[]; exclude?: string[] } {
  return (config as { test?: { include?: string[]; exclude?: string[] } }).test ?? {};
}

describe("canlı bileşen isteyen testler", () => {
  it("liste boş değil", () => {
    // Liste boşalsaydı aşağıdaki iddiaların hepsi boş küme üzerinde geçerdi ve
    // dosya yeşil yanıp hiçbir şey ifade etmezdi — bu deponun adını koyduğu
    // hata sınıfı.
    expect(LIVE_TESTS.length).toBeGreaterThan(0);
  });

  it("listedeki her dosya gerçekten var", () => {
    // Yeniden adlandırılmış bir dosya sessizce İKİ kümeden de düşerdi:
    // varsayılan koşum onu dışlamaya devam eder (desen artık hiçbir şeye
    // uymuyor), `test:live` de toplayamaz. Yani test hiç koşmaz ve iki koşum da
    // yeşil kalır.
    for (const file of LIVE_TESTS) {
      expect(existsSync(root + file), `${file} yok — liste bayatlamış`).toBe(true);
    }
  });

  it("varsayılan koşum hepsini DIŞLIYOR", () => {
    const exclude = testBlock(defaultConfig).exclude ?? [];

    for (const file of LIVE_TESTS) {
      expect(exclude).toContain(file);
    }
  });

  it("`test:live` koşumu YALNIZCA onları topluyor", () => {
    // "Yalnızca" önemli: canlı yapılandırma varsayılan deseni de toplasaydı,
    // koordinatör Redis'i kaldırdığında bütün paket koşar ve canlı testin
    // sonucu 270 testin gürültüsünde kaybolurdu.
    expect(testBlock(liveConfig).include).toEqual([...LIVE_TESTS]);
  });

  it("listedeki dosya kendini AYRICA atlamıyor", () => {
    // Çift koruma en sinsi hâl: dosya hem dışlanmış hem `describe.skip` olsaydı
    // `npm run test:live` sıfır iddia koşturup **yeşil** yanardı. Koordinatör
    // "canlı testler geçti" diye okur, oysa hiçbir şey koşmamıştır.
    for (const file of LIVE_TESTS) {
      const source = readFileSync(root + file, "utf8");
      const skips = source.match(/^\s*(describe|it|test)\.skip\b/gm) ?? [];

      expect(skips, `${file}: \`test:live\` koşumunda atlanan blok var`).toEqual([]);
    }
  });
});

describe("CI'ın sağladığı bileşene bağlı testler", () => {
  /**
   * Ekran görüntüsü bekçileri chromium istiyor ve CI onu **kuruyor**. Bu yüzden
   * listede değiller: koşulsuz koşuyorlar ve tarayıcı yoksa kırmızı yanıyorlar.
   *
   * <p>
   * Karar koordinatörün: testi kendini sessizce atlayacak hâle getirmek yerine
   * CI'a chromium kuruldu, çünkü "sessizce atlayan bekçi, bekçinin kendisinden
   * tehlikeli". Bu test o kararı sabitliyor — biri dosyayı canlı listeye
   * taşırsa, kararı bilerek değiştirmiş olması gerekiyor.
   * </p>
   */
  it("ekran görüntüsü paketi canlı listede DEĞİL", () => {
    expect(LIVE_TESTS).not.toContain("tests/screenshots/capture.test.tsx");
  });

  it("ekran görüntüsü paketi varsayılan desenle toplanıyor", () => {
    const include = testBlock(defaultConfig).include ?? [];

    expect(include).toContain("tests/**/*.test.tsx");
    expect(existsSync(root + "tests/screenshots/capture.test.tsx")).toBe(true);
  });
});
