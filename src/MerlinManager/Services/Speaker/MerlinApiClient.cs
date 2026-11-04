using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MerlinManager.Services.Speaker;

/// <summary>
/// Accès minimal à l'API Merlin (backend de l'application officielle), utilisé pour récupérer
/// les identifiants Wi-Fi du point d'accès de l'enceinte.
/// </summary>
public sealed class MerlinApiClient : IDisposable
{
    private const string TokenUrl = "https://sso.hello-merlin.com/auth/realms/hello-merlin/protocol/openid-connect/token";
    private const string ApiBaseUrl = "https://api.hello-merlin.com/api/v1/";
    private const string ClientId = "app";

    private readonly HttpClient _httpClient;
    private string _accessToken;

    public MerlinApiClient()
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(ApiBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        // En-têtes envoyés par l'application Android 1.20.5
        _httpClient.DefaultRequestHeaders.Add("X-Merlin-App-Version", "1.20.5");
        _httpClient.DefaultRequestHeaders.Add("X-Merlin-App-Build", "1020005");
        _httpClient.DefaultRequestHeaders.Add("X-Merlin-OS", "Android");
        _httpClient.DefaultRequestHeaders.Add("X-Merlin-OS-Version", "14");
    }

    public async Task LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = ClientId,
            ["username"] = username,
            ["password"] = password
        });

        using var response = await _httpClient.PostAsync(TokenUrl, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new MerlinApiException(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.BadRequest
                ? "Email ou mot de passe incorrect"
                : $"Échec de l'authentification ({(int)response.StatusCode})");
        }

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        _accessToken = json.RootElement.GetProperty("access_token").GetString();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    /// <summary>
    /// Crée une synchronisation côté serveur, comme le fait l'application avant de se connecter à l'enceinte.
    /// La réponse contient notamment le SSID et le mot de passe du point d'accès de l'enceinte.
    /// </summary>
    public async Task<SynchroInfo> CreateSynchroAsync(CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        using var response = await _httpClient.PostAsJsonAsync("synchros", new { status = "PENDING", type = "TRANSFERT" }, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new MerlinApiException($"Création de la synchronisation refusée ({(int)response.StatusCode}) : {body}");
        }

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        return new SynchroInfo
        {
            Id = GetString(root, "id"),
            Ssid = GetString(root, "ssid"),
            Password = GetString(root, "password"),
            PendingSsid = GetString(root, "pendingSsid"),
            PendingPassword = GetString(root, "pendingPassword"),
            PlaylistText = GetString(root, "playlistText"),
            RawJson = body
        };
    }

    /// <summary>
    /// Clôture la synchronisation créée par <see cref="CreateSynchroAsync"/> (statut FAIL : rien n'a été synchronisé côté serveur).
    /// </summary>
    public async Task CloseSynchroAsync(string synchroId, CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        using var response = await _httpClient.PutAsJsonAsync($"synchros/{synchroId}", new { status = "FAIL", type = "TRANSFERT" }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private void EnsureLoggedIn()
    {
        if (string.IsNullOrEmpty(_accessToken))
        {
            throw new InvalidOperationException("Authentification requise");
        }
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText()
        };
    }
}

public sealed class SynchroInfo
{
    public string Id { get; init; }
    public string Ssid { get; init; }
    public string Password { get; init; }
    public string PendingSsid { get; init; }
    public string PendingPassword { get; init; }
    public string PlaylistText { get; init; }
    public string RawJson { get; init; }
}

public sealed class MerlinApiException(string message) : Exception(message);
