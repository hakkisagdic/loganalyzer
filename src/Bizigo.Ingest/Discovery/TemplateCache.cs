using System.Collections.Concurrent;

namespace Bizigo.Ingest.Discovery;

/// <summary>
/// Maskelenmiş imza → <c>template_id</c>. Sıcak yolun sidecar'a sormadan
/// etiket yazabilmesini sağlayan tek şey.
///
/// <para>
/// <b>Neden doğru çalışıyor:</b> Drain3 kümelemesini maskelenmiş metin
/// üzerinde yapıyor ve deterministik. Aynı maskelenmiş metin daima aynı
/// kümeye düşüyor, dolayısıyla imzayı bir kez sorup önbelleğe koymak sonraki
/// tüm aynı-imzalı olaylar için geçerli bir cevap veriyor. Varsayımın taşıyıcı
/// ayağı .NET ile Python'un aynı maskeyi üretmesi; sapma
/// <see cref="DiscoveryStats.SignatureDrift"/> ile ölçülüyor.
/// </para>
///
/// <para>
/// <b>Bayatlama:</b> sidecar tarafında <c>max_clusters</c> LRU'su bir kümeyi
/// tahliye ederse buradaki kimlik artık var olmayan bir kümeyi gösterir.
/// Kimlikler yeniden kullanılmadığı için bu "yanlış olay" değil, "artık
/// katalogda karşılığı olmayan kimlik" demek — F3 tarafında görünür ve
/// zararsız. Alternatif (her olayda doğrulama) sidecar'ı sıcak yola sokardı.
/// </para>
/// </summary>
public sealed class TemplateCache(int capacity)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity), "Sınırsız önbellek yasak.");

    private long _tick;
    private int _trimming;

    public int Count => _entries.Count;

    public bool TryGet(string signature, out string templateId)
    {
        if (_entries.TryGetValue(signature, out var entry))
        {
            entry.LastUsed = Interlocked.Increment(ref _tick);
            templateId = entry.TemplateId;
            return true;
        }

        templateId = string.Empty;
        return false;
    }

    public void Set(string signature, string templateId)
    {
        if (signature.Length == 0 || templateId.Length == 0)
        {
            return;
        }

        _entries[signature] = new Entry(templateId) { LastUsed = Interlocked.Increment(ref _tick) };

        if (_entries.Count > _capacity)
        {
            Trim();
        }
    }

    /// <summary>
    /// Yaklaşık LRU: en eski %20 atılır. Kesin LRU için her okumada kilit
    /// gerekirdi; bu önbellek her <c>failed</c> olayda okunuyor, yani kilit
    /// doğrudan ingest'in üstüne biner. Yaklaşıklığın bedeli birkaç fazladan
    /// keşif isteği — sınırlı kuyruk zaten onu da soğuruyor.
    /// </summary>
    private void Trim()
    {
        if (Interlocked.Exchange(ref _trimming, 1) == 1)
        {
            return;
        }

        try
        {
            var overflow = _entries.Count - _capacity;
            if (overflow <= 0)
            {
                return;
            }

            var victims = _entries
                .OrderBy(static pair => pair.Value.LastUsed)
                .Take(overflow + (_capacity / 5))
                .Select(static pair => pair.Key)
                .ToArray();

            foreach (var key in victims)
            {
                _entries.TryRemove(key, out _);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _trimming, 0);
        }
    }

    private sealed class Entry(string templateId)
    {
        public string TemplateId { get; } = templateId;

        public long LastUsed { get; set; }
    }
}
