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
 * (Tip sökme Node 22.18 ve 23.6'dan itibaren varsayılan; bayrak orada da
 * zararsız, yalnızca daha eski 22.x sürümlerinde zorunlu.)
 * </p>
 */

const REPO = fileURLToPath(new URL("../../..", import.meta.url));
const UI = fileURLToPath(new URL("../..", import.meta.url));

const CLICKHOUSE = "Host=localhost;Port=8123;Database=bizigo;Username=bizigo;Password=bizigo";

const CONTROL_PLANE = "Host=localhost;Port=5432;Database=bizigo;Username=bizigo;Password=bizigo";

/**
 * Analistin IdP grubu.
 *
 * <p>
 * Kapsam eşlemesi ve envanter artık <c>catalog/simulators/filo.yaml</c>'dan
 * geliyor (S05); bu sabit yalnızca <b>giriş akışının</b> hangi kimlikle
 * koşacağını söylüyor. Eşlemenin kendisi burada DEĞİL — iki yerde durup
 * ayrışmaları, ayrışan şeyin kapsam olması demekti.
 * </p>
 */
const IDP_GROUP = "/network/core";

/**
 * Koşumun hedefi — `playwright.config.ts`'teki `E2E_TARGET` ile **aynı**
 * değişkeni okuyor (T49).
 *
 * <p>
 * İki dosya arasında paylaşılan bir sabit yerine aynı ortam değişkeni: bu
 * betik Playwright'tan <b>ayrı bir süreçte</b> koşuyor (<c>npm run e2e</c>'nin
 * ilk adımı) ve import etseydi Playwright'ın yapılandırmasını yan etkileriyle
 * birlikte yüklerdi.
 * </p>
 */
const TARGET = process.env.E2E_TARGET === "container" ? "container" : "local";

/** Yığından beklenen servisler ve neden gerektikleri. */
const BASE_SERVICES: ReadonlyArray<readonly [service: string, why: string]> = [
  ["clickhouse", "olay tablosu — arama ve RCA ekranlarının verisi"],
  ["postgres", "kontrol düzlemi — envanter, katalog, kapsam eşlemesi"],
  ["keycloak", "kimlik — giriş akışı gerçek OIDC üzerinden yürüyor"],
  ["rustfs", "ham arşiv — olay detayındaki ham bayt görünümü"],
  ["sidecar", "şablon çıkarımı — boru hattı özetindeki keşif göstergesi"],
];

/**
 * Container kipinde API ve ekran da yığından geliyor — ve **ön koşul
 * olmaları** bu kipin bütün anlamı.
 *
 * <p>
 * Eksik olduklarında koşum burada duruyor, testin içinde değil. Aksi hâlde
 * container'a karşı koşacağını sanan bir koşum yerel bir sunucuyu bulup yeşil
 * yanardı — yani container hakkında hiçbir şey söylemeyen, ama söylediğini
 * sanan bir sonuç (§7).
 * </p>
 */
const CONTAINER_SERVICES: ReadonlyArray<readonly [service: string, why: string]> = [
  ["api", "container'daki API — bu kipte hedefin kendisi"],
  ["ui", "container'daki ekran — BFF'in container ağında çalıştığı ancak burada sınanıyor"],
];

const REQUIRED_SERVICES = TARGET === "container"
  ? [...BASE_SERVICES, ...CONTAINER_SERVICES]
  : BASE_SERVICES;

const UP_COMMAND = TARGET === "container"
  ? "cd deploy && docker compose --profile api up -d --wait"
  : "cd deploy && docker compose up -d --wait clickhouse postgres rustfs keycloak sidecar";

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
    // GRUP FİLODAN (S05). Eskiden varsayılan `golden` grubuna yazıyordu ve
    // filo `network/core` üretiyor — ikisi ayrışsaydı analistin kapsamı
    // envanteri görür, OLAYLARI görmezdi: ekran boş, sebep görünmez.
    ["seed", "golden", "--replace", "--events", "40000", "--span-days", "14",
     "--owner-group", "network/core"],
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

  // KAPSAM VE ENVANTER ARTIK FİLODAN (S05).
  //
  // Buradaki iki adım eskiden ELLE SQL'di: bir `INSERT` kapsam eşlemesini
  // kuruyor, `seedInventory()` envanteri ClickHouse'taki olaylardan türetiyordu.
  // İkisi de ürünün yolunu atlıyordu ve aradaki fark sessizdi — ekran dolu
  // görünür, doldurabilme iddiası hiç sınanmamış olurdu.
  //
  // İkinci adımın ayrı bir bedeli daha vardı: envanteri GELMİŞ VERİDEN türetmek,
  // veri göndermemiş bir kaynağı envantere hiç yazmıyor — yani susan cihaz
  // envanterde yok ve sessizlik alarmı sınanamıyor.
  process.stdout.write("· filo uygulanıyor (kapsam eşlemesi + kaynak envanteri)\n");

  const bizigo = "src/Bizigo.Cli/bin/Debug/net10.0/bizigo";
  run(bizigo, ["fleet", "apply", "catalog/simulators"], REPO, {
    DOTNET_ROOT: `${process.env.HOME}/.dotnet`,
    BIZIGO_CONTROLPLANE: CONTROL_PLANE,
  });
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

/**
 * Container'daki API'yi tohumlamadan **sonra** geri kaldırır (T49).
 *
 * <h3>Sıra burada bir doğruluk koşulu, tercih değil</h3>
 *
 * <p>
 * <c>scopeAndInventory</c> API'yi durduruyor ve gerekçesi orada yazılı:
 * <c>Program.cs</c> kapsam eşlemesini <b>açılışta bir kez</b> belleğe alıyor
 * (<c>AccessScopeResolver.RefreshAsync</c>) ve bir daha tazelemiyor. Yerelde
 * bunu Playwright telafi ediyor — durdurulan container'ın yerine yeni bir
 * süreç açıyor. Container kipinde Playwright <b>hiçbir şey başlatmıyor</b>,
 * dolayısıyla telafi eden kimse yok.
 * </p>
 *
 * <p>
 * Bu adım olmasaydı zincir şu olurdu: API açık kalır → kapsam önbelleği
 * tohumlamadan ÖNCEKİ hâliyle (boş) durur → analistin kapsamı boş → hiçbir
 * olay dönmez → <b>ekran boş</b>. Hata yok, sayaç yok, belirti yok — testler
 * "veri görünmüyor" der ve sebep container'ın kendisinde aranır.
 * </p>
 *
 * <p>
 * <c>ui</c> yeniden başlatılmıyor: BFF açılışta hiçbir şey önbelleklemiyor,
 * her isteği API'ye vekilliyor. Gereksiz bir yeniden başlatma, kaldırma
 * süresini uzatmaktan başka bir şey yapmazdı.
 * </p>
 */
function startContainerApi(): void {
  process.stdout.write("· container'daki API geri kaldırılıyor (kapsam önbelleği tazelensin)\n");

  run(
    "docker",
    ["compose", "-f", "deploy/docker-compose.yml", "--profile", "api", "up", "-d", "--wait", "api"],
    REPO,
  );
}

assertStackIsUp();
build();
seed();
await scopeAndInventory();

if (TARGET === "container") {
  // Arayüz derlemesi YOK: imaj kendi `next build`'ini çalıştırdı ve container
  // onu koşturuyor. Burada yeniden derlemek, koşumun hedefi OLMAYAN bir çıktı
  // üretmek olurdu — ve yeşil bir koşum, aslında sınanmayan bir imaj hakkında
  // konuşurdu.
  startContainerApi();
} else {
  buildUi();
}

process.stdout.write(`· hazır (hedef: ${TARGET})\n`);
