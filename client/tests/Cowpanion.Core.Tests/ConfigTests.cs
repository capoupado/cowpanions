using System.Text.Json;
using Cowpanion.Core.Configuration;

namespace Cowpanion.Core.Tests;

public class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cowpanion-tests", Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_dir, "config.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Missing_file_is_created_with_defaults_and_a_client_id()
    {
        using var store = new ConfigStore(ConfigPath);
        var cfg = store.Load();
        Assert.True(File.Exists(ConfigPath));
        Assert.Equal(3, cfg.Scale);
        Assert.Equal("primary", cfg.Monitors);
        Assert.Equal(30, cfg.ActiveFps);
        Assert.Equal(5, cfg.IdleFps);
        Assert.False(cfg.MooEnabled);
        Assert.True(cfg.MultiplayerEnabled);
        Assert.Equal("commons", cfg.Pasture);
        Assert.Equal(4, cfg.OfflineHerdSize);
        Assert.Equal("", cfg.Variant);
        Assert.Matches("^[0-9a-f]{32}$", cfg.ClientId);

        using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        Assert.True(doc.RootElement.TryGetProperty("clientId", out _));
        Assert.True(doc.RootElement.TryGetProperty("variant", out _));
        Assert.True(doc.RootElement.TryGetProperty("offlineHerdSize", out _));
    }

    [Fact]
    public void Client_id_is_stable_across_loads()
    {
        using var store = new ConfigStore(ConfigPath);
        var a = store.Load();
        var b = store.Load();
        Assert.Equal(a.ClientId, b.ClientId);
    }

    [Fact]
    public void Invalid_values_are_clamped_not_fatal()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """
            {
              "scale": 99, "activeFps": 1000, "idleFps": -3, "monitors": "everything",
              "offlineHerdSize": 50, "sleepAfterIdleMinutes": 0, "serverUrl": "http://not-a-socket",
              "pasture": "Has Spaces!", "displayName": "  a\tvery   long   name that goes on and on ",
              "clientId": "nothex", "variant": "Bad-Variant"
            }
            """);
        using var store = new ConfigStore(ConfigPath);
        var cfg = store.Load();
        Assert.Equal(6, cfg.Scale);
        Assert.Equal(60, cfg.ActiveFps);
        Assert.Equal(1, cfg.IdleFps);
        Assert.Equal("primary", cfg.Monitors);
        Assert.Equal(12, cfg.OfflineHerdSize);
        Assert.Equal(1, cfg.SleepAfterIdleMinutes);
        Assert.StartsWith("wss://", cfg.ServerUrl);
        Assert.Equal("commons", cfg.Pasture);
        Assert.True(cfg.DisplayName.Length <= 16);
        Assert.Equal("a very long name", cfg.DisplayName);
        Assert.Matches("^[0-9a-f]{32}$", cfg.ClientId);
        Assert.Equal("", cfg.Variant);
        Assert.NotEmpty(store.Log);
        Assert.False(File.Exists(store.BadPath));
    }

    [Fact]
    public void Malformed_file_is_backed_up_and_replaced()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, "{ this is not json");
        using var store = new ConfigStore(ConfigPath);
        var cfg = store.Load();
        Assert.True(File.Exists(store.BadPath));
        Assert.Equal("{ this is not json", File.ReadAllText(store.BadPath));
        Assert.Equal(3, cfg.Scale);
        var reparsed = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        Assert.Equal(JsonValueKind.Object, reparsed.RootElement.ValueKind);
    }

    [Fact]
    public void Unknown_properties_are_ignored_and_known_ones_kept()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """{ "scale": 2, "futureSetting": true, "pasture": "Friends" }""");
        using var store = new ConfigStore(ConfigPath);
        var cfg = store.Load();
        Assert.Equal(2, cfg.Scale);
        Assert.Equal("friends", cfg.Pasture);
    }

    [Fact]
    public async Task External_edit_raises_Changed_after_debounce_but_own_save_does_not()
    {
        using var store = new ConfigStore(ConfigPath);
        var cfg = store.Load();
        var tcs = new TaskCompletionSource<CowpanionConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += c => tcs.TrySetResult(c);
        store.StartWatching();

        cfg.OfflineHerdSize = 7;
        store.Save(cfg);
        var ownSave = await Task.WhenAny(tcs.Task, Task.Delay(1500));
        Assert.NotSame(tcs.Task, ownSave);

        await Task.Delay(1600); // let the own-write suppression window expire
        string text = File.ReadAllText(ConfigPath).Replace("\"offlineHerdSize\": 7", "\"offlineHerdSize\": 9");
        File.WriteAllText(ConfigPath, text);
        var finished = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Same(tcs.Task, finished);
        Assert.Equal(9, (await tcs.Task).OfflineHerdSize);
    }

    [Fact]
    public void Variant_derivation_is_deterministic_and_within_the_list()
    {
        var variants = new[] { "black0", "black1", "brown", "white0", "white1", "white_darkspots", "white_pinkspots" };
        string id = ConfigValidator.NewClientId();
        string v1 = ConfigValidator.DeriveVariant(id, variants);
        string v2 = ConfigValidator.DeriveVariant(id, variants);
        Assert.Equal(v1, v2);
        Assert.Contains(v1, variants);
        Assert.Equal("", ConfigValidator.DeriveVariant(id, Array.Empty<string>()));
    }

    [Fact]
    public void Display_name_sanitiser_never_splits_a_surrogate_pair()
    {
        string name = "abcdefghijklmno😀xyz"; // 15 chars + emoji (2 UTF-16 units)
        string s = ConfigValidator.SanitizeDisplayName(name);
        Assert.Equal("abcdefghijklmno", s);
        Assert.Equal("", ConfigValidator.SanitizeDisplayName("\t  "));
    }
}
