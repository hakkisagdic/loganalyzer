import { NextResponse, type NextRequest } from "next/server";

import { readTelemetryConfig, telemetryState } from "@/lib/telemetry/config";

export const dynamic = "force-dynamic";

/**
 * Telemetri vekili — tarayıcı ile PostHog arasındaki **tek** yol.
 *
 * <p>
 * `api/bff/[...path]` ile aynı desen ve aynı gerekçe: tarayıcı üçüncü bir
 * alan adına <b>hiç</b> konuşmuyor. Üç şey kazanıyoruz:
 * </p>
 *
 * <ol>
 *   <li><b>Müşteri ağı yalnızca ürünün kendi adresini görüyor.</b> Bir log
 *   analiz ürününü değerlendiren güvenlik ekibinin ilk sorduğu şey bu, ve
 *   "tarayıcı posthog.com'a gidiyor" cevabı satın almayı durduran cevap.</li>
 *   <li><b>İstemci IP'si yukarı akışa gitmiyor.</b> Vekil `X-Forwarded-For`
 *   taşımıyor — PostHog kaynak IP'sini coğrafi çözümlemede kullanıyor ve
 *   müşteri ağının adresi bizim analitiğimizde işi yok. Bunun bedeli
 *   ülke kırılımının kaybolması; bilinçli takas.</li>
 *   <li>Reklam engelleyiciler telemetriyi sessizce kesmiyor — kesilirse
 *   kendi ucumuz kesiliyor demektir ve o görünür.</li>
 * </ol>
 */

/**
 * Yukarı akışa geçmesine izin verilen yollar — **beyaz liste**, düzenli
 * ifadelerle.
 *
 * <p>
 * Listede olmayan en önemli şey <c>s/</c>: <b>oturum kaydı</b> (session
 * replay) ucu. Kasıtlı. Bu ürünün ekranında duran şey müşterinin log
 * satırları; oturum kaydı açıldığı anda o satırlar videoya giriyor ve kimse
 * o kararı vermiş olmuyor. İstemci tarafında da kapalı
 * (<c>disable_session_recording</c>) ama <b>iki kapı</b> var çünkü tek kapı
 * bir sürüm yükseltmesinde varsayılanı değişebilecek bir seçenek; bu kapı
 * değişmiyor.
 * </p>
 *
 * <p>
 * Reddedilen yol <b>sessizce düşmüyor</b>, 403 dönüyor. Sessiz reddin sonucu
 * "telemetri açık ama bazı olaylar hiç gelmiyor" olurdu — bulunması aylar
 * alan sınıf.
 * </p>
 */
const ALLOWED_UPSTREAM_PATHS: readonly RegExp[] = [
  /^e$/, // olay yakalama (eski uç)
  /^i\/v0\/e$/, // olay yakalama (güncel uç)
  /^batch$/, // toplu gönderim
  /^decide$/, // özellik bayrakları (eski uç)
  /^flags$/, // özellik bayrakları (güncel uç)
  /^array\/[^/]+\/config(\.js)?$/, // uzaktan yapılandırma
  /^engage$/, // kişi özellikleri
];

/** Varlık sunucusuna giden yollar — `assetHost` üzerinden. */
const ASSET_PATH = /^static\//;

/** Yukarı akışa taşınan istek başlıkları. Beyaz liste. */
const FORWARDED_REQUEST_HEADERS = ["content-type", "accept", "accept-encoding"];

/** Tarayıcıya taşınan yanıt başlıkları. Beyaz liste. */
const FORWARDED_RESPONSE_HEADERS = ["content-type", "content-encoding", "cache-control"];

interface RouteContext {
  readonly params: Promise<{ path: string[] }>;
}

function refuse(status: number, error: string, hint?: string): NextResponse {
  // Gövde biçimi `api/proxy.ts` ile aynı: `{ error, hint }`.
  return NextResponse.json({ error, hint }, { status });
}

async function handle(request: NextRequest, context: RouteContext): Promise<NextResponse> {
  const state = telemetryState();

  if (state.status === "disabled") {
    // 404 DEĞİL, 403. 404 "böyle bir uç yok" der ve geliştiriciyi olmayan bir
    // yazım hatasını aramaya yollar. 403 "uç var, kapalı" diyor.
    return refuse(403, "Telemetri kapalı.", "TELEMETRY_ENABLED=true ile açılıyor.");
  }

  if (state.status === "misconfigured") {
    return refuse(
      503,
      "Telemetri açık ama yapılandırılmamış.",
      `Eksik ortam değişkenleri: ${state.missing.join(", ")}`,
    );
  }

  const { path } = await context.params;
  const upstreamPath = path.map(encodeURIComponent).join("/");

  const config = readTelemetryConfig();
  const isAsset = ASSET_PATH.test(upstreamPath);

  if (!isAsset && !ALLOWED_UPSTREAM_PATHS.some((pattern) => pattern.test(upstreamPath))) {
    return refuse(
      403,
      `Telemetri vekilinde izinli olmayan yol: /${upstreamPath}`,
      "İzinli yollar `ALLOWED_UPSTREAM_PATHS` içinde. Oturum kaydı ucu (`/s`) bilinçli olarak dışarıda.",
    );
  }

  const base = isAsset ? config.assetHost : config.host;
  const target = `${base}/${upstreamPath}${request.nextUrl.search}`;

  const headers = new Headers();

  for (const name of FORWARDED_REQUEST_HEADERS) {
    const value = request.headers.get(name);

    if (value) {
      headers.set(name, value);
    }
  }

  // `X-Forwarded-For` BİLEREK yok — yukarıdaki (2) numaralı gerekçe. Oturum
  // çerezi de yok: PostHog'un bizim oturumumuzla hiç işi olmamalı.

  const body =
    request.method === "GET" || request.method === "HEAD" ? undefined : await request.arrayBuffer();

  let upstream: Response;

  try {
    upstream = await fetch(target, {
      method: request.method,
      headers,
      body,
      redirect: "manual",
      cache: "no-store",
    });
  } catch {
    // Telemetrinin ulaşılamaz olması ÜRÜNÜ bozmamalı: istemci tarafı bu
    // yanıtı yutuyor. Yine de 502 dönüyoruz ki ağ sekmesine bakan biri
    // "gitmiyor" ile "gidiyor ama boş" arasını ayırabilsin.
    return refuse(502, "Telemetri sunucusuna ulaşılamıyor.", `Hedef: ${base}`);
  }

  const responseHeaders = new Headers();

  for (const name of FORWARDED_RESPONSE_HEADERS) {
    const value = upstream.headers.get(name);

    if (value) {
      responseHeaders.set(name, value);
    }
  }

  return new NextResponse(await upstream.arrayBuffer(), {
    status: upstream.status,
    headers: responseHeaders,
  });
}

export const GET = handle;
export const POST = handle;
export const OPTIONS = handle;
