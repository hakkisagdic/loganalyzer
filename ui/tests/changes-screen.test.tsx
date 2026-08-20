import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";

import { ChangeTable, type ChangeRow } from "@/app/degisiklikler/ChangeTable";
import { formatInstant } from "@/lib/ui/time";
import { ConnectorTable } from "@/app/degisiklikler/connectorler/ConnectorTable";
import {
  CREDENTIAL_MASK,
  changeWriteRequest,
  createRequest,
  credentialForSave,
  toggleRequest,
  type ConnectorSummary,
} from "@/lib/changes/connector";
import { screenState } from "@/lib/ui/screen-state";

/**
 * Değişiklik akışı ve connector ekranları (T24/T25).
 *
 * <p>
 * <b>En sert testi <c>Maskeli_deger_sunucuya_geri_gonderilmiyor</c>:</b> servis
 * tarafında redaksiyon kapısı var ve kırmızı yanabildiği ölçüldü, ama
 * kullanıcının gördüğü yerde maskenin tuttuğunu hiçbir şey sınamıyordu. Bir
 * düzenleme formunda maskeyi geri göndermek, düz metin parola yazmaktan daha
 * sinsi: kullanıcı "değiştirmedim" diye kaydeder, kayıtlı gizli anahtar
 * <c>••••••••</c> olur ve bir sonraki çekim "kimlik doğrulama reddedildi" der.
 * Hiç kimse sebebini anlamaz.
 * </p>
 */

const connector: ConnectorSummary = {
  id: "0198f0c2-1a2b-7c3d-8e4f-5a6b7c8d9e01",
  slug: "gh-network",
  name: "GitHub — ağ yapılandırması",
  connector_type: "Webhook",
  owner_group: "network/core",
  config: { provider: "github", targetKind: "Config" },
  credential_set: true,
  interval_seconds: null,
  enabled: false,
  last_run_at: null,
  last_run_state: null,
  last_error: "",
  receive_path: "/v1/changes/webhooks/gh-network",
};

// ------------------------------------------------- kimlik bilgisi maskesi

describe("kimlik bilgisi maskesi", () => {
  it("maskeli değer sunucuya geri gönderilmiyor", () => {
    // Ekran mevcut değeri hiç görmüyor; bir alan yanlışlıkla maskeyle
    // doldurulursa istek gövdesine GİRMEMELİ.
    expect(credentialForSave(CREDENTIAL_MASK)).toBeUndefined();
    expect(credentialForSave(`  ${CREDENTIAL_MASK}  `)).toBeUndefined();
  });

  it("boş alan da 'değiştirme' anlamına geliyor", () => {
    // Boşu "sil" saysaydık her ad düzeltmesi kimlik bilgisini uçururdu.
    expect(credentialForSave("")).toBeUndefined();
    expect(credentialForSave("   ")).toBeUndefined();
    expect(credentialForSave(null)).toBeUndefined();
    expect(credentialForSave(undefined)).toBeUndefined();
  });

  it("gerçek bir değer geçiyor", () => {
    expect(credentialForSave("  s3cr3t-anahtar  ")).toBe("s3cr3t-anahtar");
  });

  it("etkin/pasif isteği kimlik bilgisi alanını hiç taşımıyor", () => {
    const body = toggleRequest(connector);

    expect(body).not.toHaveProperty("credential");
    expect(JSON.stringify(body)).not.toContain(CREDENTIAL_MASK);
    expect(body.enabled).toBe(true);
    expect(body.slug).toBe("gh-network");
  });

  it("form maskeyle doldurulmuş olsa bile gövdeye maske girmiyor", () => {
    const body = createRequest({
      slug: "gh-network",
      name: "GitHub",
      connectorType: "Webhook",
      ownerGroup: "network/core",
      provider: "github",
      targetKind: "Config",
      defaultChangeKind: "deploy",
      intervalSeconds: "900",
      credential: CREDENTIAL_MASK,
    });

    expect(body.credential).toBeUndefined();
    expect(JSON.stringify(body)).not.toContain(CREDENTIAL_MASK);
  });

  it("tabloda maskenin kendisi bile basılmıyor", () => {
    // Basılan bir maske, bir sonraki düzenlemede geri gönderilebilecek bir
    // metin hâline gelir. Sütun bir rozet gösteriyor, değer değil.
    const html = renderToStaticMarkup(
      <ConnectorTable
        rows={[connector]}
        canManage
        testingId={null}
        onTest={() => {}}
        onToggle={() => {}}
      />,
    );

    expect(html).not.toContain(CREDENTIAL_MASK);
    expect(html).toContain("kayıtlı");
  });

  it("kimlik bilgisi olmayan connector ayırt ediliyor", () => {
    const html = renderToStaticMarkup(
      <ConnectorTable
        rows={[{ ...connector, credential_set: false }]}
        canManage
        testingId={null}
        onTest={() => {}}
        onToggle={() => {}}
      />,
    );

    expect(html).toContain("yok");
    expect(html).not.toContain(CREDENTIAL_MASK);
  });
});

// ------------------------------------------------------ target_kind çevirisi

describe("target_kind çevirisi", () => {
  it("elle giriş target_kind'i METİN olarak yolluyor", () => {
    // Bu çeviri bir kez kırıldı: yanıt tarafı enum'u sayı olarak yayınlıyordu,
    // ekran metin bekliyordu ve uç sessizce "yok" görünüyordu.
    const body = changeWriteRequest({
      ownerGroup: "network/core",
      targetKind: "Config",
      targetId: "  fw-core-01  ",
      changeKind: "  config_push  ",
      actor: "esra.yildiz",
      summary: "ACL güncellendi",
      timestamp: "",
    });

    expect(body.target_kind).toBe("Config");
    expect(typeof body.target_kind).toBe("string");

    // Alan adları snake_case — API'nin tamamı öyle konuşuyor.
    expect(Object.keys(body)).toEqual(
      expect.arrayContaining(["owner_group", "target_kind", "target_id", "change_kind"]),
    );

    // Kırpma gövdede yapılıyor, sunucuda değil.
    expect(body.target_id).toBe("fw-core-01");
    expect(body.change_kind).toBe("config_push");
  });

  it("boş zaman damgası gövdeye hiç girmiyor", () => {
    // Sunucu boş bırakılan zamanı "şimdi" sayıyor; boş bir dizge göndermek
    // ayrıştırma hatası olurdu.
    const body = changeWriteRequest({
      ownerGroup: "network/core",
      targetKind: "Device",
      targetId: "sw-01",
      changeKind: "firmware",
      actor: "",
      summary: "",
      timestamp: "   ",
    });

    expect(body).not.toHaveProperty("timestamp");
  });

  it("yerel saat UTC'ye çevriliyor", () => {
    const body = changeWriteRequest({
      ownerGroup: "network/core",
      targetKind: "Device",
      targetId: "sw-01",
      changeKind: "firmware",
      actor: "",
      summary: "",
      timestamp: "2026-08-18T09:14",
    });

    expect(body.timestamp).toBe(new Date("2026-08-18T09:14").toISOString());
  });

  it("webhook connector'ı targetKind'i yapılandırmaya metin olarak koyuyor", () => {
    const body = createRequest({
      slug: "gh",
      name: "GitHub",
      connectorType: "Webhook",
      ownerGroup: "network/core",
      provider: "github",
      targetKind: "Config",
      defaultChangeKind: "deploy",
      intervalSeconds: "900",
      credential: "anahtar",
    });

    expect(body.config).toMatchObject({ provider: "github", targetKind: "Config" });
    expect(body.connector_type).toBe("Webhook");

    // Yeni connector PASİF doğuyor: etkinleştirmeden önce bağlantı denensin.
    expect(body.enabled).toBe(false);
  });

  it("cihaz connector'ında aralık sayıya çevriliyor, webhook'ta null kalıyor", () => {
    const device = createRequest({
      slug: "fw", name: "FW", connectorType: "DeviceConfig", ownerGroup: "network/core",
      provider: "", targetKind: "", defaultChangeKind: "", intervalSeconds: "900", credential: "p",
    });

    const webhook = createRequest({
      slug: "gh", name: "GH", connectorType: "Webhook", ownerGroup: "network/core",
      provider: "github", targetKind: "Config", defaultChangeKind: "deploy",
      intervalSeconds: "900", credential: "p",
    });

    expect(device.interval_seconds).toBe(900);
    expect(webhook.interval_seconds).toBeNull();
  });
});

// ------------------------------------------------------------- dört durum

describe("dört durum", () => {
  it("veri gelmeden yükleniyor", () => {
    expect(screenState(null, null)).toBe("loading");
  });

  it("hata boş durumu bastırıyor", () => {
    // Hata varken "kayıt yok" göstermek yanlış bilgi olurdu: kayıt olabilir,
    // yalnızca okunamadı.
    expect(screenState([], "API'ye ulaşılamıyor.")).toBe("error");
    expect(screenState([connector], "API'ye ulaşılamıyor.")).toBe("error");
  });

  it("hatasız boş liste boş durum", () => {
    expect(screenState([], null)).toBe("empty");
  });

  it("dolu liste hazır", () => {
    expect(screenState([connector], null)).toBe("ready");
  });
});

// -------------------------------------------------------- çok dilli gövde

describe("çok dilli gövde", () => {
  const rows: ChangeRow[] = [
    {
      change_id: "1",
      timestamp: "2026-08-18T09:19:47Z",
      owner_group: "network/core",
      target_kind: "Config",
      target_id: "fw-core-01",
      change_kind: "config_push",
      actor: "esra.yildiz",
      summary: "fw-core-01 dış ACL'e 10.20.0.0/16 eklendi",
      source: "manual",
      external_ref: "",
    },
    {
      change_id: "2",
      timestamp: "2026-08-18T10:02:11Z",
      owner_group: "network/core",
      target_kind: "Device",
      target_id: "الجدار-الناري-٠١",
      change_kind: "firmware",
      actor: "أحمد",
      summary: "تم تحديث البرنامج الثابت للجهاز إلى الإصدار السابع",
      source: "github",
      external_ref: "https://github.com/bizigo/network-config/actions/runs/1",
    },
    {
      change_id: "3",
      timestamp: "2026-08-18T11:30:00Z",
      owner_group: "network/core",
      target_kind: "Service",
      target_id: "核心交换机零一",
      change_kind: "deploy",
      actor: "张伟",
      summary: "用户登录失败，请检查凭据配置已更新并重新加载防火墙规则",
      source: "gitlab",
      external_ref: "",
    },
  ];

  it("serbest metin sütunları dir=auto taşıyor", () => {
    // Arapça bir gövde soldan sağa hizalanırsa okunamaz hâle geliyor;
    // `DataTable` bunu `freeText` sütunlarda çözüyor ve ekranın onu gerçekten
    // kullandığı ancak burada ölçülebiliyor.
    const html = renderToStaticMarkup(<ChangeTable rows={rows} />);

    expect(html).toContain('dir="auto"');
    expect(html).toContain("الجدار-الناري-٠١");
    expect(html).toContain("核心交换机零一");
  });

  it("her satır çiziliyor ve anahtarlar benzersiz", () => {
    const html = renderToStaticMarkup(<ChangeTable rows={rows} />);

    for (const row of rows) {
      expect(html).toContain(row.target_id);
    }

    expect(new Set(rows.map((row) => row.change_id)).size).toBe(rows.length);
  });

  it("çok satırda tablo bozulmuyor", () => {
    // "Çok veri" durumu: 500 satır tek istekte geliyor (API sınırı 2000).
    const many: ChangeRow[] = Array.from({ length: 500 }, (_, index) => ({
      ...rows[0]!,
      change_id: `row-${index}`,
      target_id: `fw-${index}`,
    }));

    const html = renderToStaticMarkup(<ChangeTable rows={many} />);

    expect(html).toContain("fw-0");
    expect(html).toContain("fw-499");
    // Başlık satırı bir kez çiziliyor.
    expect(html.split("<thead>").length - 1).toBe(1);
  });

  it("dış bağlantı yeni sekmede ve noopener ile açılıyor", () => {
    const html = renderToStaticMarkup(<ChangeTable rows={rows} />);

    expect(html).toContain('rel="noreferrer noopener"');
    expect(html).toContain('target="_blank"');
  });

  it("zaman damgası ürün genelindeki tek biçimde", () => {
    // T28 denetimi üç ayrı biçim buldu: burası dakika hassasiyetinde ve
    // dilimsizdi, olay ekranları saniyeli ve `Z` ekliydi, alarm ekranları ise
    // YEREL saatte. Sonuncusu bir alarm tetiklenmesini log satırıyla eşleştiren
    // kullanıcıya saat farkı kadar sapmış iki zaman gösteriyordu. Tek biçim
    // `lib/ui/time.ts`'te ve UTC olduğu açıkça yazılı.
    expect(formatInstant("2026-08-18T09:19:47Z")).toBe("2026-08-18 09:19:47Z");
    // Boş değer tabloyu bozmuyor.
    expect(formatInstant(null)).toBe("—");
    // Çözülemeyen damga GİZLENMİYOR: bozuk bir değer, gösterilmeyen bir
    // değerden çok daha fazla bilgi taşıyor.
    expect(formatInstant("bozuk")).toBe("bozuk");
  });
});

// ------------------------------------------------------------ yetki yansıması

describe("yetki", () => {
  it("yetkisiz kullanıcıya etkinleştirme düğmesi gösterilmiyor", () => {
    // Yetki uçta zaten zorlanıyor; ekran onu YANSITIYOR. Düğmeyi gösterip 403
    // aldırmak, kullanıcıya sebebini söylemeyen bir arıza gibi görünürdü.
    const html = renderToStaticMarkup(
      <ConnectorTable
        rows={[connector]}
        canManage={false}
        testingId={null}
        onTest={() => {}}
        onToggle={() => {}}
      />,
    );

    expect(html).not.toContain("Etkinleştir");
    // Bağlantı denemesi okuma sayılıyor, herkese açık.
    expect(html).toContain("Bağlantıyı dene");
  });

  it("düşen son koşum tabloda görünüyor", () => {
    const html = renderToStaticMarkup(
      <ConnectorTable
        rows={[{ ...connector, enabled: true, last_run_state: "Failed" }]}
        canManage
        testingId={null}
        onTest={() => {}}
        onToggle={() => {}}
      />,
    );

    expect(html).toContain("son koşum düştü");
  });
});
