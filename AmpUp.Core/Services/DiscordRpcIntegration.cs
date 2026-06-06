using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AmpUp.Core.Models;

namespace AmpUp.Core.Services;

/// <summary>
/// Local Discord RPC client for desktop Discord. It connects to the
/// discord-ipc-{n} named pipe, authenticates with Discord RPC, then flips
/// self mute/deafen through GET_VOICE_SETTINGS + SET_VOICE_SETTINGS.
/// </summary>
public sealed class DiscordRpcIntegration : IDisposable
{
    private const int HandshakeOpcode = 0;
    private const int FrameOpcode = 1;
    private const int CloseOpcode = 2;
    private const int PipeCount = 10;
    private static readonly string[] VoiceScopes = { "rpc", "rpc.voice.read", "rpc.voice.write" };

    private readonly DiscordRpcConfig _config;
    private readonly Action<DiscordRpcConfig> _persist;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new();
    private NamedPipeClientStream? _pipe;
    private string? _pipeName;
    private bool _authenticated;

    public DiscordRpcIntegration(DiscordRpcConfig config, Action<DiscordRpcConfig> persistConfig)
    {
        _config = config;
        _persist = persistConfig;
    }

    public bool IsReady => _authenticated && _pipe?.IsConnected == true;
    public bool? LastKnownMute { get; private set; }
    public bool? LastKnownDeafen { get; private set; }

    public async Task ToggleMuteAsync(CancellationToken ct = default)
        => await ToggleVoiceFlagAsync("mute", ct);

    public async Task ToggleDeafenAsync(CancellationToken ct = default)
        => await ToggleVoiceFlagAsync("deaf", ct);

    public async Task SetMuteAsync(bool muted, CancellationToken ct = default)
        => await SetVoiceFlagAsync("mute", muted, ct);

    public async Task SetDeafenAsync(bool deafened, CancellationToken ct = default)
        => await SetVoiceFlagAsync("deaf", deafened, ct);

    public async Task ToggleNoiseSuppressionAsync(CancellationToken ct = default)
        => await ToggleVoiceFlagAsync("noise_suppression", ct);

    public async Task LeaveVoiceChannelAsync(CancellationToken ct = default)
    {
        if (!_config.Enabled)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            await EnsureAuthenticatedAsync(ct);
            await SendCommandAsync("SELECT_VOICE_CHANNEL",
                new JObject { ["channel_id"] = JValue.CreateNull() }, ct);
        }
        catch (Exception ex)
        {
            Logger.Log($"Discord RPC leave voice failed: {ex.Message}");
            ClosePipe();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool?> GetMuteAsync(CancellationToken ct = default)
        => await GetVoiceFlagAsync("mute", ct);

    public async Task<bool?> GetDeafenAsync(CancellationToken ct = default)
        => await GetVoiceFlagAsync("deaf", ct);

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            ClosePipe();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ToggleVoiceFlagAsync(string flag, CancellationToken ct)
    {
        if (!_config.Enabled)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            await EnsureAuthenticatedAsync(ct);
            var settings = await SendCommandAsync("GET_VOICE_SETTINGS", new JObject(), ct);
            bool current = settings.Value<bool?>(flag) ?? false;
            UpdateCachedFlags(settings);
            var args = new JObject { [flag] = !current };
            var updated = await SendCommandAsync("SET_VOICE_SETTINGS", args, ct);
            UpdateCachedFlags(updated);
        }
        catch (Exception ex)
        {
            Logger.Log($"Discord RPC toggle {flag} failed: {ex.Message}");
            ClosePipe();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SetVoiceFlagAsync(string flag, bool value, CancellationToken ct)
    {
        if (!_config.Enabled)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            await EnsureAuthenticatedAsync(ct);
            var updated = await SendCommandAsync("SET_VOICE_SETTINGS",
                new JObject { [flag] = value }, ct);
            UpdateCachedFlags(updated);
        }
        catch (Exception ex)
        {
            Logger.Log($"Discord RPC set {flag} failed: {ex.Message}");
            ClosePipe();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool?> GetVoiceFlagAsync(string flag, CancellationToken ct)
    {
        if (!_config.Enabled)
            return null;

        await _gate.WaitAsync(ct);
        try
        {
            await EnsureAuthenticatedAsync(ct);
            var settings = await SendCommandAsync("GET_VOICE_SETTINGS", new JObject(), ct);
            UpdateCachedFlags(settings);
            return settings.Value<bool?>(flag);
        }
        catch (Exception ex)
        {
            Logger.Log($"Discord RPC read {flag} failed: {ex.Message}");
            ClosePipe();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (IsReady)
            return;

        var clientId = ResolveClientId();
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Discord RPC Client ID is not configured.");

        await ConnectAsync(clientId, ct);

        if (!string.IsNullOrWhiteSpace(_config.AccessToken)
            && _config.TokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(5))
        {
            try
            {
                await AuthenticateAsync(_config.AccessToken, ct);
                return;
            }
            catch (Exception ex)
            {
                Logger.Log($"Discord RPC stored token failed: {ex.Message}");
                _config.AccessToken = "";
                _config.ConnectedUser = "";
                _persist(_config);
                ClosePipe();
                await ConnectAsync(clientId, ct);
            }
        }

        var code = await AuthorizeAsync(clientId, ct);
        var token = await ExchangeCodeAsync(code, ct);
        await AuthenticateAsync(token.AccessToken, ct);
    }

    private async Task ConnectAsync(string clientId, CancellationToken ct)
    {
        ClosePipe();

        for (int i = 0; i < PipeCount; i++)
        {
            var pipeName = $"discord-ipc-{i}";
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(350, ct);
                _pipe = pipe;
                _pipeName = pipeName;

                var handshake = new JObject
                {
                    ["v"] = 1,
                    ["client_id"] = clientId,
                };
                await WritePacketAsync(HandshakeOpcode, handshake, ct);
                var ready = await ReadPayloadAsync(ct);
                if (ready.Value<string>("evt") != "READY")
                    throw new InvalidOperationException($"Unexpected Discord RPC handshake response: {ready}");

                return;
            }
            catch
            {
                pipe.Dispose();
                if (_pipe == pipe) _pipe = null;
            }
        }

        throw new InvalidOperationException("Discord desktop RPC pipe was not found. Start Discord and try again.");
    }

    private async Task<string> AuthorizeAsync(string clientId, CancellationToken ct)
    {
        var args = new JObject
        {
            ["client_id"] = clientId,
            ["scopes"] = new JArray(VoiceScopes),
        };
        var data = await SendCommandAsync("AUTHORIZE", args, ct);
        var code = data.Value<string>("code");
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Discord did not return an authorization code.");
        return code;
    }

    private async Task<TokenResponse> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var clientId = ResolveClientId();
        var clientSecret = ResolveClientSecret();
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("Discord RPC Client Secret is not configured.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = ResolveRedirectUri(),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://discord.com/api/oauth2/token")
        {
            Content = new FormUrlEncodedContent(form),
        };

        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);

        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord token exchange failed: {(int)response.StatusCode} {json}");

        var payload = JObject.Parse(json);
        var accessToken = payload.Value<string>("access_token") ?? "";
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("Discord token exchange returned no access token.");

        _config.AccessToken = accessToken;
        _config.RefreshToken = payload.Value<string>("refresh_token") ?? _config.RefreshToken;
        int expiresIn = payload.Value<int?>("expires_in") ?? 604800;
        _config.TokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn));
        _persist(_config);

        return new TokenResponse(accessToken);
    }

    private async Task AuthenticateAsync(string accessToken, CancellationToken ct)
    {
        var data = await SendCommandAsync("AUTHENTICATE", new JObject { ["access_token"] = accessToken }, ct);
        var user = data["user"] as JObject;
        if (user != null)
        {
            _config.ConnectedUser = user.Value<string>("username") ?? user.Value<string>("id") ?? "";
            _persist(_config);
        }
        _authenticated = true;
    }

    private async Task<JObject> SendCommandAsync(string command, JObject args, CancellationToken ct)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var payload = new JObject
        {
            ["cmd"] = command,
            ["args"] = args,
            ["nonce"] = nonce,
        };

        await WritePacketAsync(FrameOpcode, payload, ct);

        while (true)
        {
            var response = await ReadPayloadAsync(ct);
            if (response.Value<string>("nonce") != nonce)
                continue;

            if (response.Value<string>("evt") == "ERROR")
            {
                var err = response["data"] as JObject;
                var message = err?.Value<string>("message") ?? response.ToString(Formatting.None);
                throw new InvalidOperationException(message);
            }

            return response["data"] as JObject ?? new JObject();
        }
    }

    private async Task WritePacketAsync(int opcode, JObject payload, CancellationToken ct)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("Discord RPC pipe is not connected.");
        var json = payload.ToString(Formatting.None);
        var body = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        BitConverter.GetBytes(opcode).CopyTo(header, 0);
        BitConverter.GetBytes(body.Length).CopyTo(header, 4);
        await pipe.WriteAsync(header, ct);
        await pipe.WriteAsync(body, ct);
        await pipe.FlushAsync(ct);
    }

    private async Task<JObject> ReadPayloadAsync(CancellationToken ct)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("Discord RPC pipe is not connected.");
        var header = await ReadExactAsync(pipe, 8, ct);
        int opcode = BitConverter.ToInt32(header, 0);
        int length = BitConverter.ToInt32(header, 4);
        if (opcode == CloseOpcode)
            throw new InvalidOperationException("Discord closed the RPC connection.");
        if (length <= 0 || length > 1024 * 1024)
            throw new InvalidOperationException($"Invalid Discord RPC frame length: {length}");

        var body = await ReadExactAsync(pipe, length, ct);
        return JObject.Parse(Encoding.UTF8.GetString(body));
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken ct)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), ct);
            if (read <= 0)
                throw new EndOfStreamException("Discord RPC pipe ended unexpectedly.");
            offset += read;
        }
        return buffer;
    }

    private void UpdateCachedFlags(JObject settings)
    {
        if (settings.TryGetValue("mute", out var mute))
            LastKnownMute = mute.Value<bool>();
        if (settings.TryGetValue("deaf", out var deaf))
            LastKnownDeafen = deaf.Value<bool>();
    }

    private string ResolveClientId()
        => FirstNonEmpty(
            Environment.GetEnvironmentVariable("AMPUP_DISCORD_CLIENT_ID"),
            _config.ClientId);

    private string ResolveClientSecret()
        => FirstNonEmpty(
            Environment.GetEnvironmentVariable("AMPUP_DISCORD_CLIENT_SECRET"),
            _config.ClientSecret);

    private string ResolveRedirectUri()
        => FirstNonEmpty(
            Environment.GetEnvironmentVariable("AMPUP_DISCORD_REDIRECT_URI"),
            _config.RedirectUri,
            "http://127.0.0.1");

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        return "";
    }

    private void ClosePipe()
    {
        _authenticated = false;
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _pipeName = null;
    }

    public void Dispose()
    {
        ClosePipe();
        _gate.Dispose();
        _http.Dispose();
    }

    private sealed record TokenResponse(string AccessToken);
}
