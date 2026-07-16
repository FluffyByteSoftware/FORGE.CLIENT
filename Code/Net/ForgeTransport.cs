/*
 * (ForgeTransport.cs)
 *------------------------------------------------------------
 * Created - 7/14/2026
 * Created by - Seliris
 *-------------------------------------------------------------
 */

using LiteNetLib.Utils;
using Networking.Tcp;
using Shared.Networking;
using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace ForgeClient.Code.Net;

/// <summary>
/// Owns the client's TLS/TCP transport to the LoginServer: the connection
/// endpoint, the one-shot framed send/receive the auth flow uses, and the
/// dev-only certificate trust callback.
/// </summary>
/// <remarks>Client mirror of the Probe's ProbeTransport, which is the
/// verified specification for this path. One connection per call - open TLS,
/// write one frame, read one frame, close - because the LoginServer closes
/// after every auth attempt. The held-open character-create exchange does not
/// belong here and will stand up its own connection when that brick lands.
/// </remarks>
internal static class ForgeTransport
{
    // MUST match the LoginServer's network.json BindAddress/Port. Dev value;
    // becomes configuration when the client grows a config story.
    internal const string ServerHost = "10.0.0.84";

    // MUST match the "Port" in the LoginServer's network.json.
    internal const int ServerPort = 9997;

    // SNI target only - with accept-all validation it never has to match the
    // dev certificate's CN.
    internal const string TargetHost = "Stratum";

    // Opens a fresh TLS connection, sends one framed packet, reads exactly
    // one framed response, and closes. Mirrors ProbeTransport verbatim.
    internal static async Task<(uint typeId, byte[] payload)>
        SendAndReceiveAsync<T>(T packet)
        where T : struct, IPacketWritable
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ServerHost, ServerPort).ConfigureAwait(false);

        using var ssl = new SslStream(
            tcp.GetStream(),
            leaveInnerStreamOpen: false,
            userCertificateValidationCallback: AcceptAnyServerCertificate);

        var sslOptions = new SslClientAuthenticationOptions
        {
            TargetHost = TargetHost,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };

        await ssl.AuthenticateAsClientAsync(sslOptions).ConfigureAwait(false);

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

    // DEV-ONLY TRUST HOLE: accepts any server certificate, exactly like the
    // Probe against the self-signed dev cert. A shipped client must validate
    // or pin. This callback is the single place that decision lives, so the
    // fix is one function - but it MUST happen before anything public.
    internal static bool AcceptAnyServerCertificate(
        object sender,
#nullable enable
        X509Certificate? certificate,
        X509Chain? chain,
#nullable disable
        SslPolicyErrors errors) => true;
}

/*
 *------------------------------------------------------------
 * (ForgeTransport.cs)
 * See License.txt for licensing information.
 *-----------------------------------------------------------
 */