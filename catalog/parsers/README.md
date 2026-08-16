# Parser kataloğu

YAML parser plugin'leri. Format: [F1 §3](../../README.md) — `apiVersion / metadata /
match / pipeline / map / tests`.

Dizin **T08**'in konusu (Cisco ASA/IOS, FortiGate, PAN-OS, MikroTik, Juniper, F5,
HAProxy, nginx + altın örnek dosyaları). T05 motoru ve CLI'yi kurar; buradaki
içerik motor gerçek vendor logu görmeden yazılmaz.

CI her PR'da şunu koşturur:

```sh
bizigo parser lint catalog/parsers
bizigo parser test catalog/parsers
```

Testsiz bir parser şema düzeyinde reddedilir — `tests` bloğu zorunludur.
