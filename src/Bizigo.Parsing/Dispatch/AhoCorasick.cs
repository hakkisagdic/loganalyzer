namespace Bizigo.Parsing.Dispatch;

/// <summary>
/// Literal ön filtre otomatı (F1 §4.2 kademe 2).
///
/// <para>
/// Bütün parser'ların <c>match.contains</c> literalleri tek bir otomata
/// derleniyor ve satır <b>bir kez</b> taranıyor. Alternatif — her parser için
/// ayrı <c>IndexOf</c> — parser sayısıyla doğrusal büyürdü; yüzlerce parser
/// hedefinde bu, satır başına yüzlerce tarama demek.
/// </para>
///
/// <para>
/// Eşleştirme <b>ordinal</b> ve büyük/küçük harf duyarlı: log formatları
/// sabittir ve duyarsız eşleştirme Türkçe kültürde <c>I/ı</c> tuzağına açık
/// olurdu. Literal yazımı parser YAML'ında neyse odur.
/// </para>
/// </summary>
public sealed class AhoCorasick
{
    private sealed class Node
    {
        public Dictionary<char, int> Next { get; } = [];
        public int Fail { get; set; }
        public List<int>? Outputs { get; set; }
    }

    private readonly List<Node> _nodes = [new Node()];

    private AhoCorasick()
    {
    }

    public int PatternCount { get; private set; }

    /// <param name="patterns">
    /// (literal, sahip kimliği) çiftleri. Aynı literal birden çok sahibe ait olabilir.
    /// </param>
    public static AhoCorasick Build(IEnumerable<(string Literal, int OwnerId)> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var automaton = new AhoCorasick();

        foreach (var (literal, ownerId) in patterns)
        {
            if (string.IsNullOrEmpty(literal))
            {
                continue;
            }

            automaton.Add(literal, ownerId);
        }

        automaton.BuildFailureLinks();
        return automaton;
    }

    private void Add(string literal, int ownerId)
    {
        var current = 0;

        foreach (var c in literal)
        {
            if (!_nodes[current].Next.TryGetValue(c, out var next))
            {
                next = _nodes.Count;
                _nodes.Add(new Node());
                _nodes[current].Next[c] = next;
            }

            current = next;
        }

        (_nodes[current].Outputs ??= []).Add(ownerId);
        PatternCount++;
    }

    private void BuildFailureLinks()
    {
        var queue = new Queue<int>();

        foreach (var (_, child) in _nodes[0].Next)
        {
            _nodes[child].Fail = 0;
            queue.Enqueue(child);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var (c, child) in _nodes[current].Next)
            {
                var fail = _nodes[current].Fail;

                while (fail != 0 && !_nodes[fail].Next.ContainsKey(c))
                {
                    fail = _nodes[fail].Fail;
                }

                _nodes[child].Fail = _nodes[fail].Next.TryGetValue(c, out var target) && target != child
                    ? target
                    : 0;

                // Çıktılar fail zinciri boyunca birleştiriliyor: "abc" eşleşirken
                // "bc" de eşleşiyorsa ikisinin de sahipleri aday olmalı.
                var failOutputs = _nodes[_nodes[child].Fail].Outputs;
                if (failOutputs is not null)
                {
                    (_nodes[child].Outputs ??= []).AddRange(failOutputs);
                }

                queue.Enqueue(child);
            }
        }
    }

    /// <summary>Satırı bir kez tarar ve eşleşen sahiplerin kimliklerini verir.</summary>
    public HashSet<int> Match(ReadOnlySpan<char> input)
    {
        var matches = new HashSet<int>();
        var current = 0;

        foreach (var c in input)
        {
            while (current != 0 && !_nodes[current].Next.ContainsKey(c))
            {
                current = _nodes[current].Fail;
            }

            if (_nodes[current].Next.TryGetValue(c, out var next))
            {
                current = next;
            }

            var outputs = _nodes[current].Outputs;
            if (outputs is null)
            {
                continue;
            }

            foreach (var owner in outputs)
            {
                matches.Add(owner);
            }
        }

        return matches;
    }
}
