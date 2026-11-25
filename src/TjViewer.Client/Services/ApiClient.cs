using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TjViewer.Shared;

namespace TjViewer.Client.Services;

public class ApiException : Exception
{
    public int Status { get; }
    public ApiException(int status, string message) : base(message) => Status = status;
}

public sealed record ApiMessage(string? Message, string? Error);

/// <summary>服务器 REST 封装。令牌经 Authorization 头携带；错误统一提取服务端 error 字段。</summary>
public class ApiClient
{
    private readonly HttpClient _http;
    private readonly AppSession _s;

    public ApiClient(AppSession s)
    {
        _s = s;
        _http = new HttpClient { BaseAddress = new Uri(s.ServerUrl + "/"), Timeout = TimeSpan.FromSeconds(30) };
    }

    private async Task<T> Send<T>(HttpMethod m, string url, object? body, bool auth)
    {
        using var req = new HttpRequestMessage(m, url);
        if (auth && _s.AccessToken is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _s.AccessToken);
        if (body is not null)
            req.Content = JsonContent.Create(body, options: SignalJson.Options);

        using var resp = await _http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new ApiException((int)resp.StatusCode, ExtractError(json) ?? $"请求失败（{(int)resp.StatusCode}）");
        return JsonSerializer.Deserialize<T>(json, SignalJson.Options)!;
    }

    private static string? ExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var e)) return e.GetString();
        }
        catch { }
        return null;
    }

    public Task<AuthResponse> Register(RegisterRequest r) => Send<AuthResponse>(HttpMethod.Post, ApiRoutes.Register.TrimStart('/'), r, false);
    public Task<AuthResponse> Login(LoginRequest r) => Send<AuthResponse>(HttpMethod.Post, ApiRoutes.Login.TrimStart('/'), r, false);
    public Task<AuthResponse> Refresh(string refreshToken) => Send<AuthResponse>(HttpMethod.Post, ApiRoutes.Refresh.TrimStart('/'), new RefreshRequest(refreshToken), false);
    public Task<ApiMessage> ResetRequest(string email) => Send<ApiMessage>(HttpMethod.Post, ApiRoutes.ResetRequest.TrimStart('/'), new PasswordResetRequest(email), false);
    public Task<ApiMessage> ResetConfirm(string token, string newPassword) => Send<ApiMessage>(HttpMethod.Post, ApiRoutes.ResetConfirm.TrimStart('/'), new PasswordResetConfirm(token, newPassword), false);

    public Task<List<DeviceDto>> GetDevices() => Send<List<DeviceDto>>(HttpMethod.Get, ApiRoutes.Devices.TrimStart('/'), null, true);
    public Task<DeviceDto> RegisterDevice(RegisterDeviceRequest r) => Send<DeviceDto>(HttpMethod.Post, ApiRoutes.Devices.TrimStart('/'), r, true);
    public Task<DeviceDto> UpdateDevice(string id, UpdateDeviceRequest r) => Send<DeviceDto>(HttpMethod.Patch, $"{ApiRoutes.Devices.TrimStart('/')}/{id}", r, true);
    public Task<ApiMessage> DeleteDevice(string id) => Send<ApiMessage>(HttpMethod.Delete, $"{ApiRoutes.Devices.TrimStart('/')}/{id}", null, true);
    public Task<ApiMessage> Heartbeat(string deviceId) => Send<ApiMessage>(HttpMethod.Post, $"{ApiRoutes.Devices.TrimStart('/')}/heartbeat", new HeartbeatRequest(deviceId), true);
    public Task<DeviceCodes> GetCodes(string deviceId, bool fixedCode)
        => Send<DeviceCodes>(HttpMethod.Post, ApiRoutes.Codes.TrimStart('/'), new CodesRequest(deviceId, fixedCode), true);
}
