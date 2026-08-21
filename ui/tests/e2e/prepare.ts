import { execFileSync } from "node:child_process";
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";

/**
 * Uçtan uca koşumun ön hazırlığı — `npm run e2e` bunu ÖNCE çalıştırıyor.
 *
 * <p>
 * Dört iş yapıyor ve <b>dördü de sessizce atlanamıyor</b>: yığın kontrolü,
 * derleme, veri tohumlama, arayüz derlemesi. Biri eksikse koşum burada
 * duruyor — testin içinde değil. Sebebi §7: koşuma girip ortamı bulamayan bir
 * bekçi, kendini "atlandı" diye gösteren bir kırmızıdır ve kimse okumaz.
 * </p>
 *
 * <h3>Neden Playwright'ın `globalSetup`'ı değil</h3>
 *
 * <p>
 * Playwright <c>webServer</c>'ı <c>globalSetup</c>'tan <b>önce</b> başlatıyor.
 * Derleme globalSetup'ta dururken sunucu, henüz var olmayan bir ikiliyi
 * çalıştırmaya çalışıyordu ve hata <c>exit code 127</c> oluyordu — sebebi
 * söylemeyen bir kırmızı. Ölçüldü: ilk koşum tam buradan düştü.
 * </p>
 *
 * <p>
 * Betik <c>node</c> ile doğrudan koşuyor: <c>--experimental-strip-types</c> tip
 * bildirimlerini söküyor, yani ayrı bir çevirici bağımlılığı gerekmiyor. Bayrak
 * <b>Node 22.6</b>'dan beri var ve CI Node 22 kullanıyor; önkoşul
 * <c>ui/package.json</c>'daki <c>engines</c> alanında beyan edilmiş durumda.
 * (Node 23+ bunu varsayılan yapıyor, bayrak orada zararsız.)
 * </p>
 */

const REPO = fileURLToPath(new URL("../../..", import.meta.url));
const UI = fileURLToPath(new URL("../..", import.meta.url));

const CLICKHOUSE = "Host=localhost;Port=8123;Database=bizigo;Username=bizigo;Password=bizigo";

/** Analistin IdP grubu ve tohumlanan verinin kapsam grubu. */
const IDP_GROUP = "/network/core";
const OWNER_GROUP = "golden";

/** Yığından beklenen servisler ve neden gerektikleri. */
const REQUIRED_SERVICES: ReadonlyArray<readonly [service: string, why: string]> = [
  ["clickhouse", "olay tablosu — arama ve RCA ekranlarının verisi"],
  ["postgres", "kontrol düzlemi — envanter, katalog, kapsam eşlemesi"],
  ["keycloak", "kimlik — giriş akışı gerçek OIDC üzerinden yürüyor"],
  ["rustfs", "ham arşiv — olay detayındaki ham bayt görünümü"],
  ["sidecar", "şablon çıkarımı — boru hattı özetindeki keşif göstergesi"],
];

const UP_COMMAND =
  "cd deploy && docker compose up -d --wait clickhouse postgres rustfs keycloak sidecar";

// `NodeJS.ProcessEnv` DEĞİL: Next bu arayüzü `NODE_ENV`'i zorunlu kılacak
// şekilde genişletiyor ve buradaki ek değişkenler onu taşımıyor. Zaten
// istediğimiz şey de tam olarak "process.env'in üstüne birkaç anahtar".
function run(command: string, args: readonly string[], cwd: string, env?: Record<string, string>): string {
  return execFileSync(command, args, {
    cwd,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "inherit"],
    env: { ...process.env, ...env },
    maxBuffer: 32 * 1024 * 1024,
  });
}

/**
 * Yığın ayakta mı — <b>compose'un kendi cevabıyla</b>.
 *
 * <p>
 * Portları tek tek yoklamak yerine compose'a soruyoruz: bir servis "açık ama
 * sağlıksız" olabiliyor (Keycloak realm import'unu bitirmeden port dinliyor) ve
 * port yoklaması o hâli sağlıklı sayardı. Sağlık kontrolleri zaten
 * <c>docker-compose.yml</c>'de yazılı; ikinci bir tanım yazmak onların
 * ayrışabileceği bir yer daha açardı.
 * </p>
 */
function assertStackIsUp(): void {
  let raw: string;

  try {
    raw = run("docker", ["compose", "-f", "deploy/docker-compose.yml", "ps", "--format", "json"], REPO);
  } catch (cause) {
    throw new Error(
      `Docker'a ulaşılamadı. Yığın olmadan uçtan uca koşum anlamsız.\n\n  ${UP_COMMAND}\n`,
      { cause },
    );
  }

  // Compose sürümüne göre çıktı ya satır başına bir nesne ya tek dizi.
  const trimmed = raw.trim();
  const rows: Array<{ Service?: string; State?: string; Health?: string }> = trimmed.startsWith("[")
    ? JSON.parse(trimmed)
    : trimmed
        .split("\n")
        .filter((line) => line.trim().length > 0)
        .map((line) => JSON.parse(line));

  const byService = new Map(rows.map((row) => [row.Service ?? "", row]));
  const missing: string[] = [];

  for (const [service, why] of REQUIRED_SERVICES) {
    const row = byService.get(service);
    const healthy =
      row?.State === "running" && (row.Health === "healthy" || (row.Health ?? "") === "");

    if (!healthy) {
      const state = row ? `${row.State}${row.Health ? `/${row.Health}` : ""}` : "yok";
      missing.push(`  ${service.padEnd(12)} ${state.padEnd(20)} ${why}`);
    }
  }

  if (missing.length > 0) {
    throw new Error(
      `Yığın hazır değil — ${missing.length} servis eksik:\n\n${missing.join("\n")}\n\n` +
        `Kaldırmak için:\n\n  ${UP_COMMAND}\n`,
    );
  }
}

/**
 * API ve CLI ikilileri.
 *
 * <p>
 * Playwright'ın <c>webServer</c>'ı derlenmiş ikiliyi çalıştırıyor; derlemeyi
 * onun içine koymak, sunucu açılış zaman aşımını derleme süresiyle yarıştırırdı.
 * </p>
 */
function build(): void {
  const dotnet = `${process.env.HOME}/.dotnet/dotnet`;
  const env = { DOTNET_ROOT: `${process.env.HOME}/.dotnet` };

  if (!existsSync(dotnet)) {
    throw new Error(
      `.NET 10 SDK bulunamadı: ${dotnet}\n` +
        "PATH'teki `dotnet` /usr/local/share/dotnet'e çözülüyor ve orada yalnızca SDK 8/9 var (CLAUDE.md §12).",
    );
  }

  // Tek çağrıda iki proje verilemiyor: MSBuild `MSB1008` ile reddediyor.
  for (const project of ["src/Bizigo.Api", "src/Bizigo.Cli"]) {
    process.stdout.write(`· ${project} derleniyor\n`);
    run(dotnet, ["build", project, "--configuration", "Debug"], REPO, env);
  }
}

/**
 * Ölçüm verisi — <c>bizigo seed golden</c>.
 *
 * <p>
 * Doğrudan <c>INSERT</c> yok: satırlar üretimdeki yoldan geçiyor
 * (<c>EncodingDetector → EventComposer → EventNormalizer → EventWriter</c>),
 * yani <c>signature_hash</c>, <c>template_id</c> ve <c>attrs</c> gerçek
 * değerlerini alıyor. Elle yazılmış bir satır bunların hepsinde ayrışabilir ve
 * ayrıştığı hiçbir yerde görünmez — ekran görüntüsü de o yalanı gösterirdi.
 * </p>
 */
function seed(): void {
  if (process.env.E2E_SKIP_SEED === "1") {
    process.stdout.write("· tohumlama atlandı (E2E_SKIP_SEED=1)\n");
    return;
  }

  const bizigo = "src/Bizigo.Cli/bin/Debug/net10.0/bizigo";
  const env = { DOTNET_ROOT: `${process.env.HOME}/.dotnet`, BIZIGO_CLICKHOUSE: CLICKHOUSE };

  process.stdout.write("· ClickHouse göçleri\n");
  run(bizigo, ["schema", "migrate", "db/clickhouse"], REPO, env);

  process.stdout.write("· altın örnekler yükleniyor\n");
  run(
    bizigo,
    ["seed", "golden", "--replace", "--events", "40000", "--span-days", "14"],
    REPO,
    env,
  );
}

/**
 * Kontrol düzlemi göçleri ve kapsam verisi — <b>Playwright hiçbir şey
 * başlatmadan ÖNCE</b>.
 *
 * <h3>Neden testin içinde değil</h3>
 *
 * <p>
 * Daha önce buradaki iki satır (<c>idp_group_mapping</c> ve envanter) testin
 * <c>beforeAll</c>'undaydı ve <b>yerelde geçiyordu</b>. CI'da geçmezdi ve
 * sebebi kodda yazılı: <c>Program.cs</c> kapsam eşlemesini <b>açılışta bir
 * kez</b> belleğe alıyor (<c>AccessScopeResolver.RefreshAsync</c>) ve bir daha
 * tazelemiyor. Playwright ise <c>webServer</c>'ları testlerden önce başlatıyor.
 * </p>
 *
 * <p>
 * Zincir şuydu: API açılır → eşleme tablosu <b>temiz veritabanında boş</b> →
 * önbellek boş yüklenir → test satırı yazar (çok geç) → analistin kapsamı boş
 * kalır → hiçbir olay dönmez → ekran boş. Yerelde geçmesinin tek sebebi
 * satırın önceki bir koşumdan kalmış olmasıydı; yani test yeşildi ama sebebi
 * makinenin geçmişiydi.
 * </p>
 *
 * <p>
 * EF göçü de burada koşuyor, çünkü tablo göçle doğuyor: temiz bir veritabanında
 * göç uygulanmadan satır yazılamaz.
 * </p>
 */
async function scopeAndInventory(): Promise<void> {
  const dotnet = `${process.env.HOME}/.dotnet/dotnet`;
  const env = { DOTNET_ROOT: `${process.env.HOME}/.dotnet` };

  // Container'daki API varsa DURDURULUYOR ve sebebi yukarıdaki önbellek:
  // Playwright yerelde zaten koşan bir sunucuyu tekrar kullanıyor
  // (`reuseExistingServer`), o da bu tohumlamadan ÖNCE açılmış olabilir — yani
  // eski, boş bir kapsam önbelleğiyle. CI'da bu hâl yok (orada her koşum kendi
  // sürecini açıyor), ve yerelin CI'yı yalanlaması bu turda düşülen tuzağın ta
  // kendisi. Durdurulunca Playwright yerelde de yeni bir süreç açıyor.
  try {
    run("docker", ["compose", "-f", "deploy/docker-compose.yml", "--profile", "api", "stop", "api"], REPO);
    process.stdout.write("· container'daki API durduruldu (kapsam önbelleği bayat kalmasın)\n");
  } catch {
    // Koşmuyorsa yapılacak bir şey yok.
  }

  process.stdout.write("· kontrol düzlemi göçleri\n");
  run(dotnet, ["tool", "restore"], REPO, env);
  run(
    dotnet,
    [
      "ef", "database", "update",
      "--project", "src/Bizigo.ControlPlane",
      "--startup-project", "src/Bizigo.Api",
    ],
    REPO,
    { ...env, ASPNETCORE_ENVIRONMENT: "Development" },
  );

  process.stdout.write("· kapsam eşlemesi\n");
  psql(`INSERT INTO bizigo.idp_group_mapping (idp_group, owner_group, note)
        VALUES ('${IDP_GROUP}', '${OWNER_GROUP}', 'uctan uca ekran goruntusu kosumu')
        ON CONFLICT (idp_group) DO UPDATE SET owner_group = EXCLUDED.owner_group;`);

  process.stdout.write("· kaynak envanteri\n");
  await seedInventory();
}

/** Kontrol düzlemine tek ifade — compose'un içindeki `psql` üzerinden. */
function psql(sql: string): void {
  run(
    "docker",
    [
      "compose", "-f", "deploy/docker-compose.yml", "exec", "-T", "postgres",
      "psql", "-U", "bizigo", "-d", "bizigo", "-v", "ON_ERROR_STOP=1", "-c", sql,
    ],
    REPO,
  );
}

/** ClickHouse'a tek sorgu; satırlar TSV. */
async function clickhouse(sql: string): Promise<string[][]> {
  const response = await fetch("http://localhost:8123/", {
    method: "POST",
    headers: {
      "X-ClickHouse-User": "bizigo",
      "X-ClickHouse-Key": "bizigo",
      "X-ClickHouse-Database": "bizigo",
    },
    body: sql,
  });

  if (!response.ok) {
    throw new Error(`ClickHouse sorguyu reddetti: ${await response.text()}`);
  }

  return (await response.text())
    .trim()
    .split("\n")
    .filter((line) => line.length > 0)
    .map((line) => line.split("\t"));
}

/**
 * Kaynak envanteri.
 *
 * <p>
 * <b>Liste ClickHouse'tan okunuyor, buraya yazılmıyor.</b> Parser dizinlerini
 * ikinci kez saymak, tohumlayıcı değiştiği gün sessizce ayrışan bir kopya
 * olurdu (§9).
 * </p>
 *
 * <p>
 * <c>parser_id</c> BASKIN parser'a bağlanıyor ve o da ölçülüyor
 * (<c>argMax</c>). Bağlamanın zararsız olduğu koda bakılarak doğrulandı: bağlı
 * parser tutmazsa <c>Dispatcher</c> kademe 2'ye düşüyor ve
 * <c>RecordBoundMiss</c> sayıyor — hiçbir satır kaybolmuyor.
 * </p>
 */
async function seedInventory(): Promise<void> {
  const rows = await clickhouse(
    `SELECT source_id,
            any(vendor),
            any(product),
            argMax(parser_id, satir) AS baskin_parser
     FROM (
       SELECT source_id, vendor, product, parser_id, count() AS satir
       FROM events
       WHERE owner_group = '${OWNER_GROUP}' AND parser_id != ''
       GROUP BY source_id, vendor, product, parser_id
     )
     GROUP BY source_id ORDER BY source_id FORMAT TSV`,
  );

  if (rows.length === 0) {
    throw new Error(
      `ClickHouse'ta '${OWNER_GROUP}' grubunda hiç kaynak yok. Tohumlama koştu mu?`,
    );
  }

  for (const row of rows) {
    for (const value of row) {
      if (value.includes("'")) {
        throw new Error(`Envanter değeri tırnak taşıyor, ifade kurulamaz: ${value}`);
      }
    }
  }

  const values = rows
    .map(
      ([sourceId, vendor, product, parserId]) =>
        `('${sourceId}', '${sourceId}', '${OWNER_GROUP}', '${vendor}', '${product}', ` +
        `'${parserId}', 'auto', 'golden', true, now(), now())`,
    )
    .join(",\n         ");

  psql(
    `INSERT INTO bizigo.sources
       (source_id, hostname, owner_group, vendor, product,
        parser_id, encoding, source_class, enabled, created_at, updated_at)
     VALUES ${values}
     ON CONFLICT (source_id) DO UPDATE SET
       owner_group = EXCLUDED.owner_group,
       vendor = EXCLUDED.vendor,
       product = EXCLUDED.product,
       parser_id = EXCLUDED.parser_id,
       updated_at = now();`,
  );
}

/** Arayüz derlemesi — `next start` derlenmiş çıktı istiyor. */
function buildUi(): void {
  if (process.env.E2E_SKIP_BUILD === "1" && existsSync(`${UI}/.next`)) {
    process.stdout.write("· arayüz derlemesi atlandı (E2E_SKIP_BUILD=1)\n");
    return;
  }

  process.stdout.write("· arayüz derleniyor\n");
  run("npm", ["run", "build"], UI);
}

assertStackIsUp();
build();
seed();
await scopeAndInventory();
buildUi();

process.stdout.write("· hazır\n");
