import { afterEach, describe, expect, it, vi } from "vitest";

import { api } from "@/lib/api/client";
import {
  ApiError,
  ForbiddenError,
  NotFoundError,
  RateLimitedError,
  SessionExpiredError,
  TransportError,
} from "@/lib/api/errors";

/**
 * T14 — istemcinin hata sözleşmesi.
 *
 * <p>
 * Durum kodları arayüzde farklı yerlere gidiyor ve karıştırılmaları
 * kullanıcıya yanlış şeyi söyler: 401 "yeniden giriş", 403 "yetkiniz yok",
 * 404 "bulunamadı". Özellikle son ikisi: F1'de <b>kapsam dışı bir olay 404
 * dönüyor</b>, 403 değil — 403 "böyle bir olay var" bilgisini sızdırırdı.
 * </p>
 */

function respondWith(status: number, body: unknown, contentType = "application/json") {
  globalThis.fetch = vi.fn(async () =>
    new Response(body === undefined ? "" : JSON.stringify(body), {
      status,
      headers: { "content-type": contentType },
    }),
  ) as unknown as typeof fetch;
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("hata eşlemesi", () => {
  it("401 oturum hatasına çevriliyor", async () => {
    respondWith(401, { error: "Oturum yok.", hint: "Yeniden giriş yapın." });

    const error = await api.get("/auth/me").catch((e: unknown) => e);

    expect(error).toBeInstanceOf(SessionExpiredError);
    expect((error as ApiError).hint).toBe("Yeniden giriş yapın.");
  });

  it("403 yetki hatasına çevriliyor", async () => {
    respondWith(403, { error: "Yetkiniz yok." });

    await expect(api.get("/v1/parsers")).rejects.toBeInstanceOf(ForbiddenError);
  });

  it("404 bulunamadıya çevriliyor ve ipucu korunuyor", async () => {
    respondWith(404, {
      error: "Olay bulunamadı.",
      hint: "Nesne henüz yüklenmemiş olabilir; /v1/health/pipeline arşiv gecikmesini gösterir.",
    });

    const error = (await api
      .get("/v1/events/{id}", { path: { id: "abc" } })
      .catch((e: unknown) => e)) as NotFoundError;

    expect(error).toBeInstanceOf(NotFoundError);
    // Kapsam dışı olay da buraya düşüyor; mesaj "yetkiniz yok" DEMEMELİ,
    // yoksa 404'ün varlık sebebi olan bilgi gizleme bozulur.
    expect(error.message).not.toContain("yetki");
    expect(error.hint).toContain("/v1/health/pipeline");
  });

  it("429 hız sınırına çevriliyor", async () => {
    respondWith(429, undefined);

    await expect(api.get("/v1/sources")).rejects.toBeInstanceOf(RateLimitedError);
  });

  it("gövdesi olmayan hata için okunabilir metin üretiliyor", async () => {
    respondWith(403, undefined);

    const error = (await api.get("/v1/parsers").catch((e: unknown) => e)) as ForbiddenError;

    expect(error.message).toBe("Bu işlem için yetkiniz yok.");
    expect(error.hint).toBeTruthy();
  });

  it("JSON yerine HTML gelirse ham gövde yutulmuyor", async () => {
    respondWith(500, undefined, "text/html");
    globalThis.fetch = vi.fn(async () =>
      new Response("<html>502 Bad Gateway</html>", {
        status: 502,
        headers: { "content-type": "text/html" },
      }),
    ) as unknown as typeof fetch;

    const error = (await api.get("/v1/sources").catch((e: unknown) => e)) as ApiError;

    expect(error).toBeInstanceOf(ApiError);
    expect(error.hint).toContain("502 Bad Gateway");
  });

  it("ağ kesintisi taşıma hatasına çevriliyor", async () => {
    globalThis.fetch = vi.fn(async () => {
      throw new Error("network down");
    }) as unknown as typeof fetch;

    await expect(api.get("/v1/sources")).rejects.toBeInstanceOf(TransportError);
  });
});

describe("adres kurulumu", () => {
  it("bütün istekler BFF vekilinden geçiyor", async () => {
    const spy = vi.fn<typeof fetch>(async () => Response.json({ ok: true }));
    globalThis.fetch = spy;

    await api.get("/v1/events/{id}", { path: { id: "a b/c" } });

    // Mutlak API adresi istemcide YOK: tarayıcı Bizigo.Api'yi hiç görmüyor.
    expect(spy.mock.calls[0]?.[0]).toBe("/api/bff/v1/events/a%20b%2Fc");
  });

  it("sorgu parametreleri ve diziler ekleniyor", async () => {
    const spy = vi.fn<typeof fetch>(async () => Response.json({ ok: true }));
    globalThis.fetch = spy;

    await api.get("/v1/sources", {
      query: { limit: 50, owner_group: ["network/core", "network/edge"] } as never,
    });

    const url = String(spy.mock.calls[0]?.[0]);
    expect(url).toContain("limit=50");
    expect(url).toContain("owner_group=network%2Fcore");
    expect(url).toContain("owner_group=network%2Fedge");
  });

  it("eksik yol parametresi sessizce geçmiyor", async () => {
    globalThis.fetch = vi.fn(async () => Response.json({})) as unknown as typeof fetch;

    await expect(api.get("/v1/events/{id}", { path: {} as never })).rejects.toBeInstanceOf(TypeError);
  });
});
