#!/usr/bin/env python3
"""
`kenar-guveni-mimarisi.md` -> `kenar-guveni-mimarisi.html` üreteci.

HTML **üretilmiş** bir dosyadır; elle düzenlenmez. Kaynak markdown değişirse:

    python3 docs/arastirma/uret.py

Yollar bu dosyanın konumundan türüyor — mutlak yol YOK. Bu bilinçli:
`.githooks/post-commit` içindeki sabitlenmiş mutlak yol tam olarak bu yüzden
inceleme sırasında düştü (bkz. PR #3). Bir üretecin yalnızca yazanın
makinesinde çalışması, çalışmadığı hiçbir yerde görünmez.
"""
import html
import json
import re
from pathlib import Path

BURASI = Path(__file__).resolve().parent
KAYNAK = BURASI / "kenar-guveni-mimarisi.md"
CIKTI = BURASI / "kenar-guveni-mimarisi.html"
PARCA = BURASI / "kenar-guveni-mimarisi-artifact.html"

src = KAYNAK.read_text(encoding="utf-8")
lines = src.split("\n")

# ---------- satir ici ----------
KOKEN = re.compile(r"\*{0,2}\[(doğrulandı[^\]]*|doğrulanmadı[^\]]*|doğrulanamadı[^\]]*|üçüncü taraf[^\]]*|Kafka dokümanı[^\]]*)\]\*{0,2}")

def koken(m):
    t = m.group(1)
    tur = "dogru" if t.startswith("doğrulandı") else "supheli"
    # Çip metni ZATEN kaçırılmış geliyor (`kacir()` önce koştu); burada yalnızca
    # kendi içindeki backtick kod işaretine çevriliyor.
    govde = re.sub(r"`([^`]+)`", lambda mm: "<code>" + mm.group(1) + "</code>", t)
    return '<span class="koken ' + tur + '">' + govde + '</span>'

def kacir(s):
    s = s.replace("&lt;", "\x00LT\x00").replace("&gt;", "\x00GT\x00").replace("&amp;", "\x00AMP\x00")
    s = html.escape(s)
    return s.replace("\x00LT\x00", "&lt;").replace("\x00GT\x00", "&gt;").replace("\x00AMP\x00", "&amp;")

def ici(s):
    s = kacir(s)
    # KÖKEN ÖNCE: `[doğrulandı: `index/builder.go:327`]` gibi bir işaret kendi
    # içinde backtick taşıyabiliyor. Kod dönüşümü önce koşarsa çip metninin
    # içine <code> HTML'i giriyor, rozet onu bir kez daha kaçırıyor ve ekranda
    # literal "&lt;code&gt;" görünüyor — ölçüldü, bir örnekte çıktı.
    s = KOKEN.sub(koken, s)
    s = re.sub(r"`([^`]+)`", lambda m: f"<code>{m.group(1)}</code>", s)
    s = re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", s)
    s = re.sub(r"(?<!\*)\*([^*\n]+)\*(?!\*)", r"<em>\1</em>", s)
    return s

# ---------- blok ----------
out, i, n = [], 0, len(lines)
bolumler, katmanlar = [], []
acik_bolum = False

def kapat():
    global acik_bolum
    if acik_bolum:
        out.append("</section>")
        acik_bolum = False

while i < n:
    ln = lines[i]

    if ln.startswith("# "):
        i += 1; continue

    if re.match(r"^---\s*$", ln):
        i += 1; continue

    if ln.startswith("## "):
        kapat()
        baslik = ln[3:].strip()
        m = re.match(r"^(\d+)\s*·\s*(.+)$", baslik)
        num, ad = (m.group(1), m.group(2)) if m else ("", baslik)
        sid = f"b{num or len(bolumler)+1}"
        bolumler.append({"id": sid, "num": num, "ad": ad})
        out.append(f'<section id="{sid}" class="bolum">')
        out.append(f'<h2><span class="bnum">{num}</span><span class="bad">{ici(ad)}</span></h2>')
        acik_bolum = True
        i += 1; continue

    if ln.startswith("### "):
        baslik = ln[4:].strip()
        bayrak = ""
        mb = re.search(r"(⭐|⚠️)\s*\*([^*]+)\*", baslik)
        if mb:
            tur = "anahtar" if mb.group(1) == "⭐" else "uyari"
            bayrak = f'<span class="bayrak {tur}">{html.escape(mb.group(2))}</span>'
            baslik = baslik[:mb.start()].strip()
        ml = re.match(r"^(L\d+)\s*—\s*(.+)$", baslik)
        if ml:
            kid = ml.group(1).lower()
            katmanlar.append({"id": kid, "kod": ml.group(1), "ad": ml.group(2)})
            out.append(f'<h3 id="{kid}" class="katman"><span class="kkod">{ml.group(1)}</span>'
                       f'<span class="kad">{ici(ml.group(2))}</span>{bayrak}</h3>')
        else:
            out.append(f'<h3>{ici(baslik)}{bayrak}</h3>')
        i += 1; continue

    if ln.startswith("|"):
        blok = []
        while i < n and lines[i].startswith("|"):
            blok.append(lines[i]); i += 1
        hucre = lambda r: [c.strip() for c in r.strip().strip("|").split("|")]
        bas = hucre(blok[0])
        govde = [hucre(r) for r in blok[2:]] if len(blok) > 2 else []
        t = ['<div class="tsar"><table>', "<thead><tr>"]
        t += [f"<th>{ici(c)}</th>" for c in bas]
        t.append("</tr></thead><tbody>")
        for r in govde:
            t.append("<tr>" + "".join(f"<td>{ici(c)}</td>" for c in r) + "</tr>")
        t.append("</tbody></table></div>")
        out.append("".join(t)); continue

    if re.match(r"^[-*] ", ln):
        it = []
        while i < n and re.match(r"^[-*] ", lines[i]):
            it.append(ici(lines[i][2:].strip())); i += 1
        out.append("<ul>" + "".join(f"<li>{x}</li>" for x in it) + "</ul>"); continue

    if re.match(r"^\d+\. ", ln):
        it = []
        while i < n and re.match(r"^\d+\. ", lines[i]):
            it.append(ici(re.sub(r"^\d+\. ", "", lines[i]).strip())); i += 1
        out.append("<ol>" + "".join(f"<li>{x}</li>" for x in it) + "</ol>"); continue

    if ln.startswith("> "):
        it = []
        while i < n and lines[i].startswith("> "):
            it.append(lines[i][2:].strip()); i += 1
        out.append(f"<blockquote>{ici(' '.join(it))}</blockquote>"); continue

    if ln.strip() == "":
        i += 1; continue

    p = [ln]
    i += 1
    while i < n and lines[i].strip() and not re.match(r"^(#{1,3} |\||[-*] |\d+\. |> |---\s*$)", lines[i]):
        p.append(lines[i]); i += 1
    metin = " ".join(x.strip() for x in p)
    sinif = ' class="deck"' if metin.startswith("*") and metin.endswith("*") else ""
    out.append(f"<p{sinif}>{ici(metin)}</p>")

kapat()


yapi = {"bolumler": bolumler, "katmanlar": katmanlar}
govde = "\n".join(out)


# --- sol ray: gezinme AYNI ZAMANDA katman şeması ---
ray = []
for b in yapi["bolumler"]:
    ray.append(f'<a class="rb" href="#{b["id"]}"><span class="rnum">{html.escape(b["num"])}</span>{html.escape(b["ad"])}</a>')
    if b["num"] == "2":
        ray.append('<div class="yigin" role="group" aria-label="Katman yığını, L0 tabanda">')
        for k in yapi["katmanlar"]:
            ray.append(
                f'<a class="ky" href="#{k["id"]}">'
                f'<span class="ktik" aria-hidden="true"></span>'
                f'<span class="kkodr">{html.escape(k["kod"])}</span>'
                f'<span class="kadr">{html.escape(k["ad"])}</span></a>'
            )
        ray.append('</div>')
RAY = "\n".join(ray)

HTML = f'''<title>Kenar Güveni Mimarisi</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;500&family=IBM+Plex+Sans:ital,wght@0,400;0,500;0,600;1,400&family=Spectral:ital,wght@0,500;0,600;1,500&display=swap">
<style>
/* ---- jetonlar: acik palet bare :root'ta, tamami ---- */
:root {{
  --ground:#EDF1F3; --surface:#FCFDFD; --surface2:#F4F7F8;
  --ink:#101A20; --muted:#55666F; --rule:#D3DCE1; --rule2:#E4EAEE;
  --accent:#12506E; --accent-soft:#DCE9F0;
  --enforced:#0C6053; --enforced-bg:#DDEDE9;
  --declared:#8A5606; --declared-bg:#F5E9D6;
  --observed:#3F4F8C; --observed-bg:#E2E6F4;
  --alarm:#912A2D; --alarm-bg:#F6E0E0;
  --shadow:0 1px 2px rgba(16,26,32,.06), 0 8px 24px -16px rgba(16,26,32,.18);
}}
@media (prefers-color-scheme: dark) {{
  :root:not([data-theme="light"]) {{
    --ground:#0B1114; --surface:#121B20; --surface2:#172127;
    --ink:#DCE5E9; --muted:#8A9BA4; --rule:#22313A; --rule2:#1A252B;
    --accent:#6FB4D8; --accent-soft:#16323F;
    --enforced:#4CB3A0; --enforced-bg:#123029;
    --declared:#D9A648; --declared-bg:#33260F;
    --observed:#8898D8; --observed-bg:#1B2140;
    --alarm:#E0736F; --alarm-bg:#3A1B1C;
    --shadow:0 1px 2px rgba(0,0,0,.4), 0 8px 24px -16px rgba(0,0,0,.7);
  }}
}}
:root[data-theme="dark"] {{
  --ground:#0B1114; --surface:#121B20; --surface2:#172127;
  --ink:#DCE5E9; --muted:#8A9BA4; --rule:#22313A; --rule2:#1A252B;
  --accent:#6FB4D8; --accent-soft:#16323F;
  --enforced:#4CB3A0; --enforced-bg:#123029;
  --declared:#D9A648; --declared-bg:#33260F;
  --observed:#8898D8; --observed-bg:#1B2140;
  --alarm:#E0736F; --alarm-bg:#3A1B1C;
  --shadow:0 1px 2px rgba(0,0,0,.4), 0 8px 24px -16px rgba(0,0,0,.7);
}}

*, *::before, *::after {{ box-sizing:border-box; }}
body {{
  margin:0; background:var(--ground); color:var(--ink);
  font-family:"IBM Plex Sans", ui-sans-serif, system-ui, sans-serif;
  font-size:16px; line-height:1.68; -webkit-font-smoothing:antialiased;
}}
:focus-visible {{ outline:2px solid var(--accent); outline-offset:2px; border-radius:2px; }}
@media (prefers-reduced-motion: reduce) {{ * {{ animation:none !important; transition:none !important; }} }}

/* ---- kabuk ---- */
.kabuk {{ max-width:1320px; margin:0 auto; padding:0 24px 96px; }}

/* ---- kunye ---- */
.kunye {{ padding:64px 0 36px; border-bottom:1px solid var(--rule); }}
.etiket {{
  font-family:"IBM Plex Mono", ui-monospace, monospace; font-size:11px; font-weight:500;
  letter-spacing:.14em; text-transform:uppercase; color:var(--accent); margin:0 0 18px;
  display:flex; flex-wrap:wrap; gap:8px 18px;
}}
.kunye h1 {{
  font-family:Spectral, Georgia, serif; font-weight:600; font-size:clamp(30px,4.4vw,52px);
  line-height:1.1; letter-spacing:-.015em; margin:0 0 20px; max-width:19ch; text-wrap:balance;
}}
.kunye .ozet {{
  font-size:17px; color:var(--muted); max-width:64ch; margin:0;
  font-style:italic; font-family:Spectral, Georgia, serif;
}}
.olcum {{ display:flex; flex-wrap:wrap; gap:10px; margin:26px 0 0; padding:0; list-style:none; }}
.olcum li {{
  font-family:"IBM Plex Mono", monospace; font-size:12px; color:var(--muted);
  background:var(--surface); border:1px solid var(--rule); border-radius:2px; padding:5px 10px;
}}
.olcum b {{ color:var(--ink); font-weight:500; }}

/* ---- iki sutun ---- */
.govde {{ display:grid; grid-template-columns:250px minmax(0,1fr); gap:56px; align-items:start; }}
@media (max-width:1000px) {{ .govde {{ grid-template-columns:1fr; gap:0; }} }}

/* ---- sol ray: gezinme AYNI ZAMANDA katman semasi ---- */
.ray {{ position:sticky; top:24px; padding:36px 0 0; max-height:calc(100vh - 48px); overflow-y:auto; }}
@media (max-width:1000px) {{ .ray {{ position:static; max-height:none; padding-bottom:32px; border-bottom:1px solid var(--rule); }} }}
.raybas {{
  font-family:"IBM Plex Mono", monospace; font-size:10px; letter-spacing:.16em;
  text-transform:uppercase; color:var(--muted); margin:0 0 14px;
}}
.rb {{
  display:flex; gap:10px; align-items:baseline; text-decoration:none; color:var(--ink);
  font-size:13.5px; padding:6px 0; border-bottom:1px solid var(--rule2);
}}
.rb:hover {{ color:var(--accent); }}
.rnum {{
  font-family:"IBM Plex Mono", monospace; font-size:11px; color:var(--accent);
  min-width:14px; font-weight:500;
}}
.yigin {{
  margin:10px 0 14px 8px; padding:10px 0 10px 18px; position:relative;
  border-left:1px solid var(--rule);
}}
.yigin::after {{
  content:"bağımlılık yönü"; position:absolute; left:-1px; bottom:-19px;
  font-family:"IBM Plex Mono", monospace; font-size:9px; letter-spacing:.1em;
  text-transform:uppercase; color:var(--muted); white-space:nowrap;
}}
.ky {{
  display:grid; grid-template-columns:auto auto minmax(0,1fr); gap:8px; align-items:baseline;
  text-decoration:none; color:var(--muted); font-size:12.5px; padding:3.5px 0; position:relative;
}}
.ky:hover {{ color:var(--accent); }}
.ky:hover .ktik {{ background:var(--accent); }}
.ktik {{
  width:9px; height:1px; background:var(--rule); margin-left:-18px; align-self:center;
  display:block; position:absolute; left:0; top:50%;
}}
.kkodr {{
  font-family:"IBM Plex Mono", monospace; font-size:11px; font-weight:500;
  color:var(--accent); min-width:22px;
}}
.kadr {{ overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }}

/* ---- ana metin ---- */
main {{ padding:36px 0 0; min-width:0; max-width:74ch; }}
.bolum {{ padding:0 0 20px; }}
.bolum + .bolum {{ margin-top:56px; padding-top:44px; border-top:1px solid var(--rule); }}
h2 {{
  font-family:Spectral, Georgia, serif; font-weight:600; font-size:clamp(22px,2.6vw,30px);
  line-height:1.2; letter-spacing:-.01em; margin:0 0 26px; display:flex; gap:14px;
  align-items:baseline; text-wrap:balance;
}}
.bnum {{
  font-family:"IBM Plex Mono", monospace; font-size:13px; font-weight:500; color:var(--accent);
  border:1px solid var(--accent-soft); background:var(--accent-soft);
  border-radius:2px; padding:2px 7px; flex:none; align-self:center;
}}
h3 {{
  font-family:Spectral, Georgia, serif; font-weight:600; font-size:19px; line-height:1.32;
  margin:38px 0 12px; text-wrap:balance;
}}
h3.katman {{
  display:flex; flex-wrap:wrap; gap:10px; align-items:baseline;
  margin-top:52px; padding-top:20px; border-top:2px solid var(--accent-soft);
}}
.kkod {{
  font-family:"IBM Plex Mono", monospace; font-size:13px; font-weight:500;
  color:var(--surface); background:var(--accent); border-radius:2px; padding:3px 8px; flex:none;
}}
.kad {{ flex:1 1 auto; min-width:0; }}
p {{ margin:0 0 16px; }}
p.deck {{ color:var(--muted); font-family:Spectral, Georgia, serif; font-size:17px; }}
ul, ol {{ margin:0 0 18px; padding-left:22px; }}
li {{ margin:0 0 7px; }}
li::marker {{ color:var(--accent); }}
strong {{ font-weight:600; }}
em {{ font-style:italic; }}
code {{
  font-family:"IBM Plex Mono", ui-monospace, monospace; font-size:.87em;
  background:var(--surface2); border:1px solid var(--rule2); border-radius:2px;
  padding:1px 5px; overflow-wrap:anywhere;
}}
blockquote {{
  margin:22px 0; padding:14px 20px; border-left:3px solid var(--accent);
  background:var(--surface); color:var(--muted); font-family:Spectral, Georgia, serif;
  font-size:16.5px;
}}
blockquote p {{ margin:0; }}

/* ---- koken cipleri: belgenin kendi tezi kendine uygulaniyor ---- */
.koken {{
  font-family:"IBM Plex Mono", monospace; font-size:10.5px; font-weight:500;
  letter-spacing:.02em; padding:1px 6px; border-radius:2px; white-space:nowrap;
  border:1px solid transparent; vertical-align:.08em;
}}
.koken.dogru {{ color:var(--enforced); background:var(--enforced-bg); border-color:var(--enforced-bg); }}
.koken.supheli {{ color:var(--declared); background:var(--declared-bg); border-color:var(--declared-bg); }}

.bayrak {{
  font-family:"IBM Plex Mono", monospace; font-size:10.5px; font-weight:500;
  letter-spacing:.04em; text-transform:uppercase; padding:2px 7px; border-radius:2px; flex:none;
}}
.bayrak.anahtar {{ color:var(--observed); background:var(--observed-bg); }}
.bayrak.uyari {{ color:var(--alarm); background:var(--alarm-bg); }}

/* ---- tablolar ---- */
.tsar {{
  overflow-x:auto; margin:22px 0 26px; border:1px solid var(--rule);
  border-radius:3px; background:var(--surface); box-shadow:var(--shadow);
}}
table {{ border-collapse:collapse; width:100%; font-size:13.5px; line-height:1.55; }}
thead th {{
  text-align:left; font-family:"IBM Plex Mono", monospace; font-size:10.5px; font-weight:500;
  letter-spacing:.1em; text-transform:uppercase; color:var(--muted);
  padding:11px 14px; border-bottom:1px solid var(--rule); background:var(--surface2);
  white-space:nowrap;
}}
tbody td {{
  padding:11px 14px; border-bottom:1px solid var(--rule2); vertical-align:top;
  font-variant-numeric:tabular-nums;
}}
tbody tr:last-child td {{ border-bottom:0; }}
tbody td:first-child {{ white-space:normal; min-width:11ch; }}
table code {{ background:var(--surface2); }}

/* ---- alt ---- */
.dipnot {{
  margin-top:64px; padding-top:24px; border-top:1px solid var(--rule);
  font-size:12.5px; color:var(--muted); max-width:74ch;
}}
.dipnot p {{ margin:0 0 8px; }}
</style>

<div class="kabuk">
  <header class="kunye">
    <p class="etiket"><span>Bizigo · altyapı araştırması</span><span>21 Ağustos 2026</span><span>6 mercek · 13 ajan</span></p>
    <h1>Kenar güveni: 100 mikroservislik bir estate'te kod bilgisi</h1>
    <p class="ozet">Altı ayrı merceğin taraması, her mercek için onu çürütmeye çalışan bir doğrulama turu, ve bir eksiklik eleştirisi. Doğrulamanın düzelttiği her yerde düzeltilmiş hâl esas alındı; doğrulanamayan her sayı işaretli.</p>
    <ul class="olcum">
      <li><b>11</b> katman · L0–L10</li>
      <li><b>40</b> doğrulanmış iddia</li>
      <li><b>12</b> doğrulanamamış</li>
      <li><b>1</b> doğrulama ajanı düştü (529)</li>
    </ul>
  </header>

  <div class="govde">
    <nav class="ray" aria-label="Belge yapısı">
      <p class="raybas">Bölümler</p>
      {RAY}
    </nav>

    <main>
{govde}
      <div class="dipnot">
        <p>Yeşil çipler doğrulama turunun kaynakla teyit ettiği iddiaları, sarı çipler teyit edilemeyenleri işaretliyor — belgenin kendi tezi olan “her kenar kökenini taşımalı” kuralı, belgenin kendisine uygulandı.</p>
        <p>Doğrulama aşamasının bir ajanı (servis kataloğu merceği) 529 hatasıyla düştü; o merceğin iddiaları adversarial kontrolden <em>geçmedi</em> ve buradaki ağırlıkları buna göre okunmalı.</p>
      </div>
    </main>
  </div>
</div>
'''


# ---------------------------------------------------------------------------
# İKİ ÇIKTI, ve ayrı olmalarının sebebi somut.
#
# `…-artifact.html` bir PARÇA: `<title>` ile başlıyor, `<html>`/`<head>`/`<body>`
# yok. Artifact platformu onu kendi iskeletine sarıyor; kendi `<!doctype>`umuzu
# koymak `<body>` içine ikinci bir belge gömmek olurdu.
#
# `…​.html` ise TAM bir belge: depodaki kopya `file://` ile doğrudan açılıyor ve
# orada `<!doctype>` yokluğu tarayıcıyı quirks mode'a düşürüyor, `<meta charset>`
# yokluğu da Türkçe karakterleri bozuyor. İki hedefin iki farklı gereksinimi var;
# tek dosyayla ikisini birden karşılamak mümkün değil.
# ---------------------------------------------------------------------------
kafa, ayirac, govde_markup = HTML.partition('<div class="kabuk">')

if not ayirac:
    raise SystemExit("Şablon değişmiş: '<div class=\"kabuk\">' bulunamadı.")

STANDALONE = (
    "<!doctype html>\n"
    '<html lang="tr">\n'
    "<head>\n"
    '<meta charset="utf-8">\n'
    '<meta name="viewport" content="width=device-width, initial-scale=1">\n'
    + kafa
    + "</head>\n<body>\n"
    + ayirac
    + govde_markup
    + "</body>\n</html>\n"
)

CIKTI.write_text(STANDALONE, encoding="utf-8")
PARCA.write_text(HTML, encoding="utf-8")

kok = BURASI.parent.parent
print(f"yazıldı: {CIKTI.relative_to(kok)} ({len(STANDALONE)} karakter, tam belge)")
print(f"yazıldı: {PARCA.relative_to(kok)} ({len(HTML)} karakter, artifact parçası)")
print(f"bölüm: {len(bolumler)} · katman: {len(katmanlar)}")
