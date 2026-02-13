using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Organik.Net;

class PacketDataException : IOException { }

enum PacketStatusCode
{
    OK,
    INVALID_ID,
    INVALID_SIZE,
    DATA_ERROR,
    CONNECTION_CLOSED,
    SENDER_UNKNOWN,
    EXCEPTION
}

public class Packet
{
    private byte[] _data;
    private byte _id;

    public byte[] Data { get { return _data; } }
    public byte ID { get { return _id; } }

    public Packet(UdpReceiveResult incomingData)
    {
        _id = incomingData.Buffer[0];
        if (incomingData.Buffer.Length < 2)
        {
            _data = [];
            return;
        }
        _data = [.. incomingData.Buffer.TakeLast(incomingData.Buffer.Length - 1)];
    }

    public static implicit operator Packet(UdpReceiveResult incomingData)
    {
        return new Packet(incomingData);
    }

    public static implicit operator Packet(Task<UdpReceiveResult> incomingTask)
    {
        if (!incomingTask.IsCompleted) incomingTask.Wait();
        return incomingTask.Result;
    }
}

public class UdpHandler : UdpClient
{
    public enum HandlerState
    {
        READY = 0,
        LISTENING = 1,
        CONNECTING = 2,
        CONNECTED = 3,
        DISCONNECTED = 4,
        ERROR = 5
    };

    // Delegate for packet handlers
    public delegate Task PacketHandler(UdpHandler handler, Packet packet);

    // Dictionary to store packet ID -> handler mappings
    private readonly ConcurrentDictionary<byte, PacketHandler> _packetHandlers = new();

    // Dictionary to store active connections (endpoint -> handler instance)
    private static readonly ConcurrentDictionary<string, UdpHandler> ActiveConnections = new();

    protected readonly CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(1000), TimeProvider.System);
    private CancellationToken Token
    {
        get
        {
            var tk = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000), TimeProvider.System);
            var token = tk.Token;
            token.ThrowIfCancellationRequested();
            return token;
        }
    }

    private IPEndPoint? _remoteEndPoint;
    public IPEndPoint? RemoteEndPoint => _remoteEndPoint;

    public HandlerState State { get; protected set; }

    protected UdpHandler() : base(AddressFamily.InterNetwork)
    {
        this.ExclusiveAddressUse = false;
        State = HandlerState.READY;
    }

    // Async enumerable for listening and parsing packets
    private async IAsyncEnumerable<Packet> ListenAndParse([EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        while (!token.IsCancellationRequested && Client != null)
        {
            UdpReceiveResult? result = null;
            try
            {
                result = await ReceiveAsync(token);
                _remoteEndPoint = result?.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                continue;
            }
            yield return new Packet(result ?? new([], new(IPAddress.None, 0)));
        }
    }

    // Method to register packet handlers
    public void RegisterPacketHandler(byte packetId, PacketHandler handler)
    {
        _packetHandlers[packetId] = handler;
    }

    // Method to register multiple handlers at once
    public void RegisterPacketHandlers(Dictionary<byte, PacketHandler> handlers)
    {
        foreach (var kvp in handlers)
        {
            _packetHandlers[kvp.Key] = kvp.Value;
        }
    }

    // Main listening method that processes packets
    public async Task ListenAsync()
    {
        if (State != HandlerState.READY && State != HandlerState.CONNECTED)
            throw new InvalidOperationException("Handler is not in valid state");

        State = HandlerState.LISTENING;

        try
        {
            await foreach (var packet in ListenAndParse(Token))
            {
                // Process packet with registered handler
                if (_packetHandlers.TryGetValue(packet.ID, out var handler))
                {
                    try
                    {
                        await handler(this, packet);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error in packet handler for ID {packet.ID}: {ex.Message}");
                        State = HandlerState.ERROR;
                    }
                }
                else
                {
                    Console.WriteLine($"No handler registered for packet ID {packet.ID} from {RemoteEndPoint}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in listener: {ex.Message}");
            State = HandlerState.ERROR;
        }
        finally
        {
            State = HandlerState.DISCONNECTED;
            if (_remoteEndPoint != null)
            {
                var key = $"{_remoteEndPoint.Address}:{_remoteEndPoint.Port}";
                ActiveConnections.TryRemove(key, out _);
            }
        }
    }

    // Send packet back to the connected client
    public async Task SendPacketAsync(byte packetId, byte[] data)
    {
        if (_remoteEndPoint == null)
            throw new InvalidOperationException("No client connected");

        var packet = new byte[data.Length + (packetId > 4 ? 5 : 1)];
        packet[0] = packetId;
        unsafe
        {
            int* ptr = (int*)packet[0x1];
            *ptr = (Int32)(DateTime.Now.Ticks % Int32.MaxValue);
        }
        ;
        Array.Copy(data, 0, packet, 1, data.Length);

        await SendAsync(packet, packet.Length, _remoteEndPoint);
    }

    private async Task PrependInt32(Int32 toPrepend)
    {

    }
    
    public static async Task HandleIncomingConnections(
        int port,
        Dictionary<byte, PacketHandler> packetHandlers,
        CancellationToken cancellationToken = default)
    {
        var mainListener = new UdpHandler();

        try
        {
            mainListener.Client.Bind(new DnsEndPoint("api.amyseni.space", port));
            mainListener.State = HandlerState.LISTENING;

            Console.WriteLine($"Listening on port {port}");
            cancellationToken.ThrowIfCancellationRequested();
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var result = await mainListener.ReceiveAsync(cancellationToken);
                    var remoteEndPoint = result.RemoteEndPoint;
                    var connectionKey = $"{remoteEndPoint.Address}:{remoteEndPoint.Port}";

                    // Check if we already have a handler for this connection
                    if (!ActiveConnections.ContainsKey(connectionKey))
                    {
                        Console.WriteLine($"New connection from {remoteEndPoint}");

                        // Create new handler for this specific connection
                        var connectionHandler = new UdpHandler();
                        connectionHandler.RegisterPacketHandlers(packetHandlers);
                        connectionHandler._remoteEndPoint = remoteEndPoint;
                        connectionHandler.State = HandlerState.CONNECTED;

                        // Add to active connections
                        ActiveConnections[connectionKey] = connectionHandler;

                        // Process the initial packet
                        var packet = new Packet(result);
                        if (packetHandlers.TryGetValue(packet.ID, out var handler))
                        {
                            await handler(connectionHandler, packet);
                        }

                        // Start listening for more packets from this connection
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await connectionHandler.ListenAsync();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Connection handler error: {ex.Message}");
                            }
                            finally
                            {
                                ActiveConnections.TryRemove(connectionKey, out _);
                                Console.WriteLine($"Connection closed for {remoteEndPoint}");
                            }
                        }, cancellationToken);
                    }
                    else
                    {
                        // Forward to existing connection handler
                        var existingHandler = ActiveConnections[connectionKey];
                        var packet = new Packet(result);
                        if (packetHandlers.TryGetValue(packet.ID, out var handler))
                        {
                            await handler(existingHandler, packet);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        Console.WriteLine($"Error handling incoming connection on port {port}: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            mainListener.Dispose();
            Console.WriteLine($"Stopped listening on port {port}");
        }
    }
}

public static class Extensions
{
    public static void Map(this IEnumerable objects, Delegate @delegate)
    {
        var enumerator = objects.GetEnumerator();
        while (enumerator!.MoveNext())
        {
            @delegate.DynamicInvoke(enumerator?.Current);
        }
    }
    public static IEnumerable<T> Expand<T>(this Range range) where T : INumber<T>, IComparisonOperators<T, Byte, bool>, IComparable<Byte>, new()
    {
        var list = new List<T>();
        (var offset, var length) = range.GetOffsetAndLength(range.End.Value - range.Start.Value);

        for (int i = 0; i < offset + length; i++)
        {
            list.Add(T.Parse(i.ToString(), NumberStyles.Integer, CultureInfo.CurrentCulture));
        }
        return list.AsEnumerable();
    }
}


public static Delegate MapChatHandler = async delegate (UdpHandler.PacketHandler handler)
    {
        int i = 0;
        handler += (h, p) => h.PrependInt32(h.Incoming)
        switch (i)
        {
            case 0:
                handler = new((h, p) => h.SendPacketAsync(0, System.Text.Encoding.ASCII.GetBytes("24\x00")));
                break;
            case 1:
                handler = (h, p) => { Console.WriteLine($"{DateTime.Now.ToString("hh:mm:ss.fff tt")} => {h.Client.RemoteEndPoint}"); return Task.Run(() => Task.Delay(10)); };
                break;
            case 2:
                handler = (h, p) => Task.Run(() => Task.Delay(10));
                break;
            case 3:
                break;
            case 4:
                handler = (h, p) => h.SendPacketAsync(0x03, []);
                break;
            case 5:
                break;
            case 6:
                break;
            case 7:
                break;
            case 8:
                break;
            case 9:
                break;
            case 10:
                break;
            case 11:
                break;
            case 12:
                break;
            case 13:
                break;
            case 14:
                break;
            case 15:
                break;
            case 16:
                break;
            case 17:
                break;
            case 18:
                break;
            case 19:
                break;
            case 20:
                break;
            case 21:
                break;
            case 22:
                break;
            case 23:
                break;
            case 24:
                break;
            case 25:
                break;
            case 26:
                break;
            case 27:
                break;
            case 28:
                break;
            case 29:
                break;
            case 30:
                break;
            case 31:
                break;
            case 32:
                break;
            case 33:
                break;

        }
        i += 1;
    };
// Example usage program
class Program
{

    static async Task Main(string[] args)
    {
        var ports = new int[] { 17108, 17115, 17126 };

        Console.WriteLine($"UDP Server starting on ports: {string.Join(", ", ports)}");
        Console.WriteLine("Press Ctrl+C to stop");

        var cts = new CancellationTokenSource();

        // Set up packet handlers
        var packetHandlers = new List<UdpHandler.PacketHandler>(50);
        var i = 0;

        packetHandlers[0x00] = (handler, packet) => handler.SendPacketAsync(0x00, System.Text.Encoding.ASCII.GetBytes("24\x00"));
        packetHandlers[0x01] = (handler, packet) => handler.SendPacketAsync(0x01, []);
        packetHandlers[0x02] = (handler, packet) => handler.SendPacketAsync(0x01, []);
        packetHandlers[0x03] = (handler, packet) => handler.SendPacketAsync(0x04, []);
        packetHandlers[0x04] = (handler, packet) => Task.Run(() => { });
        packetHandlers[9] = (handler, packet) => handler.SendPacketAsync(0x01, []);

        // Start listeners for each port
        var listenerTasks = new List<Task>();
        foreach (var port in ports)
        {
            var ctk = new CancellationTokenSource(200).Token;
            ctk.ThrowIfCancellationRequested();
            var task = Task.Run(async () =>
            {
                await UdpHandler.HandleIncomingConnections(port, (
                    new List<byte>((0..49).Expand<byte>()).AsEnumerable().ToList().Zip(
                        packetHandlers.AsEnumerable()).Aggregate(new Dictionary<byte, UdpHandler.PacketHandler>(),
                            (dict, hndl) =>
                            {
                                dict[hndl.First] = hndl.Second;
                                return dict;
                            }
                        )
                    )
                );
            }, ctk);
            listenerTasks.Add(task);
        }

        // Handle graceful shutdown
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutting down...");
            cts.Cancel();
        };

        try
        {
            await Task.WhenAll(listenerTasks);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Server shutdown complete");
        }
    }

    static async Task HandleMessagePacket(Packet packet)
    {
        var message = System.Text.Encoding.UTF8.GetString(packet.Data);
        Console.WriteLine($"Message packet received: {message}");
    }

    static async Task HandlePingPacket(Packet packet)
    {
        Console.WriteLine($"Ping packet received");
    }
}