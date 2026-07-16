/*
 * (UdpSession.cs)
 *------------------------------------------------------------
 * Created - 7/14/2026
 * Created by - Seliris
 *-------------------------------------------------------------
 */

using Godot;
using LiteNetLib;
using LiteNetLib.Utils;
using Shared.Networking;
using Shared.Networking.Packets.Auth;
using Shared.Networking.Packets.Comparable;
using Shared.Networking.Packets.Diagnostics;
using Shared.Networking.Packets.LifeCycle;
using System;
using System.Buffers.Binary;
using System.Threading;

namespace ForgeClient.Code.Net;

/// <summary>
/// The client's UDP session against Sentinel: presents the session token as
/// connection-request data, walks the admission sequence (auth ack, version
/// challenge/result), proves the keep-alive and gameplay-channel echoes,
/// and then holds the session open until stopped.
/// </summary>
/// <remarks>Client mirror of the Probe's UdpLegs with the two deliberate
/// departures a real client requires. First, polling runs on a dedicated
/// background thread (PollEvents + 15 ms sleep, mirroring UdpHost) rather
/// than a bounded loop - and never in _Process, where a main-thread load
/// stall would starve keep-alives and false-kick the peer at
/// DisconnectTimeout. Second, the session connects to the endpoint the auth
/// response advertised rather than a hardcoded one. All event callbacks run
/// on the poll thread: GD.Print is safe there, the scene tree is not.
/// </remarks>
internal sealed class UdpSession
{
    private readonly NetManager _client;
    private readonly string _token;
    private readonly string _host;
    private readonly int _port;

    private Thread? _pollThread;
    private volatile bool _running;

    // Echo-verification state, written and read on the poll thread only.
    private long _sentPingTimestampMs;
    private long _sentDiagNonce;

    /// <summary>
    /// Parses an advertised "host:port" endpoint and constructs a session
    /// ready to connect with the given single-use token (30 s lifetime -
    /// connect promptly after auth).
    /// </summary>
    internal UdpSession(string advertisedEndpoint, string token)
    {
        int split = advertisedEndpoint.LastIndexOf(':');

        if (split <= 0 || split == advertisedEndpoint.Length - 1)
            throw new ArgumentException(
                $"Malformed UDP endpoint \"{advertisedEndpoint}\".",
                nameof(advertisedEndpoint));

        _host = advertisedEndpoint[..split];
        _port = int.Parse(advertisedEndpoint[(split + 1)..]);
        _token = token;

        var listener = new EventBasedNetListener();
        _client = new NetManager(listener);

        listener.PeerConnectedEvent += _ =>
            GD.Print("[Forge] UDP connected; awaiting auth ack.");

        listener.PeerDisconnectedEvent += (_, info) =>
            GD.Print($"[Forge] UDP disconnected: {info.Reason}.");

        listener.NetworkReceiveEvent += OnReceive;
    }

    /// <summary>
    /// Connects to Sentinel and starts the dedicated poll thread. The
    /// admission sequence then runs event-driven on that thread.
    /// </summary>
    internal void Start()
    {
        GD.Print($"[Forge] UDP connecting to {_host}:{_port} ...");

        _client.Start();
        _client.Connect(_host, _port, _token);

        _running = true;
        _pollThread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "ForgeUdpPoll",
        };
        _pollThread.Start();
    }

    /// <summary>
    /// Stops polling and tears the connection down. Safe to call more than
    /// once; called from _ExitTree so quitting closes the session clean.
    /// </summary>
    internal void Stop()
    {
        if (!_running)
            return;

        _running = false;
        _pollThread?.Join();
        _client.Stop();

        GD.Print("[Forge] UDP session stopped.");
    }

    // The load-bearing loop: mirrors UdpHost's cadence. Nothing here ever
    // runs on Godot's main thread, so a load stall cannot starve the peer.
    private void PollLoop()
    {
        while (_running)
        {
            _client.PollEvents();
            Thread.Sleep(15);
        }
    }

    // One handler, dispatched on the 4-byte big-endian type header - read
    // via BinaryPrimitives, never reader.GetUInt. Runs on the poll thread.
    private void OnReceive(
        NetPeer peer, NetPacketReader reader, byte channel,
        DeliveryMethod deliveryMethod)
    {
        byte[] data = reader.GetRemainingBytes();
        reader.Recycle();

        if (data.Length < sizeof(uint))
        {
            GD.PrintErr(
                $"[Forge] UDP packet too short ({data.Length}); dropping.");
            return;
        }

        uint typeId = BinaryPrimitives.ReadUInt32BigEndian(data);

        var payload = new NetDataReader();
        // SetSource maxSize is absolute, not a count from offset:
        // AvailableBytes = maxSize - position. Pass data.Length, or every
        // read underflows.
        payload.SetSource(data, sizeof(uint), data.Length);

        if (typeId == MessagePacketIds.AuthMessage.UdpAuthAck)
        {
            HandleAuthAck(payload);
            return;
        }

        if (typeId == MessagePacketIds.AuthMessage.VersionChallenge)
        {
            HandleVersionChallenge(peer, payload);
            return;
        }

        if (typeId == MessagePacketIds.AuthMessage.VersionResult)
        {
            HandleVersionResult(peer, payload);
            return;
        }

        if (typeId == MessagePacketIds.LifeCycleMessage.Pong)
        {
            HandlePong(peer, payload);
            return;
        }

        if (typeId == MessagePacketIds.ZoneDataMessage.Pong)
        {
            HandleDiagnosticPong(payload);
            return;
        }

        GD.PrintErr($"[Forge] Unexpected UDP type 0x{typeId:X8}; dropping.");
    }

    private static void HandleAuthAck(NetDataReader payload)
    {
        if (payload.AvailableBytes < 1)
        {
            GD.PrintErr("[Forge] Auth ack payload too short; dropping.");
            return;
        }

        var result = (UdpAuthResult)payload.GetByte();

        if (result == UdpAuthResult.Authenticated)
            GD.Print("[Forge] UDP session authenticated; "
                + "awaiting version challenge.");
        else
            GD.PrintErr($"[Forge] UDP auth result: {result}.");
    }

    private static void HandleVersionChallenge(NetPeer peer, NetDataReader payload)
    {
        string serverVersion = payload.GetString();

        GD.Print($"[Forge] Version challenge: server={serverVersion}; "
            + $"responding with {GameProtocolVersion.Current}.");

        Send(peer, new VersionResponsePacket
        {
            Version = GameProtocolVersion.Current,
        });
    }

    private void HandleVersionResult(NetPeer peer, NetDataReader payload)
    {
        if (payload.AvailableBytes < 1)
        {
            GD.PrintErr("[Forge] Version result payload too short.");
            return;
        }

        var result = (VersionResult)payload.GetByte();

        if (result != VersionResult.Ok)
        {
            // Mismatch: Sentinel is already tearing this session down.
            GD.PrintErr($"[Forge] Version result: {result}.");
            return;
        }

        GD.Print("[Forge] Protocol version accepted; session live.");

        // Session confirmed: fire one keep-alive ping. Single-shot echo
        // proof, exactly like the Probe - recurring cadence stays a filed
        // decision (LiteNetLib's own keep-alive owns liveness).
        _sentPingTimestampMs =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Send(peer, new PingPacket(_sentPingTimestampMs));

        GD.Print($"[Forge] Ping sent (ts={_sentPingTimestampMs}); "
            + "awaiting echo.");
    }

    private void HandlePong(NetPeer peer, NetDataReader payload)
    {
        if (payload.AvailableBytes < sizeof(long))
        {
            GD.PrintErr("[Forge] Pong payload too short; dropping.");
            return;
        }

        long echoed = payload.GetLong();

        if (echoed != _sentPingTimestampMs)
        {
            GD.PrintErr($"[Forge] Pong mismatch: sent "
                + $"{_sentPingTimestampMs}, got {echoed}.");
            return;
        }

        long rttMs =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - echoed;

        GD.Print($"[Forge] Keep-alive echo verified (rtt~{rttMs} ms).");

        // Chain the gameplay-channel diagnostic, mirroring the Probe: a
        // random nonce, not a timestamp - this proves 0x03 routing, and a
        // random value makes echo-by-luck vanishingly unlikely.
        _sentDiagNonce = Random.Shared.NextInt64();

        Send(peer, new GameDiagnosticPingPacket(_sentDiagNonce));

        GD.Print($"[Forge] Diagnostic ping sent (nonce={_sentDiagNonce}); "
            + "awaiting 0x03 echo.");
    }

    private void HandleDiagnosticPong(NetDataReader payload)
    {
        if (payload.AvailableBytes < sizeof(long))
        {
            GD.PrintErr("[Forge] Diagnostic pong too short; dropping.");
            return;
        }

        long echoed = payload.GetLong();

        if (echoed != _sentDiagNonce)
        {
            GD.PrintErr($"[Forge] Diagnostic mismatch: sent "
                + $"{_sentDiagNonce}, got {echoed}.");
            return;
        }

        GD.Print("[Forge] Gameplay channel diagnostic verified. "
            + "Full chain complete; session held open.");
    }

    // Serializes and sends one packet with the 4-byte big-endian type
    // header, mirroring UdpHost.Send / the Probe's SendUdpPacket.
    private static void Send<T>(NetPeer peer, T packet)
        where T : struct, IPacketWritable
    {
        var writer = new NetDataWriter();

        byte[] typeHeader = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(typeHeader, packet.TypeId);
        writer.Put(typeHeader);

        packet.Serialize(writer);

        peer.Send(writer, 0, DeliveryMethod.ReliableOrdered);
    }
}

/*
 *------------------------------------------------------------
 * (UdpSession.cs)
 * See License.txt for licensing information.
 *-----------------------------------------------------------
 */