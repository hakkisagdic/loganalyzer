import type { NextConfig } from "next";

const config: NextConfig = {
  reactStrictMode: true,

  /*
   * Container imajı için (T49). `next build` çıktısına, uygulamayı çalıştırmak
   * için gereken node_modules'ın SADECE izlenebilir kısmını kopyalıyor.
   *
   * Alternatifi imaja `node_modules`'ın tamamını koymaktı: geliştirme
   * bağımlılıkları — Playwright, tarayıcı ikilileri, vitest — üretim imajına
   * girer ve imaj yüz megabaytlarca büyür. Büyüklük tek başına bir gerekçe
   * olmazdı; ikinci ve asıl sebep yüzey: üretimde koşan bir imajda tarayıcı
   * ikilisi bulundurmak, kimsenin karar vermediği bir şey.
   *
   * YEREL DÖNGÜYE DOKUNMUYOR: `next dev` bu alanı hiç okumuyor, `next start`
   * de eskisi gibi `.next`'ten koşuyor. Değişen tek şey `next build`'in AYRICA
   * `.next/standalone` üretmesi.
   */
  output: "standalone",

  // Tarayıcıya inen paketin içinde hiçbir sunucu sırrı olmamalı. Next yalnızca
  // `NEXT_PUBLIC_` önekli değişkenleri istemciye gömer; bu dosyada bilinçli
  // olarak hiç `env` girdisi yok — `KEYCLOAK_CLIENT_SECRET` gibi bir değeri
  // buraya eklemek onu paketin içine yazardı.

  async headers() {
    return [
      {
        source: "/:path*",
        headers: [
          { key: "X-Content-Type-Options", value: "nosniff" },
          { key: "Referrer-Policy", value: "same-origin" },
          // Oturum çerezi taşıyan bir uygulamada çerçeveleme, tıklama hırsızlığı
          // (clickjacking) demek.
          { key: "X-Frame-Options", value: "DENY" },
        ],
      },
    ];
  },
};

export default config;
