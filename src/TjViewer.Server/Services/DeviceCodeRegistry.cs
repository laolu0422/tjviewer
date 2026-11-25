using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TjViewer.Server.Data;
using TjViewer.Shared;

namespace TjViewer.Server.Services;

/// <summary>
/// 陌生设备连接码注册表：每个设备生成「观看」「控制」两套独立的
/// 9 位数字连接码 + 8 位字母数字验证码，连接码由服务端统一分配避免冲突。
/// 固定连接码持久化到 SystemConfig；验证码仅内存保存，使用成功后轮换。
/// </summary>
public sealed class DeviceCodeRegistry
{
    private const string FixedPrefix = "code.fixed.";
    private const int MaxFails = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromSeconds(60);

    private sealed class Entry
    {
        public bool Fixed;
        public string WatchCode = "", WatchPassword = "";
        public string ControlCode = "", ControlPassword = "";
    }

    private sealed class FixedPair
    {
        public string Watch = "", Control = "";
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _byDevice = new();
    private readonly Dictionary<string, (string DeviceId, string Mode)> _byCode = new();
    private readonly Dictionary<string, (int Fails, DateTime Until)> _attempts = new();
    private readonly Dictionary<string, FixedPair> _persistedFixed = new();
    private readonly HashSet<string> _reservedCodes = new();
    private readonly IServiceScopeFactory _scopes;
    private bool _fixedLoaded;

    public DeviceCodeRegistry(IServiceScopeFactory scopes) => _scopes = scopes;

    private async Task EnsureFixedLoadedAsync()
    {
        if (_fixedLoaded) return;
        List<SystemConfig> list;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            list = await db.SystemConfigs.Where(c => c.Key.StartsWith(FixedPrefix)).ToListAsync();
        }
        lock (_lock)
        {
            if (_fixedLoaded) return;
            foreach (var c in list)
            {
                var parts = c.Key.Split('.');
                if (parts.Length != 4 || c.Value.Length != 9) continue;
                var dev = parts[2];
                var mode = parts[3];
                if (!_persistedFixed.TryGetValue(dev, out var p)) _persistedFixed[dev] = p = new FixedPair();
                if (mode == "watch") p.Watch = c.Value; else if (mode == "control") p.Control = c.Value;
                _reservedCodes.Add(c.Value);
            }
            _fixedLoaded = true;
        }
    }

    /// <summary>获取设备连接码；首次调用或切换固定/动态模式时分配，之后保持稳定。</summary>
    public async Task<DeviceCodes> EnsureAsync(string deviceId, bool fixedCode)
    {
        await EnsureFixedLoadedAsync();
        List<(string Key, string Value)> persist = new();
        DeviceCodes result;

        lock (_lock)
        {
            if (!_byDevice.TryGetValue(deviceId, out var e)) _byDevice[deviceId] = e = new Entry();

            if (e.WatchCode.Length == 0 || e.Fixed != fixedCode)
            {
                RemoveCodesLocked(e);
                e.Fixed = fixedCode;
                string watch, control;
                if (fixedCode && _persistedFixed.TryGetValue(deviceId, out var p) && p.Watch.Length == 9 && p.Control.Length == 9)
                {
                    watch = p.Watch;
                    control = p.Control;
                }
                else
                {
                    watch = NewCodeLocked();
                    control = NewCodeLocked();
                    if (fixedCode)
                    {
                        if (!_persistedFixed.TryGetValue(deviceId, out var fp)) _persistedFixed[deviceId] = fp = new FixedPair();
                        fp.Watch = watch;
                        fp.Control = control;
                        persist.Add(($"{FixedPrefix}{deviceId}.watch", watch));
                        persist.Add(($"{FixedPrefix}{deviceId}.control", control));
                    }
                }
                e.WatchCode = watch;
                e.ControlCode = control;
                _byCode[watch] = (deviceId, "watch");
                _byCode[control] = (deviceId, "control");
                _reservedCodes.Add(watch);
                _reservedCodes.Add(control);
            }

            if (e.WatchPassword.Length == 0) e.WatchPassword = NewPassword();
            if (e.ControlPassword.Length == 0) e.ControlPassword = NewPassword();
            result = new DeviceCodes(e.WatchCode, e.WatchPassword, e.ControlCode, e.ControlPassword, e.Fixed);
        }

        if (persist.Count > 0)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var (key, value) in persist)
            {
                var cfg = await db.SystemConfigs.FindAsync(key);
                if (cfg is null) db.SystemConfigs.Add(new SystemConfig { Key = key, Value = value });
                else cfg.Value = value;
            }
            await db.SaveChangesAsync();
        }
        return result;
    }

    /// <summary>验证连接码与验证码；失败会累计次数并临时锁定。</summary>
    public bool TryConsume(string code, string password, out string deviceId, out string mode, out string error)
    {
        deviceId = ""; mode = ""; error = "";
        code = (code ?? "").Trim();
        password = (password ?? "").Trim();
        if (code.Length != 9 || !code.All(char.IsDigit)) { error = "连接码格式不正确"; return false; }
        if (password.Length != 8) { error = "验证码格式不正确"; return false; }

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_attempts.TryGetValue(code, out var a) && a.Until > now)
            {
                error = "尝试次数过多，请 1 分钟后再试";
                return false;
            }
            if (!_byCode.TryGetValue(code, out var map) || !_byDevice.TryGetValue(map.DeviceId, out var e))
            {
                Fail(code, now);
                error = "连接码无效或已失效";
                return false;
            }
            var expected = map.Mode == "watch" ? e.WatchPassword : e.ControlPassword;
            if (!SafeEquals(expected, password))
            {
                Fail(code, now);
                error = "验证码错误";
                return false;
            }
            _attempts.Remove(code);
            deviceId = map.DeviceId;
            mode = map.Mode;
            return true;
        }
    }

    /// <summary>连接成功后轮换指定模式的验证码（看/控分别轮换）。</summary>
    public DeviceCodes? Rotate(string deviceId, string mode)
    {
        lock (_lock)
        {
            if (!_byDevice.TryGetValue(deviceId, out var e)) return null;
            if (mode == "watch") e.WatchPassword = NewPassword();
            else e.ControlPassword = NewPassword();
            return new DeviceCodes(e.WatchCode, e.WatchPassword, e.ControlCode, e.ControlPassword, e.Fixed);
        }
    }

    private void Fail(string code, DateTime now)
    {
        _attempts.TryGetValue(code, out var a);
        var fails = a.Fails + 1;
        _attempts[code] = (fails, fails >= MaxFails ? now.Add(Lockout) : now);
    }

    private void RemoveCodesLocked(Entry e)
    {
        if (e.WatchCode.Length > 0) _byCode.Remove(e.WatchCode);
        if (e.ControlCode.Length > 0) _byCode.Remove(e.ControlCode);
    }

    private string NewCodeLocked()
    {
        for (int i = 0; i < 200; i++)
        {
            var code = RandomNumberGenerator.GetInt32(100000000, 1000000000).ToString();
            if (_reservedCodes.Contains(code) || _byCode.ContainsKey(code)) continue;
            return code;
        }
        throw new InvalidOperationException("无法分配连接码，请稍后重试");
    }

    private static string NewPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var chars = new char[8];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    private static bool SafeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }
}
