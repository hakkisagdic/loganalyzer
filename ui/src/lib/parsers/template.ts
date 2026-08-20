/**
 * Yeni parser iskeleti (T19).
 *
 * <p>
 * Boş bir editör, formatı bilmeyen birine hiçbir şey söylemiyor. İskelet
 * <b>kapılardan geçen</b> bir parser: gömülü testi var, pattern'i doğrusal
 * motorda derleniyor ve örnek satırı kendi testinde duruyor. Kullanıcı ilk
 * denemesinde yeşil görüyor ve oradan kendi vendor'ına doğru değiştiriyor —
 * baştan kırmızı bir ekran, hangi hatanın kendi yazdığından geldiğini
 * gizlerdi.
 * </p>
 *
 * <p>
 * <c>match.contains</c> yorumu bilinçli: T08 raporunun 4. maddesi tam olarak
 * bunun yanlış anlaşılmasından çıktı — <c>match</c> bir doğruluk garantisi
 * değil, envanter bağı olan trafikte hiç çalışmıyor. Yeni parser yazan kişinin
 * bunu iskeletten öğrenmesi, katalogda bir kez daha keşfedilmesinden ucuz.
 * </p>
 */
export const NEW_PARSER_TEMPLATE = `apiVersion: bizigo.dev/v1
kind: Parser
metadata:
  id: vendor.urun.olay
  version: 0.1.0
  vendor: Vendor
  product: Ürün
  description: Bu parser'ın ne ayrıştırdığı, tek cümle.

match:
  transport: [syslog]
  # Literal ön filtre — PERFORMANS için. Ayırt ediciliği buraya değil
  # pipeline'ın ilk adımına koyun: envanter bağı olan trafikte 'match' hiç
  # çalışmıyor (T08 raporu #4).
  contains: ["ÖRNEK-OLAY"]

pipeline:
  - grok:
      field: message
      patterns:
        - '^ÖRNEK-OLAY %{WORD:action} %{IPV4:src_ip}$'

map:
  core:
    action: "{{ action }}"
    src_ip: "{{ src_ip }}"

tests:
  # Gömülü test zorunlu: testsiz parser yayınlanamıyor. Örnek satırı
  # UYDURMAYIN — "Ham arşivden getir" ile gerçek bir satır çekin, uydurma
  # örnekle yazılan parser üretimde çuvallıyor.
  - name: temel
    input: 'ÖRNEK-OLAY accept 10.0.0.1'
    expect:
      parse_status: ok
      core.action: "accept"
      core.src_ip: "10.0.0.1"
`;

/**
 * Adım iskeletini boru hattına ekliyor.
 *
 * <p>
 * Metnin sonuna eklemek yanlış olurdu: <c>pipeline</c> genelde ortada duruyor
 * ve adım <c>map</c>'ten sonra yazılırsa şema hatası veriyor. <c>pipeline:</c>
 * bloğunun <b>sonu</b> aranıyor — bir sonraki kök anahtarın başladığı satır.
 * </p>
 */
export function appendStep(yaml: string, snippet: string): string {
  const lines = yaml.split("\n");
  const start = lines.findIndex((line) => /^pipeline\s*:/.test(line));

  if (start < 0) {
    return `${yaml.trimEnd()}\n\npipeline:\n${indent(snippet)}\n`;
  }

  let end = start + 1;
  while (end < lines.length && !/^[A-Za-z_]/.test(lines[end]!)) {
    end += 1;
  }

  // Blok sonundaki boş satırlar bloğun değil, ayracın parçası: adım onlardan
  // önce girmeli, yoksa `map:`in hemen üstüne yapışır.
  let insert = end;
  while (insert > start + 1 && lines[insert - 1]!.trim().length === 0) {
    insert -= 1;
  }

  return [...lines.slice(0, insert), ...indent(snippet).split("\n"), ...lines.slice(insert)].join("\n");
}

function indent(snippet: string): string {
  return snippet
    .split("\n")
    .map((line) => (line.length > 0 ? `  ${line}` : line))
    .join("\n");
}
