import { readiness } from "@/lib/health/readiness";

/**
 * <c>GET /api/health/ready</c> — container sağlık kontrolünün yokladığı uç
 * (T62).
 *
 * <p>
 * Gerekçenin tamamı <c>@/lib/health/readiness</c> içinde; burada duran şey
 * yalnızca taşıma. Mantığın ayrı dosyada olması testin sunucu kaldırmadan
 * koşabilmesi için: kırık bir bağımlılığı ölçmek onu <b>kırabilmeyi</b>
 * gerektiriyor ve konteynerle kırmak bu paketin işi değil (§2).
 * </p>
 *
 * <p>
 * <b>Durum kodu ölçütün kendisi.</b> Hazırsa 200, değilse <b>503</b> — ve bu
 * ayrım T49'un sağlık kontrolünü düzeltiyor: eski ölçüt <c>&lt; 500</c>'dü ve
 * kök sayfanın 307'si onu her hâlde geçiyordu. 503, "sunucu ayakta ama
 * hizmet veremiyor"un standart kodu; 500 <b>değil</b>, çünkü sürecin kendi
 * arızası değil.
 * </p>
 *
 * <p>
 * <c>force-dynamic</c>: Next bu ucu derleme anında sabitleyebilir ve o zaman
 * sağlık kontrolü <b>kalkış anındaki</b> cevabı sonsuza kadar okurdu — sessizce
 * yeşil kalan bir kapı, yani bu ucun kapattığı hatanın birebir aynısı.
 * </p>
 */
export const dynamic = "force-dynamic";

export async function GET(): Promise<Response> {
  const report = await readiness();

  return Response.json(report, {
    status: report.ready ? 200 : 503,
    headers: {
      // Aracı bir vekil cevabı önbelleğe alırsa sağlık kontrolü eski gerçeği
      // okur; yığında bugün böyle bir vekil yok ama başlık bir satır.
      "cache-control": "no-store",
    },
  });
}
