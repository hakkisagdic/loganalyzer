import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

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
    // Testler modül düzeyindeki oturum deposunu paylaşıyor; `beforeEach`
    // temizliyor ama dosyaları ayrı süreçlere dağıtmak daha güvenli.
    isolate: true,
  },
});
