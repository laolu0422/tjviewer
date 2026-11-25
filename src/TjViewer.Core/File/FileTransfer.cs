using System.Security.Cryptography;
using System.Text.Json;
using TjViewer.Core.Net;
using TjViewer.Shared;

namespace TjViewer.Core.Files;

/// <summary>P2P 文件发送（见 PRD FR-08/M6）：要约 → 分块（带偏移）→ 完成。KCP 保证可靠有序。</summary>
public static class FileSender
{
    private const int ChunkSize = 32768;

    public static async Task SendFileAsync(string path, IMessageChannel channel, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("文件不存在", path);
        string sha = await ComputeSha256Async(path, ct);
        var id = Guid.NewGuid();
        var idBytes = id.ToByteArray();

        var offer = JsonSerializer.SerializeToUtf8Bytes(new FileOffer(id.ToString("N"), fi.Name, fi.Length, sha), SignalJson.Options);
        channel.Send(ChannelMux.Wrap(ChannelMsgType.File, Concat(new byte[] { FileMsgType.Offer }, offer)));

        using var fs = System.IO.File.OpenRead(path);
        var buf = new byte[ChunkSize];
        long offset = 0;
        int n;
        while ((n = await fs.ReadAsync(buf, ct)) > 0)
        {
            var chunk = new byte[1 + 16 + 8 + n];
            chunk[0] = FileMsgType.Chunk;
            idBytes.CopyTo(chunk, 1);
            BitConverter.GetBytes(offset).CopyTo(chunk, 17);
            Array.Copy(buf, 0, chunk, 25, n);
            channel.Send(ChannelMux.Wrap(ChannelMsgType.File, chunk));
            offset += n;
            progress?.Report((double)offset / fi.Length);
            while (channel.WaitSnd > 100 && !ct.IsCancellationRequested) await Task.Delay(10, ct);
        }

        var done = new byte[17];
        done[0] = FileMsgType.Done;
        idBytes.CopyTo(done, 1);
        channel.Send(ChannelMux.Wrap(ChannelMsgType.File, done));
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        using var fs = System.IO.File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }
}

/// <summary>P2P 文件接收：写入临时文件 → 完成后 SHA256 校验 → 落盘（见 PRD FR-08）。</summary>
public sealed class FileReceiver : IDisposable
{
    private sealed class State
    {
        public FileOffer Offer = null!;
        public string TempPath = "";
        public FileStream? Stream;
        public long Received;
    }

    private readonly IMessageChannel _channel;
    private readonly string _saveDir;
    private readonly Dictionary<string, State> _transfers = new();
    private readonly Action<byte[]> _onMessage;

    public event Action<string, double>? Progress;       // name, 0..1
    public event Action<string, string>? Completed;      // name, finalPath
    public event Action<string, string>? Failed;         // name, reason

    public FileReceiver(IMessageChannel channel, string saveDir)
    {
        _channel = channel;
        _saveDir = saveDir;
        Directory.CreateDirectory(saveDir);
        _onMessage = OnMessage;
        _channel.MessageReceived += _onMessage;
    }

    private void OnMessage(byte[] msg)
    {
        if (msg.Length < 2 || msg[0] != ChannelMsgType.File) return;
        var payload = msg[1..];
        byte sub = payload[0];
        try
        {
            if (sub == FileMsgType.Offer)
            {
                var offer = JsonSerializer.Deserialize<FileOffer>(payload[1..], SignalJson.Options);
                if (offer is null) return;
                var safeName = SanitizeName(offer.Name);
                var temp = Path.Combine(_saveDir, safeName + ".part");
                var st = new State { Offer = offer, TempPath = temp, Stream = System.IO.File.Create(temp) };
                _transfers[offer.TransferId] = st;
            }
            else if (sub == FileMsgType.Chunk && payload.Length >= 25)
            {
                var id = new Guid(payload[1..17]).ToString("N");
                if (!_transfers.TryGetValue(id, out var st) || st.Stream is null) return;
                long offset = BitConverter.ToInt64(payload, 17);
                var data = payload[25..];
                st.Stream.Seek(offset, SeekOrigin.Begin);
                st.Stream.Write(data, 0, data.Length);
                st.Received = Math.Max(st.Received, offset + data.Length);
                Progress?.Invoke(st.Offer.Name, (double)st.Received / st.Offer.Size);
            }
            else if (sub == FileMsgType.Done && payload.Length >= 17)
            {
                var id = new Guid(payload[1..17]).ToString("N");
                if (!_transfers.TryGetValue(id, out var st)) return;
                st.Stream?.Dispose();
                st.Stream = null;
                _transfers.Remove(id);
                var actual = FileSender.ComputeSha256Async(st.TempPath).GetAwaiter().GetResult();
                if (!string.Equals(actual, st.Offer.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    System.IO.File.Delete(st.TempPath);
                    Failed?.Invoke(st.Offer.Name, "校验不一致");
                    return;
                }
                var final = Path.Combine(_saveDir, SanitizeName(st.Offer.Name));
                System.IO.File.Move(st.TempPath, final, overwrite: true);
                Completed?.Invoke(st.Offer.Name, final);
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke("文件", ex.Message);
        }
    }

    /// <summary>防路径穿越：仅取文件名，去除目录分量（见 PRD §7.2）。</summary>
    private static string SanitizeName(string name)
    {
        var n = Path.GetFileName(name.Replace('\\', '/'));
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return string.IsNullOrWhiteSpace(n) ? "file" : n;
    }

    public void Dispose()
    {
        _channel.MessageReceived -= _onMessage;
        foreach (var st in _transfers.Values) st.Stream?.Dispose();
        _transfers.Clear();
    }
}
