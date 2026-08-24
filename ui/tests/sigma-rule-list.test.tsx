import { describe, expect, it } from "vitest";

import { gatedLabel } from "@/app/alarmlar/AlertsOverview";

/**
 * Sigma kurallarının listedeki görünümü (T33).
 *
 * <p>
 * Ortak nokta: <b>ekran iki farklı iş kalemini tek rozette toplamıyor.</b>
 * Topladığı an liste, kullanıcının neyin kapatacağını göremediği bir çöp
 * kutusuna dönüyor — ve o kutu hiç boşalmıyor.
 * </p>
 */
describe("gated rozeti remedy'ye göre", () => {
  it("şema bekleyen ile asla derlenmeyeni AYIRIYOR", () => {
    // "31'i şema bekliyor, 11'i asla derlenmeyecek" iki farklı cümle.
    // İkincisi kullanıcıya bir KAPSAM SINIRI olarak sunulmalı, iş kalemi
    // olarak değil — `Pending`/`Exempt` ayrımının ekrandaki hâli.
    expect(gatedLabel("[schema] dns_query_name: parser yok")).toBe("şema bekliyor");
    expect(gatedLabel("[upstream] x: backend desteklemiyor")).toBe("desteklenmiyor");
  });

  it("eşleme bekleyeni de ayrı adlandırıyor", () => {
    expect(gatedLabel("[pipeline] url: eşlenmemiş")).toBe("eşleme bekliyor");
    expect(gatedLabel("[pipeline_or_schema] x: bilinmiyor")).toBe("eşleme bekliyor");
  });

  it("sebep yoksa ya da tanınmıyorsa UYDURMUYOR", () => {
    // Tanınmayan bir `remedy` için bir etiket uydurmak, olmayan bir bilgiyi
    // varmış gibi göstermek olurdu. Genel etiket dürüst.
    expect(gatedLabel(undefined)).toBe("koşamaz");
    expect(gatedLabel("")).toBe("koşamaz");
    expect(gatedLabel("[bilinmeyen] x: y")).toBe("koşamaz");
  });

  it("sebep biçimi senkronun yazdığıyla aynı", () => {
    // Senkron `[remedy] kolon: mesaj` yazıyor. Biçim ayrışırsa etiket
    // sessizce "koşamaz"a düşer — yani ayrım kaybolur ama hiçbir şey
    // kırmızı yanmaz. Bu test o biçimi çiviliyor.
    const fromSync = "[schema] dns_query_name: `dns_query_name` bu şemada eşlenemiyor";

    expect(gatedLabel(fromSync)).toBe("şema bekliyor");
  });
});
