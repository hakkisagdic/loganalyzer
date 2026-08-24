import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { STORAGE_KEY, nextEntries } from "@/app/olaylar/SavedSearches";

import { ACCESS_TOKEN, REFRESH_TOKEN } from "./harness";

/**
 * **Tarayıcı deposuna ne yazılıyor** (T27).
 *
 * <p>
 * T13'ün token izolasyon bekçisi üç yerden ikisini kapatıyor: giriş akışının
 * <b>her yanıtının her baytı</b> ve çerezin kendisi taranıyor. Üçüncüsü —
 * <c>localStorage</c> — T27 taramasına kadar <b>hiç sınanmamıştı</b>, oysa
 * ticket onu açıkça sayıyor.
 * </p>
 *
 * <p>
 * Bugün oraya token yazılmıyor. Bu dosyanın işi o durumu <b>tutmak</b>: risk
 * "bugün sızıyor" değil, "yarın yeni bir yazıcı eklenir ve kimse bakmaz".
 * Rota işleyicileri sunucuda koştuğu için <c>localStorage</c>'a hiç
 * erişemiyor; oraya yazabilen tek şey istemci bileşenleri, dolayısıyla bekçi
 * de <b>yazıcı kümesini</b> denetliyor.
 * </p>
 */

const srcRoot = fileURLToPath(new URL("../src", import.meta.url));

/**
 * <b>Bilinen yazıcılar.</b> Kümesi sabit ve büyümesi görünür bir karar —
 * <c>ExpectedExemptCount</c>'un tarayıcı tarafındaki karşılığı (§8).
 *
 * <p>
 * Buraya bir satır eklemek, eklenen şeyin cihazda kalıcı olduğunu kabul etmek
 * demek. Liste elle tutuluyor ama <b>denetlenen küme değil beklenen küme</b>:
 * asıl tarama dosya sisteminden geliyor, liste yalnızca "bunları biliyoruz"
 * diyor. Bu fark bu depoda beş kez ödendi.
 * </p>
 */
const KNOWN_WRITERS: ReadonlyMap<string, string> = new Map([
  ["app/olaylar/SavedSearches.tsx", "Kayıtlı aramalar: yalnızca ad + uygulama içi URL."],
  ["components/ui/ThemeToggle.tsx", "Tema tercihi: `light` / `dark` sabiti."],
]);

/** `src/` altındaki bütün dosyalar — yansımanın dosya sistemi karşılığı. */
function sourceFiles(directory = srcRoot, prefix = ""): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const relative = prefix ? `${prefix}/${entry.name}` : entry.name;

    return entry.isDirectory()
      ? sourceFiles(`${directory}/${entry.name}`, relative)
      : relative.endsWith(".ts") || relative.endsWith(".tsx")
        ? [relative]
        : [];
  });
}

describe("tarayıcı deposuna yazan kod", () => {
  it("yalnızca bilinen dosyalar yazıyor", () => {
    // Kümesini elle yazılmış bir listeden DEĞİL dosya sisteminden buluyor;
    // liste yalnızca beklentiyi taşıyor. Yeni bir yazıcı eklendiğinde bu test
    // kırmızı yanıyor ve ekleyen kişi ne sakladığına karar vermek zorunda
    // kalıyor.
    const writers = sourceFiles().filter((file) =>
      /(localStorage|sessionStorage)\.setItem/.test(readFileSync(`${srcRoot}/${file}`, "utf8")),
    );

    expect([...writers].sort()).toEqual([...KNOWN_WRITERS.keys()].sort());
  });

  it("tema yazıcısı yalnızca tema sabitini saklıyor", () => {
    const source = readFileSync(`${srcRoot}/components/ui/ThemeToggle.tsx`, "utf8");
    const calls = source.match(/localStorage\.setItem\([^)]*\)/g) ?? [];

    expect(calls).toHaveLength(1);
    // `JSON.stringify` yok: saklanan şey bir nesne değil, iki değerli bir metin.
    // Nesne olsaydı alan eklemek sessizce mümkün olurdu.
    expect(calls[0]).not.toContain("JSON.stringify");
  });
});

describe("kayıtlı aramalarda saklanan şekil", () => {
  it("yalnızca `name` ve `href` yazılıyor", () => {
    // §8'in tarayıcı karşılığı: kayda eklenen her alan, kimse karar vermeden
    // cihazda kalıcı hâle gelir.
    const [entry] = nextEntries([], "son bir saat", "/olaylar?from=-1h");

    expect(Object.keys(entry!).sort()).toEqual(["href", "name"]);
  });

  it("saklanan hiçbir alanda token yok", () => {
    // Bugün geçmesi bekleniyor ve geçiyor. Değeri, YARIN kayda bir alan
    // eklendiğinde bu satırın hâlâ sorulmuş olması.
    const stored = JSON.stringify(
      nextEntries([], "aramam", `/olaylar?q=${encodeURIComponent("giriş başarısız")}`),
    );

    expect(stored).not.toContain(ACCESS_TOKEN);
    expect(stored).not.toContain(REFRESH_TOKEN);
  });

  it("anahtar ürüne özel bir önek taşıyor", () => {
    // Aynı kaynağı paylaşan başka bir uygulama varsa çarpışmasın; ve depoyu
    // elle inceleyen biri neyin bize ait olduğunu görebilsin.
    expect(STORAGE_KEY.startsWith("bizigo.")).toBe(true);
  });

  it("aynı ad iki kayıt bırakmıyor", () => {
    const first = nextEntries([], "aynı ad", "/olaylar?from=-1h");
    const second = nextEntries(first, "aynı ad", "/olaylar?from=-24h");

    expect(second).toHaveLength(1);
    expect(second[0]!.href).toBe("/olaylar?from=-24h");
  });
});
