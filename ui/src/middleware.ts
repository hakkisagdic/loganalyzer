import { NextResponse, type NextRequest } from "next/server";

/**
 * Korunan yolların ilk kapısı.
 *
 * <p>
 * Burada yapılan tek şey <b>çerezin var olup olmadığına</b> bakmak. Oturumun
 * gerçekten geçerli olduğu burada anlaşılamaz: token'lar sunucu belleğindeki
 * depoda ve middleware ayrı bir çalışma zamanında koşuyor. Gerçek doğrulama
 * sayfada (<c>currentUser</c>) ve vekilde (<c>proxyToApi</c>) yapılıyor.
 * </p>
 *
 * <p>
 * Yine de değerli: çerezi hiç olmayan bir ziyaretçi, sayfayı çizip API'ye
 * gidip geri dönmek yerine tek adımda giriş akışına düşüyor.
 * </p>
 */
export function middleware(request: NextRequest): NextResponse {
  const cookieName = process.env.BFF_COOKIE_NAME ?? "bizigo.sid";

  if (request.cookies.has(cookieName)) {
    return NextResponse.next();
  }

  const login = new URL("/api/auth/login", request.nextUrl.origin);
  login.searchParams.set("returnTo", request.nextUrl.pathname + request.nextUrl.search);

  return NextResponse.redirect(login);
}

export const config = {
  /*
   * Muaf tutulanlar:
   *   /api/auth/*  — giriş ve çıkış akışının kendisi
   *   /api/bff/*   — vekil; oturumsuz isteğe 401 JSON dönüyor, yönlendirme değil
   *   /signin-oidc — OIDC dönüş ucu; çerez tam burada yazılıyor
   *   /giris       — giriş sayfası
   *   /_next/*     — derleme çıktısı
   */
  matcher: ["/((?!api/auth|api/bff|signin-oidc|giris|_next/static|_next/image|favicon.ico).*)"],
};
