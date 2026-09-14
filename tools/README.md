# `tools/`

Depo içi araçlar. Ürünün parçası **değil** — ölçüm ve bekçi yardımcıları.

## `machine-resources.sh` — kaynak kapısı

`CLAUDE.md` §3'ün işaret ettiği kapı: ağır bir iş (derleme, test paketi,
konteyner) başlatmadan önce makinenin taşıyıp taşıyamayacağını söylüyor.

```bash
./tools/machine-resources.sh check    # çıkış 1 ise BAŞLAMA
```

### Neden depoda — ve neden geç eklendi

Betik uzun süre yalnızca `~/.claude/scripts/` altında durdu. Depo ona **beş
yerde** atıf yapıyordu ve **içermiyordu**: yani taze bir klonda §3'ün işaret
ettiği dosya yoktu, ve bunu bir ajan sorarak buldu (*"betiğin depoda olmaması
bilinçli mi, bilmiyorum"*). Bir protokol kuralının tek kişinin ev dizinine
bağlanması, o kuralın **tek makinede var olması** demek.

Kopya **birebir** — değiştirilmedi. Değiştirmek yükseltmesini bir merge işine
çevirirdi; `catalog/patterns/` ile aynı gerekçe.

### ⚠ Bugün YALNIZCA macOS'ta doğru ölçüyor — ve Linux'ta sessizce yanlış

Her okuyucusu BSD/macOS'a özgü. Ölçüldü:

| Ne | Nasıl okuyor | Linux'ta ne oluyor |
| --- | --- | --- |
| Bellek | `memory_pressure` | Komut yok → **`100`** basıyor, yani *"%100 boş"* — kapı **her zaman geçiyor** |
| Disk | `df -g "$HOME"` | GNU `df` `-g` tanımıyor → **`0`** GiB, yani kapı **her zaman engelliyor** |
| Swap | `sysctl -n vm.swapusage` | Yok → boş |
| Sayfalama | `vm_stat` `Swapins` | Yok → boş |

İkisinin yönü **zıt** ve ikisi de sessiz: bellek kapısı yanlış yeşil, disk kapısı
yanlış kırmızı. Yani bu betik bir Linux makinesinde koşarsa ürettiği sayı bir
ölçüm değil.

**Bu bir kapanmamış kalem, kapanmış bir karar değil.** Kurulum hedefi Linux ve
Windows; kapı orada da anlamlı olmalı. Düzeltmesi platform başına okuyucu
(`/proc/meminfo`, `df -BG`, `/proc/vmstat` `pswpin`) ve **ölçülmeden yazılmamalı**
— tahminle yazılmış bir kaynak kapısı, ölçtüğünü sandığı şeyi ölçmeyen bir
kapıdır ve bu deponun en pahalı sınıfı orası.

### Durum kaydı makinede, depoda değil

`claim` / `release` durumu `~/.claude/machine-resources.d` altında tutuluyor ve
öyle kalması doğru: o **makinenin** anlık hâli, deponun değil. İki ajan aynı
makinede koşuyorsa aynı kaydı görüyor; farklı makinelerde birbirine karışmıyor.

## `*-kirmizi-olcumu.py`

Bekçilerin **kırmızı yanabildiğini** ölçen betikler (§6). Her biri bir kusuru
dosyaya yazıyor, kusurun gerçekten orada olduğunu **okuyup iddia ediyor**,
testi koşturuyor ve yedekten geri alıyor.

Geri almanın `git checkout` ile yapılmaması bilinçli: commit edilmemiş işi ezer.
`copy2` yerine `copy` + `touch` da bilinçli — `copy2` zaman damgasını da
kopyalıyor ve MSBuild değişikliği görmüyor, yani "geri alındı" denen ağaç
derlemeye hiç ulaşmıyor.
