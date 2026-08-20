import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

/**
 * WCAG AA kontrast denetimi — **açık ve koyu temada** (T28 kabul kriteri).
 *
 * <p>
 * Ticket kontrastı açıkça istiyor ve "denetlendi" demek bir görüş; burada
 * hesaplanıyor. Jetonlar <c>tokens.css</c>'te tanımlı, tema başına ayrı
 * bloklarda ve birbirlerine <c>var()</c> ile bağlılar — yani bir jetonun bir
 * temadaki gerçek değeri okunmadan kontrast bilinemiyor. Bu dosya o zinciri
 * çözüyor ve <b>gerçekten kullanılan</b> ön plan/arka plan çiftlerini ölçüyor.
 * </p>
 *
 * <p>
 * Bu bekçi bir bulgu üretti: koyu temada <c>--danger</c> okunabilirlik için
 * açılıyor (<c>red-500</c>) ve tehlike düğmesi o kırmızının üstünde beyaz
 * metinle <b>3.76:1</b> kalıyordu — AA normal metin sınırı 4.5:1. Dolgu ayrı
 * bir jetona (<c>--danger-solid</c>) çıkarıldı.
 * </p>
 */

const TOKENS = fileURLToPath(new URL("../src/app/tokens.css", import.meta.url));

/** WCAG 2.x normal metin sınırı. Büyük metin 3:1'e düşüyor; burada hepsi normal. */
const AA_NORMAL = 4.5;

type Theme = "light" | "dark";

/**
 * <c>tokens.css</c>'i tema başına okuyor.
 *
 * <p>
 * Üç blok var: <c>:root</c> (ortak palet + açık tema), <c>[data-theme="dark"]</c>
 * ve <c>@media (prefers-color-scheme: dark)</c>. Sonuncusu ikincisinin
 * kopyası — <b>bu testin ayrıca sınadığı bir şey</b>: ikisi ayrışırsa işletim
 * sistemi koyu temadayken ürün başka görünür.
 * </p>
 */
function readTokens(): { light: Map<string, string>; dark: Map<string, string>; media: Map<string, string> } {
  const css = readFileSync(TOKENS, "utf8");

  const light = new Map<string, string>();
  const dark = new Map<string, string>();
  const media = new Map<string, string>();

  let target: Map<string, string> | undefined;
  let inMedia = false;

  for (const raw of css.split("\n")) {
    const line = raw.trim();

    if (line.startsWith("@media")) {
      inMedia = true;
      continue;
    }

    if (!inMedia && (line.startsWith(":root") || line.startsWith('[data-theme="light"]'))) {
      target = light;
      continue;
    }

    if (line.startsWith('[data-theme="dark"]') || line.startsWith(":root:not([data-theme])")) {
      target = inMedia ? media : dark;
      continue;
    }

    if (line.startsWith("}")) {
      target = undefined;
      continue;
    }

    const match = /^(--[\w-]+)\s*:\s*([^;]+);/.exec(line);

    if (match && target) {
      target.set(match[1]!, match[2]!.trim());
    }
  }

  return { light, dark, media };
}

const { light, dark, media } = readTokens();

/** `var(--x)` zincirini çözüyor; tema değeri yoksa ortak palete düşüyor. */
function resolve(name: string, theme: Theme): string {
  const scope = theme === "light" ? light : dark;

  for (let guard = 0; guard < 10; guard += 1) {
    const value = scope.get(name) ?? light.get(name);

    if (value === undefined) {
      throw new Error(`Tanımsız jeton: ${name} (${theme})`);
    }

    const reference = /^var\((--[\w-]+)\)$/.exec(value);

    if (!reference) {
      return value;
    }

    name = reference[1]!;
  }

  throw new Error(`Döngüsel jeton zinciri: ${name}`);
}

function channel(value: number): number {
  const c = value / 255;
  return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

function luminance(hex: string): number {
  const clean = hex.trim().replace("#", "");

  const full =
    clean.length === 3
      ? clean
          .split("")
          .map((c) => c + c)
          .join("")
      : clean;

  if (!/^[0-9a-fA-F]{6}$/.test(full)) {
    throw new Error(`Renk çözülemedi: ${hex}`);
  }

  const r = channel(Number.parseInt(full.slice(0, 2), 16));
  const g = channel(Number.parseInt(full.slice(2, 4), 16));
  const b = channel(Number.parseInt(full.slice(4, 6), 16));

  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(foreground: string, background: string): number {
  const a = luminance(foreground);
  const b = luminance(background);
  const [hi, lo] = a > b ? [a, b] : [b, a];

  return (hi + 0.05) / (lo + 0.05);
}

/**
 * Ekranlarda **gerçekten yan yana duran** çiftler.
 *
 * <p>
 * Bütün jeton kombinasyonlarını taramak anlamsız olurdu: çoğu hiç yan yana
 * gelmiyor ve gelmeyecek bir çiftin kontrastı bir şey söylemiyor. Liste,
 * bileşenlerin CSS'inden okunarak kuruldu.
 * </p>
 */
const PAIRS: readonly { readonly name: string; readonly fg: string; readonly bg: string }[] = [
  { name: "gövde metni", fg: "--text-1", bg: "--surface-2" },
  { name: "ikincil metin", fg: "--text-2", bg: "--surface-2" },
  { name: "kart metni", fg: "--text-1", bg: "--surface-1" },
  { name: "kart ikincil metni", fg: "--text-2", bg: "--surface-1" },
  { name: "tablo başlığı", fg: "--text-2", bg: "--surface-3" },
  { name: "birincil düğme", fg: "--accent-on", bg: "--accent" },
  { name: "tehlike düğmesi", fg: "--danger-on", bg: "--danger-solid" },
  { name: "hata metni", fg: "--danger", bg: "--danger-soft" },
  { name: "başarı rozeti", fg: "--success", bg: "--success-soft" },
  { name: "uyarı rozeti", fg: "--warning", bg: "--warning-soft" },
  { name: "vurgu rozeti", fg: "--accent-strong", bg: "--accent-soft" },
  { name: "bağlantı", fg: "--accent-strong", bg: "--surface-2" },
];

describe.each<Theme>(["light", "dark"])("%s tema — WCAG AA", (theme) => {
  it.each(PAIRS)("$name kontrastı 4.5:1 üstünde", ({ fg, bg }) => {
    const ratio = contrast(resolve(fg, theme), resolve(bg, theme));

    expect(
      ratio,
      `${fg} / ${bg} (${theme}) = ${ratio.toFixed(2)}:1 — AA sınırı ${AA_NORMAL}:1`,
    ).toBeGreaterThanOrEqual(AA_NORMAL);
  });
});

describe("iki koyu tema tanımı ayrışmıyor", () => {
  /**
   * <c>[data-theme="dark"]</c> ile <c>@media (prefers-color-scheme: dark)</c>
   * aynı değerleri vermek zorunda.
   *
   * <p>Ayrışırlarsa, işletim sistemi koyu temadayken ürün, düğmeyle koyu temaya
   * geçmiş kullanıcıya göre <b>başka</b> görünür — ve bunu kimse fark etmez,
   * çünkü iki yolu aynı anda denemek gerekiyor.</p>
   */
  it("aynı jeton kümesini aynı değerlerle tanımlıyorlar", () => {
    expect([...media.keys()].sort()).toEqual([...dark.keys()].sort());

    for (const [name, value] of dark) {
      expect(media.get(name), `${name} iki koyu tanım arasında ayrışmış`).toBe(value);
    }
  });
});

describe("hesap doğru", () => {
  // Bekçinin kendisi ölçülmeden güvenilmez: bilinen değerler tutuyor mu.
  it("bilinen kontrast oranlarını üretiyor", () => {
    expect(contrast("#000000", "#ffffff")).toBeCloseTo(21, 1);
    expect(contrast("#ffffff", "#ffffff")).toBeCloseTo(1, 5);
    // Bulguyu doğuran ölçüm: beyaz metin `red-500` üstünde AA'yı geçmiyor.
    expect(contrast("#ffffff", "#ef4444")).toBeLessThan(AA_NORMAL);
    // Ve `red-600` üstünde geçiyor — düzeltmenin dayanağı.
    expect(contrast("#ffffff", "#dc2626")).toBeGreaterThanOrEqual(AA_NORMAL);
  });
});
