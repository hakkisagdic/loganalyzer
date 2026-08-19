import { beforeAll, beforeEach, describe, expect, it } from "vitest";

import {
  ACCESS_TOKEN,
  API_URL,
  installFakes,
  nextRequest,
  REFRESH_TOKEN,
  rememberPending,
  serializeResponse,
  setupEnvironment,
  type FakeIdp,
} from "./harness";

/**
 * T16 — ham baytların indirilmesi.
 *
 * <p>
 * İki iddia var ve ikisi de ölçülüyor: indirilen baytlar cihazın gönderdiğiyle
 * <b>birebir aynı</b>, ve bu yol da erişim token'ını tarayıcıya taşımıyor.
 * İkincisi tek başına yeterli değil — token'ın <b>gerçekten kullanıldığı</b> da
 * doğrulanıyor, yoksa hiç token üretilmese de test geçerdi.
 * </p>
 */

/** `İstanbul şubesi: bağlantı reddedildi` — windows-1254, 36 bayt. */
const DEVICE_BYTES = Uint8Array.from([
  0xdd, 0x73, 0x74, 0x61, 0x6e, 0x62, 0x75, 0x6c, 0x20,
  0xfe, 0x75, 0x62, 0x65, 0x73, 0x69, 0x3a, 0x20,
  0x62, 0x61, 0xf0, 0x6c, 0x61, 0x6e, 0x74, 0xfd, 0x20,
  0x72, 0x65, 0x64, 0x64, 0x65, 0x64, 0x69, 0x6c, 0x64, 0x69,
]);

const EVENT_ID = "0199a1b2-c3d4-7000-8000-000000000001";

let fake: FakeIdp;

/**
 * Rota işleyicileri **ölçülen gövdenin dışında** yükleniyor.
 *
 * <p>
 * Test içinde <c>await import(...)</c> yapmak, Next modüllerinin dönüştürülme
 * maliyetini (yüklü makinede saniyeler) testin duvar saati bütçesine yazıyordu
 * ve paket paralel koşarken ilk test bütçeyi aşıyordu. F1'in dersi tam olarak
 * bu: <b>bütçeyi büyütmek değil, duvar saatini denklemden çıkarmak</b>.
 * </p>
 */
let login: typeof import("@/app/api/auth/login/route").GET;
let callback: typeof import("@/app/signin-oidc/route").GET;
let downloadRaw: typeof import("@/app/olaylar/[id]/ham/route").GET;

beforeAll(async () => {
  await setupEnvironment();

  ({ GET: login } = await import("@/app/api/auth/login/route"));
  ({ GET: callback } = await import("@/app/signin-oidc/route"));
  ({ GET: downloadRaw } = await import("@/app/olaylar/[id]/ham/route"));
});

beforeEach(() => {
  fake = installFakes();
});

async function signIn(): Promise<string> {
  const loginResponse = await login(nextRequest("/api/auth/login"));
  const state = new URL(loginResponse.headers.get("location")!).searchParams.get("state")!;
  rememberPending(state);

  const callbackResponse = await callback(nextRequest(`/signin-oidc?code=kod-123&state=${state}`));

  return `bizigo.sid=${callbackResponse.cookies.get("bizigo.sid")!.value}`;
}

function rawBody(): unknown {
  return {
    event_id: EVENT_ID,
    object_key: "raw/network/core/2026/08/16/12/firewall/01J.ndjson.zst",
    objects_scanned: 1,
    received_at: "2026-08-16T12:30:05Z",
    source_key: "10.1.1.1",
    transport: { proto: "syslog-udp", peer: "10.1.1.1:514" },
    encoding_declared: "windows-1254",
    encoding_detected: "windows-1254",
    raw_b64: Buffer.from(DEVICE_BYTES).toString("base64"),
  };
}

async function download(cookie?: string) {
  return downloadRaw(nextRequest(`/olaylar/${EVENT_ID}/ham`, cookie ? { cookie } : {}), {
    params: Promise.resolve({ id: EVENT_ID }),
  });
}

describe("ham indirme", () => {
  it("baytlar cihazın gönderdiğiyle birebir aynı", async () => {
    const cookie = await signIn();
    fake.apiBody = rawBody();

    const response = await download(cookie);
    const received = new Uint8Array(await response.arrayBuffer());

    expect(response.status).toBe(200);
    expect(received.length).toBe(DEVICE_BYTES.length);
    expect([...received]).toEqual([...DEVICE_BYTES]);

    // UTF-8'e çevrilmiş olsaydı 40 bayt olurdu; 36 bayt, kodlamanın
    // korunduğunun ölçüsü (K4).
    expect(received.length).toBe(36);
    expect(response.headers.get("content-length")).toBe("36");
  });

  it("dosya olarak iniyor ve önbelleğe girmiyor", async () => {
    const cookie = await signIn();
    fake.apiBody = rawBody();

    const response = await download(cookie);

    expect(response.headers.get("content-type")).toBe("application/octet-stream");
    expect(response.headers.get("content-disposition")).toContain(`${EVENT_ID}.bin`);
    // Kapsam değişirse eski yanıt yetkisiz bir kullanıcıya servis edilebilirdi.
    expect(response.headers.get("cache-control")).toBe("no-store");
  });

  it("yanıtın hiçbir baytında token yok — ama API isteğinde var", async () => {
    const cookie = await signIn();
    fake.apiBody = rawBody();

    const response = await download(cookie);
    const serialized = await serializeResponse(response);

    expect(serialized).not.toContain(ACCESS_TOKEN);
    expect(serialized).not.toContain(REFRESH_TOKEN);

    // Kanıtın diğer yarısı: token GERÇEKTEN kullanılıyor.
    expect(fake.apiRequests).toHaveLength(1);
    expect(fake.apiRequests[0]!.authorization).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(fake.apiRequests[0]!.url).toBe(`${API_URL}/v1/events/${EVENT_ID}/raw`);
    // Oturum çerezi API'ye gitmiyor (K31).
    expect(fake.apiRequests[0]!.cookie).toBeNull();
  });

  it("kapsam dışı olayda 404 kalıyor, 403'e çevrilmiyor", async () => {
    const cookie = await signIn();
    fake.apiStatus = 404;
    fake.apiBody = {
      error: "Ham kayıt arşivde bulunamadı.",
      hint: "Nesne henüz yüklenmemiş olabilir; /v1/health/pipeline arşiv gecikmesini gösterir.",
    };

    const response = await download(cookie);

    // 403 "böyle bir olay var ama sizin değil" bilgisini sızdırırdı.
    expect(response.status).toBe(404);
    expect(await response.json()).toMatchObject({ hint: expect.stringContaining("/v1/health/pipeline") });
  });

  it("oturumsuz istek API'ye hiç gitmiyor", async () => {
    const response = await download();

    expect(response.status).toBe(401);
    expect(fake.apiRequests).toHaveLength(0);
  });
});
