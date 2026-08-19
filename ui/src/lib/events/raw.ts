/**
 * Ham baytların görünür hâli (T16).
 *
 * <p>
 * Ham arşivin bütün varlık sebebi bu ekranda ortaya çıkıyor: kodlama tespiti
 * yanlışsa düzeltilecek olan şey <b>orijinal baytlar</b>, çözülmüş metin değil
 * (K4). Bu yüzden API base64 döndürüyor ve çözme burada, <b>görüntüleme
 * amacıyla</b> yapılıyor — indirilen dosya bu yoldan hiç geçmiyor.
 * </p>
 */

/** Base64 → baytlar. Bozuk base64 sessizce boş dönmüyor. */
export function decodeBase64(value: string): Uint8Array {
  const binary = atob(value);
  const bytes = new Uint8Array(binary.length);

  for (let index = 0; index < binary.length; index += 1) {
    bytes[index] = binary.charCodeAt(index);
  }

  return bytes;
}

export interface DecodedText {
  readonly text: string;
  /** Çözmede gerçekten kullanılan etiket. */
  readonly encoding: string;
  /**
   * Tespit edilen kodlama tarayıcı/Node tarafından tanınmadı ve UTF-8'e
   * düşüldü. Sessiz bırakmak, yanlış çözülmüş metni doğru sanmaya yol açardı.
   */
  readonly fellBack: boolean;
}

/**
 * Baytları metne çeviriyor.
 *
 * <p>
 * <c>fatal: false</c> bilinçli: geçersiz bayt dizisi hata fırlatmak yerine
 * <c>U+FFFD</c> üretiyor. Amaç metni <b>olduğu gibi göstermek</b>; çözülemeyen
 * bir bayt varsa kullanıcı onu ekranda görmeli, boş bir kutu değil.
 * </p>
 */
export function decodeText(bytes: Uint8Array, encoding: string): DecodedText {
  const label = encoding.trim().length > 0 ? encoding.trim() : "utf-8";

  try {
    return {
      text: new TextDecoder(label).decode(bytes),
      encoding: label,
      fellBack: false,
    };
  } catch {
    return {
      text: new TextDecoder("utf-8").decode(bytes),
      encoding: "utf-8",
      fellBack: true,
    };
  }
}

const PRINTABLE_START = 0x20;
const PRINTABLE_END = 0x7e;

/**
 * Klasik hex dökümü: <c>ofset  16 bayt  |ascii|</c>.
 *
 * <p>
 * ASCII sütunu olmadan hex tek başına okunmuyor; ofset olmadan da "104.
 * baytta ne var" sorusu cevaplanmıyor — F1'de bayt sadakati tam olarak bu
 * soruyla doğrulandı (103 bayt girdi, 103 bayt çıktı).
 * </p>
 */
export function toHexDump(bytes: Uint8Array, bytesPerLine = 16): string {
  const lines: string[] = [];

  for (let offset = 0; offset < bytes.length; offset += bytesPerLine) {
    const slice = bytes.subarray(offset, offset + bytesPerLine);

    const hex = Array.from(slice, (byte) => byte.toString(16).padStart(2, "0"))
      .join(" ")
      .padEnd(bytesPerLine * 3 - 1, " ");

    const ascii = Array.from(slice, (byte) =>
      byte >= PRINTABLE_START && byte <= PRINTABLE_END ? String.fromCharCode(byte) : ".",
    ).join("");

    lines.push(`${offset.toString(16).padStart(8, "0")}  ${hex}  |${ascii}|`);
  }

  return lines.join("\n");
}
