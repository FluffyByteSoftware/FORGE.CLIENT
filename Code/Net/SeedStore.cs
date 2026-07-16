/*
 * (SeedStore.cs)
 *------------------------------------------------------------
 * Created - 7/14/2026
 * Created by - Seliris
 *-------------------------------------------------------------
 */

using Godot;
using System;
using System.IO;

namespace ForgeClient.Code.Net;

/// <summary>
/// Persists the per-account Ed25519 private seed between sessions, so key
/// auth can run without a fresh password login.
/// </summary>
/// <remarks>Dev posture: plain Base64 text files under Godot's user://
/// directory, one per account. This class is the single place seed
/// persistence lives - upgrading to encrypted-at-rest storage (DPAPI or the
/// OS credential store, filed as a pre-ship gate) changes this file's guts
/// and nothing else. Keys expire server-side after 3 days and every password
/// success mints a fresh one, so a stale file self-heals through the
/// password path.</remarks>
internal static class SeedStore
{
    private const string SeedDirectory = "E:/Forge/seeds";

    /// <summary>
    /// Stores an account's private seed, overwriting any previous one -
    /// every password success mints a fresh key, so latest always wins.
    /// </summary>
    internal static void Save(string accountId, byte[] seed)
    {
        string dir = ProjectSettings.GlobalizePath(SeedDirectory);
        Directory.CreateDirectory(dir);

        File.WriteAllText(
            PathFor(accountId), Convert.ToBase64String(seed));
    }

    /// <summary>
    /// Loads an account's stored seed, or null when none exists or the file
    /// is unreadable - the caller falls back to password auth either way.
    /// </summary>
    internal static byte[]? Load(string accountId)
    {
        string path = PathFor(accountId);

        if (!File.Exists(path))
            return null;

        try
        {
            return Convert.FromBase64String(File.ReadAllText(path).Trim());
        }
        catch (FormatException)
        {
            // Corrupt file: treat as absent. The password path re-mints.
            return null;
        }
    }

    // Filename derives from the account id. Account ids that are valid on
    // the server are safe filename material today; revisit if account id
    // rules ever loosen.
    private static string PathFor(string accountId) =>
        Path.Combine(
            ProjectSettings.GlobalizePath(SeedDirectory),
            $"{accountId}.seed");
}

/*
 *------------------------------------------------------------
 * (SeedStore.cs)
 * See License.txt for licensing information.
 *-----------------------------------------------------------
 */