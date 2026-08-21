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
 * Betik <c>node</c> ile doğrudan koşuyor: Node 24 tip bildirimlerini kendisi
 * söküyor, yani ayrı bir çevirici bağımlılığı eklemeye gerek yok.
 * </p>
 */

const REPO = fileURLToPath(new URL("../../..", import.meta.url));
const UI = fileURLToPath(new URL("../..", import.meta.url));

const CLICKHOUSE = "Host=localhost;Port=8123;Database=bizigo;Username=bizigo;Password=bizigo";

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
buildUi();

process.stdout.write("· hazır\n");
