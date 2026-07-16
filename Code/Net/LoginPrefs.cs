/*
 * (LoginPrefs.cs)
 *------------------------------------------------------------
 * Created - 7/16/2026
 * Created by - Seliris
 *-------------------------------------------------------------
 */

using Godot;

namespace ForgeClient.Code.Net;

/// <summary>
/// Persists the login form's non-secret state between sessions: server
/// address, account name, and the remember-me flag. The password is never
/// stored here or anywhere else.
/// </summary>
/// <remarks>One flat section in user://client.cfg via Godot's ConfigFile.
/// Load failures (missing or corrupt file) fall back to defaults silently -
/// the form just starts blank, exactly like a first launch.</remarks>
internal sealed class LoginPrefs
{
    private const string FilePath = "user://client.cfg";
    private const string Section = "login";

    internal string Server { get; set; } = "10.0.0.84:9997";
    internal string Account { get; set; } = string.Empty;
    internal bool Remember { get; set; }

    /// <summary>
    /// Loads stored prefs, or defaults when no valid file exists.
    /// </summary>
    internal static LoginPrefs Load()
    {
        var prefs = new LoginPrefs();
        var file = new ConfigFile();

        if (file.Load(FilePath) != Error.Ok)
            return prefs;

        prefs.Server = (string)file.GetValue(
            Section, "server", prefs.Server);
        prefs.Account = (string)file.GetValue(
            Section, "account", prefs.Account);
        prefs.Remember = (bool)file.GetValue(
            Section, "remember", prefs.Remember);

        return prefs;
    }

    /// <summary>
    /// Writes the current values to disk, overwriting previous prefs.
    /// </summary>
    internal void Save()
    {
        var file = new ConfigFile();

        file.SetValue(Section, "server", Server);
        file.SetValue(Section, "account", Account);
        file.SetValue(Section, "remember", Remember);

        file.Save(FilePath);
    }
}

/*
 *------------------------------------------------------------
 * (LoginPrefs.cs)
 * See License.txt for licensing information.
 *-----------------------------------------------------------
 */