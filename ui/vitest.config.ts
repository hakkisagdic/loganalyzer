import { fileURLToPath } from "node:url";
import { configDefaults, defineConfig } from "vitest/config";

import { LIVE_TESTS } from "./vitest.live-tests";

export default defineConfig({
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  // JSX otomatik dönüşümü: bileşen testleri `React`'i içeri aktarmıyor
  // (Next de aktarmıyor — tsconfig `jsx: preserve` ile aynı davranış).
  esbuild: { jsx: "automatic" },
  test: {
    // Rota işleyicileri sunucu tarafı: Web Fetch API'si Node 18+'ta yerleşik,
    // ayrı bir DOM ortamına gerek yok.
    environment: "node",
    include: ["tests/**/*.test.ts", "tests/**/*.test.tsx"],
    // Canlı bileşen isteyen testler burada, DOSYANIN İÇİNDE DEĞİL dışlanıyor
    // (T27). Gerekçesi `vitest.live-tests.ts`'te; özeti: bir tur önce niyet
    // dosyada, gerçek `include` deseninde yazıyordu ve ikisinin ayrıştığını
    // kimse okumadı. `npm run test:live` ile koşuyorlar.
    exclude: [...configDefaults.exclude, ...LIVE_TESTS],
    // Testler modül düzeyindeki oturum deposunu paylaşıyor; `beforeEach`
    // temizliyor ama dosyaları ayrı süreçlere dağıtmak daha güvenli.
    isolate: true,
  },
});
