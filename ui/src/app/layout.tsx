import type { Metadata } from "next";
import type { ReactNode } from "react";

import { themeBootstrapScript } from "@/components/ui/ThemeToggle";

import "./globals.css";

export const metadata: Metadata = {
  title: "Bizigo Log Analyzer",
  description: "Log toplama, normalleştirme ve sorgulama",
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    // `lang="tr"` ekran okuyucunun doğru sesletimi ve tarayıcının doğru
    // tireleme kuralları için. Log GÖVDELERİ başka dillerde olabiliyor; onlar
    // `dir="auto"` taşıyan hücrelerde gösteriliyor (bkz. DataTable).
    <html lang="tr" suppressHydrationWarning>
      <head>
        {/*
          Tema, React bağlanmadan önce uygulanıyor. Aksi hâlde koyu tema seçmiş
          bir kullanıcı her sayfa yüklenişinde bir kare beyaz ekran görüyor.
        */}
        <script dangerouslySetInnerHTML={{ __html: themeBootstrapScript }} />
      </head>
      <body>{children}</body>
    </html>
  );
}
