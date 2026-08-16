namespace Bizigo.Storage.ClickHouse;

/// <summary>
/// Bir .sql dosyasını tek tek çalıştırılabilir ifadelere böler.
/// ClickHouse istemcisi tek komutta birden fazla ifade kabul etmiyor.
/// Tırnak içi metin, tanımlayıcı tırnakları ve yorumlardaki ';' karakterleri ayraç sayılmaz.
/// </summary>
public static class SqlStatementSplitter
{
    public static IReadOnlyList<string> Split(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var statements = new List<string>();
        var current = new System.Text.StringBuilder();

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            // -- satır yorumu
            if (c == '-' && next == '-')
            {
                while (i < sql.Length && sql[i] != '\n') { i++; }
                current.Append('\n');
                continue;
            }

            // /* blok yorumu */
            if (c == '/' && next == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) { i++; }
                i++;
                continue;
            }

            // 'metin' — ClickHouse'ta kaçış \' ve '' ikisi de geçerli
            if (c == '\'' || c == '"' || c == '`')
            {
                var quote = c;
                current.Append(c);
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\\' && i + 1 < sql.Length)
                    {
                        current.Append(sql[i]).Append(sql[i + 1]);
                        i += 2;
                        continue;
                    }

                    current.Append(sql[i]);

                    if (sql[i] == quote)
                    {
                        // '' → kaçırılmış tırnak, metin devam ediyor
                        if (i + 1 < sql.Length && sql[i + 1] == quote)
                        {
                            current.Append(sql[i + 1]);
                            i += 2;
                            continue;
                        }

                        break;
                    }

                    i++;
                }

                continue;
            }

            if (c == ';')
            {
                AddIfNotBlank(statements, current);
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        AddIfNotBlank(statements, current);
        return statements;
    }

    private static void AddIfNotBlank(List<string> target, System.Text.StringBuilder buffer)
    {
        var text = buffer.ToString().Trim();
        if (text.Length > 0)
        {
            target.Add(text);
        }
    }
}
