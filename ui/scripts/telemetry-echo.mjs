#!/usr/bin/env node
/**
 * Yerel telemetri alıcısı — PostHog'un yerine geçer.
 *
 * Amacı bir demo değil bir KANIT. `TELEMETRY_HOST` buraya çevrildiğinde,
 * üründen çıkan her olay burada açılıyor ve **kanarya metinlerine** karşı
 * taranıyor: aramada yazdığınız IP, kullanıcı adı ya da sorgu metni paylodda
 * geçiyorsa süreç bunu bağırıyor ve çıkış kodunu 1 yapıyor.
 *
 * "Sorgu gitmiyor" cümlesini okumak ile giden byte'lara bakmak farklı şeyler.
 * Bu betik ikincisini yapıyor.
 *
 *   node ui/scripts/telemetry-echo.mjs --port 8000 \
 *     --canary "10.0.4.17" --canary "admin@musteri.com"
 *
 * Sonra ui/.env.local içinde:
 *   TELEMETRY_ENABLED=true
 *   TELEMETRY_PROJECT_KEY=phc_yerel_test
 *   TELEMETRY_HOST=http://127.0.0.1:8399
 *
 * ⚠️ `localhost` DEĞİL `127.0.0.1` yazın, ve varsayılan portu değiştirmeyin.
 * Ölçüldü ve yarım saat yedi: bu betik `127.0.0.1`'e bağlanıyor (yani yalnızca
 * IPv4), Node'un `fetch`i ise `localhost`u önce `::1`'e çözüyor. Docker Desktop
 * bu makinede `*:8000`'i IPv6'da tutuyordu, dolayısıyla üründen çıkan her olay
 * sessizce DOCKER'A gidiyor ve 404 dönüyordu — vekil doğru, hedef adres doğru,
 * trafik başka bir servise. Bu ürünün en pahalı hata sınıfının (CLAUDE.md §7)
 * bir aracın içindeki hâli.
 */
import { createServer } from "node:http";
import { gunzipSync, inflateSync } from "node:zlib";

const args = process.argv.slice(2);

function flag(name, fallback) {
  const at = args.indexOf(`--${name}`);
  return at >= 0 && args[at + 1] ? args[at + 1] : fallback;
}

const port = Number(flag("port", "8399"));

// Kanaryalar: paylodda GEÇMEMESİ gereken metinler. Birden fazla verilebilir.
const canaries = [];
for (let i = 0; i < args.length; i += 1) {
  if (args[i] === "--canary" && args[i + 1]) {
    canaries.push(args[i + 1]);
  }
}

let received = 0;
let breaches = 0;

/** posthog-js gzip'liyor, posthog-node çoğu zaman düz gönderiyor. İkisi de açılmalı. */
function decode(buffer, encoding) {
  try {
    if (encoding === "gzip") return gunzipSync(buffer).toString("utf8");
    if (encoding === "deflate") return inflateSync(buffer).toString("utf8");
  } catch {
    // Sıkıştırma açılmadıysa ham hâlini denemek, sessizce boş dönmekten iyi.
  }
  return buffer.toString("utf8");
}

/** posthog-js gövdeyi bazen `data=<base64>` olarak form-encoded yolluyor. */
function unwrap(text) {
  if (text.startsWith("data=")) {
    try {
      return Buffer.from(decodeURIComponent(text.slice(5)), "base64").toString("utf8");
    } catch {
      return text;
    }
  }
  return text;
}

function events(parsed) {
  if (Array.isArray(parsed)) return parsed;
  if (parsed && Array.isArray(parsed.batch)) return parsed.batch;
  return parsed ? [parsed] : [];
}

const server = createServer((req, res) => {
  const chunks = [];

  req.on("data", (chunk) => chunks.push(chunk));
  req.on("end", () => {
    const raw = decode(Buffer.concat(chunks), req.headers["content-encoding"]);
    const body = unwrap(raw);

    // Yol her zaman yazılıyor: hangi ucun kullanıldığını görmek, vekilin
    // beyaz listesinin doğru olup olmadığını da gösteriyor.
    process.stdout.write(`\n── ${req.method} ${req.url}\n`);

    let parsed;
    try {
      parsed = JSON.parse(body);
    } catch {
      if (body.length > 0) {
        process.stdout.write(`   (JSON değil, ${body.length} byte)\n`);
      }
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify({ status: 1 }));
      return;
    }

    for (const item of events(parsed)) {
      if (!item || typeof item !== "object" || !item.event) continue;

      received += 1;

      const props = { ...(item.properties ?? {}) };
      // PostHog'un kendi iç alanlarını ayıklayıp ürünün gönderdiğini
      // görünür kılıyoruz — asıl bakılacak şey o.
      const kendi = {};
      for (const [k, v] of Object.entries(props)) {
        if (!k.startsWith("$")) kendi[k] = v;
      }

      process.stdout.write(`   olay: ${item.event}\n`);
      process.stdout.write(`   kimlik: ${item.distinct_id ?? item.distinctId ?? "(yok)"}\n`);
      process.stdout.write(`   ürünün gönderdiği: ${JSON.stringify(kendi)}\n`);

      const phOzel = Object.keys(props).filter((k) => k.startsWith("$"));
      if (phOzel.length > 0) {
        process.stdout.write(`   posthog alanları: ${phOzel.join(", ")}\n`);
      }
    }

    // KANARYA TARAMASI — ham gövdenin tamamında, ayıklanmış hâlinde değil.
    // Bir alanın adında ya da PostHog'un eklediği bir alanda geçmesi de ihlal.
    for (const canary of canaries) {
      if (body.includes(canary)) {
        breaches += 1;
        process.stdout.write(`\n   ❌ KANARYA İHLALİ: "${canary}" paylodda GEÇİYOR\n`);
      }
    }

    if (canaries.length > 0 && breaches === 0) {
      process.stdout.write(`   ✓ ${canaries.length} kanaryanın hiçbiri paylodda yok\n`);
    }

    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ status: 1 }));
  });
});

function kapat() {
  process.stdout.write(`\n${"─".repeat(60)}\n`);
  process.stdout.write(`toplam olay: ${received}\n`);
  process.stdout.write(`kanarya ihlali: ${breaches}\n`);
  // Çıkış kodu ihlale bağlı: bu betik bir CI adımı olarak da koşabilir.
  process.exit(breaches > 0 ? 1 : 0);
}

process.on("SIGINT", kapat);
process.on("SIGTERM", kapat);

server.listen(port, "127.0.0.1", () => {
  // Adres BİLEREK `127.0.0.1` yazılıyor, `localhost` değil: kopyalayıp
  // `TELEMETRY_HOST`a yapıştıran kişi doğru olanı yapıştırsın (bkz. yukarıdaki
  // IPv6 notu).
  process.stdout.write(`telemetri echo dinliyor: http://127.0.0.1:${port}\n`);
  process.stdout.write(`TELEMETRY_HOST=http://127.0.0.1:${port}   ← "localhost" YAZMAYIN\n`);
  if (canaries.length > 0) {
    process.stdout.write(`kanaryalar: ${canaries.map((c) => `"${c}"`).join(", ")}\n`);
  } else {
    process.stdout.write(`kanarya verilmedi — yalnızca yankı modu (--canary "metin")\n`);
  }
});
