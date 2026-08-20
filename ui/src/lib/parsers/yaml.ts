/**
 * Parser YAML'ının sözdizimi vurgulaması ve şema tamamlaması (T19).
 *
 * <p>
 * <b>Kütüphane yok — bilinçli.</b> Bir editör kütüphanesi (CodeMirror, Monaco)
 * yüzlerce kilobayt getiriyor, kendi erişilebilirlik yüzeyini taşıyor ve T28'in
 * denetiminde ayrıca hesabı verilecek bir bağımlılık oluyor. Buradaki iş
 * <b>tek bir formatın</b> vurgulanması: satır tabanlı bir tarayıcı yeterli ve
 * saf fonksiyon olduğu için tam olarak sınanabiliyor.
 * </p>
 *
 * <p>
 * Vurgulama <b>yaklaşık</b> olabilir; yanlış renk kimseyi yanıltmaz. Doğruluk
 * iddiası taşıyan tek şey sunucunun kapı kararı — o da satır numarasıyla
 * geliyor ve editör onu ayrı bir katmanda işaretliyor.
 * </p>
 */

export const TOKEN_KINDS = [
  "comment",
  "key",
  "string",
  "template",
  "number",
  "keyword",
  "punct",
  "text",
] as const;

export type TokenKind = (typeof TOKEN_KINDS)[number];

export interface Token {
  readonly text: string;
  readonly kind: TokenKind;
}

/** YAML'ın düz skaler anahtar kelimeleri. `null`/`~` ayrıca anlamlı (T08 #6). */
const KEYWORDS = new Set(["true", "false", "null", "Null", "NULL", "~", "yes", "no", "on", "off"]);

const KEY_PATTERN = /^(\s*)(?:(-)(\s+))?([A-Za-z_@$][\w.@$-]*)(\s*:)/;
const NUMBER_PATTERN = /^-?\d+(?:\.\d+)?$/;

/**
 * Bir satırı belirteçlere ayırıyor.
 *
 * <p>
 * Yorum işareti tırnak <b>içinde</b> yorum başlatmıyor: grok pattern'leri
 * <c>#</c> içerebiliyor ve satırın yarısını griye boyamak, kullanıcının
 * pattern'i okuyamaması demekti.
 * </p>
 */
export function tokenizeLine(line: string): Token[] {
  const tokens: Token[] = [];

  const keyMatch = KEY_PATTERN.exec(line);
  let rest = line;

  if (keyMatch) {
    const [matched, indent, dash, dashSpace, name, colon] = keyMatch;

    if (indent) tokens.push({ text: indent, kind: "text" });
    if (dash) tokens.push({ text: dash + (dashSpace ?? ""), kind: "punct" });
    tokens.push({ text: name!, kind: "key" });
    tokens.push({ text: colon!, kind: "punct" });

    rest = line.slice(matched.length);
  } else {
    const leading = /^(\s*)(-\s+)?/.exec(line)!;

    if (leading[1]) tokens.push({ text: leading[1], kind: "text" });
    if (leading[2]) tokens.push({ text: leading[2], kind: "punct" });

    rest = line.slice(leading[0].length);
  }

  tokens.push(...tokenizeValue(rest));

  return tokens.filter((token) => token.text.length > 0);
}

function tokenizeValue(value: string): Token[] {
  const tokens: Token[] = [];
  let buffer = "";
  let index = 0;

  const flush = () => {
    if (buffer.length === 0) return;

    tokens.push({ text: buffer, kind: KEYWORDS.has(buffer.trim()) ? "keyword" : classify(buffer) });
    buffer = "";
  };

  while (index < value.length) {
    const char = value[index]!;

    // Yorum yalnızca tırnak DIŞINDA başlıyor; tırnaklı bölgeler aşağıda
    // bütün olarak tüketiliyor, dolayısıyla buraya gelen `#` gerçekten yorum.
    if (char === "#") {
      flush();
      tokens.push({ text: value.slice(index), kind: "comment" });
      return tokens;
    }

    if (char === "'" || char === '"') {
      flush();

      const end = findClosingQuote(value, index, char);
      tokens.push(...tokenizeString(value.slice(index, end)));
      index = end;
      continue;
    }

    if (char === "[" || char === "]" || char === "{" || char === "}" || char === ",") {
      flush();
      tokens.push({ text: char, kind: "punct" });
      index += 1;
      continue;
    }

    buffer += char;
    index += 1;
  }

  flush();
  return tokens;
}

/** Kapanmayan tırnak satır sonuna kadar sürüyor — yazarken yarım kalmış hâl. */
function findClosingQuote(value: string, start: number, quote: string): number {
  for (let index = start + 1; index < value.length; index += 1) {
    if (value[index] === "\\") {
      index += 1;
      continue;
    }

    if (value[index] === quote) {
      return index + 1;
    }
  }

  return value.length;
}

/**
 * Tırnaklı metnin içindeki `{{ alan }}` şablonları ayrı vurgulanıyor.
 *
 * <p>Şablon, `map` bloğunun tamamının anlamını taşıyor: çözülemeyen bir şablon
 * alanı <b>hiç yazmıyor</b> ve bu davranış katalogda kasten kullanılıyor.
 * Metnin geri kalanından ayırt edilmesi, yazarken en çok bakılan yer olduğu
 * için.</p>
 */
function tokenizeString(text: string): Token[] {
  const tokens: Token[] = [];
  const pattern = /\{\{[^}]*\}\}/g;
  let cursor = 0;
  let match: RegExpExecArray | null;

  while ((match = pattern.exec(text)) !== null) {
    if (match.index > cursor) {
      tokens.push({ text: text.slice(cursor, match.index), kind: "string" });
    }

    tokens.push({ text: match[0], kind: "template" });
    cursor = match.index + match[0].length;
  }

  if (cursor < text.length) {
    tokens.push({ text: text.slice(cursor), kind: "string" });
  }

  return tokens;
}

function classify(text: string): TokenKind {
  const trimmed = text.trim();

  if (trimmed.length === 0) return "text";
  if (NUMBER_PATTERN.test(trimmed)) return "number";

  return "text";
}
