# RTK — token kıyan CLI vekili

RTK (`rtk`, "Rust Token Killer") Claude Code'un `Bash` çağrılarını araya girip
yeniden yazan bir vekil. `grep`, `read`, `git diff`, test koşumları gibi
**çıktısı büyük** komutları kendi süzülmüş biçimiyle çalıştırıp modele daha az
token döndürüyor. Kurulum bu depoda değil, **global ayarda**; yani depoyu
klonlayan biri onu otomatik almıyor.

Bu belgedeki bütün sayı ve çıktılar **24 Ağustos 2026, ~19:17'de bu makinede**
koşturuldu. Sayaçlar canlıdır — yarın koşturursan hepsi büyümüş olur; yalnızca
büyüklük mertebesini oku, rakamı değil. Kayma hızı ölçüldü: global toplam
**aynı oturumda üç dakikada 84458 → 84528** oldu.

## 1 · Kurulum doğrulaması ve isim çakışması tuzağı

```
$ which rtk
/usr/local/bin/rtk
$ rtk --version
rtk 0.43.0
```

`rtk` adında **iki ayrı araç** var. Bizimki `rtk-ai/rtk`; diğeri
`reachingforthejack/rtk` (Rust Type Kit) ve aynı ismi kullanıyor. Ayırt etme
yolu tek komut:

```
rtk gain
```

Çalışıyorsa doğru ikili kuruludur. `command not found` ya da anlamsız bir hata
veriyorsa yanlış olan kuruludur — çünkü `gain` yalnızca token vekilinde var.

## 2 · Hook nasıl çalışıyor

Yeniden yazımı yapan tek yer `~/.claude/settings.json`: `PreToolUse` altında
`Bash` matcher'ı `rtk hook claude` çalıştırıyor, o da stdin'den gelen JSON'u
okuyup komutu değiştiriyor. Bu deponun `.claude/settings.json`'ında **rtk
yok** — orada yalnızca graphify hook'ları duruyor (`Bash|Grep`, `Read|Glob`).
`.claude/settings.local.json` ne worktree'de ne ana depoda var.

Sonuç: rtk yeniden yazımı tamamen global ayardan geliyor, depo bunu ne
genişletiyor ne devre dışı bırakıyor; iki hook aynı `Bash` matcher'ında yan yana
çalışıyor.

Hangi komutun yeniden yazılacağını yan etkisiz sorabilirsin (`hook check` kuru
koşum — **kuralın varlığını** gösterir, hook'un tetiklendiğini değil):

```
$ rtk hook check "git status"
rtk git status                              # exit 0
$ rtk hook check "dotnet build"
rtk dotnet build                            # exit 0
$ rtk hook check "docker compose config --quiet"
No rewrite for: docker compose config --quiet   # exit 1
```

Üçüncüsü bu deponun CI bekçilerinden biri ve RTK onu tanımıyor — yani compose
komutlarında araya hiç girmiyor.

**Hook'un canlı tetiklendiğini kanıtlayan şey `hook check` değil, telemetri.**
Ölçüldü: bu belgeyi yazarken ham `wc -l docs/rtk.md` yazıldı (hiç `rtk`
yazılmadan), dönen çıktı süzülmüş `156` oldu — ham `wc` biçimi
`     156 docs/rtk.md` değil — ve hemen ardından telemetride şu satır belirdi:

```
08-24 19:15 ▲ rtk wc -l docs/rtk.md     -83% (5)
```

Yani hook komutu gerçekten `rtk wc`'ye çevirmiş.

## 3 · Ölçülen tasarruf

Global kapsam (`rtk gain`; blok birebir, tablonun **10 satırından ilk 6'sı**,
gerisi `…` ile kırpıldı):

```
RTK Token Savings (Global Scope)
════════════════════════════════════════════════════════════

Total commands:    84528
Input tokens:      232.2M
Output tokens:     93.5M
Tokens saved:      138.9M (59.8%)
Total exec time:   8306m59s (avg 5.9s)
Efficiency meter: ██████████████░░░░░░░░░░ 59.8%

By Command
────────────────────────────────────────────────────────────────────────
  #  Command                   Count   Saved    Avg%    Time  Impact
────────────────────────────────────────────────────────────────────────
 1.  rtk grep                  26213   61.5M   25.4%    1.6s  ██████████
 2.  rtk read                   9540   12.1M    9.3%    39ms  ██░░░░░░░░
 3.  rtk jest run                375   11.1M   95.3%   45.5s  ██░░░░░░░░
 4.  rtk vitest run              610   10.5M   77.6%   43.5s  ██░░░░░░░░
 5.  rtk:toml ps aux             205   10.3M   98.2%    5.5s  ██░░░░░░░░
 6.  rtk lint eslint .            13    6.6M   99.9%   10.2s  █░░░░░░░░░
…
```

Bu worktree kökü (`rtk gain --project`; aynı biçimde **ilk 3 satır**, gerisi
kırpıldı):

```
RTK Token Savings (Project Scope)
════════════════════════════════════════════════════════════
Scope: /.../worktrees/proje-ozeti-tanitim-353f6e

Total commands:    811
Input tokens:      888.1K
Output tokens:     461.4K
Tokens saved:      426.7K (48.0%)
Total exec time:   7m30s (avg 555ms)
Efficiency meter: ████████████░░░░░░░░░░░░ 48.0%

By Command
────────────────────────────────────────────────────────────────────────
  #  Command                   Count   Saved    Avg%    Time  Impact
────────────────────────────────────────────────────────────────────────
 1.  rtk grep                    269  174.8K   26.5%   422ms  ██████████
 2.  rtk read                    221  127.3K   17.0%     6ms  ███████░░░
 3.  rtk git diff main...H...      3   93.3K   83.9%   193ms  █████░░░░░
…
```

Okuma notu: **"saved" gerçekleşmiş bir tasarruf değil, tahmindir** — RTK ham
komutun üreteceği çıktı boyutunu kestirip kendi çıktısıyla farkını yazıyor.
Aritmetiği basit: `saved = input − output`, yüzde de `saved / input`. Ölçüldü —
küçük bir kapsamda input 108, output 45, "Tokens saved: 63 (58.3%)".

### Kapsam **cwd** başına, proje ya da worktree başına değil

`rtk gain --help` kendi ifadesiyle: *"Filter statistics to current project
(current working directory)"*. Yani sayaç **tam olarak içinde bulunduğun
dizine** ait; bir alt dizine geçmek onu sıfırlar. Ölçüldü:

| Çalışılan dizin | `rtk gain --project` |
| --- | --- |
| worktree kökü | 811 komut |
| worktree'nin `docs/` dizini | `No tracking data yet.` |
| ana depo kökü | 2314 komut |
| ana deponun `docs/` dizini | 3 komut |

Sonuç: gördüğün rakam ne projenin ne dalın toplamı — **o dizinde koşan komutlar**.
Bir dalın gerçek toplamını merak ediyorsan tek bir sayı yok.

## 4 · Nerede kazandırıyor, nerede fark etmiyor

| Komut sınıfı | Ortalama kısaltma | Neden |
| --- | --- | --- |
| `ps aux`, `ps -eo ...` | %98 | Çıktının neredeyse tamamı gürültü; RTK tabloyu daraltıyor |
| `jest` / `vitest run` | %78–95 | Yeşil testlerin satır satır dökümü modele gerekmiyor |
| `eslint .` | %99.9 | Temiz koşumda söylenecek şey yok |
| `git diff` (geniş) | %84 | Bağlam satırları kırpılıyor |
| `grep` | %25–27 | En çok çağrılan komut; oran düşük ama **hacim yüzünden en büyük kalem** (61.5M) |
| `read` | %9–17 | Dosya içeriği zaten sıkıştırılamaz; kazanç neredeyse yok |
| `docker compose`, `machine-resources.sh`, `keploy` | %0 | RTK tanımıyor, komut olduğu gibi geçiyor |

Son satır bu depo için önemli: günlük olarak en çok koştuğumuz üç şeyin üçü de
RTK'nın kapsamı dışında. Ölçüm ve entegrasyon işlerinde token faturasını
düşüren şey RTK değil, komutun kendi `--quiet`/`--format` bayrakları.

## 5 · Meta komutlar

Bunlar hook tarafından yeniden yazılmaz, doğrudan `rtk` ile çağrılır:

| Komut | Ne yapar |
| --- | --- |
| `rtk gain` | Global tasarruf özeti + en çok kazandıran 10 komut |
| `rtk gain --project` | Yalnızca **içinde bulunduğun dizin** (bkz. §3) |
| `rtk gain --history` | Son komutların tek tek oranı — `-83% (5)` = **5 token tasarruf**, sonuç boyutu değil |
| `rtk discover` | Claude Code oturum geçmişini tarayıp "bunu rtk ile koşsaydın" listesi çıkarır |
| `rtk session` | Oturum oturum RTK "adoption" oranı |
| `rtk proxy <cmd>` | Süzmeyi atlayıp ham komutu çalıştırır, **ama kullanımı yine sayar** |
| `rtk hook check "<cmd>"` | Yan etkisiz: o komut yeniden yazılır mıydı, nasıl |

Parantezin tasarruf olduğu kontrollü ölçüldü: boş bir kapsamda tek bir komut
koşturuldu; `gain --project` "Input 6.0K / Output 3.3K / Tokens saved: 2.8K
(46.1%)" dedi, `--history` satırı `-46% (2.8K)` çıktı. Sonuç boyutu olsaydı
orada 3.3K yazardı.

### `rtk discover` iki yerde yanıltıyor

**Birincisi kapsam.** Varsayılan **makinenin tamamı değil, mevcut proje** —
`--help`: *"Scan all projects (default: current project only)"*. Ölçüldü: bu
depoda `rtk discover` "33 sessions, 2391 Bash commands"; `rtk discover --all`
"1826 sessions, 158375 Bash commands".

**İkincisi "kaçırılmış" kelimesi.** `discover` Claude Code transcript'inde
**modelin yazdığı ham komutu** okuyor; hook'un sonradan yaptığı yeniden yazımı
görmüyor. Yani hook'un zaten yakaladığı bir komut "kaçırılmış" sütununda
görünüyor. Ölçülerek kanıtlandı: ham `wc -l docs/rtk.md` koşturulmadan önce
`discover` "wc -l → 14", sonra "wc -l → 15" dedi; **aynı komut** bu sırada
`gain` telemetrisine `rtk wc -l docs/rtk.md` olarak da girdi.

Bu, `discover`'ın "Already using RTK: %2" ile `gain`'in binlerce `rtk ...`
satırı arasındaki çelişkiyi çözüyor: **`discover` modelin ne yazdığını,
`gain` ne koştuğunu sayıyor.** `session`'ın "adoption" sütunu da birinci
kaynağı okuyor, o yüzden o oran da düşük görünür. `discover`'ın
"~274.6K tokens saveable" toplamı bu yüzden **zaten tasarruf edilmiş** olanı da
içeriyor; onu bir hedef değil, üst sınır say.

Gerçek "TOP UNHANDLED COMMANDS" sıralamasında bu depo için ilk sıra
`python3` (56) — `docker compose`'un (17) üç katı. Bu depoya özgü olarak ilgi
çekenler: `docker compose` (17), `git grep` (14), `graphify explain` (12),
`keploy` (7, listenin sonlarında). `machine-resources.sh` tek satır değil,
kırpılmış iki ayrı satır olarak (9 ve 8) geçiyor.

## 6 · Açık uçlar — bilerek kapatmadım

- **`~/.claude/RTK.md`'nin son satırı kırık.** "Refer to CLAUDE.md for full
command reference" diyor ama ne global ne proje CLAUDE.md'sinde böyle bir
referans var (arandı, yok). Tam liste için `rtk --help`.
- **Koşturulmayanlar:** `rtk cc-economics`, `rtk verify`, `rtk trust`.
`rtk --help`'te var olduklarını gördüm, davranışlarını denemedim.
- **Hook'un stdin akışı elle beslenmedi.** Canlı çalıştığı §2'deki telemetri
turuyla kanıtlandı; `rtk hook claude`'a elle JSON verilmedi.

## İlgili yollar

- `/usr/local/bin/rtk` — ikili
- `~/.claude/RTK.md` — global kural dosyası (`~/.claude/CLAUDE.md`'nin ilk
satırı `@RTK.md`, yani her projede yükleniyor)
- `~/.claude/settings.json` — hook'un tanımlı olduğu **tek** yer
(`grep -rl "rtk hook" ~/.claude/*.json ~/.claude/plugins` yalnızca onu döndürdü)
- `.claude/settings.json` (bu depo) — graphify hook'ları; rtk yok
