/**
 * Satır bazlı fark — **saf fonksiyon**, React'ten bağımsız (T20).
 *
 * <p>
 * Hazır bir diff kütüphanesi alınmadı: `ui/` bugün hiç görsel bağımlılık
 * taşımıyor ve ilkini fark görünümü uğruna almak, T28'in erişilebilirlik
 * denetiminin kapsamını bir ekran için büyütmek olurdu.
 * </p>
 *
 * <p>
 * Hesap bileşenin dışında, çünkü içinde olsaydı ancak render edilerek
 * sınanabilirdi — ve o testler yavaş, kırılgan ve okunmaz olur.
 * </p>
 */

export type DiffKind = "same" | "added" | "removed";

export interface DiffLine {
  readonly kind: DiffKind;
  /** Önceki sürümdeki satır numarası (1 tabanlı); eklenen satırda yok. */
  readonly leftNumber: number | undefined;
  /** Yeni sürümdeki satır numarası; silinen satırda yok. */
  readonly rightNumber: number | undefined;
  readonly text: string;
}

export interface DiffResult {
  readonly lines: readonly DiffLine[];
  readonly added: number;
  readonly removed: number;
  /**
   * Fark hesaplanamayacak kadar büyük.
   *
   * <p>Klasik LCS'in belleği O(n·m): iki bin satırlık iki dosya dört milyon
   * hücre demek. Sessizce donmak yerine bunu <b>söylüyoruz</b>.</p>
   */
  readonly tooLarge: boolean;
}

/**
 * LCS tablosunun hücre sınırı.
 *
 * <p>Parser YAML'ları onlarca satır; bu sınıra ortak ön/son ek kırpıldıktan
 * sonra ulaşmak pratikte mümkün değil. Yine de var, çünkü "pratikte olmaz"
 * bir sınır değil.</p>
 */
export const MAX_DIFF_CELLS = 1_000_000;

/**
 * Satırlara böler.
 *
 * <p>`\r\n` ve `\n` ikisi de: YAML bir Windows makinesinde düzenlenmişse her
 * satır sonda görünmez bir `\r` taşır ve karşılaştırma **her satırı değişmiş**
 * gösterirdi.</p>
 */
export function splitLines(text: string): string[] {
  if (text.length === 0) {
    return [];
  }

  const lines = text.split(/\r\n|\r|\n/);

  // Dosya sonundaki tek `\n`'in ürettiği boş eleman atılıyor. Atılmasaydı
  // düzgün biten HER YAML'ın farkı sonda hayalet bir boş satır gösterirdi —
  // her ekranda, her karşılaştırmada. Bedeli, yalnızca son satır sonunun
  // değiştiği bir farkın görünmemesi; YAML yükleyicisi de o farkı zaten
  // önemsemiyor.
  if (lines.length > 1 && lines[lines.length - 1] === "") {
    lines.pop();
  }

  return lines;
}

/**
 * Karşılaştırma anahtarı.
 *
 * <p>
 * <b>NFC normalize ediliyor.</b> Aynı görünen iki satır farklı Unicode
 * bileşimlerinde yazılabiliyor (`é` tek kod noktası ya da `e` + birleşik
 * aksan). Ham karşılaştırma bunları "değişmiş" gösterirdi — üstelik ürünün
 * kendisi ingest'te NFC'ye normalize ediyor, yani ekranda gösterilen fark
 * boru hattının zaten sildiği bir farkı raporlardı.
 * </p>
 *
 * <p>
 * Gösterilen metin <b>ham</b> kalıyor; normalize edilen yalnızca eşitlik
 * kararı.
 * </p>
 */
function key(line: string): string {
  return line.normalize("NFC");
}

/** Ortak ön ekin uzunluğu — LCS tablosunu küçültmenin en ucuz yolu. */
function commonPrefix(left: readonly string[], right: readonly string[]): number {
  const limit = Math.min(left.length, right.length);
  let index = 0;

  while (index < limit && key(left[index]!) === key(right[index]!)) {
    index += 1;
  }

  return index;
}

function commonSuffix(left: readonly string[], right: readonly string[], skip: number): number {
  const limit = Math.min(left.length, right.length) - skip;
  let index = 0;

  while (
    index < limit &&
    key(left[left.length - 1 - index]!) === key(right[right.length - 1 - index]!)
  ) {
    index += 1;
  }

  return index;
}

/**
 * İki YAML gövdesinin satır satır farkı.
 *
 * <p>
 * Önce ortak ön ve son ek kırpılıyor: parser düzenlemeleri çoğunlukla birkaç
 * satıra dokunuyor ve kırpma LCS tablosunu o birkaç satıra indiriyor. Sınır
 * bundan <b>sonra</b> uygulanıyor, yani gerçekten büyük olan farklar için.
 * </p>
 */
export function diffLines(previous: string, next: string): DiffResult {
  const left = splitLines(previous);
  const right = splitLines(next);

  const prefix = commonPrefix(left, right);
  const suffix = commonSuffix(left, right, prefix);

  const leftMiddle = left.slice(prefix, left.length - suffix);
  const rightMiddle = right.slice(prefix, right.length - suffix);

  if (leftMiddle.length * rightMiddle.length > MAX_DIFF_CELLS) {
    return { lines: [], added: 0, removed: 0, tooLarge: true };
  }

  const lines: DiffLine[] = [];

  for (let index = 0; index < prefix; index += 1) {
    lines.push({ kind: "same", leftNumber: index + 1, rightNumber: index + 1, text: left[index]! });
  }

  const middle = lcsDiff(leftMiddle, rightMiddle, prefix);
  lines.push(...middle);

  for (let index = 0; index < suffix; index += 1) {
    const leftNumber = left.length - suffix + index + 1;
    const rightNumber = right.length - suffix + index + 1;
    lines.push({
      kind: "same",
      leftNumber,
      rightNumber,
      text: left[leftNumber - 1]!,
    });
  }

  return {
    lines,
    added: lines.filter((line) => line.kind === "added").length,
    removed: lines.filter((line) => line.kind === "removed").length,
    tooLarge: false,
  };
}

/** Klasik LCS tablosu ve geri izleme. Yalnızca kırpılmış orta parça üzerinde. */
function lcsDiff(left: readonly string[], right: readonly string[], offset: number): DiffLine[] {
  const rows = left.length;
  const columns = right.length;

  // (rows + 1) × (columns + 1) tablo, tek düz dizide: satır başına ayrı dizi
  // ayırmak bu boyutlarda ölçülebilir bir maliyet.
  const table = new Int32Array((rows + 1) * (columns + 1));
  const at = (row: number, column: number) => row * (columns + 1) + column;

  for (let row = rows - 1; row >= 0; row -= 1) {
    for (let column = columns - 1; column >= 0; column -= 1) {
      table[at(row, column)] =
        key(left[row]!) === key(right[column]!)
          ? table[at(row + 1, column + 1)]! + 1
          : Math.max(table[at(row + 1, column)]!, table[at(row, column + 1)]!);
    }
  }

  const lines: DiffLine[] = [];
  let row = 0;
  let column = 0;

  while (row < rows && column < columns) {
    if (key(left[row]!) === key(right[column]!)) {
      lines.push({
        kind: "same",
        leftNumber: offset + row + 1,
        rightNumber: offset + column + 1,
        text: left[row]!,
      });
      row += 1;
      column += 1;
    } else if (table[at(row + 1, column)]! >= table[at(row, column + 1)]!) {
      lines.push({ kind: "removed", leftNumber: offset + row + 1, rightNumber: undefined, text: left[row]! });
      row += 1;
    } else {
      lines.push({ kind: "added", leftNumber: undefined, rightNumber: offset + column + 1, text: right[column]! });
      column += 1;
    }
  }

  while (row < rows) {
    lines.push({ kind: "removed", leftNumber: offset + row + 1, rightNumber: undefined, text: left[row]! });
    row += 1;
  }

  while (column < columns) {
    lines.push({ kind: "added", leftNumber: undefined, rightNumber: offset + column + 1, text: right[column]! });
    column += 1;
  }

  return lines;
}
