using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace UnhingedSync.Services;

public enum SyncthingState { NotInstalled, NotRunning, Unauthorized, Running }

public sealed record SyncthingStatus(SyncthingState State, string? DeviceId, string? Detail);

public sealed record PendingDevice(string DeviceId, string Name, string Address);

public sealed record PendingFolder(string FolderId, string Label, string OfferedByDeviceId);

public sealed record PeerDevice(
    string DeviceId, string Name, bool SharesOurFolder, int CompletionPercent, bool IsIntroducer);

/// <summary>
/// Talks to the local Syncthing over its REST API so pairing can happen inside this
/// app instead of a separate browser UI. The API key is read from Syncthing's own
/// config.xml, which is why the setup script runs Syncthing in the user session --
/// as a service the config lands somewhere we cannot predict.
/// </summary>
public sealed class SyncthingClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private string? _apiKey;
    private string _baseUri = "http://127.0.0.1:8384";

    public string? ConfigPath { get; private set; }

    public bool TryLoadCredentials()
    {
        ConfigPath = LocateConfig();
        if (ConfigPath is null) return false;

        try
        {
            var doc = XDocument.Load(ConfigPath);
            var gui = doc.Root?.Element("gui");
            _apiKey = gui?.Element("apikey")?.Value;

            var address = gui?.Element("address")?.Value;
            if (!string.IsNullOrWhiteSpace(address))
            {
                // 0.0.0.0 means "all interfaces"; we always talk to it locally.
                _baseUri = "http://" + address.Replace("0.0.0.0", "127.0.0.1");
            }
            return !string.IsNullOrWhiteSpace(_apiKey);
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Where Syncthing itself says a folder lives, read straight from config.xml with no
    /// REST call and no running daemon required.
    ///
    /// This exists because the app and Syncthing used to choose the share independently:
    /// the app fell back to a default under the user profile while Syncthing replicated
    /// somewhere else entirely, and the only symptom was an empty build list. Whatever
    /// Syncthing is actually replicating is the authority on where the share is.
    /// </summary>
    public static string? TryGetFolderPathFromConfig(string folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId)) return null;

        var configPath = LocateConfig();
        if (configPath is null) return null;

        try
        {
            foreach (var folder in XDocument.Load(configPath).Root?.Elements("folder")
                                  ?? Enumerable.Empty<XElement>())
            {
                if (folder.Attribute("id")?.Value == folderId)
                {
                    var path = folder.Attribute("path")?.Value;
                    return string.IsNullOrWhiteSpace(path) ? null : path;
                }
            }
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
        return null;
    }

    private static string? LocateConfig()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                 })
        {
            var candidate = Path.Combine(root, "Syncthing", "config.xml");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public string WebUiUri => _baseUri;

    public async Task<SyncthingStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (!TryLoadCredentials())
            return new(SyncthingState.NotInstalled, null,
                "Syncthing is not installed, or has never been started on this machine.");

        try
        {
            var status = await GetAsync("system/status", ct);
            var id = status?["myID"]?.GetValue<string>();
            return new(SyncthingState.Running, id, null);
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            // Syncthing answered, it just rejected our key. Telling someone to "start it"
            // here sends them chasing a process that is already running.
            return new(SyncthingState.Unauthorized, null,
                $"Syncthing is running but rejected the API key read from {ConfigPath}. " +
                "That usually means it is running with a different config directory than " +
                "the one this app found. Open the Syncthing UI to confirm which one is live.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new(SyncthingState.NotRunning, null,
                $"Nothing is answering at {_baseUri}. Start Syncthing and try again. If it is " +
                "already running, it may be using a different GUI address than its config states.");
        }
    }

    public async Task<List<PendingDevice>> GetPendingDevicesAsync(CancellationToken ct = default)
    {
        var pending = await GetAsync("cluster/pending/devices", ct);
        var result = new List<PendingDevice>();
        if (pending is null) return result;

        foreach (var (deviceId, info) in pending.AsObject())
        {
            result.Add(new PendingDevice(
                deviceId,
                info?["name"]?.GetValue<string>() ?? "(unnamed)",
                info?["address"]?.GetValue<string>() ?? ""));
        }
        return result;
    }

    public async Task<List<PendingFolder>> GetPendingFoldersAsync(CancellationToken ct = default)
    {
        var pending = await GetAsync("cluster/pending/folders", ct);
        var result = new List<PendingFolder>();
        if (pending is null) return result;

        foreach (var (folderId, info) in pending.AsObject())
        {
            var offeredBy = info?["offeredBy"]?.AsObject();
            if (offeredBy is null) continue;

            foreach (var (deviceId, offer) in offeredBy)
            {
                result.Add(new PendingFolder(
                    folderId,
                    offer?["label"]?.GetValue<string>() ?? folderId,
                    deviceId));
            }
        }
        return result;
    }

    /// <summary>
    /// Adds a peer. Marking one as an introducer means WE accept devices THEY introduce
    /// to us -- the direction is easy to get backwards. With one hub flagged as
    /// introducer, onboarding 30 people is 30 pairings instead of 435, because everyone
    /// learns about everyone else through the hub.
    ///
    /// autoAcceptFolders is deliberately tied to the introducer flag: it lets that device
    /// create folders on our disk, which is reasonable for a hub we chose to trust and
    /// reckless for an arbitrary peer.
    /// </summary>
    public async Task AddDeviceAsync(
        string deviceId, string name, bool isIntroducer = false, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["deviceID"] = deviceId,
            ["name"] = name,
            ["addresses"] = new JsonArray("dynamic"),
            ["introducer"] = isIntroducer,
            ["autoAcceptFolders"] = isIntroducer
        };
        await PostAsync("config/devices", body, ct);
    }

    /// <summary>Adds a device to the folder's device list, leaving the rest untouched.</summary>
    public async Task ShareFolderWithAsync(string folderId, string deviceId, CancellationToken ct = default)
    {
        var folder = await GetAsync($"config/folders/{folderId}", ct)
            ?? throw new InvalidOperationException($"Syncthing has no folder '{folderId}'.");

        var devices = folder["devices"]?.AsArray() ?? new JsonArray();
        var already = devices.Any(d => d?["deviceID"]?.GetValue<string>() == deviceId);
        if (already) return;

        devices.Add(new JsonObject { ["deviceID"] = deviceId });
        folder["devices"] = devices;

        await PutAsync($"config/folders/{folderId}", folder, ct);
    }

    /// <summary>
    /// Accepts a folder someone offered us, which is the other half of pairing: they
    /// share it, we say where it goes locally and whether we are allowed to publish.
    /// </summary>
    public async Task AcceptFolderAsync(
        string folderId, string label, string localPath, string offeredByDeviceId,
        bool receiveOnly, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["id"] = folderId,
            ["label"] = label,
            ["path"] = localPath,
            ["type"] = receiveOnly ? "receiveonly" : "sendreceive",
            ["devices"] = new JsonArray(new JsonObject { ["deviceID"] = offeredByDeviceId })
        };
        await PostAsync("config/folders", body, ct);
    }

    public async Task<List<PeerDevice>> GetPeersAsync(string folderId, CancellationToken ct = default)
    {
        var devices = await GetAsync("config/devices", ct);
        var status = await GetAsync("system/status", ct);
        var myId = status?["myID"]?.GetValue<string>();

        var folder = await TryGetAsync($"config/folders/{folderId}", ct);
        var sharedWith = folder?["devices"]?.AsArray()
            .Select(d => d?["deviceID"]?.GetValue<string>())
            .Where(id => id is not null)
            .ToHashSet() ?? [];

        var result = new List<PeerDevice>();
        if (devices is null) return result;

        foreach (var device in devices.AsArray())
        {
            var id = device?["deviceID"]?.GetValue<string>();
            if (id is null || id == myId) continue;

            var shares = sharedWith.Contains(id);
            var completion = 0;
            if (shares)
            {
                var c = await TryGetAsync($"db/completion?folder={folderId}&device={id}", ct);
                completion = (int)Math.Round(c?["completion"]?.GetValue<double>() ?? 0);
            }

            result.Add(new PeerDevice(
                id,
                device?["name"]?.GetValue<string>() ?? "(unnamed)",
                shares,
                completion,
                device?["introducer"]?.GetValue<bool>() ?? false));
        }
        return result;
    }

    /// <summary>
    /// The folder's type here: sendreceive, receiveonly, sendonly. Used as a fallback
    /// signal for this machine's role when setup predates the role being recorded --
    /// receive-only is exactly what an artist's machine looks like.
    /// </summary>
    public async Task<string> GetFolderTypeAsync(string folderId, CancellationToken ct = default)
    {
        var folder = await TryGetAsync($"config/folders/{folderId}", ct);
        return folder?["type"]?.GetValue<string>() ?? "";
    }

    /// <summary>Our own view of how much of the folder has arrived, 0-100.</summary>
    public async Task<int> GetLocalCompletionAsync(string folderId, CancellationToken ct = default)
    {
        var c = await TryGetAsync($"db/completion?folder={folderId}", ct);
        return (int)Math.Round(c?["completion"]?.GetValue<double>() ?? 0);
    }

    // ---------------------------------------------------------------- transport

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{_baseUri}/rest/{path}");
        request.Headers.Add("X-API-Key", _apiKey);
        return request;
    }

    private async Task<JsonNode?> GetAsync(string path, CancellationToken ct)
    {
        using var response = await Http.SendAsync(Request(HttpMethod.Get, path), ct);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private async Task<JsonNode?> TryGetAsync(string path, CancellationToken ct)
    {
        try { return await GetAsync(path, ct); }
        catch (Exception e) when (e is HttpRequestException or JsonException) { return null; }
    }

    private async Task PostAsync(string path, JsonNode body, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, path);
        request.Content = JsonContent.Create(body);
        using var response = await Http.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);
    }

    private async Task PutAsync(string path, JsonNode body, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Put, path);
        request.Content = JsonContent.Create(body);
        using var response = await Http.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"Syncthing returned {(int)response.StatusCode} {response.ReasonPhrase}. {detail}".Trim());
    }
}
