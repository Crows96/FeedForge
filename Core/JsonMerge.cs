using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FeedForge;

/// <summary>
/// Склейка нескольких готовых JSON (результатов сборки) в один: каталоги и офферы по порядку частей.
/// Если id офферов или каталогов у частей совпали, у более поздней части добавляется префикс «номер части_»
/// (ссылки в каталогах переписываются).
/// </summary>
public static class JsonMerge
{
    public static void Merge(IReadOnlyList<string> files, string dst, out int catalogs, out int offers)
    {
        int cat = 0, off = 0;
        var docs = new List<JsonDocument>();
        try
        {
            foreach (var f in files) docs.Add(JsonDocument.Parse(File.ReadAllBytes(f)));

            // конфликты id офферов между частями
            var usedOffers = new HashSet<string>(StringComparer.Ordinal);
            var clash = new HashSet<string>[docs.Count];
            for (int i = 0; i < docs.Count; i++)
            {
                var mine = new HashSet<string>(StringComparer.Ordinal);
                if (docs[i].RootElement.TryGetProperty("offers", out var offArr) && offArr.ValueKind == JsonValueKind.Array)
                    foreach (var o in offArr.EnumerateArray())
                        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                            mine.Add(idEl.GetString() ?? "");
                clash[i] = new HashSet<string>(mine.Where(usedOffers.Contains), StringComparer.Ordinal);
                usedOffers.UnionWith(mine);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            string tmp = dst + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions
                       { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    w.WriteStartObject();

                    w.WriteStartArray("catalogs");
                    var usedCats = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < docs.Count; i++)
                    {
                        int prefix = i + 1;
                        string MapOffer(string id) => clash[i].Contains(id) ? $"{prefix}_{id}" : id;
                        var thisPart = new List<string>();
                        if (docs[i].RootElement.TryGetProperty("catalogs", out var cats) && cats.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var c in cats.EnumerateArray())
                            {
                                w.WriteStartObject();
                                foreach (var p in c.EnumerateObject())
                                {
                                    if (p.NameEquals("offers") && p.Value.ValueKind == JsonValueKind.Array)
                                    {
                                        w.WriteStartArray("offers");
                                        foreach (var el in p.Value.EnumerateArray())
                                            w.WriteStringValue(el.ValueKind == JsonValueKind.String ? MapOffer(el.GetString() ?? "") : "");
                                        w.WriteEndArray();
                                    }
                                    else if (p.NameEquals("id") && p.Value.ValueKind == JsonValueKind.String)
                                    {
                                        string id = p.Value.GetString() ?? "";
                                        string m = usedCats.Contains(id) ? $"{prefix}_{id}" : id;
                                        thisPart.Add(m);
                                        w.WriteString("id", m);
                                    }
                                    else p.WriteTo(w);
                                }
                                w.WriteEndObject();
                                cat++;
                            }
                        }
                        usedCats.UnionWith(thisPart);
                    }
                    w.WriteEndArray();

                    w.WriteStartArray("offers");
                    for (int i = 0; i < docs.Count; i++)
                    {
                        int prefix = i + 1;
                        if (!docs[i].RootElement.TryGetProperty("offers", out var offArr) || offArr.ValueKind != JsonValueKind.Array) continue;
                        foreach (var o in offArr.EnumerateArray())
                        {
                            off++;
                            string? id = o.ValueKind == JsonValueKind.Object && o.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                                ? idEl.GetString() : null;
                            if (id == null || !clash[i].Contains(id)) { o.WriteTo(w); continue; }

                            w.WriteStartObject();
                            foreach (var p in o.EnumerateObject())
                            {
                                if (p.NameEquals("id")) w.WriteString("id", $"{prefix}_{id}");
                                else p.WriteTo(w);
                            }
                            w.WriteEndObject();
                        }
                    }
                    w.WriteEndArray();

                    w.WriteNumber("version", 2);
                    w.WriteEndObject();
                }
                File.Move(tmp, dst, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
        finally
        {
            foreach (var d in docs) d.Dispose();
        }
        catalogs = cat;
        offers = off;
    }
}
