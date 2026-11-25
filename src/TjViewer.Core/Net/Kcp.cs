namespace TjViewer.Core.Net;

/// <summary>
/// KCP 可靠 UDP 协议（ikcp 算法 C# 移植，见 PRD FR-09 / M1）。
/// 低延迟 ARQ：快速重传 + 选择性 ACK + 拥塞窗口。单线程调用 Update/Input/Recv/Send。
/// </summary>
public class Kcp
{
    public const uint IKCP_RTO_NDL = 30;
    public const uint IKCP_RTO_MIN = 100;
    public const uint IKCP_RTO_DEF = 200;
    public const uint IKCP_RTO_MAX = 60000;
    public const byte IKCP_CMD_PUSH = 81;
    public const byte IKCP_CMD_ACK = 82;
    public const byte IKCP_CMD_WASK = 83;
    public const byte IKCP_CMD_WINS = 84;
    public const uint IKCP_ASK_SEND = 1;
    public const uint IKCP_ASK_TELL = 2;
    public const uint IKCP_WND_SND = 32;
    public const uint IKCP_WND_RCV = 32;
    public const uint IKCP_MTU_DEF = 1400;
    public const uint IKCP_ACK_FAST = 3;
    public const uint IKCP_INTERVAL = 100;
    public const uint IKCP_OVERHEAD = 24;
    public const uint IKCP_DEADLINK = 20;
    public const uint IKCP_THRESH_INIT = 2;
    public const uint IKCP_THRESH_MIN = 2;
    public const uint IKCP_PROBE_INIT = 7000;
    public const uint IKCP_PROBE_LIMIT = 120000;

    private class Segment
    {
        public uint conv, cmd, frg, wnd, ts, sn, una, len;
        public uint resendts, rto, fastack, xmit;
        public byte[] data = Array.Empty<byte>();
        public int Size => (int)len;
    }

    public uint Conv { get; }
    private uint _mtu = IKCP_MTU_DEF, _mss = IKCP_MTU_DEF - IKCP_OVERHEAD;
    private uint _state;
    private uint _sndUna, _sndNxt, _rcvNxt;
    private uint _ssthresh = IKCP_THRESH_INIT;
    private uint _rxRttvar, _rxSrtt, _rxMinrto = IKCP_RTO_MIN;
    private uint _sndWnd = IKCP_WND_SND, _rcvWnd = IKCP_WND_RCV, _rmtWnd = IKCP_WND_RCV;
    private uint _cwnd, _probe;
    private uint _interval = IKCP_INTERVAL, _tsFlush = IKCP_INTERVAL;
    private uint _nodelay, _updated;
    private uint _tsProbe, _probeWait = IKCP_PROBE_INIT;
    private uint _deadLink = IKCP_DEADLINK, _incr;
    private uint _current;
    private readonly List<Segment> _sndQueue = new(), _rcvQueue = new(), _sndBuf = new(), _rcvBuf = new();
    private readonly List<uint> _acklist = new();
    private Action<byte[], int>? _output;
    private byte[] _buffer;

    public Kcp(uint conv)
    {
        Conv = conv;
        _buffer = new byte[_mtu];
    }

    public void SetOutput(Action<byte[], int> output) => _output = output;

    public int NoDelay(int nodelay, int interval, int resend, int nc)
    {
        if (nodelay >= 0)
        {
            _nodelay = (uint)nodelay;
            _rxMinrto = nodelay != 0 ? IKCP_RTO_NDL : IKCP_RTO_MIN;
        }
        if (interval >= 0) _interval = (uint)Math.Clamp(interval, 5, 5000);
        if (resend >= 0) _fastResend = (uint)resend;
        if (nc >= 0) _noCongestion = nc != 0;
        return 0;
    }

    /// <summary>收发窗口大小。默认 32 在 10Mbps / 长 RTT 下会限制吞吐，媒体通道需放大。</summary>
    public int WndSize(uint snd, uint rcv)
    {
        if (snd > 0) _sndWnd = snd;
        if (rcv > 0) _rcvWnd = rcv;
        return 0;
    }
    private uint _fastResend;
    private bool _noCongestion;

    private static uint Ibound(uint lower, uint middle, uint upper) => Math.Min(Math.Max(lower, middle), upper);
    private static int Itimediff(uint later, uint earlier) => unchecked((int)(later - earlier));

    private void Output(byte[] data, int size)
    {
        if (size <= 0 || _output is null) return;
        _output(data, size);
    }

    private static void EncodeSeg(byte[] ptr, ref int offset, Segment seg)
    {
        WriteU32(ptr, ref offset, seg.conv);
        ptr[offset++] = (byte)seg.cmd;
        ptr[offset++] = (byte)seg.frg;
        WriteU16(ptr, ref offset, (ushort)seg.wnd);
        WriteU32(ptr, ref offset, seg.ts);
        WriteU32(ptr, ref offset, seg.sn);
        WriteU32(ptr, ref offset, seg.una);
        WriteU32(ptr, ref offset, seg.len);
    }

    private static void WriteU32(byte[] p, ref int o, uint v) { p[o++] = (byte)v; p[o++] = (byte)(v >> 8); p[o++] = (byte)(v >> 16); p[o++] = (byte)(v >> 24); }
    private static void WriteU16(byte[] p, ref int o, ushort v) { p[o++] = (byte)v; p[o++] = (byte)(v >> 8); }
    private static uint ReadU32(byte[] p, ref int o) { uint v = (uint)(p[o] | (p[o + 1] << 8) | (p[o + 2] << 16) | (p[o + 3] << 24)); o += 4; return v; }
    private static ushort ReadU16(byte[] p, ref int o) { ushort v = (ushort)(p[o] | (p[o + 1] << 8)); o += 2; return v; }

    /// <summary>发送应用数据（自动分片）。返回 0 成功。</summary>
    public int Send(byte[] data)
    {
        if (data.Length == 0) return -1;
        int count = data.Length <= (int)_mss ? 1 : (data.Length + (int)_mss - 1) / (int)_mss;
        if (count >= 255) return -2;
        for (int i = 0; i < count; i++)
        {
            int size = Math.Min(data.Length - i * (int)_mss, (int)_mss);
            var seg = new Segment { len = (uint)size, frg = (uint)(count - i - 1), data = new byte[size] };
            Array.Copy(data, i * (int)_mss, seg.data, 0, size);
            _sndQueue.Add(seg);
        }
        return 0;
    }

    /// <summary>接收一个完整应用消息。返回长度，-1 无数据，-2 缓冲不足，-3 分片不全。</summary>
    public int Recv(byte[] buffer)
    {
        if (_rcvQueue.Count == 0) return -1;
        int peek = PeekSize();
        if (peek < 0) return -3;
        if (peek > buffer.Length) return -2;
        bool recover = _rcvQueue.Count >= _rcvWnd;
        int offset = 0;
        // 合并分片
        var merged = new List<Segment>();
        foreach (var seg in _rcvQueue)
        {
            merged.Add(seg);
            if (seg.frg == 0) break;
        }
        foreach (var seg in merged)
        {
            Array.Copy(seg.data, 0, buffer, offset, seg.data.Length);
            offset += seg.data.Length;
            _rcvQueue.Remove(seg);
        }
        // 从 rcv_buf 移入 rcv_queue
        while (_rcvBuf.Count > 0)
        {
            var seg = _rcvBuf[0];
            if (seg.sn == _rcvNxt && _rcvQueue.Count < _rcvWnd)
            {
                _rcvBuf.RemoveAt(0);
                _rcvQueue.Add(seg);
                _rcvNxt++;
            }
            else break;
        }
        if (_rcvQueue.Count < _rcvWnd && recover) _probe |= IKCP_ASK_TELL;
        return offset;
    }

    public int PeekSize()
    {
        if (_rcvQueue.Count == 0) return -1;
        var first = _rcvQueue[0];
        if (first.frg == 0) return first.data.Length;
        if (_rcvQueue.Count < first.frg + 1) return -1;
        int length = 0;
        foreach (var seg in _rcvQueue)
        {
            length += seg.data.Length;
            if (seg.frg == 0) break;
        }
        return length;
    }

    /// <summary>输入底层 UDP 数据报。</summary>
    public int Input(byte[] data, int size)
    {
        if (size < (int)IKCP_OVERHEAD) return -1;
        int offset = 0;
        uint prevUna = _sndUna;
        bool flag = false;
        uint maxSeg = 0;
        while (size - offset >= (int)IKCP_OVERHEAD)
        {
            uint conv = ReadU32(data, ref offset);
            byte cmd = data[offset++];
            byte frg = data[offset++];
            ushort wnd = ReadU16(data, ref offset);
            uint ts = ReadU32(data, ref offset);
            uint sn = ReadU32(data, ref offset);
            uint una = ReadU32(data, ref offset);
            uint len = ReadU32(data, ref offset);
            if (conv != Conv) return -2;
            if (size - offset < len) return -3;
            if (cmd != IKCP_CMD_PUSH && cmd != IKCP_CMD_ACK && cmd != IKCP_CMD_WASK && cmd != IKCP_CMD_WINS) return -4;
            _rmtWnd = wnd;
            ParseUna(una);
            ShrinkBuf();
            if (cmd == IKCP_CMD_ACK)
            {
                if (Itimediff(_current, ts) >= 0) UpdateAck(Itimediff(_current, ts));
                ParseAck(sn);
                ShrinkBuf();
                if (!flag) { flag = true; maxSeg = sn; }
                else if (Itimediff(sn, maxSeg) > 0) maxSeg = sn;
            }
            else if (cmd == IKCP_CMD_PUSH)
            {
                if (Itimediff(sn, _rcvNxt + _rcvWnd) < 0)
                {
                    AckPush(sn, ts);
                    if (Itimediff(sn, _rcvNxt) >= 0)
                    {
                        var seg = new Segment { conv = conv, cmd = cmd, frg = frg, wnd = wnd, ts = ts, sn = sn, una = una, len = len, data = new byte[len] };
                        if (len > 0) Array.Copy(data, offset, seg.data, 0, (int)len);
                        ParseData(seg);
                    }
                }
            }
            else if (cmd == IKCP_CMD_WASK) _probe |= IKCP_ASK_TELL;
            else if (cmd == IKCP_CMD_WINS) { }
            offset += (int)len;
        }
        if (flag) ParseFastack(maxSeg);
        if (Itimediff(_sndUna, prevUna) > 0 && _cwnd < _rmtWnd)
        {
            uint mss = _mss;
            if (_cwnd < _ssthresh) { _cwnd++; _incr += mss; }
            else
            {
                if (_incr < mss) _incr = mss;
                _incr += (mss * mss) / _incr + (mss / 16);
                if ((_cwnd + 1) * mss <= _incr) _cwnd++;
            }
            if (_cwnd > _rmtWnd) { _cwnd = _rmtWnd; _incr = _rmtWnd * mss; }
        }
        return 0;
    }

    private void AckPush(uint sn, uint ts) { _acklist.Add(sn); _acklist.Add(ts); }

    private void ParseUna(uint una)
    {
        for (int i = 0; i < _sndBuf.Count;)
        {
            if (Itimediff(una, _sndBuf[i].sn) > 0) _sndBuf.RemoveAt(i);
            else break;
        }
    }

    private void ParseAck(uint sn)
    {
        if (Itimediff(sn, _sndUna) < 0 || Itimediff(sn, _sndNxt) >= 0) return;
        for (int i = 0; i < _sndBuf.Count;)
        {
            if (sn == _sndBuf[i].sn) { _sndBuf.RemoveAt(i); break; }
            if (Itimediff(sn, _sndBuf[i].sn) < 0) break;
            i++;
        }
    }

    private void ParseFastack(uint sn)
    {
        if (Itimediff(sn, _sndUna) < 0 || Itimediff(sn, _sndNxt) >= 0) return;
        foreach (var seg in _sndBuf)
        {
            if (Itimediff(sn, seg.sn) < 0) break;
            if (sn != seg.sn) seg.fastack++;
        }
    }

    private void ParseData(Segment newseg)
    {
        uint sn = newseg.sn;
        if (Itimediff(sn, _rcvNxt + _rcvWnd) >= 0 || Itimediff(sn, _rcvNxt) < 0) return;
        bool insert = false;
        int idx = _rcvBuf.Count;
        for (int i = _rcvBuf.Count - 1; i >= 0; i--)
        {
            var seg = _rcvBuf[i];
            if (seg.sn == sn) { insert = false; break; }
            if (Itimediff(sn, seg.sn) > 0) { insert = true; idx = i + 1; break; }
            idx = i;
            insert = true;
        }
        if (_rcvBuf.Count == 0) { insert = true; idx = 0; }
        if (insert) _rcvBuf.Insert(idx, newseg);
        // 移入 rcv_queue
        while (_rcvBuf.Count > 0)
        {
            var seg = _rcvBuf[0];
            if (seg.sn == _rcvNxt && _rcvQueue.Count < _rcvWnd)
            {
                _rcvBuf.RemoveAt(0);
                _rcvQueue.Add(seg);
                _rcvNxt++;
            }
            else break;
        }
    }

    private void ShrinkBuf()
    {
        if (_sndBuf.Count > 0) _sndUna = _sndBuf[0].sn;
        else _sndUna = _sndNxt;
    }

    private void UpdateAck(int rtt)
    {
        if (rtt < 0) return;
        if (_rxSrtt == 0) { _rxSrtt = (uint)rtt; _rxRttvar = (uint)(rtt / 2); }
        else
        {
            int delta = rtt - (int)_rxSrtt;
            if (delta < 0) delta = -delta;
            _rxRttvar = (3 * _rxRttvar + (uint)delta) / 4;
            _rxSrtt = (7 * _rxSrtt + (uint)rtt) / 8;
            if (_rxSrtt < 1) _rxSrtt = 1;
        }
        uint rto = _rxSrtt + Math.Max(_interval, 4 * _rxRttvar);
        _rxMinrto = Ibound(IKCP_RTO_MIN, rto, IKCP_RTO_MAX);
    }

    /// <summary>时钟驱动（毫秒）。需周期调用（如每 10–100ms）。</summary>
    public void Update(uint current)
    {
        _current = current;
        if (_updated == 0) { _updated = 1; _tsFlush = _current; }
        int slap = Itimediff(_current, _tsFlush);
        if (slap >= 10000 || slap < -10000)
        {
            _tsFlush = _current;
            slap = 0;
        }
        if (slap >= 0)
        {
            _tsFlush += _interval;
            if (Itimediff(_current, _tsFlush) >= 0) _tsFlush = _current + _interval;
            Flush();
        }
    }

    public uint Check(uint current)
    {
        if (_updated == 0) return current;
        int tsFlush = Itimediff(current, _tsFlush);
        if (tsFlush >= 10000 || tsFlush < -10000) tsFlush = 0;
        return tsFlush <= 0 ? current : (uint)tsFlush;
    }

    private void Flush()
    {
        if (_updated == 0) return;
        uint current = _current;
        var buffer = _buffer;
        int offset = 0;
        uint count = 0;

        // flush ACK
        for (int i = 0; i < _acklist.Count; i += 2)
        {
            if (offset + (int)IKCP_OVERHEAD > _mtu) { Output(buffer, offset); offset = 0; }
            var seg = new Segment { conv = Conv, cmd = IKCP_CMD_ACK, wnd = WndUnused(), ts = _acklist[i + 1], sn = _acklist[i], una = _rcvNxt };
            EncodeSeg(buffer, ref offset, seg);
            count++;
        }
        _acklist.Clear();

        // probe window size
        if (_rmtWnd == 0)
        {
            if (_probeWait == 0) { _probeWait = IKCP_PROBE_INIT; _tsProbe = _current + _probeWait; }
            else if (Itimediff(_current, _tsProbe) >= 0)
            {
                if (_probeWait < IKCP_PROBE_INIT) _probeWait = IKCP_PROBE_INIT;
                _probeWait += _probeWait / 2;
                if (_probeWait > IKCP_PROBE_LIMIT) _probeWait = IKCP_PROBE_LIMIT;
                _tsProbe = _current + _probeWait;
                _probe |= IKCP_ASK_SEND;
            }
        }
        else { _tsProbe = 0; _probeWait = 0; }

        if ((_probe & IKCP_ASK_SEND) != 0)
        {
            if (offset + (int)IKCP_OVERHEAD > _mtu) { Output(buffer, offset); offset = 0; }
            EncodeSeg(buffer, ref offset, new Segment { conv = Conv, cmd = IKCP_CMD_WASK, wnd = WndUnused(), una = _rcvNxt });
        }
        if ((_probe & IKCP_ASK_TELL) != 0)
        {
            if (offset + (int)IKCP_OVERHEAD > _mtu) { Output(buffer, offset); offset = 0; }
            EncodeSeg(buffer, ref offset, new Segment { conv = Conv, cmd = IKCP_CMD_WINS, wnd = WndUnused(), una = _rcvNxt });
        }
        _probe = 0;

        // calculate window size
        uint cwnd = Math.Min(_sndWnd, _rmtWnd);
        if (!_noCongestion) cwnd = Math.Min(_cwnd, cwnd);
        uint newSegsCount = 0;

        // move snd_queue -> snd_buf
        while (Itimediff(_sndNxt, _sndUna + cwnd) < 0 && _sndQueue.Count > 0)
        {
            var seg = _sndQueue[0];
            _sndQueue.RemoveAt(0);
            seg.conv = Conv;
            seg.cmd = IKCP_CMD_PUSH;
            seg.wnd = WndUnused();
            seg.ts = current;
            seg.sn = _sndNxt++;
            seg.una = _rcvNxt;
            seg.resendts = current;
            seg.rto = _rxMinrto;
            seg.fastack = 0;
            seg.xmit = 0;
            _sndBuf.Add(seg);
            newSegsCount++;
        }

        uint resent = _fastResend > 0 ? _fastResend : uint.MaxValue;
        uint rtomin = _nodelay == 0 ? (_rxMinrto >> 3) : 0;
        int lost = 0;
        bool change = false;

        // flush snd_buf
        foreach (var seg in _sndBuf)
        {
            bool needsend = false;
            if (seg.xmit == 0)
            {
                needsend = true;
                seg.xmit++;
                seg.rto = _rxMinrto;
                seg.resendts = current + seg.rto + rtomin;
            }
            else if (Itimediff(current, seg.resendts) >= 0)
            {
                needsend = true;
                seg.xmit++;
                if (_nodelay == 0) seg.rto += _rxMinrto;
                else seg.rto += _rxMinrto / 2;
                seg.resendts = current + seg.rto;
                lost = 1;
            }
            else if (seg.fastack >= resent)
            {
                needsend = true;
                seg.xmit++;
                seg.fastack = 0;
                seg.resendts = current + seg.rto;
                change = true;
            }
            if (needsend)
            {
                seg.ts = current;
                seg.wnd = WndUnused();
                seg.una = _rcvNxt;
                int need = (int)IKCP_OVERHEAD + seg.data.Length;
                if (offset + need > _mtu) { Output(buffer, offset); offset = 0; }
                EncodeSeg(buffer, ref offset, seg);
                if (seg.data.Length > 0) { Array.Copy(seg.data, 0, buffer, offset, seg.data.Length); offset += seg.data.Length; }
                if (seg.xmit >= _deadLink) _state = 0xffffffff;
            }
        }
        if (offset > 0) { Output(buffer, offset); offset = 0; }

        // congestion control
        if (change)
        {
            uint inflight = _sndNxt - _sndUna;
            _ssthresh = inflight / 2;
            if (_ssthresh < IKCP_THRESH_MIN) _ssthresh = IKCP_THRESH_MIN;
            _cwnd = _ssthresh + resent;
            _incr = _cwnd * _mss;
        }
        if (lost != 0)
        {
            _ssthresh = cwnd / 2;
            if (_ssthresh < IKCP_THRESH_MIN) _ssthresh = IKCP_THRESH_MIN;
            _cwnd = 1;
            _incr = _mss;
        }
        if (_cwnd < 1) { _cwnd = 1; _incr = _mss; }
    }

    private ushort WndUnused()
    {
        uint n = _rcvWnd > (uint)_rcvQueue.Count ? _rcvWnd - (uint)_rcvQueue.Count : 0;
        return (ushort)Math.Min(n, 65535);
    }

    public uint WaitSnd => (uint)(_sndBuf.Count + _sndQueue.Count);
    public bool IsDead => _state == 0xffffffff;
}
