import type { NextConfig } from "next";

const config: NextConfig = {
  reactStrictMode: true,

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
