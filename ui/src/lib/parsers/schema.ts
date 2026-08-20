/**
 * Parser formatının şeması — tamamlama ve yardım için (T19).
 *
 * <p>
 * <b>Bu bir ikinci doğrulayıcı değil.</b> Anahtar listeleri
 * <c>ParserYamlLoader</c>'daki kümelerin karşılığı ve amacı yalnızca <b>öneri</b>
 * üretmek: burada eksik kalan bir anahtar kullanıcıya önerilmez ama yazarsa
 * kabul edilir, fazla olan bir anahtar önerilir ama sunucu reddeder. Doğruluk
 * iddiası tek yerde — kapının kendisinde.
 * </p>
 *
 * <p>
 * Ayrışma riski gerçek ve kabul edilmiş: motora yeni bir adım eklendiğinde
 * burası güncellenmezse öneri listesi eksik kalır. Bunun bedeli "öneri
 * görünmüyor"; sessiz yanlış davranış değil. Aynı listeyi sunucudan bir uçla
 * çekmek, tamamlama için ağ isteği demekti — ve tamamlamanın her tuşta
 * çalışması gerekiyor.
 * </p>
 */

export interface SchemaKey {
  readonly name: string;
  readonly hint: string;
}

/** Kök anahtarlar — `ParserYamlLoader.RootKeys`. */
export const ROOT_KEYS: readonly SchemaKey[] = [
  { name: "apiVersion", hint: "Sabit: bizigo.dev/v1" },
  { name: "kind", hint: "Sabit: Parser" },
  { name: "metadata", hint: "Kimlik, sürüm, vendor, ürün" },
  { name: "match", hint: "Dispatcher ön filtresi — performans için, ayırt edicilik için değil" },
  { name: "pipeline", hint: "Adımlar; her adım tek iş yapar" },
  { name: "map", hint: "Alanların core/OCSF/OTel karşılıkları" },
  { name: "tests", hint: "Gömülü testler — boş bırakılamaz, yayın kapısı reddeder" },
  { name: "pattern_definitions", hint: "Yalnızca bu parser'a özel grok tanımları" },
];

export const METADATA_KEYS: readonly SchemaKey[] = [
  { name: "id", hint: "vendor.ürün.olay — katalogdaki tekil kimlik" },
  { name: "version", hint: "Semantik sürüm; yayın bunu kullanıyor" },
  { name: "vendor", hint: "Cihaz üreticisi" },
  { name: "product", hint: "Ürün adı" },
  { name: "license", hint: "Parser'ın lisansı" },
  { name: "description", hint: "Bir cümle: bu parser ne ayrıştırıyor" },
  { name: "specificity", hint: "Aday sıralaması; yüksek olan önce denenir" },
];

export const MATCH_KEYS: readonly SchemaKey[] = [
  { name: "transport", hint: "syslog, otlp …" },
  { name: "contains", hint: "Literal ön filtre. Ayırt edicilik BURADA DEĞİL, pipeline'da olmalı" },
  { name: "source_labels", hint: "Envanter etiketleri" },
];

export const MAP_KEYS: readonly SchemaKey[] = [
  { name: "core", hint: "Sıcak kolonlar — ts, host, src_ip, action …" },
  { name: "ocsf", hint: "OCSF sınıf/etkinlik alanları" },
  { name: "otel", hint: "OTel semconv alanları" },
];

export const TEST_KEYS: readonly SchemaKey[] = [
  { name: "name", hint: "Testin adı" },
  { name: "input", hint: "Ham satır — gerçek cihaz çıktısı olmalı" },
  { name: "expect", hint: "Beklentiler; `null` yazmak 'bu alan hiç olmamalı' demek" },
];

/** `map.core` içinde tanınan alanlar — `ParserYamlLoader.CoreFields`. */
export const CORE_FIELDS: readonly string[] = [
  "ts", "host", "vendor", "product",
  "src_ip", "dst_ip", "src_port", "dst_port",
  "proto", "action", "user_name", "severity_num", "outcome", "body",
];

/** Her adımın ortak anahtarları — `ParserYamlLoader.CommonStepKeys`. */
const COMMON_STEP_KEYS: readonly SchemaKey[] = [
  { name: "on_failure", hint: "fail (varsayılan) · continue · tag" },
  { name: "tag", hint: "on_failure: tag için etiket adı" },
];

export interface StepType {
  readonly name: string;
  readonly hint: string;
  readonly keys: readonly SchemaKey[];
  /** Editöre eklenecek iskelet — girinti çağıranda ayarlanıyor. */
  readonly snippet: string;
}

export const STEP_TYPES: readonly StepType[] = [
  {
    name: "grok",
    hint: "Pattern ile alan yakalama",
    keys: [
      { name: "field", hint: "Üzerinde çalışılacak alan (genelde message)" },
      { name: "patterns", hint: "Sırayla denenir; ilk tutan kazanır" },
      { name: "pattern_definitions", hint: "Üst setteki bir tanımı geçersiz kılmak için" },
      ...COMMON_STEP_KEYS,
    ],
    snippet: [
      "- grok:",
      "    field: message",
      "    patterns:",
      "      - '^ÖRNEK %{WORD:action}$'",
    ].join("\n"),
  },
  {
    name: "kv",
    hint: "anahtar=değer çözümleme (tırnak farkında)",
    keys: [
      { name: "field", hint: "Üzerinde çalışılacak alan" },
      { name: "separator", hint: "Çiftler arası ayraç (varsayılan boşluk)" },
      { name: "assign", hint: "Anahtar/değer ayracı (varsayılan =)" },
      { name: "quoted", hint: "Tırnaklı değerlerde ayraç yok sayılsın mı" },
      { name: "target_prefix", hint: "Üretilen alanlara önek" },
      { name: "include", hint: "Yalnızca bu anahtarlar" },
      { name: "exclude", hint: "Bu anahtarlar atlansın" },
      ...COMMON_STEP_KEYS,
    ],
    snippet: ["- kv:", "    field: message"].join("\n"),
  },
  {
    name: "json",
    hint: "JSON gövdesini alanlara açma",
    keys: [
      { name: "field", hint: "JSON taşıyan alan" },
      { name: "target_prefix", hint: "Üretilen alanlara önek" },
      { name: "flatten", hint: "İç içe nesneler a.b.c olarak düzleşsin mi" },
      ...COMMON_STEP_KEYS,
    ],
    snippet: ["- json:", "    field: message"].join("\n"),
  },
  {
    name: "csv",
    hint: "Sabit sütunlu satırlar",
    keys: [
      { name: "field", hint: "Üzerinde çalışılacak alan" },
      { name: "columns", hint: "Sütun adları, sırayla" },
      { name: "separator", hint: "Sütun ayracı" },
      { name: "quote", hint: "Tırnak karakteri" },
      { name: "trim", hint: "Baştaki/sondaki boşluklar kırpılsın mı" },
      ...COMMON_STEP_KEYS,
    ],
    snippet: ["- csv:", "    field: message", "    columns: [a, b]"].join("\n"),
  },
  {
    name: "date",
    hint: "Zaman damgası çözme",
    keys: [
      { name: "field", hint: "Zamanı taşıyan alan" },
      {
        name: "formats",
        hint: ".NET biçimleri artı UNIX · UNIX_MS · UNIX_US · UNIX_NS · UNIX_AUTO · ISO8601 · SYSLOG",
      },
      { name: "timezone_field", hint: "Cihazın yazdığı saat dilimi alanı (±HHmm de tanınıyor)" },
      { name: "default_timezone", hint: "Alan yoksa/çözülemezse kullanılan IANA adı" },
      { name: "target", hint: "Sonucun yazılacağı alan; varsayılan olay zamanı" },
      ...COMMON_STEP_KEYS,
    ],
    snippet: [
      "- date:",
      "    field: log_datetime",
      "    formats: [ISO8601]",
      "    default_timezone: UTC",
    ].join("\n"),
  },
  {
    name: "convert",
    hint: "Alan tiplerini dönüştürme",
    keys: [{ name: "fields", hint: "alan: int | long | double | bool | ip | string" }, ...COMMON_STEP_KEYS],
    snippet: ["- convert:", "    fields:", "      src_port: int"].join("\n"),
  },
  {
    name: "drop",
    hint: "Alanları atma",
    keys: [{ name: "fields", hint: "Atılacak alan adları" }, ...COMMON_STEP_KEYS],
    snippet: ["- drop:", "    fields: [gecici_alan]"].join("\n"),
  },
];

const STEP_NAMES = new Set(STEP_TYPES.map((step) => step.name));

/**
 * İmlecin bulunduğu **bölüm**.
 *
 * <p>Girintiye değil, en son görülen kök anahtara bakılıyor: YAML girintisi
 * blok içinde değişiyor ve "iki boşluk = metadata" gibi bir kural akış
 * biçimindeki (`metadata: { id: x }`) satırlarda hemen yanılırdı.</p>
 */
export type SchemaSection = "root" | "metadata" | "match" | "pipeline" | "map" | "tests" | "expect";

export function sectionAt(text: string, line: number): SchemaSection {
  const lines = text.split("\n");
  let section: SchemaSection = "root";

  for (let index = 0; index <= line && index < lines.length; index += 1) {
    const current = lines[index]!;
    const root = /^([A-Za-z_][\w]*)\s*:/.exec(current);

    if (root) {
      const name = root[1]!;
      section =
        name === "metadata" || name === "match" || name === "pipeline" || name === "map" || name === "tests"
          ? name
          : "root";
      continue;
    }

    // `expect:` testin içinde ve kendi anahtar uzayı var (core.* / ocsf.* /
    // fields.* / parse_status). Ayrı bölüm sayılmazsa `tests` anahtarları
    // önerilir ve öneri gürültüye dönerdi.
    if (section === "tests" && /^\s+expect\s*:/.test(current)) {
      section = "expect";
      continue;
    }

    if (section === "expect" && /^\s*-\s/.test(current)) {
      section = "tests";
    }
  }

  return section;
}

export interface Completion {
  /** İmlecin solunda kalan ve değiştirilecek olan parça. */
  readonly prefix: string;
  readonly options: readonly SchemaKey[];
}

const EXPECT_KEYS: readonly SchemaKey[] = [
  { name: "parse_status", hint: "ok · partial · failed" },
  { name: "tags", hint: "Etiket dizisi" },
  { name: "@timestamp", hint: "Çözülen olay zamanı (ISO 8601)" },
  ...CORE_FIELDS.map((field) => ({ name: `core.${field}`, hint: "core alanı" })),
];

/**
 * İmlecin bulunduğu yerde önerilecek anahtarlar.
 *
 * <p>
 * Saf fonksiyon: girdi metin ve imleç konumu, çıktı öneri listesi. DOM'a
 * dokunmadığı için tam olarak sınanabiliyor — bir tamamlama menüsünün sessizce
 * yanlış öneri vermesi, hiç öneri vermemesinden kötü.
 * </p>
 *
 * <p>
 * Değer tarafında öneri <b>yok</b>: imlecin solunda iki nokta varsa kullanıcı
 * değer yazıyor demektir ve oraya anahtar önermek yazdığını bozardı.
 * </p>
 */
export function suggest(text: string, caret: number): Completion | null {
  const start = text.lastIndexOf("\n", caret - 1) + 1;
  const linePrefix = text.slice(start, caret);

  if (linePrefix.includes(":")) {
    return null;
  }

  const token = /([A-Za-z_@$][\w.@$-]*)$/.exec(linePrefix);
  const prefix = token?.[1] ?? "";

  const lineNumber = text.slice(0, start).split("\n").length - 1;
  const section = sectionAt(text, lineNumber);

  const pool = poolFor(section, linePrefix);
  const matches = pool.filter((key) => key.name.startsWith(prefix) && key.name !== prefix);

  return matches.length > 0 ? { prefix, options: matches } : null;
}

function poolFor(section: SchemaSection, linePrefix: string): readonly SchemaKey[] {
  switch (section) {
    case "metadata":
      return METADATA_KEYS;
    case "match":
      return MATCH_KEYS;
    case "map":
      // `core:` altındaki satırlar alan adı bekliyor; `map:` altındakiler
      // hangi görünüm olduğunu.
      return /^\s{4,}/.test(linePrefix)
        ? CORE_FIELDS.map((field) => ({ name: field, hint: "core alanı" }))
        : MAP_KEYS;
    case "tests":
      return TEST_KEYS;
    case "expect":
      return EXPECT_KEYS;
    case "pipeline":
      return pipelinePool(linePrefix);
    default:
      return ROOT_KEYS;
  }
}

/**
 * Boru hattında iki bağlam var: adım tipini seçmek (`- ` ile başlayan satır) ve
 * seçilen adımın anahtarlarını yazmak. İkisini ayırmadan öneri listesi hem
 * adım adlarını hem her adımın anahtarlarını içerirdi — yani hiçbir şey
 * söylemezdi.
 */
function pipelinePool(linePrefix: string): readonly SchemaKey[] {
  if (/^\s*-\s*$/.test(linePrefix) || /^\s*-\s*[A-Za-z_]*$/.test(linePrefix)) {
    return STEP_TYPES.map((step) => ({ name: step.name, hint: step.hint }));
  }

  return COMMON_STEP_KEYS.concat(
    STEP_TYPES.flatMap((step) => step.keys.filter((key) => !STEP_NAMES.has(key.name))),
  ).filter((key, index, all) => all.findIndex((other) => other.name === key.name) === index);
}
