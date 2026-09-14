---
kind: spec
title: "Koordinasyon defteri — kim ne yapıyor, ve boşta kalan nasıl görülür"
---

# Koordinasyon defteri

Bu belge bir turda **hangi ajanın hangi ticket'ta olduğunu** ve **hangi
worktree'ye bağlı olduğunu** tutuyor. Bir gösterge değil, bir **bekçinin
yokluğunun telafisi**.

## Neden var — ölçülmüş bir olay

2026-09-05'te on bir ajanlı bir turda **dört ajan aynı anda boşta kaldı** ve
koordinatör bunu fark etmedi; **kullanıcı fark etti**. Sebepleri ayrı ayrı
sıradandı:

| Ajan | Neden boştaydı |
| --- | --- |
| M01'in ajanı | *"M04 sana"* denmiş, M04 başkasına verilmiş, **haber verilmemiş** |
| M03'ün ajanı | Worktree bağlama denemesi `TARGET_TURN_ACTIVE` ile düşmüş, **tekrar denenmemiş** |
| M09'un ajanı | Ticket verilmiş, **worktree hiç açılmamış** |
| T47'nin ajanı | Ticket'ını bitirmiş, yeni iş gelmemiş |

Üçü doğrudan koordinatörün hatasıydı. Ama asıl bulgu ajanın kendi cümlesinde:

> **Boşta kaldığımı ben de bilmiyordum.** Ajan boşta beklerken bunu görecek
> bir sinyal yok — koordinatör bir ticket'ı başkasına verdiğinde eski sahibi
> *"sırada ben varım"* varsayımıyla oturuyor.

Bu, §7'nin sınıfının koordinasyon katmanındaki hâli: **hata yok, sayaç yok,
belirti yok.** Ajan bekliyor, koordinatör çalıştığını sanıyor, ve ikisi de
kendi içinde tutarlı.

## Ölçüm tuzağı — dosya zaman damgası vekil değildir

Boşluğu ararken koordinatör on worktree'de *"son 25 dakikada değişen dosya"*
saydı ve hepsinde **sıfır** buldu; bundan *"hiçbiri çalışmıyor"* sonucunu
çıkardı. **Yanlıştı:** o sırada bir ajan 45 `RequireAuthorization` çağrısını
sayıyordu — okuma ve ölçüm **dosyaya yazmıyor**.

Yani bu deponun kendi kuralı burada da geçti: bir sayı elde etmek onu doğru
sayı yapmıyor. Doğru sinyal `traycer_list_agents` + ajanın **son raporu**,
dosya sistemi değil.

## Bugünkü dağılım (2026-09-05)

| Ajan (id başı) | Unvan (bayat) | Gerçek ticket | Worktree |
| --- | --- | --- | --- |
| `3b21336a` | M01 | **M05** — RCA araçları | `m05-rca-araclari` |
| `18679fb1` | T44 | **M02** — komut çekirdeği | `m02-komut-cekirdegi` |
| `cb73d46a` | T38 | **M03** — sim araçları | `m03-sim-araclari` |
| `d43dd553` | M04 | **M04** — okuma araçları | `m04-okuma-araclari` |
| `33d5b6b8` | M06 | **M06** → sonra M07 | `m06-redaksiyon-kapisi` |
| `f190f726` | M08 | **M08** (teslim edildi) | `m08-kimlik-tasima` |
| `f3d4377c` | T48+T49 | **M09** — kimlik keşfi | `m09-kimlik-kesfi` |
| `abbb210e` | T32 | **T47** — kalite ölçümü | `t32-sigma-derleme` |
| `ea15057f` | FS-b | **T55** — keşif konsolidasyonu | `t55-kesif-konsolidasyonu` |
| `4c2b27fd` | F5 | **S1** — topoloji-lite | `f5-kapsam-karari` |
| `—` | — | **T56** — vault iddia denetimi | `t56-vault-iddia-denetimi` |

**Unvan sütunu bilerek duruyor:** Traycer'ın ajan başlığı ilk brief'ten
kalıyor ve ticket değiştikçe bayatlıyor. Arayüzde `T32` görünen ajan bugün
T47'de. Bu belge o ayrışmayı görünür tutuyor — `EpicStatusTests`'in ticket
statüleri için yaptığının koordinasyon katmanındaki karşılığı, ama
**mekanizması yok**: kimse bu tabloyu gerçekle karşılaştırmıyor.

## Kural

**Bir ticket'ı başkasına verdiğinde eski sahibine söyle.** Sinyal koymak
koordinatörün işi; ajanın kendi boşluğunu görecek bir yolu yok.

**Worktree bağlama denemesi düşerse tekrar dene.** `TARGET_TURN_ACTIVE`
geçici bir hâl, ve tek denemede bırakmak ajanı yanlış dalda ya da dalsız
bırakıyor.

**Ticket verirken worktree'yi aynı turda aç.** *"Açacağım"* demek ile açmak
arasındaki fark bir ajanın boşta geçirdiği süre.

## Bu belgenin bilmediği şey

Tablo **elle** tutuluyor ve bayatlayacak — tam olarak tarif ettiği hatanın
kendisine açık. Mekanikleştirmenin yolu var (`traycer_list_agents` çıktısı ile
karşılaştıran bir bekçi) ama bugün yazılmadı, ve yazılmadığı burada duruyor.
