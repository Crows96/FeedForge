using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeedForge;

internal static class JsonIo
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Запись через временный файл: при сбое старый файл не портится.</summary>
    public static void WriteAtomic(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, value.GetType(), Options), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// Хранилище: глобальные настройки (app.json в %AppData%\FeedForge) и сети (по файлу на сеть в папке данных\networks).
/// Папку данных можно сменить в настройках, например на папку, которая синхронизируется между компьютерами.
/// </summary>
public sealed class AppStorage
{
    public static string AppDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FeedForge");
    private static string SettingsPath => Path.Combine(AppDir, "app.json");

    public AppSettings Settings { get; private set; } = new();
    public List<NetworkConfig> Networks { get; private set; } = new();
    /// <summary>Сообщение о проблемах при загрузке (повреждённые файлы), показывается пользователю.</summary>
    public string? Warning { get; private set; }

    private string NetworksDir => Path.Combine(Settings.DataDir, "networks");

    public static AppStorage Load()
    {
        var st = new AppStorage();
        try
        {
            if (File.Exists(SettingsPath))
                st.Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonIo.Options) ?? new AppSettings();
        }
        catch
        {
            st.Warning = "Не удалось прочитать app.json, использованы настройки по умолчанию.";
        }
        st.Settings.Normalize();

        // Первый запуск новой версии: наборы настроек из старой программы становятся сетями (без источников)
        if (!Directory.Exists(st.NetworksDir)) st.MigrateOldPresets();

        st.ReloadNetworks();
        return st;
    }

    public void SaveSettings()
    {
        try { JsonIo.WriteAtomic(SettingsPath, Settings); }
        catch (Exception ex) { Warning = "Не удалось сохранить настройки: " + ex.Message; }
    }

    public void ReloadNetworks()
    {
        Networks = new List<NetworkConfig>();
        if (!Directory.Exists(NetworksDir)) return;

        var bad = new List<string>();
        foreach (var file in Directory.GetFiles(NetworksDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var n = JsonSerializer.Deserialize<NetworkConfig>(File.ReadAllText(file), JsonIo.Options);
                if (n == null || string.IsNullOrWhiteSpace(n.Id)) throw new InvalidDataException("пустой файл");
                n.Build ??= new UiSettings();
                n.Sources ??= new List<NetworkSource>();
                if (string.IsNullOrWhiteSpace(n.Slug)) n.Slug = UniqueSlug(n.Name, n);
                Networks.Add(n);
            }
            catch
            {
                bad.Add(Path.GetFileName(file));
            }
        }
        if (bad.Count > 0) Warning = "Не удалось прочитать файлы сетей: " + string.Join(", ", bad);
        Networks = Networks.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void SaveNetwork(NetworkConfig n)
    {
        try { JsonIo.WriteAtomic(Path.Combine(NetworksDir, n.Id + ".json"), n); }
        catch (Exception ex) { Warning = "Не удалось сохранить сеть «" + n.Name + "»: " + ex.Message; }
    }

    public void DeleteNetwork(NetworkConfig n)
    {
        try
        {
            string path = Path.Combine(NetworksDir, n.Id + ".json");
            if (File.Exists(path)) File.Delete(path);
            string hist = HistoryPath(n);
            if (File.Exists(hist)) File.Delete(hist);
        }
        catch { }
        Networks.Remove(n);
    }

    // ---- история запусков: по файлу на сеть, последние 100 записей
    private string HistoryPath(NetworkConfig n) => Path.Combine(Settings.DataDir, "history", n.Id + ".json");

    public List<RunRecord> LoadHistory(NetworkConfig n)
    {
        try
        {
            string p = HistoryPath(n);
            if (File.Exists(p))
                return JsonSerializer.Deserialize<List<RunRecord>>(File.ReadAllText(p), JsonIo.Options) ?? new List<RunRecord>();
        }
        catch { }
        return new List<RunRecord>();
    }

    public void AddHistory(NetworkConfig n, RunRecord record)
    {
        try
        {
            var list = LoadHistory(n);
            list.Insert(0, record);
            if (list.Count > 100) list.RemoveRange(100, list.Count - 100);
            JsonIo.WriteAtomic(HistoryPath(n), list);
        }
        catch { }
    }

    public void AddNetwork(NetworkConfig n)
    {
        Networks.Add(n);
        Networks = Networks.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        SaveNetwork(n);
    }

    /// <summary>Slug без повторов среди сетей (кроме самой редактируемой).</summary>
    public string UniqueSlug(string wanted, NetworkConfig? except)
    {
        string baseSlug = Translit.Slug(wanted);
        string slug = baseSlug;
        int k = 2;
        while (Networks.Any(n => n != except && string.Equals(n.Slug, slug, StringComparison.OrdinalIgnoreCase)))
            slug = baseSlug + "_" + k++;
        return slug;
    }

    private void MigrateOldPresets()
    {
        try
        {
            var old = SettingsStore.Load(out bool existed);
            if (!existed) return;
            foreach (var (name, preset) in old.Presets)
            {
                var n = new NetworkConfig { Name = name, Build = preset };
                n.Slug = UniqueSlug(name, null);
                Networks.Add(n);
                SaveNetwork(n);
            }
            // из старых настроек окна берём то, что стало глобальным
            Settings.Threads = Math.Clamp(old.Last.Threads, 1, 64);
            Settings.Economy = old.Last.Economy;
            SaveSettings();
        }
        catch { }
    }
}
