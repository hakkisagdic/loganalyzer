import { NextRequest } from "next/server";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

/**
 * Telemetri vekilinin bekçileri.
 *
 * <p>
 * En önemlisi <b>oturum kaydı ucunun (`/s`) reddi</b>. İstemci tarafında da
 * kapalı (<c>disable_session_recording</c>) ama iki kapı var: o seçenek bir
 * sürüm yükseltmesinde varsayılanını değiştirebilir, buradaki beyaz liste
 * değiştirmez. Bu ürünün ekranında duran şey müşterinin log satırları;
 * onların videoya girmesi kimsenin vermediği bir karar olurdu.
 * </p>
 */

const kaydedilen = { ...process.env };

/** Modül önbelleğini temizleyip ucu ortam değişkenleriyle taze yüklüyor. */
async function loadRoute() {
  const module = await import("@/app/api/telemetry/[...path]/route");

  return module;
}

function istek(path: string, method = "POST"): NextRequest {
  return new NextRequest(`http://localhost:3000/api/telemetry/${path}`, { method });
}

function baglam(path: string) {
  return { params: Promise.resolve({ path: path.split("/") }) };
}

beforeEach(() => {
  process.env = { ...kaydedilen };
  delete process.env.TELEMETRY_ENABLED;
  delete process.env.TELEMETRY_PROJECT_KEY;
  delete process.env.TELEMETRY_IDENTIFY_USERS;
  delete process.env.TELEMETRY_IDENTITY_SALT;
});

afterEach(() => {
  process.env = { ...kaydedilen };
});

describe("telemetri vekili — kapalıyken", () => {
  it("403 dönüyor, 404 değil", async () => {
    const { POST } = await loadRoute();

    const response = await POST(istek("e"), baglam("e"));

    // 404 "böyle bir uç yok" der ve geliştiriciyi olmayan bir yazım hatasını
    // aramaya yollar. 403 "uç var, kapalı" diyor.
    expect(response.status).toBe(403);
    await expect(response.json()).resolves.toMatchObject({ error: expect.stringContaining("kapalı") });
  });
});

describe("telemetri vekili — açıkken yol beyaz listesi", () => {
  beforeEach(() => {
    process.env.TELEMETRY_ENABLED = "true";
    process.env.TELEMETRY_PROJECT_KEY = "phc_test";
  });

  it("OTURUM KAYDI ucu (/s) reddediliyor", async () => {
    const { POST } = await loadRoute();

    const response = await POST(istek("s"), baglam("s"));

    expect(response.status).toBe(403);

    const body = (await response.json()) as { error: string };

    // Sessiz düşme DEĞİL: reddin sebebi gövdede yazıyor. Sessiz redde
    // "telemetri açık ama bazı olaylar hiç gelmiyor" derdi.
    expect(body.error).toContain("/s");
  });

  const reddedilenler = [
    ["s", "oturum kaydı"],
    ["s/1234", "oturum kaydı alt yolu"],
    ["api/projects", "PostHog yönetim API'si"],
    ["", "boş yol"],
    ["e/../api/projects", "yol kaçışı denemesi"],
  ] as const;

  it.each(reddedilenler)("`/%s` reddediliyor (%s)", async (path) => {
    const { POST } = await loadRoute();

    const response = await POST(istek(path), baglam(path));

    expect(response.status).toBe(403);
  });

  const izinliler = ["e", "i/v0/e", "batch", "decide", "flags", "engage"] as const;

  it.each(izinliler)("`/%s` beyaz listede", async (path) => {
    const { POST } = await loadRoute();

    const response = await POST(istek(path), baglam(path));

    // Yukarı akış bu testte yok, dolayısıyla 502 bekleniyor — ama 403 DEĞİL.
    // Ölçtüğümüz şey "yol kapıdan geçti mi", "PostHog cevap verdi mi" değil.
    expect(response.status).not.toBe(403);
  });
});

describe("telemetri vekili — açık ama yapılandırılmamışken", () => {
  it("503 ve EKSİK DEĞİŞKENİN ADI dönüyor", async () => {
    process.env.TELEMETRY_ENABLED = "true";

    const { POST } = await loadRoute();

    const response = await POST(istek("e"), baglam("e"));

    expect(response.status).toBe(503);

    const body = (await response.json()) as { hint?: string };

    // Telemetriyi açtığını sanan yönetici ağ sekmesinde sebebi okuyor.
    // Sessizce kapalıya düşmek, haftalarca boş bir panoya bakmak olurdu.
    expect(body.hint).toContain("TELEMETRY_PROJECT_KEY");
  });
});
