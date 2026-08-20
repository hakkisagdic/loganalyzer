import { describe, expect, it } from "vitest";

import {
  chartScale,
  countFirings,
  countSilenceFirings,
  matches,
  silentSources,
} from "@/lib/alerts/preview";
import { describeSeconds, toNumber } from "@/lib/alerts/types";
import type { PreviewPoint, PreviewSource } from "@/lib/alerts/types";

function point(value: number, count = value): PreviewPoint {
  return { at: new Date(0).toISOString(), count, value };
}

function source(id: string, gaps: number[]): PreviewSource {
  return { source_id: id, owner_group: "network/core", last_seen: null, gaps_seconds: gaps };
}

/**
 * T23'ün taşıyıcı kabul kriteri arayüz tarafında burada sabitleniyor:
 * **eşik değiştikçe sayı güncelleniyor ve bunun için ağa çıkılmıyor.**
 *
 * Testlerin ağ sahtesi yok — bilerek. Hesabın saf fonksiyonlarda olması, bu
 * davranışın bir istek sayısına değil bir fonksiyonun girdisine bağlı olduğunu
 * gösteriyor; sahte bir `fetch` üzerinden sınamak, asıl iddiayı kanıtlamak
 * yerine kurulumu kanıtlardı.
 */
describe("önizleme eşik hesabı", () => {
  const series = [point(5), point(15), point(10), point(0)];

  it("eşik değiştiğinde sayı değişiyor", () => {
    expect(countFirings(series, 10, "gt")).toBe(1);
    expect(countFirings(series, 10, "gte")).toBe(2);
    expect(countFirings(series, 4, "gt")).toBe(3);
    expect(countFirings(series, 100, "gt")).toBe(0);
  });

  it("karşılaştırma kümesi motorunkiyle aynı", () => {
    expect(matches(10, 10, "gt")).toBe(false);
    expect(matches(10, 10, "gte")).toBe(true);
    expect(matches(9, 10, "lt")).toBe(true);
    expect(matches(10, 10, "lte")).toBe(true);
  });

  it("seri değişmeden yalnızca eşikle sonuç değişiyor", () => {
    // Aynı diziyle iki farklı sonuç: veri tekrar çekilmiyor.
    const before = countFirings(series, 4, "gt");
    const after = countFirings(series, 12, "gt");

    expect(before).not.toBe(after);
    expect(series).toHaveLength(4);
  });

  it("boş seride sıfır dönüyor", () => {
    expect(countFirings([], 1, "gt")).toBe(0);
  });
});

describe("sessizlik önizlemesi", () => {
  const sources = [
    source("fw-01", [300, 1800, 60]),
    source("fw-02", [120]),
    source("fw-03", [7200]),
  ];

  it("eşiği aşan boşlukları sayıyor", () => {
    expect(countSilenceFirings(sources, 900)).toBe(2);
    // Eşik 100: fw-01'in 60 sn'lik boşluğu sayılmıyor → 2 + 1 + 1.
    expect(countSilenceFirings(sources, 100)).toBe(4);
    expect(countSilenceFirings(sources, 10_000)).toBe(0);
  });

  it("kaynakları en uzun sessizliğe göre sıralıyor", () => {
    const silent = silentSources(sources, 900);

    expect(silent.map((item) => item.source_id)).toEqual(["fw-03", "fw-01"]);
    expect(silent[0]?.longestGap).toBe(7200);
    expect(silent[1]?.gapCount).toBe(1);
  });

  it("eşiği aşmayan kaynağı listelemiyor", () => {
    expect(silentSources(sources, 10_000)).toHaveLength(0);
  });
});

describe("grafik ölçeği", () => {
  it("eşik tepe değerin üstündeyse ölçek eşiğe göre alınıyor", () => {
    // Yoksa eşik çizgisi grafiğin dışında kalır ve kullanıcı "eşiğim verinin
    // neresinde" sorusunu tam da hiç tetiklenmeyen kuralda cevaplayamaz.
    expect(chartScale([point(5)], 50)).toBe(50);
    expect(chartScale([point(80)], 50)).toBe(80);
  });

  it("boş seride sıfıra bölmüyor", () => {
    expect(chartScale([], 0)).toBe(1);
  });
});

describe("şema sayıları", () => {
  it("dizge kodlanmış sayıyı çeviriyor", () => {
    // .NET 10 `long`/`double` alanları `number | string` olarak yazıyor;
    // çevirmezsek "12" >= 9 dizge karşılaştırmasına düşerdi.
    expect(toNumber("1200")).toBe(1200);
    expect(toNumber(7)).toBe(7);
    expect(toNumber(null)).toBe(0);
    expect(toNumber("abc")).toBe(0);
  });

  it("dizge gelen boşlukları da doğru sayıyor", () => {
    const mixed: PreviewSource = {
      source_id: "fw-04",
      owner_group: "network/core",
      last_seen: null,
      gaps_seconds: ["1800", 60] as unknown as number[],
    };

    expect(countSilenceFirings([mixed], 900)).toBe(1);
  });
});

describe("süre biçimi", () => {
  it("motorun eşikleriyle aynı basamakları kullanıyor", () => {
    expect(describeSeconds(45)).toBe("45 sn");
    expect(describeSeconds(900)).toBe("15 dk");
    expect(describeSeconds(7200)).toBe("2 sa");
    expect(describeSeconds(172_800)).toBe("2 gün");
  });
});
