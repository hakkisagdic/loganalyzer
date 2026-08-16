using System.Globalization;
using System.Text;

namespace Bizigo.Parsing.Schema;

/// <summary>
/// <c>"{{ srcip }}"</c> şablonlarını çözer.
///
/// <para>
/// Tek bir yer tutucudan ibaret şablon değeri <b>tipiyle</b> döndürür:
/// <c>src_port: "{{ srcport }}"</c> yazan bir parser <c>convert</c> ile
/// tamsayıya çevirdiği değeri string'e geri düşürmemeli. Karışık şablon
/// (<c>"{{ a }}:{{ b }}"</c>) doğal olarak string üretir.
/// </para>
/// <para>
/// Eksik alan sessizce boş string'e dönmez — atama <b>yapılmaz</b>. Aksi halde
/// eşleşmeyen bir alan, olayda boş bir <c>src_ip</c> olarak görünür ve sorgu
/// sonuçlarını sessizce kirletir.
/// </para>
/// </summary>
public static class TemplateRenderer
{
    public static IReadOnlyList<string> ExtractFields(string template)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{", StringComparison.Ordinal))
        {
            return [];
        }

        var fields = new List<string>();
        var index = 0;

        while (true)
        {
            var open = template.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            var name = template[(open + 2)..close].Trim();
            if (name.Length > 0)
            {
                fields.Add(name);
            }

            index = close + 2;
        }

        return fields;
    }

    public static bool TryRender(
        string template,
        IReadOnlyDictionary<string, object?> fields,
        out object? value)
    {
        value = null;

        var open = template.IndexOf("{{", StringComparison.Ordinal);
        if (open < 0)
        {
            value = template;
            return true;
        }

        // Tam olarak tek yer tutucu: tipli değeri koru.
        var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
        if (open == 0 && close == template.Length - 2)
        {
            var name = template[2..close].Trim();
            if (!fields.TryGetValue(name, out var single) || single is null)
            {
                return false;
            }

            value = single;
            return true;
        }

        var builder = new StringBuilder(template.Length);
        var index = 0;

        while (index < template.Length)
        {
            var next = template.IndexOf("{{", index, StringComparison.Ordinal);
            if (next < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            var end = template.IndexOf("}}", next + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            builder.Append(template, index, next - index);

            var field = template[(next + 2)..end].Trim();
            if (!fields.TryGetValue(field, out var resolved) || resolved is null)
            {
                return false;
            }

            builder.Append(Convert.ToString(resolved, CultureInfo.InvariantCulture));
            index = end + 2;
        }

        value = builder.ToString();
        return true;
    }
}
