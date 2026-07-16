/*
 * (AuthClient.cs)
 *------------------------------------------------------------
 * Created - 7/14/2026
 * Created by - Seliris
 *-------------------------------------------------------------
 */

using LiteNetLib.Utils;
using Shared.Networking.Packets.Auth;
using Shared.Networking.Packets.LifeCycle;
using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using SystemTools.Security;

namespace ForgeClient.Code.Net;

/// <summary>
/// The result of one authentication attempt against the LoginServer:
/// either a parsed <see cref="AuthResponsePacket"/>, or a failure
/// description when the server rejected or answered unexpectedly.
/// </summary>
/// <remarks>Response and FailureReason are mutually exclusive; exactly one
/// is meaningful per instance, discriminated by <see cref="Succeeded"/>.
/// Note that Succeeded means "got a well-formed AuthResponsePacket" - the
/// caller still switches on its Outcome (Ok vs NeedsCharacter).</remarks>
internal readonly struct AuthAttemptResult
{
    internal bool Succeeded { get; }
    internal AuthResponsePacket Response { get; }
    internal string FailureReason { get; }

    private AuthAttemptResult(
        bool succeeded, AuthResponsePacket response, string failureReason)
    {
        Succeeded = succeeded;
        Response = response;
        FailureReason = failureReason;
    }

    internal static AuthAttemptResult Ok(AuthResponsePacket response) =>
        new(true, response, string.Empty);

    internal static AuthAttemptResult Fail(string reason) =>
        new(false, default, reason);
}

/// <summary>
/// The client's password-authentication leg against the LoginServer.
/// </summary>
/// <remarks>Client mirror of the Probe's AuthLegs password path. Each call
/// is one complete TLS exchange via <see cref="ForgeTransport"/> - the
/// server closes after every auth attempt. Key auth arrives as its own
/// brick once seed storage exists.</remarks>
internal static class AuthClient
{
    /// <summary>
    /// Authenticates with account id and password. Returns the parsed
    /// response, or a failure describing the rejection or unexpected reply.
    /// </summary>
    internal static async Task<AuthAttemptResult> PasswordAuthAsync(
        string accountId, string password)
    {
        var packet = new AuthByPasswordPacket(accountId, password);

        var (typeId, payload) =
            await ForgeTransport.SendAndReceiveAsync(packet)
                .ConfigureAwait(false);

        return InterpretResponse(typeId, payload);
    }

    /// <summary>
    /// Authenticates with the account's stored Ed25519 seed: signs the
    /// current timestamp and sends it. The primary auth path once a
    /// password login has minted a seed.
    /// </summary>
    internal static async Task<AuthAttemptResult> KeyAuthAsync(
        string accountId, byte[] seed)
    {
        AuthByKeyPacket packet = BuildKeyAuthPacket(accountId, seed);

        var (typeId, payload) =
            await ForgeTransport.SendAndReceiveAsync(packet)
                .ConfigureAwait(false);

        return InterpretResponse(typeId, payload);
    }

    // Mirrors AuthLegs.BuildKeyAuthPacket exactly: one place stamps, signs,
    // and constructs, so the one-shot path and the future held-open create
    // path cannot drift. The same timestamp is both signed and sent - the
    // server re-derives the signed bytes from the packet's UnixTimestampMs.
    internal static AuthByKeyPacket BuildKeyAuthPacket(
        string accountId, byte[] seed)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        byte[] signature = SignTimestamp(seed, now);

        return new AuthByKeyPacket(accountId, now, signature);
    }

    // Sync helper: stackalloc is illegal in an async method, and the signed
    // message is the 8-byte big-endian timestamp the server verifies.
    private static byte[] SignTimestamp(byte[] seed, long unixTimestampMs)
    {
        Span<byte> message = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(message, unixTimestampMs);

        return Ed25519MessageSigner.Sign(seed, message);
    }

    // Mirrors AuthLegs.InterpretResponse: AuthResponsePacket parses through,
    // DisconnectPacket becomes a rejection with the server's reason, and any
    // other type id is reported loudly rather than guessed at.
    private static AuthAttemptResult InterpretResponse(
        uint typeId, byte[] payload)
    {
        var reader = new NetDataReader();
        reader.SetSource(payload, 0, payload.Length);

        if (typeId == AuthResponsePacket.TypeId)
            return AuthAttemptResult.Ok(
                AuthResponsePacket.Deserialize(reader));

        if (typeId == DisconnectPacket.TypeId)
        {
            DisconnectPacket disconnect =
                DisconnectPacket.Deserialize(reader);
            return AuthAttemptResult.Fail(
                $"Rejected by server: {disconnect.Reason}");
        }

        return AuthAttemptResult.Fail(
            $"Unexpected response type 0x{typeId:X8}.");
    }
}

/*
 *------------------------------------------------------------
 * (AuthClient.cs)
 * See License.txt for licensing information.
 *-----------------------------------------------------------
 */