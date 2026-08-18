import type { NextRequest, NextResponse } from "next/server";

import { apiError, proxyToApi } from "@/lib/api/proxy";

export const dynamic = "force-dynamic";

/**
 * `Bizigo.Api`'ye açılan **tek** kapı.
 *
 * <p>
 * Tarayıcı `/api/bff/v1/events/search` diyor, Next `${BIZIGO_API_URL}/v1/events/search`
 * diyor. Aradaki fark bir `Authorization` başlığı ve o başlık hiçbir zaman
 * geri yönde ilerlemiyor.
 * </p>
 */

interface RouteContext {
  readonly params: Promise<{ path: string[] }>;
}

/**
 * Vekile giren yol.
 *
 * <p>Segmentler Next tarafından zaten çözülmüş (yüzde kodlaması açılmış) hâlde
 * geliyor; yeniden birleştirirken kodluyoruz ki içinde eğik çizgi taşıyan bir
 * segment yol yapısını değiştiremesin.</p>
 */
function joinPath(segments: string[]): string {
  return segments.map(encodeURIComponent).join("/");
}

async function handle(request: NextRequest, context: RouteContext): Promise<NextResponse> {
  const { path } = await context.params;

  if (path.length === 0) {
    return apiError(404, "Vekil yolu boş.");
  }

  return proxyToApi(request, joinPath(path));
}

export const GET = handle;
export const POST = handle;
export const PUT = handle;
export const PATCH = handle;
export const DELETE = handle;
