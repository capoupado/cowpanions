using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cowpanion.Core.Configuration;

/// <summary>
/// Loads, saves and watches config.json. Malformed files are backed up to config.bad.json and replaced with
/// defaults. Changes on disk raise <see cref="Changed"/> after a 500 ms debounce, on a thread-pool thread —
/// the caller marshals to the UI. Writes made through <see cref="Save"/> do not raise <see cref="Changed"/>.
/// </summary>
public sealed class ConfigStore : IDisposable
{
    public const int DebounceMilliseconds = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object _gate = new();
    private readonly List<string> _log = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private long _ownWriteUntilTicks;
    private bool _disposed;

    public ConfigStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public string BadPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "config.bad.json");

    /// <summary>Warnings and notes from the last Load (clamped values, backups, ...).</summary>
    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_gate)
            {
                return _log.ToArray();
            }
        }
    }

    /// <summary>Raised (debounced) when the file changes on disk and re-loads cleanly. Thread-pool thread.</summary>
    public event Action<CowpanionConfig>? Changed;

    public static string DefaultPath
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(appData, "Cowpanion", "config.json");
        }
    }

    /// <summary>
    /// Reads the config, clamps, fills the clientId and writes back if anything changed or the file was missing.
    /// Never throws for a bad file: it is backed up and defaults are used.
    /// </summary>
    public CowpanionConfig Load()
    {
        lock (_gate)
        {
            _log.Clear();
        }

        CowpanionConfig config;
        bool needsSave = false;
        string dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);

        if (!File.Exists(Path))
        {
            config = new CowpanionConfig();
            needsSave = true;
            Note("config.json missing → writing defaults");
        }
        else
        {
            string text;
            try
            {
                text = File.ReadAllText(Path);
                var parsed = JsonSerializer.Deserialize<CowpanionConfig>(text, JsonOptions);
                if (parsed is null)
                {
                    throw new JsonException("file is 'null'");
                }
                config = parsed;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Note($"config.json malformed ({ex.GetType().Name}: {ex.Message}) → backed up to config.bad.json, defaults restored");
                TryBackup();
                config = new CowpanionConfig();
                needsSave = true;
            }
        }

        string before = JsonSerializer.Serialize(config, JsonOptions);
        var warnings = ConfigValidator.Clamp(config);
        foreach (var w in warnings)
        {
            Note("config: " + w);
        }
        string after = JsonSerializer.Serialize(config, JsonOptions);
        if (needsSave || before != after)
        {
            Save(config);
        }
        return config;
    }

    public void Save(CowpanionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(config, JsonOptions);
        string tmp = Path + ".tmp";
        lock (_gate)
        {
            // Ignore our own change notifications for a moment.
            _ownWriteUntilTicks = Environment.TickCount64 + 1500;
        }
        File.WriteAllText(tmp, json);
        File.Move(tmp, Path, overwrite: true);
    }

    /// <summary>Starts the debounced FileSystemWatcher. Safe to call once.</summary>
    public void StartWatching()
    {
        if (_watcher is not null)
        {
            return;
        }
        string dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);
        _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(Path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (Environment.TickCount64 < _ownWriteUntilTicks)
            {
                return;
            }
            _debounce ??= new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(DebounceMilliseconds, Timeout.Infinite);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        CowpanionConfig config;
        try
        {
            config = Load();
        }
        catch (Exception ex)
        {
            Note("config reload failed: " + ex.Message);
            return;
        }
        Changed?.Invoke(config);
    }

    private void TryBackup()
    {
        try
        {
            File.Copy(Path, BadPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Note("could not write config.bad.json: " + ex.Message);
        }
    }

    private void Note(string message)
    {
        lock (_gate)
        {
            _log.Add(message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
