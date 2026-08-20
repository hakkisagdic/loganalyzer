import { describe, expect, it } from "vitest";

import { sectionAt, suggest } from "@/lib/parsers/schema";
import { appendStep, NEW_PARSER_TEMPLATE } from "@/lib/parsers/template";
import { tokenizeLine } from "@/lib/parsers/yaml";

/**
 * Parser editörünün **saf** katmanı (T19).
 *
 * <p>
 * Vurgulama ve tamamlama DOM'a dokunmayan fonksiyonlar; ekranda çalışıp
 * çalışmadıkları ancak burada gösterilebiliyor. Sınanan şey görsel doğruluk
 * değil — yanlış renk kimseyi yanıltmaz — <b>yanlış öneri</b>: kullanıcının
 * yazdığını bozan ya da olmayan bir anahtarı öneren tamamlama, hiç
 * tamamlama olmamasından kötü.
 * </p>
 */

describe("YAML belirteçleri", () => {
  it("anahtarı, iki noktayı ve metni ayırıyor", () => {
    const tokens = tokenizeLine("  id: fortinet.fortigate.traffic");

    expect(tokens.find((t) => t.kind === "key")?.text).toBe("id");
    expect(tokens.some((t) => t.kind === "punct" && t.text.includes(":"))).toBe(true);
  });

  it("tırnak içindeki `#` yorum başlatmıyor", () => {
    // Grok pattern'leri `#` içerebiliyor; satırın yarısını griye boyamak,
    // kullanıcının pattern'i okuyamaması demekti.
    const tokens = tokenizeLine("      - '^%{WORD:tag}#%{NUMBER:code}$'");

    expect(tokens.some((t) => t.kind === "comment")).toBe(false);
    expect(tokens.some((t) => t.kind === "string")).toBe(true);
  });

  it("gerçek yorumu yakalıyor", () => {
    const tokens = tokenizeLine("  contains: [\"X\"]  # ön filtre");

    expect(tokens.find((t) => t.kind === "comment")?.text).toContain("ön filtre");
  });

  it("şablonu metinden ayırıyor", () => {
    // `{{ alan }}` `map` bloğunun anlamını taşıyan şey; yazarken en çok
    // bakılan yer olduğu için ayrı vurgulanıyor.
    const tokens = tokenizeLine('    src_ip: "{{ src_ip }}"');

    expect(tokens.find((t) => t.kind === "template")?.text).toBe("{{ src_ip }}");
  });

  it("kapanmayan tırnak satırı yutmuyor", () => {
    // Yazarken sürekli oluşan hâl: yarım kalmış tırnak bir sonraki satıra
    // taşarsa editör yazdıkça renk değiştirir ve okunmaz hâle gelir.
    const tokens = tokenizeLine("    input: 'yarım kalmış");

    expect(tokens.map((t) => t.text).join("")).toBe("    input: 'yarım kalmış");
  });

  it("hiçbir satırda karakter kaybetmiyor", () => {
    // Vurgulama katmanı `<textarea>` ile ÜST ÜSTE duruyor. Bir karakter
    // düşerse ya da eklenirse imleç yazının yanına kayar ve bunu kimse fark
    // etmez — bu yüzden iskeletin her satırı birebir korunmalı.
    for (const line of NEW_PARSER_TEMPLATE.split("\n")) {
      expect(tokenizeLine(line).map((token) => token.text).join("")).toBe(line);
    }
  });
});

describe("Şema bölümü", () => {
  const yaml = [
    "metadata:",
    "  id: a.b.c",
    "match:",
    "  contains: []",
    "pipeline:",
    "  - grok:",
    "map:",
    "  core:",
    "tests:",
    "  - name: t",
    "    expect:",
    "      parse_status: ok",
  ].join("\n");

  it("en son kök anahtardan bölümü buluyor", () => {
    expect(sectionAt(yaml, 1)).toBe("metadata");
    expect(sectionAt(yaml, 3)).toBe("match");
    expect(sectionAt(yaml, 5)).toBe("pipeline");
    expect(sectionAt(yaml, 7)).toBe("map");
  });

  it("`expect` kendi anahtar uzayı", () => {
    // `tests` anahtarları (`name`, `input`) `expect` içinde önerilseydi öneri
    // gürültüye dönerdi: orada beklenen şey `core.*`/`parse_status`.
    expect(sectionAt(yaml, 9)).toBe("tests");
    expect(sectionAt(yaml, 11)).toBe("expect");
  });
});

describe("Şema tamamlama", () => {
  function completeAfter(text: string) {
    return suggest(text, text.length);
  }

  it("kökte kök anahtarları öneriyor", () => {
    const completion = completeAfter("met");

    expect(completion?.prefix).toBe("met");
    expect(completion?.options.map((o) => o.name)).toContain("metadata");
  });

  it("metadata içinde metadata anahtarlarını öneriyor", () => {
    const completion = completeAfter("metadata:\n  ver");

    expect(completion?.options.map((o) => o.name)).toEqual(["version"]);
  });

  it("boru hattında adım tiplerini öneriyor", () => {
    const completion = completeAfter("pipeline:\n  - ");

    expect(completion?.options.map((o) => o.name)).toContain("grok");
    expect(completion?.options.map((o) => o.name)).toContain("kv");
  });

  it("değer yazarken öneri vermiyor", () => {
    // İmlecin solunda iki nokta varsa kullanıcı DEĞER yazıyor; oraya anahtar
    // önermek yazdığını bozardı.
    expect(completeAfter("metadata:\n  version: 1.0")).toBeNull();
  });

  it("tam yazılmış anahtarı tekrar önermiyor", () => {
    expect(completeAfter("metadata:\n  version")).toBeNull();
  });

  it("`expect` içinde core alanlarını öneriyor", () => {
    const completion = completeAfter("tests:\n  - name: t\n    expect:\n      core.user");

    expect(completion?.options.map((o) => o.name)).toContain("core.user_name");
  });
});

describe("Adım ekleme", () => {
  it("adımı boru hattının sonuna, `map`'ten önce koyuyor", () => {
    // Metnin sonuna eklemek `map`'ten sonra yazmak demekti ve şema hatası
    // veriyordu — yani "adım ekle" düğmesi her basışta bozuk YAML üretirdi.
    const next = appendStep(NEW_PARSER_TEMPLATE, "- kv:\n    field: message");
    const lines = next.split("\n");

    const step = lines.findIndex((line) => line.includes("- kv:"));
    const map = lines.findIndex((line) => /^map\s*:/.test(line));
    const pipeline = lines.findIndex((line) => /^pipeline\s*:/.test(line));

    expect(step).toBeGreaterThan(pipeline);
    expect(step).toBeLessThan(map);
  });

  it("boru hattı yoksa oluşturuyor", () => {
    const next = appendStep("metadata:\n  id: a\n", "- kv:\n    field: message");

    expect(next).toContain("pipeline:");
    expect(next).toContain("  - kv:");
  });

  it("eklenen adım girintili", () => {
    const next = appendStep(NEW_PARSER_TEMPLATE, "- kv:\n    field: message");

    expect(next).toContain("\n  - kv:\n      field: message");
  });
});

describe("Yeni parser iskeleti", () => {
  it("gömülü test taşıyor", () => {
    // Testsiz parser yayınlanamıyor. İskeletin testsiz gelmesi, kullanıcının
    // ilk denemesinde kapıya takılması demekti — hangi hatanın kendi
    // yazdığından geldiğini gizlerdi.
    expect(NEW_PARSER_TEMPLATE).toContain("tests:");
    expect(NEW_PARSER_TEMPLATE).toContain("expect:");
    expect(NEW_PARSER_TEMPLATE).toContain("parse_status: ok");
  });

  it("`match`in ne olmadığını yazıyor", () => {
    // T08 raporu #4: `match` bir doğruluk garantisi değil ve envanter bağı
    // olan trafikte hiç çalışmıyor. Bunun katalogda ikinci kez keşfedilmesi
    // pahalı oldu; iskeletten öğrenilmesi ucuz.
    expect(NEW_PARSER_TEMPLATE).toContain("PERFORMANS için");
  });
});
