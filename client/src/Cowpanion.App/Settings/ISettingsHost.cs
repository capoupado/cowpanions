using Cowpanion.Core.Configuration;

namespace Cowpanion.App.Settings;

/// <summary>What the settings window needs from the Orchestrator. Everything is called on the UI thread.</summary>
internal interface ISettingsHost
{
    /// <summary>A copy of the live config.</summary>
    CowpanionConfig CurrentConfig { get; }

    string ConfigPath { get; }

    string AppVersion { get; }

    /// <summary>Hotkey action name ("kill", "chat", "mute", "heart", "jump", "focusMode") → why it is not registered.</summary>
    IReadOnlyDictionary<string, string> HotkeyErrors { get; }

    /// <summary>Mutates a copy of the live config, clamps, saves and applies it. Returns the clamp corrections.</summary>
    IReadOnlyList<string> Apply(Action<CowpanionConfig> change);

    /// <summary>
    /// True while a hotkey capture box has keyboard focus: every global hotkey (Quit included) is released so the box
    /// can see the combination being pressed instead of the action firing.
    /// </summary>
    void SetHotkeysPaused(bool paused);

    void OpenConfigFile();
}
