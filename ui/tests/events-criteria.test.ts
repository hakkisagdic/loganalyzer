import { describe, expect, it } from "vitest";

import {
  MIN_FULL_TEXT_LENGTH,
  advisePagination,
  judgeQuery,
  readCriteria,
  toSearchBody,
  toSearchParams,
  type SearchCriteria,
} from "@/lib/events/criteria";

/**
 * T15'in iki kabul kriteri — ve ikisi de F1'de **ölçülmüş** sayılardan geliyor.
 *
 * <p>
 * Bu testler arayüzü değil <b>kuralı</b> tutuyor: kural doğruysa ekran nasıl
 * çizilirse çizilsin kısa sorgu tabloyu taramıyor ve kaynak filtresiz derin
 * sayfalama sessiz kalmıyor. Ekranın kendisi sunucu bileşeni ve <c>cookies()</c>
 * gerektirdiği için burada çizilmiyor; çizilen kısmın sınavı
 * <c>events-screen.test.tsx</c>.
 * </p>
 */

function criteria(overrides: Partial<SearchCriteria> = {}): SearchCriteria {
  return {
    fullText: "",
    sourceId: "",
    ownerGroup: "",
    vendor: "",
    parseStatuses: [],
    severityMin: undefined,
    proto: "",
    action: "",
    from: "",
    to: "",
    limit: 100,
    cursor: undefined,
    force: false,
    ...overrides,
  };
}

describe("kısa sorgu eşiği", () => {
  it("boş sorgu serbest — eşik yalnızca tam metin içindir", () => {
    expect(judgeQuery(criteria())).toEqual({ kind: "ready" });
  });

  it("F1'de ölçülen 9 karakterlik Türkçe sorgu koşulmuyor", () => {
    // `kullanıcı` (9) indeksten faydalanmıyor ve 1M satırda tam tarama yapıyor.
    const verdict = judgeQuery(criteria({ fullText: "kullanıcı" }));

    expect(verdict).toEqual({ kind: "too-short", length: 9 });
  });

  it("F1'de ölçülen 12 karakterlik Çince sorgu koşuluyor", () => {
    // Eşik alfabeden BAĞIMSIZ: bu bir Türkçe/CJK sorunu değil, uzunluk sorunu.
    const verdict = judgeQuery(criteria({ fullText: "用户登录失败，请检查凭据" }));

    expect(verdict).toEqual({ kind: "ready" });
  });

  it("sınırın tam üstü geçiyor, tam altı geçmiyor", () => {
    const atLimit = "a".repeat(MIN_FULL_TEXT_LENGTH);
    const below = "a".repeat(MIN_FULL_TEXT_LENGTH - 1);

    expect(judgeQuery(criteria({ fullText: atLimit }))).toEqual({ kind: "ready" });
    expect(judgeQuery(criteria({ fullText: below }))).toMatchObject({ kind: "too-short" });
  });

  it("uzunluk kod noktası sayıyor, UTF-16 birimi değil", () => {
    // Altı emoji: `String.length` 12 der ve sorgu eşiği GEÇERDİ — oysa indeks
    // açısından bu altı karakterlik bir sorgu ve tabloyu tarar.
    const query = "🔥".repeat(6);

    expect(query.length).toBe(12);
    expect(judgeQuery(criteria({ fullText: query }))).toEqual({ kind: "too-short", length: 6 });
  });

  it("kullanıcı ısrar ederse koşuluyor ama bu açık bir eylem", () => {
    expect(judgeQuery(criteria({ fullText: "kısa", force: true }))).toEqual({
      kind: "forced",
      length: 4,
    });
  });
});

describe("keyset sayfalamanın kaynak filtresi gereksinimi", () => {
  it("kaynak seçiliyse uyarı yok", () => {
    expect(advisePagination(criteria({ sourceId: "fg-ankara-01" }))).toBe("none");
  });

  it("kaynak yoksa ilk sayfada yönlendiriyor", () => {
    // Ekran dayatmıyor: filtresiz arama çalışıyor, yalnızca bedeli söyleniyor.
    expect(advisePagination(criteria())).toBe("suggest");
  });

  it("kaynak yokken sayfalamaya geçilirse uyarı sertleşiyor", () => {
    // Bedel asıl burada ödeniyor: filtresiz derin sayfa 1M satır okuyor,
    // `owner_group` + `source_id` ile 57k.
    const advice = advisePagination(
      criteria({ cursor: { afterTimestamp: "2026-08-16T12:00:00Z", afterEventId: "abc" } }),
    );

    expect(advice).toBe("warn");
  });
});

describe("adres çubuğu ↔ ölçütler", () => {
  it("yarım imleç imleç sayılmıyor", () => {
    // Yarım imleçle API'ye gitmek 400 alırdı; sessizce ilk sayfayı tekrarlamak
    // ise kullanıcının sayfaladığını sanmasına yol açardı.
    expect(readCriteria({ after_ts: "2026-08-16T12:00:00Z" }).cursor).toBeUndefined();
    expect(readCriteria({ after_id: "abc" }).cursor).toBeUndefined();
    expect(readCriteria({ after_ts: "2026-08-16T12:00:00Z", after_id: "abc" }).cursor).toEqual({
      afterTimestamp: "2026-08-16T12:00:00Z",
      afterEventId: "abc",
    });
  });

  it("bilinmeyen değerler sessizce geçmiyor", () => {
    const read = readCriteria({ parse_status: ["ok", "uydurma"], limit: "9999", severity_min: "42" });

    expect(read.parseStatuses).toEqual(["ok"]);
    // API üst sınırı 1000; buradan 9999 göndermek 400 üretirdi.
    expect(read.limit).toBe(100);
    expect(read.severityMin).toBeUndefined();
  });

  it("ölçütler adres çubuğuna dönüp geri okunabiliyor", () => {
    const original = criteria({
      fullText: "bağlantı reddedildi",
      sourceId: "fg-ankara-01",
      ownerGroup: "network/core",
      vendor: "fortinet",
      parseStatuses: ["ok", "partial"],
      severityMin: 4,
      proto: "tcp",
      action: "deny",
      limit: 200,
      cursor: { afterTimestamp: "2026-08-16T12:00:00Z", afterEventId: "id-1" },
    });

    const params = toSearchParams(original);
    const roundTrip = readCriteria(Object.fromEntries(
      [...new Set([...params.keys()])].map((key) => [key, params.getAll(key)]),
    ));

    expect(roundTrip).toEqual(original);
  });
});

describe("API gövdesine çevrim", () => {
  it("kaynak ve grup daraltması dizi olarak gidiyor", () => {
    const body = toSearchBody(criteria({ sourceId: "fg-1", ownerGroup: "network/core" }));

    expect(body.source_ids).toEqual(["fg-1"]);
    expect(body.owner_groups).toEqual(["network/core"]);
  });

  it("önem alt sınırı `gt` ile bir eksik değere çevriliyor", () => {
    // API'de `>=` yok. `severity_num` tamsayı olduğu için `gt 3` = `>= 4`.
    const body = toSearchBody(criteria({ severityMin: 4 }));

    expect(body.filters).toContainEqual({ field: "severity_num", op: "gt", values: ["3"] });
  });

  it("boş zaman aralığı gönderilmiyor — varsayılan API'de kalıyor", () => {
    const body = toSearchBody(criteria());

    expect(body.from).toBeUndefined();
    expect(body.to).toBeUndefined();
  });

  it("vendor, proto ve eylem izin listesindeki alan adlarıyla gidiyor", () => {
    const body = toSearchBody(criteria({ vendor: "fortinet", proto: "tcp", action: "deny" }));

    // Alan adları `EventReader.FilterableColumns` izin listesinden; uydurulan
    // bir ad API tarafında istisna üretiyor, sessizce yok sayılmıyor.
    expect(body.filters).toEqual([
      { field: "vendor", op: "eq", values: ["fortinet"] },
      { field: "proto", op: "eq", values: ["tcp"] },
      { field: "action", op: "eq", values: ["deny"] },
    ]);
  });

  it("imleç istekteki adlarla gidiyor", () => {
    // Yanıttaki `after_timestamp`/`after_event_id` ile birebir aynı adlar:
    // ekran aldığı imleci olduğu gibi geri gönderebiliyor.
    const body = toSearchBody(
      criteria({ cursor: { afterTimestamp: "2026-08-16T12:00:00Z", afterEventId: "id-1" } }),
    );

    expect(body.after_timestamp).toBe("2026-08-16T12:00:00Z");
    expect(body.after_event_id).toBe("id-1");
  });
});
