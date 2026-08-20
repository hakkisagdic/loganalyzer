import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

import { LIVE_TESTS } from "./vitest.live-tests";

/**
 * Canlı bileşen isteyen testler — `npm run test:live` (T27).
 *
 * Varsayılan koşum bunları dışlıyor; burası onları **yalnızca** onları
 * koşturuyor. Ayrı bir yapılandırma olmasının sebebi ergonomi değil dürüstlük:
 * `describe.skip` ile bırakıldıklarında koşturmanın tek yolu dosyayı
 * düzenlemekti, ve düzenlenmesi gereken bir test koşturulmayan bir testtir.
 *
 * Ön koşul: `docker compose -f deploy/docker-compose.yml up -d redis-session`.
 * Redis ayakta değilse test **kırmızı yanıyor** — sessizce atlamıyor. §2 gereği
 * koşturan koordinatör.
 */
export default defineConfig({
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  esbuild: { jsx: "automatic" },
  test: {
    environment: "node",
    include: [...LIVE_TESTS],
    isolate: true,
    // Kurulum kancası bağlantıyı bekliyor ve o bekleme vitest'in varsayılan
    // 10 sn'lik kanca sınırıyla YARIŞIYORDU: Redis kapalıyken kazanan vitest
    // oluyor, çıktı "Hook timed out" ve **3 skipped** diyordu — yani sebebi
    // söylemeyen bir hata ve "atlandı" gibi okunan bir kırmızı. Sınır açıkça
    // yükseltildi ki kancanın kendi mesajı kazansın.
    hookTimeout: 30_000,
  },
});
