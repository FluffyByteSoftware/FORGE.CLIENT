/*
 * (CreateClient.cs)
 *------------------------------------------------------------
 * Created - 7/14/2026
 * Created by - Seliris
 *-------------------------------------------------------------
 */

using LiteNetLib.Utils;
using Networking.Tcp;
using Shared.Networking;
using Shared.Networking.Packets.Auth;
using Shared.Networking.Packets.Character;
using Shared.Networking.Packets.LifeCycle;
using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;

namespace ForgeClient.Code.Net;

/// <summary>
/// Supplies the next character name to try. Called first with null (no prior
/// rejection), then again after each retryable rejection (NameInvalid,
/// NameTaken) with that outcome. Return null to give up and close.
/// </summary>
/// <remarks>This seam is why the client's create flow exceeds the Probe's:
/// the contract requires in-place retry on an open connection. The harness
/// feeds it a canned list; the login UI will await player input.</remarks>
internal delegate Task<string?> NextNameProvider(
    CharacterCreateOutcome? lastRejection);

/// <summary>
/// The result of one character-create flow: created, or a reason it stopped.
/// </summary>
internal readonly struct CreateFlowResult
{
    internal bool Created { get; }
    internal string FailureReason { get; }

    private CreateFlowResult(bool created, string failureReason)
    {
        Created = created;
        FailureReason = failureReason;
    }

    internal static CreateFlowResult Ok() =>
        new(true, string.Empty);

    internal static CreateFlowResult Fail(string reason) =>
        new(false, reason);
}

/// <summary>
/// The client's character-create flow: a held-open TLS connection that
/// key-auths, confirms NeedsCharacter, then sends create requests on the
/// same stream - retrying in place on retryable rejections - until the
/// character is created or the provider gives up.
/// </summary>
/// <remarks>Client mirror of the Probe's CreateLeg, extended with the retry
/// loop the wire contract requires of a real client. This is the one flow
/// that cannot use ForgeTransport's one-shot helper: create is multiple
/// exchanges on a single stream, because the server resolves the owning
/// account from the connection NeedsCharacter registered, never from the
/// packet. Framing is carried locally, exactly as CreateLeg carries its
/// own. On Created the server closes; the caller re-auths with the same
/// seed to mint its token through the single verified issuance path.
/// </remarks>
internal static class CreateClient
{
    /// <summary>
    /// Runs the full create flow. Returns Created only when the server
    /// confirmed the character; the caller then re-authenticates.
    /// </summary>
    internal static async Task<CreateFlowResult> CreateCharacterAsync(
        string accountId, byte[] seed, NextNameProvider nameProvider)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(
            ForgeTransport.ServerHost, ForgeTransport.ServerPort)
            .ConfigureAwait(false);

        using var ssl = new SslStream(
            tcp.GetStream(),
            leaveInnerStreamOpen: false,
            userCertificateValidationCallback:
                ForgeTransport.AcceptAnyServerCertificate);

        var sslOptions = new SslClientAuthenticationOptions
        {
            TargetHost = ForgeTransport.TargetHost,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };

        await ssl.AuthenticateAsClientAsync(sslOptions)
            .ConfigureAwait(false);

        // Key auth on the held connection. NeedsCharacter registers this
        // connection for the creates that follow, so the stream stays open.
        AuthByKeyPacket authPacket =
            AuthClient.BuildKeyAuthPacket(accountId, seed);
        await SendFramedAsync(ssl, authPacket).ConfigureAwait(false);

        var (authTypeId, authPayload) =
            await ReadFramedAsync(ssl).ConfigureAwait(false);

        string? authFailure = ExpectNeedsCharacter(authTypeId, authPayload);

        if (authFailure is not null)
            return CreateFlowResult.Fail(authFailure);

        // The retry loop: retryable rejections keep the connection open and
        // ask the provider again; anything else resolves the flow.
        string? name = await nameProvider(null).ConfigureAwait(false);

        while (name is not null)
        {
            var createPacket = new CharacterCreateRequestPacket(name);
            await SendFramedAsync(ssl, createPacket).ConfigureAwait(false);

            var (typeId, payload) =
                await ReadFramedAsync(ssl).ConfigureAwait(false);

            var (resolved, outcome, failure) =
                InterpretCreateResponse(typeId, payload);

            if (resolved)
                return failure is null
                    ? CreateFlowResult.Ok()
                    : CreateFlowResult.Fail(failure);

            // Retryable: same stream, next candidate from the provider.
            name = await nameProvider(outcome).ConfigureAwait(false);
        }

        return CreateFlowResult.Fail(
            "Name provider gave up; no character created.");
    }

    // Confirms the held connection's auth answered NeedsCharacter - the
    // only outcome that leaves the stream open for creates. Returns null
    // to proceed, or the failure description.
    private static string? ExpectNeedsCharacter(uint typeId, byte[] payload)
    {
        var reader = new NetDataReader();
        reader.SetSource(payload, 0, payload.Length);

        if (typeId == DisconnectPacket.TypeId)
        {
            DisconnectPacket disconnect =
                DisconnectPacket.Deserialize(reader);
            return "Auth on create connection rejected: "
                + $"{disconnect.Reason}.";
        }

        if (typeId != AuthResponsePacket.TypeId)
            return $"Unexpected auth response type 0x{typeId:X8}.";

        AuthResponsePacket response =
            AuthResponsePacket.Deserialize(reader);

        if (response.Outcome == AuthOutcome.NeedsCharacter)
            return null;

        return "Expected NeedsCharacter on create connection; got "
            + $"{response.Outcome}.";
    }

    // Resolves one create response. resolved=false means retryable - the
    // outcome rides so the provider can react. resolved=true with a null
    // failure is Created; any other resolution carries its reason.
    private static (bool resolved, CharacterCreateOutcome outcome,
        string? failure) InterpretCreateResponse(uint typeId, byte[] payload)
    {
        var reader = new NetDataReader();
        reader.SetSource(payload, 0, payload.Length);

        if (typeId == DisconnectPacket.TypeId)
        {
            DisconnectPacket disconnect =
                DisconnectPacket.Deserialize(reader);
            return (true, CharacterCreateOutcome.None,
                "Create connection closed before a response: "
                + $"{disconnect.Reason}.");
        }

        if (typeId != CharacterCreateResponsePacket.TypeId)
            return (true, CharacterCreateOutcome.None,
                $"Unexpected create response type 0x{typeId:X8}.");

        CharacterCreateResponsePacket response =
            CharacterCreateResponsePacket.Deserialize(reader);

        return response.Outcome switch
        {
            CharacterCreateOutcome.Created =>
                (true, response.Outcome, null),

            CharacterCreateOutcome.NameInvalid or
            CharacterCreateOutcome.NameTaken =>
                (false, response.Outcome, null),

            _ => (true, response.Outcome,
                $"Create outcome was {response.Outcome}."),
        };
    }

    // Self-contained frame write over the held stream, mirroring CreateLeg:
    // the verified one-shot transport stays untouched.
    private static async Task SendFramedAsync<T>(SslStream ssl, T packet)
        where T : struct, IPacketWritable
    {
        var writer = new NetDataWriter();
        packet.Serialize(writer);

        int payloadLength = writer.Length;
        int frameSize = PacketFramer.FrameSize(payloadLength);
        byte[] frame = new byte[frameSize];

        PacketFramer.WriteFrame(
            packet.TypeId, writer.Data.AsSpan(0, payloadLength), frame);

        await ssl.WriteAsync(frame.AsMemory(0, frameSize))
            .ConfigureAwait(false);
        await ssl.FlushAsync().ConfigureAwait(false);
    }

    // Self-contained frame read over the held stream: 8-byte header, then
    // exactly the declared payload length.
    private static async Task<(uint typeId, byte[] payload)>
        ReadFramedAsync(SslStream ssl)
    {
        byte[] header = new byte[PacketFramer.HeaderSize];
        await ssl.ReadExactlyAsync(header.AsMemory()).ConfigureAwait(false);

        if (!PacketFramer.TryReadHeader(
                header, out uint typeId, out int responseLength))
            throw new InvalidOperationException(
                "Malformed response header from server.");

        byte[] payload = new byte[responseLength];

        if (responseLength > 0)
            await ssl.ReadExactlyAsync(payload.AsMemory())
                .ConfigureAwait(false);

        return (typeId, payload);
    }
}

/*
 *------------------------------------------------------------
 * (CreateClient.cs)
 * See License.txt for licensing information.
 *-----------------------------------------------------------
 */