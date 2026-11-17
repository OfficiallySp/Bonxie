using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Skua.Core.AppStartup;

/// <summary>
/// Messages between the tab host and the account clients it hosts (each tab is a Bonxie process of its own).
/// On Windows these are the WM_SKUA_* window messages Bonxie has always used, posted to the client's window.
/// Elsewhere there are no window messages: every client listens on a local pipe named after its process id
/// (<see cref="ClientPipeName"/>), and the host on <see cref="HostPipeName"/>, with the same message numbers.
/// </summary>
public static class TabChannel
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Sent to a client when it moves to another host window: wParam is that window's handle.</summary>
    public const int WM_SKUA_SET_HOST = 0x0400 + 466;

    /// <summary>Client to host (pipe only): the client's window is ready. wParam is its X11 window, lParam its pid.</summary>
    public const int WM_SKUA_CHILD_READY = 0x0400 + 440;

    public static bool UsesWindowMessages => OperatingSystem.IsWindows();

    /// <summary>The host's pipe; the App sets it from --host-instance.</summary>
    public static string HostPipeName { get; set; } = "BonxieTabHostPipe";

    public static string ClientPipeName(int pid) => $"BonxieTabClient-{pid}";

    private static readonly ConcurrentDictionary<int, Channel<string>> s_outboxes = new();

    /// <summary>Posts <paramref name="msg"/> to a client without waiting. Messages to one client keep their order.</summary>
    public static void Post(IntPtr hwnd, int pid, int msg, nint wParam = 0, nint lParam = 0)
    {
        if (UsesWindowMessages)
        {
            if (hwnd != IntPtr.Zero)
                PostMessage(hwnd, (uint)msg, wParam, lParam);
            return;
        }

        if (pid > 0)
            Enqueue(pid, FormatMessage(msg, wParam, lParam));
    }

    /// <summary>Runs an action by name in a client (WM_COPYDATA on Windows).</summary>
    public static void PostAction(int pid, string actionName)
    {
        if (pid > 0 && !UsesWindowMessages)
            Enqueue(pid, "A " + Convert.ToBase64String(Encoding.UTF8.GetBytes(actionName)));
    }

    /// <summary>
    /// Sends <paramref name="msg"/> to a client and waits for its answer, like SendMessage. Off Windows a client that
    /// doesn't answer within <paramref name="timeoutMs"/> gives 0.
    /// </summary>
    public static nint Send(IntPtr hwnd, int pid, int msg, nint wParam = 0, nint lParam = 0, int timeoutMs = 1500)
    {
        if (UsesWindowMessages)
            return hwnd == IntPtr.Zero ? 0 : SendMessage(hwnd, (uint)msg, wParam, lParam);

        if (pid <= 0)
            return 0;

        try
        {
            using NamedPipeClientStream pipe = new(".", ClientPipeName(pid), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(timeoutMs);
            using StreamWriter writer = new(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            using StreamReader reader = new(pipe, Encoding.UTF8);
            writer.WriteLine(FormatMessage(msg, wParam, lParam));
            Task<string?> reply = reader.ReadLineAsync();
            if (!reply.Wait(timeoutMs) || reply.Result is not string line)
                return 0;
            return long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) ? (nint)result : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Stops queuing messages for a client that has gone.</summary>
    public static void Forget(int pid)
    {
        if (s_outboxes.TryRemove(pid, out Channel<string>? outbox))
            outbox.Writer.TryComplete();
    }

    /// <summary>Posts <paramref name="msg"/> to the host this client belongs to.</summary>
    public static void PostToHost(int msg, nint wParam = 0, nint lParam = 0)
    {
        if (UsesWindowMessages)
        {
            if (HotKeys.HostHwnd != IntPtr.Zero)
                PostMessage(HotKeys.HostHwnd, (uint)msg, wParam, lParam);
            return;
        }

        string line = "MSG|" + msg.ToString(CultureInfo.InvariantCulture) + "|" + ((long)wParam).ToString(CultureInfo.InvariantCulture)
            + "|" + ((long)lParam).ToString(CultureInfo.InvariantCulture);
        _ = Task.Run(() => WriteToHost(line));
    }

    /// <summary>Off Windows: asks the host to post <paramref name="msg"/> to every tab's client (army broadcasts).</summary>
    public static void RelayToAllClients(int msg, nint wParam = 0, nint lParam = 0)
    {
        string line = "RELAY|" + msg.ToString(CultureInfo.InvariantCulture) + "|" + ((long)wParam).ToString(CultureInfo.InvariantCulture)
            + "|" + ((long)lParam).ToString(CultureInfo.InvariantCulture);
        _ = Task.Run(() => WriteToHost(line));
    }

    /// <summary>Writes one line to the host's pipe, retrying a few times while it is busy.</summary>
    public static bool WriteToHost(string line)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using NamedPipeClientStream pipe = new(".", HostPipeName, PipeDirection.Out);
                pipe.Connect(500);
                using StreamWriter writer = new(pipe, new UTF8Encoding(false));
                writer.WriteLine(line);
                writer.Flush();
                return true;
            }
            catch
            {
                if (attempt < 4)
                    Thread.Sleep(100);
            }
        }
        return false;
    }

    /// <summary>
    /// Client side, off Windows: answers the host's messages. <paramref name="onMessage"/> gets the message number,
    /// wParam and lParam and returns the answer; <paramref name="onAction"/> runs an action sent by name.
    /// </summary>
    public static void StartClientServer(Func<int, nint, nint, Task<nint>> onMessage, Action<string> onAction, CancellationToken token = default)
    {
        if (UsesWindowMessages)
            return;

        string name = ClientPipeName(Environment.ProcessId);
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    NamedPipeServerStream connection = server;
                    server = null;
                    _ = Task.Run(() => ServeClientConnection(connection, onMessage, onAction, token));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"Bonxie tab channel: {ex.GetType().Name}: {ex.Message}");
                    await Task.Delay(200).ConfigureAwait(false);
                }
                finally
                {
                    server?.Dispose();
                }
            }
        });
    }

    private static async Task ServeClientConnection(NamedPipeServerStream connection, Func<int, nint, nint, Task<nint>> onMessage, Action<string> onAction, CancellationToken token)
    {
        using (connection)
        {
            try
            {
                using StreamReader reader = new(connection, Encoding.UTF8);
                using StreamWriter writer = new(connection, new UTF8Encoding(false)) { AutoFlush = true };
                while (!token.IsCancellationRequested && await reader.ReadLineAsync(token).ConfigureAwait(false) is string line)
                {
                    long answer = 0;
                    if (TryParseMessage(line, out int msg, out nint wParam, out nint lParam))
                    {
                        try { answer = await onMessage(msg, wParam, lParam).ConfigureAwait(false); }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Bonxie tab message {msg} failed: {ex}"); }
                    }
                    else if (line.StartsWith("A ", StringComparison.Ordinal))
                    {
                        try
                        {
                            onAction(Encoding.UTF8.GetString(Convert.FromBase64String(line[2..])));
                            answer = 1;
                        }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Bonxie tab action failed: {ex}"); }
                    }

                    try { await writer.WriteLineAsync(answer.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false); }
                    catch (IOException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The host closed the connection.
            }
        }
    }

    /// <summary>
    /// Parses a "MSG|msg|wParam|lParam" (for the host) or "RELAY|msg|wParam|lParam" (for every tab) line from a
    /// client (host side, off Windows).
    /// </summary>
    public static bool TryParseHostLine(string line, out bool relay, out int msg, out nint wParam, out nint lParam)
    {
        msg = 0;
        wParam = 0;
        lParam = 0;
        relay = line.StartsWith("RELAY|", StringComparison.Ordinal);
        if (!relay && !line.StartsWith("MSG|", StringComparison.Ordinal))
            return false;
        string[] parts = line.Split('|');
        if (parts.Length < 4
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out msg)
            || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long w)
            || !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
            return false;
        wParam = (nint)w;
        lParam = (nint)l;
        return true;
    }

    private static string FormatMessage(int msg, nint wParam, nint lParam)
        => "M " + msg.ToString(CultureInfo.InvariantCulture) + " " + ((long)wParam).ToString(CultureInfo.InvariantCulture)
           + " " + ((long)lParam).ToString(CultureInfo.InvariantCulture);

    private static bool TryParseMessage(string line, out int msg, out nint wParam, out nint lParam)
    {
        msg = 0;
        wParam = 0;
        lParam = 0;
        if (!line.StartsWith("M ", StringComparison.Ordinal))
            return false;
        string[] parts = line.Split(' ');
        if (parts.Length < 4
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out msg)
            || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long w)
            || !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
            return false;
        wParam = (nint)w;
        lParam = (nint)l;
        return true;
    }

    // One connection per client, kept open; messages wait in order while it (re)connects.
    private static void Enqueue(int pid, string line)
    {
        Channel<string> outbox = s_outboxes.GetOrAdd(pid, p =>
        {
            Channel<string> created = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            _ = Task.Run(() => PumpOutbox(p, created.Reader));
            return created;
        });
        outbox.Writer.TryWrite(line);
    }

    private static async Task PumpOutbox(int pid, ChannelReader<string> reader)
    {
        NamedPipeClientStream? pipe = null;
        StreamWriter? writer = null;
        StreamReader? replies = null;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out string? line))
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            if (pipe is null || !pipe.IsConnected)
                            {
                                writer?.Dispose();
                                replies?.Dispose();
                                pipe?.Dispose();
                                pipe = new NamedPipeClientStream(".", ClientPipeName(pid), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                                await pipe.ConnectAsync(2000).ConfigureAwait(false);
                                writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                                replies = new StreamReader(pipe, Encoding.UTF8);
                            }

                            await writer!.WriteLineAsync(line).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException or InvalidOperationException)
                        {
                            pipe?.Dispose();
                            pipe = null;
                            await Task.Delay(150).ConfigureAwait(false);
                            continue;
                        }

                        // Reading the answer keeps the client's replies from piling up in the pipe. The message has
                        // been delivered either way, so a client that is slow to answer doesn't get it twice.
                        try
                        {
                            using CancellationTokenSource answerTimeout = new(TimeSpan.FromSeconds(5));
                            await replies!.ReadLineAsync(answerTimeout.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                        {
                            pipe?.Dispose();
                            pipe = null;
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Bonxie tab channel to {pid}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            writer?.Dispose();
            replies?.Dispose();
            pipe?.Dispose();
        }
    }
}
