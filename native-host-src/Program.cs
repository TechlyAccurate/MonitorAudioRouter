using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MonitorAudioRouterNativeHost;

internal static class Program
{
    private const string PipeName = "MonitorAudioRouterHints";
    private const int TrayConnectTimeoutMs = 500;
    private const string BrowserBridgeTokenFileName = "browser-bridge.token";
    private const string AppDataFolderName = "Monitor Audio Router";
    private static readonly TimeSpan InitialMessageTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan IdleMessageTimeout = TimeSpan.FromSeconds(10);

    private static int Main()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        var receivedMessage = false;

        while (true)
        {
            var message = ReadMessage(input, receivedMessage ? IdleMessageTimeout : InitialMessageTimeout);
            if (message is null)
            {
                return 0;
            }

            receivedMessage = true;
            var forwarded = ForwardToTray(message);

            if (!TryWriteMessage(output, JsonSerializer.Serialize(new { ok = forwarded })))
            {
                return forwarded ? 0 : 1;
            }

            if (!forwarded)
            {
                return 1;
            }
        }
    }

    private static string? ReadMessage(Stream input, TimeSpan timeout)
    {
        var lengthBytes = ReadExact(input, 4, timeout);
        if (lengthBytes is null)
        {
            return null;
        }

        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length <= 0 || length > 1024 * 1024)
        {
            LogThrottled(
                "native-host-rejected-length",
                $"Native host rejected message length {length}.",
                TimeSpan.FromMinutes(5));
            return null;
        }

        var payload = ReadExact(input, length, timeout);
        if (payload is null)
        {
            Log($"Native host could not read payload length {length}.");
            return null;
        }

        return Encoding.UTF8.GetString(payload);
    }

    private static byte[]? ReadExact(Stream stream, int count, TimeSpan timeout)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            int read;
            try
            {
                var readTask = Task.Run(() => stream.Read(buffer, offset, count - offset));
                if (!readTask.Wait(timeout))
                {
                    return null;
                }

                read = readTask.GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }

            if (read == 0)
            {
                return null;
            }

            offset += read;
        }

        return buffer;
    }

    private static bool ForwardToTray(string json)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(TrayConnectTimeoutMs);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(BrowserBridgeSecurity.CreateEnvelope(json));
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogThrottled(
                "native-host-forward-failed:" + ex.Message,
                $"Native host forward failed: {ex.Message}",
                TimeSpan.FromMinutes(5));
            return false;
        }
    }

    private static bool TryWriteMessage(Stream output, string json)
    {
        try
        {
            WriteMessage(output, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteMessage(Stream output, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var length = BitConverter.GetBytes(payload.Length);
        output.Write(length, 0, length.Length);
        output.Write(payload, 0, payload.Length);
        output.Flush();
    }

    private static void Log(string message)
    {
        try
        {
            RollingLog.Write(message);
        }
        catch
        {
            // Native messaging stdout must remain protocol-clean.
        }
    }

    private static void LogThrottled(string key, string message, TimeSpan interval)
    {
        try
        {
            RollingLog.WriteThrottled(key, message, interval);
        }
        catch
        {
            // Native messaging stdout must remain protocol-clean.
        }
    }

    private static string UserDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppDataFolderName);

    private static string BrowserBridgeTokenFile => Path.Combine(UserDataRoot, BrowserBridgeTokenFileName);

    private static class BrowserBridgeSecurity
    {
        private const string EnvelopeType = "browserHintEnvelope";
        private const int TokenByteCount = 32;
        private const string TokenMutexName = @"Local\MonitorAudioRouterBrowserBridgeToken";
        private static readonly object LockObject = new();
        private static string? _token;

        public static string CreateEnvelope(string payloadJson)
        {
            return JsonSerializer.Serialize(new BrowserBridgeEnvelope
            {
                Type = EnvelopeType,
                Token = GetToken(),
                Payload = payloadJson
            });
        }

        private static string GetToken()
        {
            lock (LockObject)
            {
                if (!string.IsNullOrWhiteSpace(_token))
                {
                    return _token;
                }

                using var mutex = new Mutex(false, TokenMutexName);
                var acquired = false;
                try
                {
                    try
                    {
                        acquired = mutex.WaitOne(TimeSpan.FromSeconds(2));
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }

                    if (!acquired)
                    {
                        throw new TimeoutException("Timed out waiting for browser bridge token lock.");
                    }

                    Directory.CreateDirectory(UserDataRoot);
                    MigrateTokenIfMissing();
                    if (File.Exists(BrowserBridgeTokenFile))
                    {
                        var existing = File.ReadAllText(BrowserBridgeTokenFile).Trim();
                        if (existing.Length >= 32)
                        {
                            _token = existing;
                            return _token;
                        }
                    }

                    _token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenByteCount));
                    WriteToken(BrowserBridgeTokenFile, _token);
                    return _token;
                }
                finally
                {
                    if (acquired)
                    {
                        try
                        {
                            mutex.ReleaseMutex();
                        }
                        catch
                        {
                            // Best effort only.
                        }
                    }
                }
            }
        }

        private static void WriteToken(string path, string token)
        {
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, token, Encoding.UTF8);
            File.Move(tempPath, path, overwrite: true);
        }

        private static void MigrateTokenIfMissing()
        {
            if (File.Exists(BrowserBridgeTokenFile))
            {
                return;
            }

            var oldPath = Path.Combine(AppContext.BaseDirectory, BrowserBridgeTokenFileName);
            if (!File.Exists(oldPath))
            {
                return;
            }

            try
            {
                File.Copy(oldPath, BrowserBridgeTokenFile);
            }
            catch
            {
                // A new token can be generated if migration fails.
            }
        }
    }

    private sealed class BrowserBridgeEnvelope
    {
        public string? Type { get; set; }
        public string? Token { get; set; }
        public string? Payload { get; set; }
    }
}

internal static class RollingLog
{
    private const int MaxMessageChars = 4 * 1024;
    private const long MaxLogBytes = 1024 * 1024;
    private const long RetainedLogBytes = 768 * 1024;
    private const string MutexName = @"Local\MonitorAudioRouterLog";
    private static readonly object LockObject = new();
    private static readonly object ThrottleLockObject = new();
    private static readonly Dictionary<string, DateTimeOffset> LastThrottledWrites = new(StringComparer.OrdinalIgnoreCase);

    public static void Write(string message)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:O} {SanitizeMessage(message)}{Environment.NewLine}";
            var entryBytes = Encoding.UTF8.GetBytes(line);
            lock (LockObject)
            {
                WriteEntry(entryBytes);
            }
        }
        catch
        {
            // Native messaging stdout must remain protocol-clean.
        }
    }

    public static void WriteThrottled(string key, string message, TimeSpan interval)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            lock (ThrottleLockObject)
            {
                if (LastThrottledWrites.TryGetValue(key, out var previous) &&
                    now - previous < interval)
                {
                    return;
                }

                LastThrottledWrites[key] = now;
            }

            Write(message);
        }
        catch
        {
            // Native messaging stdout must remain protocol-clean.
        }
    }

    private static void WriteEntry(byte[] entryBytes)
    {
        using var mutex = new Mutex(false, MutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(1));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                return;
            }

            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Monitor Audio Router",
                "router.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            TrimIfNeeded(logPath, entryBytes.Length);
            using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write(entryBytes, 0, entryBytes.Length);
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch
                {
                    // Best effort only.
                }
            }
        }
    }

    private static string SanitizeMessage(string? message)
    {
        message ??= "";
        message = message.Replace("\r", "\\r").Replace("\n", "\\n");
        return message.Length <= MaxMessageChars
            ? message
            : message[..MaxMessageChars] + "... [truncated]";
    }

    private static void TrimIfNeeded(string logPath, int incomingBytes)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        var info = new FileInfo(logPath);
        if (info.Length + incomingBytes <= MaxLogBytes)
        {
            return;
        }

        var keepBytes = Math.Min(RetainedLogBytes, info.Length);
        var tail = new byte[(int)keepBytes];
        var read = 0;
        using (var readStream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            readStream.Seek(-keepBytes, SeekOrigin.End);
            while (read < tail.Length)
            {
                var count = readStream.Read(tail, read, tail.Length - read);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }
        }

        var start = FindFirstCompleteLineStart(tail, read);
        var retainedLength = Math.Max(0, read - start);
        var header = Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:O} Log trimmed; retained last {retainedLength} bytes in single-file rolling log.{Environment.NewLine}");
        using var writeStream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writeStream.Write(header, 0, header.Length);
        if (retainedLength > 0)
        {
            writeStream.Write(tail, start, retainedLength);
        }
    }

    private static int FindFirstCompleteLineStart(byte[] buffer, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (buffer[i] == (byte)'\n')
            {
                return i + 1;
            }
        }

        return 0;
    }
}
