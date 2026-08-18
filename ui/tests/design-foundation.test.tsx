import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";

import { DataTable, type Column } from "@/components/ui/DataTable";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";

/**
 * Tasarım temelinin ölçülebilir kısmı (T13; T28 bunun üstüne denetim yapacak).
 *
 * <p>
 * Görsel tutarlılık burada sınanamaz — ekran görüntüsü işi T28'in. Sınanabilen
 * şey, F2 planının <b>özel risk</b> diye işaretlediği davranış: log gövdeleri
 * Türkçe, Arapça ve Çince geliyor ve tablo bunları taşırmadan, doğru yönde
 * göstermek zorunda. Bu kurallar bileşenin içinde olmalı; her ekranın ayrı ayrı
 * hatırlaması beklenirse biri unutur ve o ekran kullanılamaz hâle gelir.
 * </p>
 */

interface Row {
  readonly id: string;
  readonly body: string;
  readonly count: number;
}

const columns: Column<Row>[] = [
  { key: "id", header: "Kimlik", width: "12rem", render: (r) => r.id },
  { key: "body", header: "Gövde", freeText: true, render: (r) => r.body },
  { key: "count", header: "Sayı", numeric: true, render: (r) => r.count },
];

const rows: Row[] = [
  { id: "1", body: "Kullanıcı girişi başarısız — İstanbul şubesi", count: 12 },
  { id: "2", body: "فشل تسجيل دخول المستخدم، يرجى التحقق من بيانات الاعتماد", count: 3 },
  { id: "3", body: "用户登录失败，请检查凭据。这是一段没有空格的长文本用于测试换行行为。", count: 7 },
];

function render(node: React.ReactElement): string {
  return renderToStaticMarkup(node);
}

describe("DataTable — çok dilli gövdeler", () => {
  const html = render(
    <DataTable caption="Olaylar" columns={columns} rows={rows} rowKey={(r) => r.id} />,
  );

  it("serbest metin hücreleri yazı yönünü içeriğe bırakıyor", () => {
    // Sabit `ltr` Arapça gövdeyi okunamaz kılıyor; `dir="auto"` tarayıcının
    // ilk güçlü karakterden karar vermesini sağlıyor.
    expect(html).toContain('dir="auto"');
    // Yalnızca serbest metin sütunlarında — kimlik ve sayı sütunları LTR kalmalı,
    // yoksa Arapça bir satırın yanındaki kimlik ters hizalanır.
    expect(html.match(/dir="auto"/g)).toHaveLength(rows.length);
  });

  it("üç alfabe de kayıpsız geçiyor", () => {
    expect(html).toContain("İstanbul");
    expect(html).toContain("فشل تسجيل دخول");
    expect(html).toContain("用户登录失败");
  });

  it("başlıklar sütun kapsamı taşıyor", () => {
    // `scope="col"` olmadan ekran okuyucu hangi hücrenin hangi başlığa ait
    // olduğunu söyleyemiyor.
    expect(html.match(/scope="col"/g)).toHaveLength(columns.length);
  });

  it("tablo kaydırılabilir bölgesi klavyeyle erişilebilir", () => {
    // Yatay kaydırma varsa fare olmadan da gezilebilmeli (WCAG 2.1.1).
    expect(html).toContain('tabindex="0"');
    expect(html).toContain('aria-label="Olaylar"');
  });

  it("altyazı görünür — tablonun ne olduğu tahmin edilmiyor", () => {
    expect(html).toContain("<caption>Olaylar</caption>");
  });
});

describe("dört durumun üçü hazır", () => {
  it("boş durum çıkmaz sokak değil", () => {
    const html = render(
      <EmptyState title="Kayıt yok" description="Filtreleri gevşetin." action={<a href="/">Sıfırla</a>} />,
    );

    expect(html).toContain("Kayıt yok");
    expect(html).toContain("Sıfırla");
  });

  it("hata durumu duyuruluyor ve ipucu ayrı gösteriliyor", () => {
    const html = render(
      <ErrorState title="Olay bulunamadı." hint="/v1/health/pipeline arşiv gecikmesini gösterir." />,
    );

    // `role="alert"` — odak değişmeden ekran okuyucuya iletiliyor.
    expect(html).toContain('role="alert"');
    // `hint` ayrı bir paragrafta: "ne oldu" ile "ne yapılacak" karışmıyor.
    expect(html).toContain("/v1/health/pipeline");
  });

  it("yükleniyor durumu ekran okuyucuya da bir şey söylüyor", () => {
    const html = render(<LoadingState label="Olaylar yükleniyor" rows={3} />);

    // Görsel iskelet ekran okuyucuya hiçbir şey anlatmıyor; metin şart.
    expect(html).toContain("Olaylar yükleniyor");
    expect(html).toContain('aria-busy="true"');
  });
});
