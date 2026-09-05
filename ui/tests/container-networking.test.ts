import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { readBffConfig } from "@/lib/auth/config";
import { discover, resetDiscoveryCache } from "@/lib/auth/oidc";

/**
 * BFF'in **container ağındaki** adres ayrımı (T49).
 *
 * <h3>Neden ayrı bir paket</h3>
 *
 * <p>
 * Kabul kriterlerinin çoğu ancak yığın kaldırılınca ölçülebiliyor ve o
 * koordinatörün tarafında. Burada ölçülen şey <b>kaldırmadan önce</b>
 * ölçülebilen kısım: container'a taşımanın gerektirdiği tek kod değişikliği
 * doğru davranıyor mu.
 * </p>
 *
 * <h3>Ölçülen ayrım</h3>
 *
 * <p>
 * Keycloak'a giden iki ayrı yol var ve ikisi container'da <b>farklı adresler</b>
 * görüyor:
 * </p>
 *
 * <ul>
 *   <li><b>Ön kanal</b> — kullanıcının tarayıcısı gidiyor: yetkilendirme ve
 *       çıkış. Tarayıcı <c>keycloak:8080</c>'i çözemez.</li>
 *   <li><b>Arka kanal</b> — Next sunucusu gidiyor: keşif, token, JWKS. Sunucu
 *       <c>localhost:8180</c>'i çözemez, çünkü container içinde orası
 *       Keycloak değil <b>container'ın kendisi</b>.</li>
 * </ul>
 *
 * <p>
 * <b>İki yön de sınanıyor ve bu şart.</b> Yalnızca ön kanal sınansaydı,
 * "belgedeki her ucu genel kökene çevir" diyen bir uygulama testi geçer ve
 * container'da <i>token alışverişini</i> sessizce kırardı — belirtisi giriş
 * akışının son adımında, sebebini söylemeyen bir hata.
 * </p>
 */

const PUBLIC_ISSUER = "http://localhost:8180/realms/bizigo";
const INTERNAL_METADATA = "http://keycloak:8080/realms/bizigo/.well-known/openid-configuration";

/** Belgeyi kim indirdi — testin asıl gözlemi. */
let fetched: string[];
let realFetch: typeof globalThis.fetch;

/**
 * Keycloak'ın container ağından inen belgesi.
 *
 * <p>
 * <c>KC_HOSTNAME</c> issuer'ı ve ön kanalı <c>localhost:8180</c>'e sabitliyor;
 * <c>KC_HOSTNAME_BACKCHANNEL_DYNAMIC</c> arka kanalı isteğin host'undan
 * türetiyor. Buradaki gövde <b>o yapılandırmanın ürettiği şekli</b> taklit
 * ediyor.
 * </p>
 */
function containerDocument(overrides: Record<string, unknown> = {}) {
  return {
    issuer: PUBLIC_ISSUER,
    authorization_endpoint: "http://localhost:8180/realms/bizigo/protocol/openid-connect/auth",
    token_endpoint: "http://keycloak:8080/realms/bizigo/protocol/openid-connect/token",
    jwks_uri: "http://keycloak:8080/realms/bizigo/protocol/openid-connect/certs",
    end_session_endpoint: "http://localhost:8180/realms/bizigo/protocol/openid-connect/logout",
    ...overrides,
  };
}

function serve(document: Record<string, unknown>): void {
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
    fetched.push(url);

    return Response.json(document);
  }) as typeof globalThis.fetch;
}

beforeEach(() => {
  fetched = [];
  realFetch = globalThis.fetch;
  resetDiscoveryCache();

  process.env.KEYCLOAK_ISSUER = PUBLIC_ISSUER;
  process.env.KEYCLOAK_CLIENT_SECRET = "test-secret";
  delete process.env.KEYCLOAK_METADATA_URL;
});

afterEach(() => {
  globalThis.fetch = realFetch;
  delete process.env.KEYCLOAK_METADATA_URL;
  resetDiscoveryCache();
});

describe("keşif belgesinin indirildiği adres", () => {
  /**
   * <b>Yerel döngü bozulmuyor.</b> Kabul kriteri 5'in kod tarafındaki
   * karşılığı: değişkeni vermeyen bir kurulum, bu alanın eklendiğini hiç fark
   * etmiyor.
   */
  it("Degisken_verilmezse_issuer_dan_turuyor", () => {
    expect(readBffConfig().metadataUrl).toBe(`${PUBLIC_ISSUER}/.well-known/openid-configuration`);
  });

  /**
   * <b>Asıl bekçi.</b> Belge iç adresten iniyor, ama güvenilen issuer
   * değişmiyor — API'nin <c>MetadataAddress</c> / <c>Authority</c> ayrımının
   * aynısı.
   */
  it("Degisken_verilince_belge_ic_adresten_iniyor", async () => {
    process.env.KEYCLOAK_METADATA_URL = INTERNAL_METADATA;
    serve(containerDocument());

    const config = readBffConfig();
    await discover(config);

    expect(fetched).toEqual([INTERNAL_METADATA]);

    // Güvenilen taraf DEĞİŞMİYOR: token'daki `iss` hâlâ genel adres.
    expect(config.issuer).toBe(PUBLIC_ISSUER);
  });
});

describe("ön kanal ve arka kanal ayrımı", () => {
  /**
   * Tarayıcının gittiği uçlar, tarayıcının <b>görebildiği</b> kökende.
   */
  it("On_kanal_uclari_genel_kokende", async () => {
    process.env.KEYCLOAK_METADATA_URL = INTERNAL_METADATA;

    // Keycloak ön kanalı da container adresiyle verseydi — yapılandırma
    // değişse ya da sürüm davranışı kaysa — tarayıcı çözemeyen bir adrese
    // yönlendirilirdi. İddia bizim tarafımızda duruyor.
    serve(
      containerDocument({
        authorization_endpoint: "http://keycloak:8080/realms/bizigo/protocol/openid-connect/auth",
        end_session_endpoint: "http://keycloak:8080/realms/bizigo/protocol/openid-connect/logout",
      }),
    );

    const document = await discover(readBffConfig());

    expect(document.authorization_endpoint).toBe(
      "http://localhost:8180/realms/bizigo/protocol/openid-connect/auth",
    );
    expect(document.end_session_endpoint).toBe(
      "http://localhost:8180/realms/bizigo/protocol/openid-connect/logout",
    );
  });

  /**
   * <b>Ters yön ve asıl değerli olan.</b> Sunucudan sunucuya giden uçlara
   * dokunulmuyor.
   *
   * <p>
   * Bu test olmasaydı "her ucu genel kökene çevir" diyen bir uygulama
   * üsttekini geçerdi ve container'da token alışverişi <c>localhost:8180</c>'e
   * gitmeye çalışırdı — yani container'ın kendisine.
   * </p>
   */
  it("Arka_kanal_uclari_container_adresinde_kaliyor", async () => {
    process.env.KEYCLOAK_METADATA_URL = INTERNAL_METADATA;
    serve(containerDocument());

    const document = await discover(readBffConfig());

    expect(document.token_endpoint).toBe(
      "http://keycloak:8080/realms/bizigo/protocol/openid-connect/token",
    );
    expect(document.jwks_uri).toBe(
      "http://keycloak:8080/realms/bizigo/protocol/openid-connect/certs",
    );
  });

  /**
   * Yerelde iki adres zaten aynı: dönüşüm hiçbir şeyi değiştirmiyor.
   */
  it("Yerelde_donusum_kimlik", async () => {
    serve({
      issuer: PUBLIC_ISSUER,
      authorization_endpoint: `${PUBLIC_ISSUER}/protocol/openid-connect/auth`,
      token_endpoint: `${PUBLIC_ISSUER}/protocol/openid-connect/token`,
      jwks_uri: `${PUBLIC_ISSUER}/protocol/openid-connect/certs`,
      end_session_endpoint: `${PUBLIC_ISSUER}/protocol/openid-connect/logout`,
    });

    const document = await discover(readBffConfig());

    expect(document.authorization_endpoint).toBe(`${PUBLIC_ISSUER}/protocol/openid-connect/auth`);
    expect(document.token_endpoint).toBe(`${PUBLIC_ISSUER}/protocol/openid-connect/token`);
  });
});

describe("issuer doğrulaması", () => {
  /**
   * Kapı metadata adresi ayrıldıktan sonra <b>daha</b> değerli: artık belgeyi
   * başka bir adresten indiriyoruz ve "indirdiğim yer, güvendiğim issuer'ı
   * veriyor mu" sorusunu soran tek yer burası.
   *
   * <p>
   * Ölçülen tuzak: <c>KC_HOSTNAME</c> ayarlı değilse Keycloak issuer'ı isteğin
   * host'undan türetiyor. O hâlde iç adresten inen belge
   * <c>http://keycloak:8080/realms/bizigo</c> derdi ve API'nin beklediğiyle
   * uyuşmazdı — her istekte 401, sebebini söyleyen hiçbir mesaj yok.
   * </p>
   */
  it("Ic_adresten_farkli_issuer_gelirse_reddediliyor", async () => {
    process.env.KEYCLOAK_METADATA_URL = INTERNAL_METADATA;
    serve(containerDocument({ issuer: "http://keycloak:8080/realms/bizigo" }));

    await expect(discover(readBffConfig())).rejects.toThrow(/issuer/i);
  });
});
