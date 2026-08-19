import { describe, expect, it } from "vitest";

import { decodeBase64, decodeText, toHexDump } from "@/lib/events/raw";

/**
 * T16 — ham baytlar.
 *
 * <p>
 * Ham arşivin bütün varlık sebebi bu ekranda görünür hâle geliyor ve F1'de o
 * yol <b>beş ayrı katmanda</b> kırılmıştı. Buradaki iddia dar ama kesin:
 * baytlar hiçbir aşamada yeniden kodlanmıyor, ve <c>windows-1254</c> bir satır
 * ekranda doğru görünüyor.
 * </p>
 */

/**
 * `İstanbul şubesi: bağlantı reddedildi` — **windows-1254** baytları.
 *
 * <p>
 * Elle yazılmış olması bilinçli: bir kodlayıcıdan üretilseydi test kendi
 * varsayımını doğrulardı. <b>36 bayt</b>; aynı metin UTF-8'de 40 bayt tutuyor,
 * yani bu dizi gerçekten cihazın gönderdiği kodlamada.
 * </p>
 */
const WINDOWS_1254_LINE = Uint8Array.from([
  0xdd, 0x73, 0x74, 0x61, 0x6e, 0x62, 0x75, 0x6c, 0x20, // İstanbul
  0xfe, 0x75, 0x62, 0x65, 0x73, 0x69, 0x3a, 0x20, //       şubesi:
  0x62, 0x61, 0xf0, 0x6c, 0x61, 0x6e, 0x74, 0xfd, 0x20, // bağlantı
  0x72, 0x65, 0x64, 0x64, 0x65, 0x64, 0x69, 0x6c, 0x64, 0x69, // reddedildi
]);

const EXPECTED_TEXT = "İstanbul şubesi: bağlantı reddedildi";

function toBase64(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("base64");
}

describe("bayt sadakati", () => {
  it("base64 çözümü baytları birebir geri veriyor", () => {
    const round = decodeBase64(toBase64(WINDOWS_1254_LINE));

    expect(round.length).toBe(WINDOWS_1254_LINE.length);
    expect([...round]).toEqual([...WINDOWS_1254_LINE]);
  });

  it("windows-1254 satır UTF-8'den kısa — yani gerçekten yeniden kodlanmamış", () => {
    // Boru hattı baytları UTF-8'e çevirseydi 40 bayt görürdük. Kodlama tespiti
    // yanlış çıktığında düzeltilecek olan şey tam olarak bu dizi (K4).
    expect(WINDOWS_1254_LINE.length).toBe(36);
    expect(Buffer.from(EXPECTED_TEXT, "utf8").length).toBe(40);
  });
});

describe("çözümlenmiş metin", () => {
  it("windows-1254 satır ekranda doğru görünüyor", () => {
    const decoded = decodeText(WINDOWS_1254_LINE, "windows-1254");

    expect(decoded.text).toBe(EXPECTED_TEXT);
    expect(decoded.fellBack).toBe(false);
  });

  it("UTF-8 varsayımı aynı satırı bozuyor — testin kırmızı yanabildiğinin ölçüsü", () => {
    // Bu satır olmadan yukarıdaki iddia, kodlamanın hiç kullanılmadığı durumda
    // da geçebilirdi.
    const wrong = decodeText(WINDOWS_1254_LINE, "utf-8");

    expect(wrong.text).not.toBe(EXPECTED_TEXT);
    expect(wrong.text).toContain("�");
  });

  it("tanınmayan kodlama sessizce doğru sanılmıyor", () => {
    const decoded = decodeText(WINDOWS_1254_LINE, "uydurma-kodlama");

    // Metin yine gösteriliyor ama `fellBack` ile işaretli: ekran bunu açıkça
    // söylüyor, yoksa yanlış çözülmüş metin doğru sanılırdı.
    expect(decoded.fellBack).toBe(true);
    expect(decoded.encoding).toBe("utf-8");
  });

  it("kodlama boşsa UTF-8 varsayılıyor ve bu bir geri düşüş değil", () => {
    const decoded = decodeText(Uint8Array.from([0x6f, 0x6b]), "");

    expect(decoded.text).toBe("ok");
    expect(decoded.encoding).toBe("utf-8");
    expect(decoded.fellBack).toBe(false);
  });
});

describe("hex dökümü", () => {
  it("ofset, hex ve ASCII sütunlarını birlikte veriyor", () => {
    const dump = toHexDump(WINDOWS_1254_LINE);
    const lines = dump.split("\n");

    // 36 bayt / 16 = 3 satır.
    expect(lines).toHaveLength(3);
    expect(lines[0]).toMatch(/^00000000 {2}dd 73 74 61 6e 62 75 6c 20 fe 75 62 65 73 69 3a {2}\|/);
    // ASCII sütunu yazdırılamayan baytı nokta gösteriyor; 0xdd yazdırılamaz.
    expect(lines[0]).toContain("|.stanbul .ubesi:|");
    expect(lines[1]).toMatch(/^00000010 {2}/);
    expect(lines[2]).toMatch(/^00000020 {2}/);
  });

  it("son satır kısa olsa da ASCII sütunu hizalı kalıyor", () => {
    const dump = toHexDump(Uint8Array.from([0x61, 0x62]));

    // Hex sütunu 16 baytlık genişliğe (47 karakter) dolduruluyor; ASCII sütunu
    // satır kısa olsa da aynı kolonda başlıyor.
    expect(dump).toBe(`00000000  61 62${" ".repeat(44)}|ab|`);
  });

  it("boş gövde boş döküm veriyor", () => {
    expect(toHexDump(Uint8Array.from([]))).toBe("");
  });
});
