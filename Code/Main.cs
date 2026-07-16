/*
 * (Main.cs)
 *------------------------------------------------------------
 * Created - 7/14/2026
 * Created by - Seliris
 *-------------------------------------------------------------
 */

using ForgeClient.Code.Net;
using Godot;
using Shared.Networking.Packets.Auth;
using Shared.Networking.Packets.Character;
using System;
using System.Threading.Tasks;

namespace ForgeClient;

/// <summary>
/// Root scene script. Currently the wire test harness: runs the full
/// verified chain against the live dev servers - auth (stored-seed key
/// auth, or password auth that mints one), character create when needed,
/// then the UDP session through admission, version check, and both echo
/// legs. Real login UI replaces this once the chain is proven.
/// </summary>
public partial class Main : Node
{
    // Fill in with a real dev account before running. Test-harness
    // constants, not a credential story - that arrives with the login UI.
    private const string AccountId = "poopypants";
    private const string Password = "poopy";

    // The held UDP session, alive from a successful Ok auth until the game
    // quits. Static because the auth flow is static; becomes real state
    // management when the harness grows into the actual client.
    private static UdpSession? _udpSession;

    public override void _Ready()
    {
        GD.Print("[Forge] Auth flow against "
            + $"{ForgeTransport.ServerHost}:{ForgeTransport.ServerPort}");

        // Fire-and-forget with explicit fault surfacing: an unobserved
        // exception in an async void would vanish silently.
        _ = RunAuthFlowAsync();
    }

    public override void _ExitTree()
    {
        // Quit path: stop the poll thread and close the session clean.
        _udpSession?.Stop();
        _udpSession = null;
    }

    // Continuations after ConfigureAwait(false) run off the main thread.
    // GD.Print is thread-safe; touching the scene tree from here is NOT -
    // when results drive UI, marshal via CallDeferred.
    private static async Task RunAuthFlowAsync()
    {
        try
        {
            byte[]? seed = SeedStore.Load(AccountId);

            if (seed is null)
            {
                GD.Print("[Forge] No stored seed; password auth first.");

                seed = await PasswordAuthAndStoreSeedAsync()
                    .ConfigureAwait(false);

                if (seed is null)
                    return;
            }
            else
            {
                GD.Print("[Forge] Stored seed found; key auth directly.");
            }

            GD.Print("[Forge] Key auth with seed ...");

            AuthAttemptResult keyResult =
                await AuthClient.KeyAuthAsync(AccountId, seed)
                    .ConfigureAwait(false);

            if (!keyResult.Succeeded)
            {
                GD.PrintErr(
                    $"[Forge] Key auth failed: {keyResult.FailureReason}");
                return;
            }

            ReportOutcome("Key auth", keyResult.Response);

            switch (keyResult.Response.Outcome)
            {
                case AuthOutcome.Ok:
                    StartUdpSession(keyResult.Response);
                    break;

                case AuthOutcome.NeedsCharacter:
                    await CreateAndReauthAsync(seed).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Forge] Auth flow threw: {ex}");
        }
    }

    // The token is single-use with a 30 s lifetime, so the session starts
    // immediately from whichever path minted it.
    private static void StartUdpSession(AuthResponsePacket response)
    {
        _udpSession = new UdpSession(
            response.UdpEndpoint, response.SessionToken);
        _udpSession.Start();
    }

    // Leg 1 with persistence: password auth, then store the freshly minted
    // seed. Returns the seed for the key-auth leg, or null when the flow
    // cannot continue. An empty IssuedPrivateKey on success is anomalous -
    // the server mints on every password success, for either outcome.
    private static async Task<byte[]?> PasswordAuthAndStoreSeedAsync()
    {
        AuthAttemptResult result =
            await AuthClient.PasswordAuthAsync(AccountId, Password)
                .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            GD.PrintErr(
                $"[Forge] Password auth failed: {result.FailureReason}");
            return null;
        }

        ReportOutcome("Password auth", result.Response);

        if (string.IsNullOrEmpty(result.Response.IssuedPrivateKey))
        {
            GD.PrintErr("[Forge] Password success returned no private "
                + "key; cannot continue to key auth.");
            return null;
        }

        byte[] seed =
            Convert.FromBase64String(result.Response.IssuedPrivateKey);
        SeedStore.Save(AccountId, seed);

        GD.Print($"[Forge] Seed stored ({seed.Length} bytes).");
        return seed;
    }

    // Client mirror of the Probe's ReportAuthLeg: Ok and NeedsCharacter are
    // both valid outcomes; anything else (including None, which the server
    // never writes) is a loud failure.
    private static void ReportOutcome(
        string leg, AuthResponsePacket response)
    {
        switch (response.Outcome)
        {
            case AuthOutcome.Ok:
                GD.Print($"[Forge] {leg} OK. "
                    + $"token={Truncate(response.SessionToken)} "
                    + $"udp={response.UdpEndpoint}");
                break;

            case AuthOutcome.NeedsCharacter:
                GD.Print($"[Forge] {leg} OK: NeedsCharacter "
                    + "(no token; create required).");
                break;

            default:
                GD.PrintErr($"[Forge] {leg}: unexpected outcome "
                    + $"{response.Outcome}; expected Ok or NeedsCharacter.");
                break;
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 12 ? value : value[..12] + "...";

    // Canned name candidates for the harness: the first is deliberately
    // invalid to exercise the in-place retry live, the second may collide
    // if re-run, the third is the landing spot. The provider seam is what
    // the login UI will eventually occupy.
    private static readonly string[] NameCandidates =
        ["X9!", "grumble", "grumblesnout"];

    private static int _nameIndex;

    private static Task<string?> NextCannedName(
        CharacterCreateOutcome? lastRejection)
    {
        if (lastRejection is not null)
            GD.Print($"[Forge] Name rejected: {lastRejection}; retrying.");

        string? next = _nameIndex < NameCandidates.Length
            ? NameCandidates[_nameIndex++]
            : null;

        if (next is not null)
            GD.Print($"[Forge] Trying name \"{next}\".");

        return Task.FromResult(next);
    }

    // The create flow plus the re-auth that mints the token through the
    // single verified issuance path - Created closes the connection clean,
    // the account now owns a character, and the same seed stays valid. An
    // Ok re-auth flows straight into the UDP session, converging with the
    // direct path exactly as the Probe's Program does.
    private static async Task CreateAndReauthAsync(byte[] seed)
    {
        GD.Print("[Forge] Create flow (held-open connection) ...");

        CreateFlowResult createResult =
            await CreateClient.CreateCharacterAsync(
                AccountId, seed, NextCannedName)
                .ConfigureAwait(false);

        if (!createResult.Created)
        {
            GD.PrintErr(
                $"[Forge] Create failed: {createResult.FailureReason}");
            return;
        }

        GD.Print("[Forge] Character created. Re-auth for token ...");

        AuthAttemptResult reauth =
            await AuthClient.KeyAuthAsync(AccountId, seed)
                .ConfigureAwait(false);

        if (!reauth.Succeeded)
        {
            GD.PrintErr(
                $"[Forge] Re-auth failed: {reauth.FailureReason}");
            return;
        }

        ReportOutcome("Re-auth", reauth.Response);

        if (reauth.Response.Outcome != AuthOutcome.Ok)
        {
            GD.PrintErr("[Forge] Expected Ok after create; got "
                + $"{reauth.Response.Outcome}.");
            return;
        }

        StartUdpSession(reauth.Response);
    }
}

/*
 *------------------------------------------------------------
 * (Main.cs)
 * See License.txt for licensing information.
 *-----------------------------------------------------------
 */