import { beforeEach, describe, expect, it, vi } from "vitest";

import { readTelemetryConfig, telemetryState } from "@/lib/telemetry/config";
import { EVENTS, allowedProperties } from "@/lib/telemetry/events";
import { pseudonymousId } from "@/lib/telemetry/identity";
import {
  DENIED_AUTOMATIC_PROPERTIES,
  MAX_STRING_LENGTH,
  scrubPathname,
  scrubProperties,
  scrubUrl,
} from "@/lib/telemetry/scrub";

/**
 * Telemetri süzgecinin bekçileri.
 *
 * <p>
 * Ölçtükleri şey "kod çalışıyor mu" değil, <b>müşteri verisi kaçabiliyor mu</b>.
 * Bu ürün log okuyor; bir arama kutusuna yazılan metin bir IP, bir kullanıcı
 * adı ya da bir sır olabilir. Aşağıdaki her testin karşılığı, kırmızı
 * yandığında sızacak olan somut bir alan.
 * </p>
 */

describe("scrubPathname — yol kalıba iniyor", () => {
  const kimlikler: [input: string, expected: string, neden: string][] = [
    [
      "/kaynaklar/9f2c1b7e-4a3d-4c8f-9e1a-2b3c4d5e6f70/olaylar",
      "/kaynaklar/:id/olaylar",
      "UUID — kaynak envanterinin kendisi",
    ],
    ["/parserlar/1428", "/parserlar/:id", "sayısal kimlik"],
    ["/rca/9a8b7c6d5e4f3a2b", "/rca/:id", "uzun onaltılık"],
    ["/olaylar", "/olaylar", "kimlik yok — olduğu gibi kalıyor"],
    ["/", "/", "kök"],
    ["/katalog/fw-core-01", "/katalog/fw-core-01", "kimlik şekli DEĞİL — ekran adı korunuyor"],
  ];

  it.each(kimlikler)("%s → %s (%s)", (input, expected) => {
    expect(scrubPathname(input)).toBe(expected);
  });

  it("eğik çizgiyle başlamayan girdi köke düşüyor", () => {
    expect(scrubPathname("olaylar/1")).toBe("/");
  });
});

describe("scrubUrl — sorgu dizesi TAMAMEN düşüyor", () => {
  /**
   * Bu ekranda sorgu dizesi kullanıcının log arama ölçütü — yani aranan şeyin
   * kendisi. Tek bir alanını kurtarmaya çalışmak, kalanının bir gün sızmasına
   * kapı bırakmak olurdu.
   */
  it("arama ölçütü gitmiyor", () => {
    const url = "http://localhost:3000/olaylar?q=admin%40musteri.com&ip=10.0.4.17#satir-42";

    const result = scrubUrl(url);

    expect(result).toBe("/olaylar");
    expect(result).not.toContain("admin");
    expect(result).not.toContain("10.0.4.17");
    expect(result).not.toContain("satir-42");
  });

  it("kimlik taşıyan yol da kalıba iniyor", () => {
    expect(scrubUrl("https://bizigo.local/kaynaklar/4242?tab=ham")).toBe("/kaynaklar/:id");
  });

  it("ayrıştırılamayan girdi köke düşüyor", () => {
    // `"::::"` DEĞİL — o taban adresle birlikte `/::::` olarak ayrıştırılıyor.
    // Gerçekten atan girdiler bunlar; `catch` dalının ölü olmadığı ölçüldü.
    expect(scrubUrl("http://")).toBe("/");
    expect(scrubUrl("//")).toBe("/");
  });

  it("betik şeması yol üretemiyor", () => {
    // `javascript:alert(1)` → pathname `alert(1)`, yani eğik çizgiyle
    // başlamıyor ve `scrubPathname` onu köke düşürüyor.
    expect(scrubUrl("javascript:alert(1)")).toBe("/");
  });

  it("başka bir alan adı verilse bile yalnızca YOL gidiyor", () => {
    expect(scrubUrl("https://sahte.site/kaynaklar/42")).toBe("/kaynaklar/:id");
  });
});

describe("scrubProperties — beyaz liste", () => {
  it("listede olmayan alan sessizce ATILIYOR, olay bozulmuyor", () => {
    const result = scrubProperties(
      {
        criteria_count: 3,
        // Bir ekranın yanlışlıkla geçireceği şey tam olarak bu:
        sorgu: "src_ip=10.0.4.17 AND user=admin",
        raw_line: "Aug 21 10:14:22 fw-core-01 %ASA-6-302013: Built connection",
      },
      allowedProperties("event_search_run"),
    );

    expect(result).toEqual({ criteria_count: 3 });
    expect(Object.keys(result)).not.toContain("sorgu");
    expect(Object.keys(result)).not.toContain("raw_line");
  });

  it("uzun metin kesiliyor", () => {
    const result = scrubProperties({ error_kind: "x".repeat(500) }, ["error_kind"]);

    expect((result.error_kind as string).length).toBe(MAX_STRING_LENGTH);
  });

  it("iç içe nesne taşınmıyor", () => {
    // Bir nesneyi olduğu gibi geçirmek, içine YARIN konacak her alanı
    // geçirmek demek — ve o alan bir log satırı olabilir.
    const result = scrubProperties({ detay: { ip: "10.0.4.17" } }, ["detay"]);

    expect(result).toEqual({});
  });

  it("NaN/Infinity hiç gitmiyor — `null` olarak gitmiyor", () => {
    expect(scrubProperties({ duration_ms: Number.NaN }, ["duration_ms"])).toEqual({});
    expect(scrubProperties({ duration_ms: Number.POSITIVE_INFINITY }, ["duration_ms"])).toEqual({});
  });

  it("dizideki metinler de kesiliyor ve dizi sınırlanıyor", () => {
    const result = scrubProperties({ liste: Array.from({ length: 50 }, () => "a".repeat(300)) }, [
      "liste",
    ]);

    const liste = result.liste as string[];

    expect(liste.length).toBe(20);
    expect(liste[0]?.length).toBe(MAX_STRING_LENGTH);
  });
});

describe("olay kataloğu", () => {
  it("her olay adı snake_case (CLAUDE.md §8)", () => {
    for (const definition of Object.values(EVENTS)) {
      expect(definition.name).toMatch(/^[a-z][a-z0-9_]*$/);
    }
  });

  it("her olayın alan listesi de snake_case", () => {
    for (const definition of Object.values(EVENTS)) {
      for (const property of definition.properties) {
        expect(property).toMatch(/^[a-z][a-z0-9_]*$/);
      }
    }
  });

  it("her olay ne ölçtüğünü yazıyor", () => {
    // Panoyu kuran kişinin okuduğu tek belge burası; boş bırakılmış bir olay
    // üç ay sonra kimsenin anlamını bilmediği bir sütun oluyor.
    for (const definition of Object.values(EVENTS)) {
      expect(definition.describes.length).toBeGreaterThan(20);
    }
  });

  it("hiçbir olay serbest metin alanı taşımıyor", () => {
    // Bu liste bilinçli olarak dar. Yeni bir olay `query`/`message`/`raw` gibi
    // bir alan getirirse bu test kırmızı yanıyor ve karar bilinçli veriliyor.
    const yasak = ["query", "sorgu", "message", "mesaj", "raw", "ham", "text", "url", "email"];

    for (const definition of Object.values(EVENTS)) {
      for (const property of definition.properties) {
        expect(yasak, `${definition.name}.${property}`).not.toContain(property);
      }
    }
  });
});

describe("pseudonymousId — kimlik takma ad", () => {
  const sub = "6b1f4d2a-7c8e-4b3a-9d1f-2e3a4b5c6d7e";

  it("aynı kullanıcı aynı kimliği alıyor", () => {
    expect(pseudonymousId(sub, "tuz")).toBe(pseudonymousId(sub, "tuz"));
  });

  it("ham `sub` çıktının İÇİNDE geçmiyor", () => {
    expect(pseudonymousId(sub, "tuz")).not.toContain(sub);
    expect(pseudonymousId(sub, "tuz")).not.toContain("6b1f4d2a");
  });

  it("farklı tuz farklı kimlik — iki kurulum birleştirilemiyor", () => {
    expect(pseudonymousId(sub, "kurulum-a")).not.toBe(pseudonymousId(sub, "kurulum-b"));
  });
});

describe("telemetryState — üç durum", () => {
  const kaydedilen = { ...process.env };

  beforeEach(() => {
    vi.unstubAllEnvs();
    process.env = { ...kaydedilen };
    delete process.env.TELEMETRY_ENABLED;
    delete process.env.TELEMETRY_PROJECT_KEY;
    delete process.env.TELEMETRY_IDENTIFY_USERS;
    delete process.env.TELEMETRY_IDENTITY_SALT;
  });

  it("varsayılan KAPALI", () => {
    expect(telemetryState().status).toBe("disabled");
  });

  const kapatanlar = ["", "false", "0", "off", "TRUEE", "evet"];

  it.each(kapatanlar)("`%s` değeri telemetriyi AÇMIYOR", (value) => {
    process.env.TELEMETRY_ENABLED = value;

    expect(telemetryState().status).toBe("disabled");
  });

  it("açık ama anahtarsız → misconfigured, sessizce kapalı DEĞİL", () => {
    process.env.TELEMETRY_ENABLED = "true";

    const state = telemetryState();

    expect(state.status).toBe("misconfigured");
    expect(state.status === "misconfigured" && state.missing).toContain("TELEMETRY_PROJECT_KEY");
  });

  it("kimlik bağlama açık ama tuzsuz → misconfigured, anonime DÜŞMÜYOR", () => {
    process.env.TELEMETRY_ENABLED = "true";
    process.env.TELEMETRY_PROJECT_KEY = "phc_test";
    process.env.TELEMETRY_IDENTIFY_USERS = "true";

    const state = telemetryState();

    expect(state.status).toBe("misconfigured");
    expect(state.status === "misconfigured" && state.missing).toContain("TELEMETRY_IDENTITY_SALT");
  });

  it("kimlik bağlama kapalıyken tuz istenmiyor", () => {
    process.env.TELEMETRY_ENABLED = "true";
    process.env.TELEMETRY_PROJECT_KEY = "phc_test";

    expect(telemetryState().status).toBe("ok");
  });

  it("varsayılan hedef EU ve kimlik bağlama kapalı", () => {
    const config = readTelemetryConfig();

    expect(config.host).toBe("https://eu.i.posthog.com");
    expect(config.identifyUsers).toBe(false);
  });
});

describe("otomatik özellik kara listesi", () => {
  it("ham URL taşıyan alanların hepsi listede", () => {
    for (const key of ["$current_url", "$referrer", "$pathname", "$initial_current_url"]) {
      expect(DENIED_AUTOMATIC_PROPERTIES).toContain(key);
    }
  });

  it("istemci IP'si listede", () => {
    expect(DENIED_AUTOMATIC_PROPERTIES).toContain("$ip");
  });
});
