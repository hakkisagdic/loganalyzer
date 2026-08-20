import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";

import { CodeEditor } from "@/components/ui/CodeEditor";
import { GateReport } from "@/app/parserlar/GateReport";
import { TryPanel } from "@/app/parserlar/TryPanel";
import type { ParseOutcome, ParserGate, ParserTry } from "@/lib/parsers/types";
import { tokenizeLine } from "@/lib/parsers/yaml";

/**
 * Parser editörünün **ölçülebilir** ekran davranışı (T19).
 *
 * <p>
 * Görsel tutarlılık burada sınanamaz — o T28'in işi. Sınanabilen şey, bu
 * ticket'ın kabul kriterlerinin gerçekten ekranda olduğu: zaman aşımının
 * "uymadı" diye okunmaması, şema hatasının satır numarasıyla görünmesi ve
 * kapının hangi aşamada durduğunun yazması.
 * </p>
 */

function outcome(overrides: Partial<ParseOutcome> = {}): ParseOutcome {
  return {
    parser_id: "test.parser",
    parser_version: "1.0.0",
    status: "ok",
    timed_out: false,
    timestamp: null,
    tags: [],
    fields: {},
    core: {},
    ocsf: {},
    otel: {},
    issues: [],
    ...overrides,
  };
}

function gate(overrides: Partial<ParserGate> = {}): ParserGate {
  return {
    ok: true,
    stage: "passed",
    parser_id: "test.parser",
    version: "1.0.0",
    passing_tests: 1,
    schema_errors: [],
    redos: [],
    tests: [],
    errors: [],
    warnings: [],
    ...overrides,
  };
}

function tryResult(overrides: Partial<ParserTry> = {}): ParserTry {
  return { mode: "draft", result: outcome(), draft: gate(), dispatch: null, ...overrides };
}

describe("Deneme sonucu — zaman aşımı", () => {
  it("`timed_out` sonucu 'ölçülemedi' diye anlatıyor, 'uymadı' diye değil", () => {
    // Kabul kriteri. `matchTimeout` DUVAR SAATİNİ ölçüyor: yüklü bir makinede
    // sağlıklı bir parser da zaman aşımına uğruyor (T08 raporu #10). İkisini
    // karıştırmak sağlıklı bir parser'ı karantinaya sokar.
    const html = renderToStaticMarkup(
      <TryPanel
        result={tryResult({ result: outcome({ status: "failed", timed_out: true }) })}
        loading={false}
        error={null}
        hasLine
      />,
    );

    expect(html).toContain("Ölçülemedi");
    expect(html).toContain("uymadı");
    expect(html).toContain("<strong>gelmiyor</strong>");
  });

  it("zaman aşımı yokken uyarı görünmüyor", () => {
    // Uyarının değeri, HER SEFERİNDE çıkmamasında: sürekli görünen bir uyarı
    // okunmaz hâle gelir ve gerçekten gerektiğinde de fark edilmez.
    const html = renderToStaticMarkup(
      <TryPanel
        result={tryResult({ result: outcome({ status: "failed", timed_out: false }) })}
        loading={false}
        error={null}
        hasLine
      />,
    );

    expect(html).not.toContain("Ölçülemedi");
  });

  it("örnek satır yokken uydurma örnek uyarısı veriyor", () => {
    const html = renderToStaticMarkup(
      <TryPanel result={null} loading={false} error={null} hasLine={false} />,
    );

    expect(html).toContain("Uydurma");
  });
});

describe("Deneme sonucu — dispatcher kademesi", () => {
  it("literal filtreye düşen satırda envanterin eksikliğini söylüyor", () => {
    // Ticket'ın taşıyıcı gözlemi: envanter bağı yerine literal filtreye düşen
    // satır, parser doğru olsa bile envanterin eksik olduğunu söylüyor. Metin
    // sunucudan geliyor — aynı yorumu iki yerde tutmamak için.
    const html = renderToStaticMarkup(
      <TryPanel
        result={tryResult({
          dispatch: {
            tier: "candidate",
            reason: "Envanter bağı yok ya da tutmadı; satır literal ön filtreden geçen adaylarla denendi.",
            attempts: 7,
            result: outcome(),
          },
        })}
        loading={false}
        error={null}
        hasLine
      />,
    );

    expect(html).toContain("Kademe 2 — literal ön filtre");
    expect(html).toContain("Envanter bağı yok");
    expect(html).toContain("7 parser denendi");
    expect(html).toContain("yeterince daraltmadığını");
  });

  it("envanter bağıyla gelen sonuçta uyarı yok", () => {
    const html = renderToStaticMarkup(
      <TryPanel
        result={tryResult({
          dispatch: { tier: "inventory_bound", reason: "Envanterde bağlı.", attempts: 1, result: outcome() },
        })}
        loading={false}
        error={null}
        hasLine
      />,
    );

    expect(html).toContain("Kademe 1 — envanter bağı");
    expect(html).not.toContain("yeterince daraltmadığını");
  });
});

describe("Kapı raporu", () => {
  it("şema hatasını satır ve sütunla gösteriyor", () => {
    // Kabul kriteri: "şema hatası satır numarasıyla gösteriliyor".
    const html = renderToStaticMarkup(
      <GateReport
        gate={gate({
          ok: false,
          stage: "schema",
          schema_errors: [{ line: 37, column: 7, message: "`grok` adımı en az bir `patterns` girdisi ister." }],
        })}
      />,
    );

    expect(html).toContain("Satır 37:7");
    expect(html).toContain("Şema kapısında durdu");
    expect(html).toContain("patterns");
  });

  it("hangi kapıda takıldığını ve neden önemli olduğunu yazıyor", () => {
    const html = renderToStaticMarkup(
      <GateReport
        gate={gate({
          ok: false,
          stage: "redos",
          redos: [
            {
              code: "GROK003",
              severity: "warning",
              blocking: true,
              message: "Doğrusal motorda derlenemedi.",
              fragment: "(?<=X)",
            },
          ],
        })}
      />,
    );

    expect(html).toContain("ReDoS kapısında durdu");
    expect(html).toContain("Yayını durduran pattern bulguları");
    // Şiddeti "warning" ama YAYINDA HATA: ekranın "bu sadece uyarı" demesini
    // engelleyen tek şey bu ayrım.
    expect(html).not.toContain("Yayını durdurmayan bulgular");
  });

  it("düşen testte beklenen ve gerçeği yan yana gösteriyor", () => {
    const html = renderToStaticMarkup(
      <GateReport
        gate={gate({
          ok: false,
          stage: "tests",
          passing_tests: 0,
          tests: [
            {
              name: "temel",
              line: 52,
              passed: false,
              expectations: [
                { key: "core.action", expected: '"deny"', actual: '"accept"', passed: false },
              ],
            },
          ],
        })}
      />,
    );

    // Hangisinin yanlış olduğu — parser mı test mi — ancak ikisi birlikte
    // görününce belli oluyor.
    expect(html).toContain("beklenen &quot;deny&quot;");
    expect(html).toContain("gerçek &quot;accept&quot;");
    expect(html).toContain("satır 52");
  });

  it("testsiz taslakta sebebi açıkça söylüyor", () => {
    const html = renderToStaticMarkup(<GateReport gate={gate({ ok: false, stage: "tests", tests: [] })} />);

    expect(html).toContain("Testsiz parser yayınlanamıyor");
  });
});

describe("Kod editörü", () => {
  const markup = renderToStaticMarkup(
    <CodeEditor
      label="Parser YAML"
      value={"metadata:\n  id: a.b.c\n  version: 1.0.0"}
      onChange={() => {}}
      tokenize={tokenizeLine}
      markers={[{ line: 2, message: "`id` boş olamaz." }]}
    />,
  );

  it("her satır için bir numara çiziyor", () => {
    // Oluk satır sayısını metinden türetiyor; bir satır eksik ya da fazla
    // olsaydı numaralar hata satırıyla hizasını kaybederdi.
    expect(markup).toContain("> 1</span>");
    expect(markup).toContain("> 3</span>");
    expect(markup.match(/_editorGutterLine_/g)).toHaveLength(3);
  });

  it("hatayı renkle DEĞİL, metinle de veriyor", () => {
    // Kırmızı bir satır numarası, kırmızıyı göremeyen için hiçbir şey
    // söylemiyor (WCAG 1.4.1). Asıl bilgi editörün altındaki listede.
    expect(markup).toContain("Satır 2:");
    expect(markup).toContain("boş olamaz");
    // İşaret HATALI satırda, başkasında değil.
    expect(markup).toContain("●2</span>");
    expect(markup).not.toContain("●1</span>");
  });

  it("vurgulama katmanı ekran okuyucudan gizli", () => {
    // Metin `<textarea>`da zaten var; iki kez okutmak gürültü üretirdi.
    expect(markup).toContain('aria-hidden="true"');
    expect(markup).toContain('aria-invalid="true"');
  });
});
