using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TjViewer.Client.Services;
using TjViewer.Shared;

namespace TjViewer.Client.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppSession _s;
    private readonly ApiClient _api;
    private SignalClient? _signal;
    private CancellationTokenSource? _hbCts;
    private readonly List<RemoteSession> _sessions = new();

    public ObservableCollection<DeviceDto> Devices { get; } = new();

    [ObservableProperty] private string status = "未连接";
    [ObservableProperty] private string currentUser = "";

    public string ThisDeviceId => _s.DeviceId ?? "";

    public event Action<ConnectIncomingPayload>? IncomingConnect;
    public event Action<RemoteSession>? OpenControl;
    public event Action<string>? Notify;

    public MainViewModel(AppSession s)
    {
        _s = s;
        _api = new ApiClient(s);
        CurrentUser = s.Username ?? "";
    }

    public async Task InitializeAsync()
    {
        var dev = await _api.RegisterDevice(new RegisterDeviceRequest(_s.DeviceKey, Environment.MachineName, null, DeviceIdentity.OsSummary));
        _s.DeviceId = dev.Id;
        await RefreshDevices();

        _signal = new SignalClient(_s);
        _signal.Presence += p => OnUi(() => UpdatePresence(p));
        _signal.ConnectIncoming += p => OnUi(() => IncomingConnect?.Invoke(p));
        _signal.ConnectReady += p => OnUi(() => OnConnectReady(p));
        _signal.ConnectFailed += p => OnUi(() => { Status = p.Reason; Notify?.Invoke(p.Reason); });
        _signal.SessionEnd += _ => OnUi(() => Notify?.Invoke("会话已结束"));
        _signal.Disconnected += reason => OnUi(() => Status = "信令断开：" + reason);
        await _signal.ConnectAsync();
        Status = "信令已连接";
        StartHeartbeat();
    }

    public async Task RefreshDevices()
    {
        var list = await _api.GetDevices();
        Devices.Clear();
        foreach (var d in list) Devices.Add(d);
    }

    public async Task ConnectAsync(DeviceDto d)
    {
        if (d.Id == ThisDeviceId) { Notify?.Invoke("不能连接本机"); return; }
        if (d.Status == DeviceStatus.Offline) { Notify?.Invoke("设备离线"); return; }
        if (_signal is null || !_signal.IsConnected) { Notify?.Invoke("信令未连接"); return; }
        Status = $"正在连接 {d.Name}…";
        await _signal.RequestConnect(d.Id, Capabilities.All);
    }

    private void OnConnectReady(ConnectReadyPayload p)
    {
        if (_signal is null) return;
        var session = new RemoteSession(_signal, p.SessionId, isController: true);
        _sessions.Add(session);
        _ = session.StartAsync();
        OpenControl?.Invoke(session);
    }

    public async Task AcceptIncoming(string sessionId)
    {
        if (_signal is null) return;
        await _signal.Accept(sessionId);
        var session = new RemoteSession(_signal, sessionId, isController: false);
        _sessions.Add(session);
        _ = session.StartAsync();
    }

    public Task RejectIncoming(string sessionId) => _signal?.Reject(sessionId, "用户拒绝") ?? Task.CompletedTask;
    public Task EndSession(string sessionId) => _signal?.EndSession(sessionId, "用户断开") ?? Task.CompletedTask;

    private void UpdatePresence(PresencePayload p)
    {
        for (int i = 0; i < Devices.Count; i++)
        {
            if (Devices[i].Id == p.DeviceId)
            {
                Devices[i] = Devices[i] with { Status = p.Status };
                break;
            }
        }
    }

    private void StartHeartbeat()
    {
        _hbCts = new CancellationTokenSource();
        var ct = _hbCts.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    if (_s.DeviceId is not null)
                    {
                        try { await _api.Heartbeat(_s.DeviceId); } catch { }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    private static void OnUi(Action a)
    {
        var app = Application.Current;
        if (app is null) return;
        if (app.Dispatcher.CheckAccess()) a();
        else app.Dispatcher.Invoke(a);
    }

    public async Task ShutdownAsync()
    {
        _hbCts?.Cancel();
        foreach (var s in _sessions) await s.DisposeAsync();
        _sessions.Clear();
        if (_signal is not null) await _signal.DisposeAsync();
    }
}
