using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Security;
using System.Text;

namespace MerlinManager.Services.Speaker;

/// <summary>
/// Connexion du PC au point d'accès Wi-Fi de l'enceinte (MERLIN_XXXXXX) via netsh.
/// Le profil Wi-Fi est conservé par Windows : seul le SSID est mémorisé dans le registre.
/// </summary>
public static class WifiService
{
    private const string RegistryRootPath = "Software\\MerlinManager";
    private const string RegistrySsidValueName = "SpeakerSsid";

    public static string SavedSpeakerSsid
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRootPath);
            return key?.GetValue(RegistrySsidValueName) as string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryRootPath);
            key?.SetValue(RegistrySsidValueName, value ?? string.Empty, RegistryValueKind.String);
        }
    }

    /// <summary>Crée (ou remplace) le profil Wi-Fi WPA2 de l'enceinte.</summary>
    public static async Task AddProfileAsync(string ssid, string password)
    {
        var profileXml = $"""
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>{SecurityElement.Escape(ssid)}</name>
              <SSIDConfig><SSID><name>{SecurityElement.Escape(ssid)}</name></SSID></SSIDConfig>
              <connectionType>ESS</connectionType>
              <connectionMode>manual</connectionMode>
              <MSM><security>
                <authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
                <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{SecurityElement.Escape(password)}</keyMaterial></sharedKey>
              </security></MSM>
            </WLANProfile>
            """;

        var profilePath = Path.Combine(Path.GetTempPath(), $"merlin-wifi-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(profilePath, profileXml, Encoding.UTF8);
            var (exitCode, output) = await RunNetshAsync($"wlan add profile filename=\"{profilePath}\" user=current");
            if (exitCode != 0)
            {
                throw new IOException($"Impossible d'ajouter le profil Wi-Fi : {output.Trim()}");
            }
        }
        finally
        {
            // Le fichier contient le mot de passe en clair
            File.Delete(profilePath);
        }
    }

    /// <summary>SSID du réseau Wi-Fi actuellement connecté, ou null.</summary>
    public static async Task<string> GetConnectedSsidAsync()
    {
        var (_, output) = await RunNetshAsync("wlan show interfaces");
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2 && parts[0].Trim() == "SSID")
            {
                var ssid = parts[1].Trim();
                return string.IsNullOrEmpty(ssid) ? null : ssid;
            }
        }
        return null;
    }

    /// <summary>
    /// Se connecte au réseau <paramref name="ssid"/> (profil existant) et attend que l'enceinte réponde en TCP.
    /// </summary>
    public static async Task ConnectToSpeakerAsync(string ssid, CancellationToken cancellationToken = default)
    {
        var (exitCode, output) = await RunNetshAsync($"wlan connect name=\"{ssid}\" ssid=\"{ssid}\"");
        if (exitCode != 0)
        {
            throw new IOException($"Connexion au Wi-Fi « {ssid} » impossible : {output.Trim()}");
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await GetConnectedSsidAsync() == ssid && await IsSpeakerReachableAsync(cancellationToken))
            {
                return;
            }
            await Task.Delay(1000, cancellationToken);
        }

        throw new TimeoutException($"L'enceinte n'est pas joignable sur le Wi-Fi « {ssid} ». Vérifiez qu'elle est en mode Wifi / Transfert.");
    }

    public static async Task ReconnectAsync(string ssid)
    {
        if (!string.IsNullOrEmpty(ssid))
        {
            await RunNetshAsync($"wlan connect name=\"{ssid}\"");
        }
    }

    // Ping ICMP plutôt qu'une connexion TCP de test : l'enceinte n'accepte probablement qu'un client à la fois
    private static async Task<bool> IsSpeakerReachableAsync(CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(MerlinSpeakerClient.DefaultHost, TimeSpan.FromSeconds(2), cancellationToken: cancellationToken);
            return reply.Status == IPStatus.Success;
        }
        catch (PingException)
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunNetshAsync(string arguments)
    {
        var startInfo = new ProcessStartInfo("netsh", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output + error);
    }
}
