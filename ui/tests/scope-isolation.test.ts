import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

/**
 * **Ekran katmanında kapsam ayrışması** (T27).
 *
 * <p>
 * <c>ScopeNegativeTests</c> (12 test) API tarafını kapsıyor: hiçbir uç başka
 * grubun verisini vermiyor. Ama F2'nin iddiası daha geniş —
 * <i>"<c>analyst.core</c> ile <c>analyst.edge</c> her ekranda farklı veri
 * görüyor"</i> — ve ekran katmanında bunu tutan bir şey yoktu.
 * </p>
 *
 * <p>
 * <b>Ekranın kapsamı delme yolu API'den geçmiyor.</b> Filtreyi
 * <c>IScopedQuery</c> uyguluyor ve istemcinin gönderdiği hiçbir ölçüt onu
 * genişletemiyor; ekranın kendi başına yapabileceği bir genişletme yok. Geriye
 * tek bir yol kalıyor ve o sessiz: <b>önbellek</b>. Sunucuda çizilmiş bir sayfa
 * iki kimlik arasında paylaşılırsa, <c>analyst.edge</c> <c>analyst.core</c>'un
 * verisini görür — hiçbir uç yanlış cevap vermeden, hiçbir hata çıkmadan.
 * </p>
 *
 * <p>
 * Bu yüzden bekçinin sorduğu soru şu: <b>sunucuda kapsamlı veri çizen her sayfa
 * dinamik mi.</b>
 * </p>
 *
 * <h3>Kural neden "her sayfa dinamik olsun" değil</h3>
 *
 * <p>
 * Çünkü ölçtüm ve öyle değil: üç sayfa veriyi <b>istemcide</b> çekiyor (tarayıcı
 * BFF vekiline konuşuyor), dolayısıyla sunucu çıktılarında kapsamlı hiçbir şey
 * yok ve önbelleklenmeleri bir sızıntı üretmiyor. Onları da dinamik yapmak,
 * kuralı gerekçesiz genişletip bir gün "zaten hepsi öyle" diye gevşetilmesine
 * zemin hazırlardı.
 * </p>
 */

const appRoot = fileURLToPath(new URL("../src/app", import.meta.url));

/**
 * Veriyi <b>istemcide</b> çeken sayfalar — sunucu çıktısında kapsamlı veri yok.
 *
 * <p>
 * Muafiyet bedava değil (§8): buraya bir satır eklemek, o sayfanın sunucuda
 * kapsamlı veri çizmediğini iddia etmek demek ve aşağıdaki ikinci test o
 * iddiayı <b>kaynaktan</b> doğruluyor.
 * </p>
 */
const CLIENT_FETCHED: ReadonlyMap<string, string> = new Map([
  ["alarmlar/page.tsx", "T23: `AlertsOverview` istemci bileşeni, veriyi vekilden çekiyor."],
  ["katalog/page.tsx", "T20: `CatalogOverview` istemci bileşeni."],
  ["katalog/inceleme/page.tsx", "T20: inceleme kuyruğu istemci bileşeni."],
]);

/** Sunucu tarafı veri erişiminin izleri. */
const SERVER_DATA_ACCESS = /apiForSession|from "@\/lib\/api\/server"|await fetch\(/;

function pages(directory = appRoot, prefix = ""): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const relative = prefix ? `${prefix}/${entry.name}` : entry.name;

    return entry.isDirectory()
      ? pages(`${directory}/${entry.name}`, relative)
      : entry.name === "page.tsx"
        ? [relative]
        : [];
  });
}

function source(page: string): string {
  return readFileSync(`${appRoot}/${page}`, "utf8");
}

const isDynamic = (page: string) => /dynamic\s*=\s*"force-dynamic"/.test(source(page));

describe("kapsamlı veri çizen sayfalar önbelleklenmiyor", () => {
  it("bekçi kümesini kendisi buluyor", () => {
    // Elle yazılmış bir sayfa listesi, bir sonraki ekran eklendiğinde sessizce
    // kör kalırdı — bu depoda aynı delik beş kez ısırdı.
    const found = pages();

    expect(found.length).toBeGreaterThan(10);
    expect(found).toContain("olaylar/page.tsx");
  });

  it("her sayfa ya dinamik ya da istemcide veri çekiyor", () => {
    const violations = pages().filter(
      (page) => !isDynamic(page) && !CLIENT_FETCHED.has(page),
    );

    expect(
      violations,
      "Bu sayfalar sunucuda çiziliyor ve `force-dynamic` taşımıyor: önbelleklenmiş " +
        "bir çıktı iki kimlik arasında paylaşılabilir.",
    ).toEqual([]);
  });

  it("muaf tutulan sayfalar gerçekten sunucuda veri çekmiyor", () => {
    // Muafiyetin kendi bekçisi. Bir sayfa istemci çekiminden sunucu çekimine
    // geçerse listede kalması onu SESSİZCE önbelleklenebilir bırakırdı — yani
    // muafiyet, kapatmak için var olduğu deliği açardı.
    for (const page of CLIENT_FETCHED.keys()) {
      expect(SERVER_DATA_ACCESS.test(source(page)), `${page} sunucuda veri çekiyor`).toBe(false);
    }
  });

  it("muafiyet listesi bayat değil", () => {
    // Silinmiş ya da yeniden adlandırılmış bir sayfa listede kalırsa, liste
    // "üç muafiyet var" demeye devam eder ve kimse bakmaz.
    const found = new Set(pages());

    for (const page of CLIENT_FETCHED.keys()) {
      expect(found.has(page), `${page} artık yok — muafiyet listesi bayat`).toBe(true);
    }
  });

  it("kapsamlı veri çizen bilinen sayfalar dinamik", () => {
    // Adları elle yazılı ve bu bilinçli: yukarıdaki kural "hiçbir ihlal yok"
    // diyor, bu satır "bu üçü gerçekten denetleniyor" diyor. Kural bir gün
    // yanlışlıkla herkesi muaf ederse bu test yine de düşer.
    for (const page of ["olaylar/page.tsx", "olaylar/[id]/page.tsx", "kaynaklar/page.tsx"]) {
      expect(isDynamic(page), `${page} dinamik değil`).toBe(true);
    }
  });
});
