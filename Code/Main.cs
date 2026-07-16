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
/// Root scene script: the login screen. Drives the full verified chain
/// from typed input - server address, account, password, remember-me -
/// through auth, character create when needed, and into the held UDP
/// session, narrating each leg on the status label.
/// </summary>
/// <remarks>Threading contract, load-bearing: auth continuations run off
/// the main thread (ConfigureAwait(false) throughout the Net layer), so
/// every UI touch from the flow marshals through <see cref="Defer"/>.
/// Field values are read on the main thread in the button handlers and
/// passed into the flow as parameters - LineEdit.Text is never read from
/// a continuation. The name-create sub-state bridges back the other way:
/// the flow awaits a TaskCompletionSource the name button completes.
/// </remarks>
public partial class Main : Control
{
    private LineEdit _serverEdit = null!;
    private LineEdit _accountEdit = null!;
    private LineEdit _passwordEdit = null!;
    private CheckBox _rememberCheck = null!;
    private Button _connectButton = null!;
    private LineEdit _nameEdit = null!;
    private Button _nameButton = null!;
    private Label _statusLabel = null!;

    private LoginPrefs _prefs = null!;

    // Owned by the main thread: assigned via Defer on session start, read
    // in _ExitTree. Single-threaded access by construction.
    private UdpSession? _udpSession;

    // The bridge between the create flow (poll/task thread) and the name
    // button (main thread). RunContinuationsAsynchronously keeps the auth
    // flow's continuation off the main thread when the button completes it.
    private TaskCompletionSource<string?>? _pendingName;

    public override void _Ready()
    {
        const string box = "CenterContainer/LoginBox/";

        _serverEdit = GetNode<LineEdit>(box + "ServerEdit");
        _accountEdit = GetNode<LineEdit>(box + "AccountEdit");
        _passwordEdit = GetNode<LineEdit>(box + "PasswordEdit");
        _rememberCheck = GetNode<CheckBox>(box + "RememberCheck");
        _connectButton = GetNode<Button>(box + "ConnectButton");
        _nameEdit = GetNode<LineEdit>(box + "NameEdit");
        _nameButton = GetNode<Button>(box + "NameButton");
        _statusLabel = GetNode<Label>(box + "StatusLabel");

        _prefs = LoginPrefs.Load();
        _serverEdit.Text = _prefs.Server;
        _accountEdit.Text = _prefs.Account;
        _rememberCheck.ButtonPressed = _prefs.Remember;

        _connectButton.Pressed += OnConnectPressed;
        _nameButton.Pressed += OnNamePressed;
    }

    public override void _ExitTree()
    {
        _udpSession?.Stop();
        _udpSession = null;
    }

    // Main thread: reads every field, validates, configures the transport,
    // persists prefs, then hands plain values to the background flow.
    private void OnConnectPressed()
    {
        string server = _serverEdit.Text.Trim();
        string account = _accountEdit.Text.Trim();
        string password = _passwordEdit.Text;
        bool remember = _rememberCheck.ButtonPressed;

        if (account.Length == 0)
        {
            _statusLabel.Text = "Account name required.";
            return;
        }

        if (!TryParseServer(server, out string host, out int port))
        {
            _statusLabel.Text = $"Bad server address \"{server}\" "
                + "(expected host:port).";
            return;
        }

        ForgeTransport.ServerHost = host;
        ForgeTransport.ServerPort = port;

        _prefs.Server = server;
        _prefs.Account = account;
        _prefs.Remember = remember;
        _prefs.Save();

        // "Don't remember me" means the disk forgets, now.
        if (!remember)
            SeedStore.Delete(account);

        SetFormEnabled(false);
        _statusLabel.Text = $"Connecting to {host}:{port} ...";

        // Fire-and-forget with explicit fault surfacing inside the flow.
        _ = RunLoginAsync(account, password, remember);
    }

    // Everything below the first await runs off the main thread.
    private async Task RunLoginAsync(
        string account, string password, bool remember)
    {
        try
        {
            byte[]? seed = remember ? SeedStore.Load(account) : null;

            if (seed is not null)
            {
                Status("Stored key found; authenticating ...");

                AuthAttemptResult fast =
                    await AuthClient.KeyAuthAsync(account, seed)
                        .ConfigureAwait(false);

                if (fast.Succeeded)
                {
                    await HandleAuthOutcomeAsync(
                        account, seed, remember, fast.Response)
                        .ConfigureAwait(false);
                    return;
                }

                // Stored key rejected - expired (3-day server expiry) or
                // stale. Fall back to password if one was typed; otherwise
                // ask for it. Either way the bad seed's fate follows the
                // checkbox: a fresh password success re-mints and re-saves.
                if (password.Length == 0)
                {
                    Status("Stored key rejected "
                        + $"({fast.FailureReason}); enter password "
                        + "and reconnect.");
                    ReleaseForm();
                    return;
                }

                Status("Stored key rejected; trying password ...");
            }

            if (password.Length == 0)
            {
                Status("Password required.");
                ReleaseForm();
                return;
            }

            Status("Password auth ...");

            AuthAttemptResult pw =
                await AuthClient.PasswordAuthAsync(account, password)
                    .ConfigureAwait(false);

            if (!pw.Succeeded)
            {
                Status($"Login failed: {pw.FailureReason}");
                ReleaseForm();
                return;
            }

            if (string.IsNullOrEmpty(pw.Response.IssuedPrivateKey))
            {
                Status("Server returned no key on password success; "
                    + "cannot continue.");
                ReleaseForm();
                return;
            }

            // Always minted, always used; disk only if remember is on.
            seed = Convert.FromBase64String(pw.Response.IssuedPrivateKey);

            if (remember)
                SeedStore.Save(account, seed);

            Status("Key issued; authenticating ...");

            AuthAttemptResult key =
                await AuthClient.KeyAuthAsync(account, seed)
                    .ConfigureAwait(false);

            if (!key.Succeeded)
            {
                Status($"Key auth failed: {key.FailureReason}");
                ReleaseForm();
                return;
            }

            await HandleAuthOutcomeAsync(
                account, seed, remember, key.Response)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Forge] Login flow threw: {ex}");
            Status($"Login failed: {ex.Message}");
            ReleaseForm();
        }
    }

    // Converges both auth paths, exactly as the harness did: Ok starts the
    // session, NeedsCharacter runs the create flow then re-auths.
    private async Task HandleAuthOutcomeAsync(
        string account, byte[] seed, bool remember,
        AuthResponsePacket response)
    {
        switch (response.Outcome)
        {
            case AuthOutcome.Ok:
                StartUdpSession(response);
                return;

            case AuthOutcome.NeedsCharacter:
                Status("No character on this account; create one.");
                break;

            default:
                Status($"Unexpected auth outcome {response.Outcome}.");
                ReleaseForm();
                return;
        }

        CreateFlowResult created =
            await CreateClient.CreateCharacterAsync(
                account, seed, NextTypedNameAsync)
                .ConfigureAwait(false);

        Defer(() =>
        {
            _nameEdit.Visible = false;
            _nameButton.Visible = false;
        });

        if (!created.Created)
        {
            Status($"Create failed: {created.FailureReason}");
            ReleaseForm();
            return;
        }

        Status("Character created; signing in ...");

        AuthAttemptResult reauth =
            await AuthClient.KeyAuthAsync(account, seed)
                .ConfigureAwait(false);

        if (!reauth.Succeeded
            || reauth.Response.Outcome != AuthOutcome.Ok)
        {
            Status("Sign-in after create failed: "
                + (reauth.Succeeded
                    ? $"outcome {reauth.Response.Outcome}"
                    : reauth.FailureReason));
            ReleaseForm();
            return;
        }

        StartUdpSession(reauth.Response);
    }

    // The provider seam, now occupied by the player: reveal the name row,
    // hand back a task the name button completes. Called on the flow's
    // thread; all UI work defers.
    private Task<string?> NextTypedNameAsync(
        CharacterCreateOutcome? lastRejection)
    {
        var tcs = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingName = tcs;

        Defer(() =>
        {
            _statusLabel.Text = lastRejection is null
                ? "Choose a character name (3-14 lowercase letters)."
                : $"Name rejected ({lastRejection}); try another.";

            _nameEdit.Visible = true;
            _nameButton.Visible = true;
            _nameEdit.Editable = true;
            _nameButton.Disabled = false;
            _nameEdit.GrabFocus();
        });

        return tcs.Task;
    }

    // Main thread: completes the pending provider task with the typed name.
    private void OnNamePressed()
    {
        string name = _nameEdit.Text.Trim();

        if (name.Length == 0)
        {
            _statusLabel.Text = "Enter a name.";
            return;
        }

        _nameEdit.Editable = false;
        _nameButton.Disabled = true;
        _statusLabel.Text = $"Submitting \"{name}\" ...";

        _pendingName?.TrySetResult(name);
    }

    // Session start deferred to the main thread so _udpSession is only
    // ever touched there. The token's 30 s lifetime dwarfs one frame of
    // deferral. The form stays disabled - the login screen's job is done.
    private void StartUdpSession(AuthResponsePacket response)
    {
        Status($"Signed in; connecting UDP {response.UdpEndpoint} ...");

        Defer(() =>
        {
            _udpSession = new UdpSession(
                response.UdpEndpoint, response.SessionToken);
            _udpSession.Start();
        });
    }

    // ---- UI marshaling helpers -------------------------------------

    // The one door between the flow's threads and the scene tree.
    private static void Defer(Action action) =>
        Callable.From(action).CallDeferred();

    private void Status(string message)
    {
        GD.Print($"[Forge] {message}");
        Defer(() => _statusLabel.Text = message);
    }

    private void ReleaseForm() =>
        Defer(() => SetFormEnabled(true));

    // Main thread only.
    private void SetFormEnabled(bool enabled)
    {
        _serverEdit.Editable = enabled;
        _accountEdit.Editable = enabled;
        _passwordEdit.Editable = enabled;
        _rememberCheck.Disabled = !enabled;
        _connectButton.Disabled = !enabled;
    }

    private static bool TryParseServer(
        string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        int split = value.LastIndexOf(':');

        if (split <= 0 || split == value.Length - 1)
            return false;

        if (!int.TryParse(value[(split + 1)..], out port) || port <= 0)
            return false;

        host = value[..split];
        return true;
    }
}

/*
 *------------------------------------------------------------
 * (Main.cs)
 * See License.txt for licensing information.
 *-----------------------------------------------------------
 */