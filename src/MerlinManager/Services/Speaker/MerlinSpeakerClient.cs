using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MerlinManager.Services.Speaker;

/// <summary>
/// Client du protocole TCP de l'enceinte Merlin (rétro-ingénierie de l'application Android 1.20.5).
/// Trame : [len = N+5][cmd][payload N octets][CRC32 LE sur cmd+payload], CRC32 MPEG-2 (poly 0x04C11DB7, init 0xFFFFFFFF).
/// Dans une réponse, payload[0] contient généralement le code de statut.
/// </summary>
public sealed class MerlinSpeakerClient : IDisposable
{
    public const string DefaultHost = "192.168.4.1";
    public const int DefaultPort = 50000;

    // Identifiants de commande
    private const byte CmdUploadFile = 0x01;
    private const byte CmdPing = 0x02;
    private const byte CmdGetAvailableSpace = 0x03;
    private const byte CmdGetTotalSize = 0x04;
    private const byte CmdGetFirmwareVersion = 0x05;
    private const byte CmdUpdatePlaylist = 0x06;
    private const byte CmdSetDate = 0x08;
    private const byte CmdEndSynchronization = 0x09;
    // 0x0A = reset de l'enceinte : volontairement non exposé
    private const byte CmdGetFile = 0x0D;
    private const byte CmdSearchFile = 0x1F;
    private const byte CmdUnknown = 0xFF;

    private const int UploadChunkSize = 100 * 1024;

    // Codes de statut de la commande d'upload (enum z06 de l'application)
    private const byte UploadStatusReady = 0;
    private const byte UploadStatusSha256Valid = 1;
    private static readonly string[] UploadStatusNames =
        ["SUCCESS", "SHA256_VALID", "NOT_ENOUGH_SPACE", "FILENAME_TOO_LARGE", "SHA256_INVALID", "BAD_LENGTH_CMD", "TIMEOUT", "FAIL_CREATE_FILE"];

    // Codes de statut de la mise à jour de playlist (enum t06 de l'application)
    private static readonly string[] PlaylistStatusNames =
        ["SUCCESS", "BAD_FILENAME_LEN", "BAD_EXTENSION_FILE", "FILE_NOT_FOUND", "FAIL_MINIFIER", "FAIL_OPEN_JSON", "FAIL_OPEN_BINARY",
         "ROOT_ELEMENT_IS_NOT_ARRAY", "BAD_ROOT_CHILD_QUANTITY", "MISSING_FIELD", "BAD_UUID_FIELD", "UUID_TOO_LARGE", "BAD_TITLE_FIELD",
         "TITLE_TOO_LARGE", "FAIL_ADD_ELEMENT", "FAIL_RENAME_BIN_FILE", "INVALID_TYPE_IN_BINARY_FILE", "IMAGE_NOT_FOUND", "MUSIC_NOT_FOUND",
         "FAVORITES_NOT_FOUND", "TOO_MANY_FAVORITES", "FAIL_CREATE_FAV_FILE", "FAIL_OPEN_FAV_FILE", "BAD_FAV_FORMAT"];

    private TcpClient _tcpClient;
    private NetworkStream _stream;

    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool IsConnected => _tcpClient?.Connected == true;

    public async Task ConnectAsync(string host = DefaultHost, int port = DefaultPort, CancellationToken cancellationToken = default)
    {
        Dispose();

        _tcpClient = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await _tcpClient.ConnectAsync(host, port, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"Connexion TCP impossible vers {host}:{port} (délai dépassé)");
        }
        _stream = _tcpClient.GetStream();
    }

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(CmdPing, [], cancellationToken);
        await ReadResponseAsync(CmdPing, cancellationToken);
    }

    public async Task<string> GetFirmwareVersionAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(CmdGetFirmwareVersion, [], cancellationToken);
        var payload = await ReadResponseAsync(CmdGetFirmwareVersion, cancellationToken);
        var patch = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
        return $"{payload[0]}.{payload[1]}.{patch}";
    }

    /// <summary>Espace libre de la carte SD (unité renvoyée telle quelle par l'enceinte).</summary>
    public async Task<uint> GetAvailableSpaceAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(CmdGetAvailableSpace, [], cancellationToken);
        var payload = await ReadResponseAsync(CmdGetAvailableSpace, cancellationToken);
        return BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
    }

    /// <summary>Taille totale de la carte SD (unité renvoyée telle quelle par l'enceinte).</summary>
    public async Task<uint> GetTotalSizeAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(CmdGetTotalSize, [], cancellationToken);
        var payload = await ReadResponseAsync(CmdGetTotalSize, cancellationToken);
        return BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
    }

    public async Task SetDateAsync(DateTimeOffset date, CancellationToken cancellationToken = default)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)date.ToUnixTimeSeconds());
        await SendAsync(CmdSetDate, payload, cancellationToken);
        var response = await ReadResponseAsync(CmdSetDate, cancellationToken);
        EnsureSuccess(response[0], "setDate", null);
    }

    /// <summary>
    /// Télécharge un fichier de la carte SD. Réponse : [statut][lenNom][nom][taille u32][SHA-256], suivie des octets bruts.
    /// </summary>
    public async Task GetFileAsync(string fileName, Stream destination, IProgress<long> progress = null, CancellationToken cancellationToken = default)
    {
        await SendAsync(CmdGetFile, Encoding.UTF8.GetBytes(fileName), cancellationToken);
        var payload = await ReadResponseAsync(CmdGetFile, cancellationToken);
        if (payload[0] != 0)
        {
            throw new MerlinSpeakerException($"getFile({fileName}) a échoué : statut={payload[0]}");
        }

        var nameLength = payload[1];
        var size = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(nameLength + 2, 4));
        var expectedSha = payload.AsSpan(nameLength + 6, 32).ToArray();

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long received = 0;
        while (received < size)
        {
            var toRead = (int)Math.Min(buffer.Length, size - received);
            var read = await ReadWithTimeoutAsync(buffer.AsMemory(0, toRead), cancellationToken);
            sha.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            progress?.Report(received);
        }

        if (!sha.GetHashAndReset().AsSpan().SequenceEqual(expectedSha))
        {
            throw new MerlinSpeakerException($"getFile({fileName}) : SHA-256 invalide");
        }
    }

    /// <summary>
    /// Recherche un fichier sur la carte SD. Renvoie son SHA-256 s'il existe (si <paramref name="contentVerification"/>), sinon null.
    /// </summary>
    public async Task<(bool Found, byte[] Sha256)> SearchFileAsync(string fileName, bool contentVerification = true, CancellationToken cancellationToken = default)
    {
        var name = Encoding.UTF8.GetBytes(fileName);
        var request = new byte[name.Length + 1];
        request[0] = contentVerification ? (byte)1 : (byte)0;
        name.CopyTo(request, 1);

        await SendAsync(CmdSearchFile, request, cancellationToken);
        var payload = await ReadResponseAsync(CmdSearchFile, cancellationToken);
        switch (payload[0])
        {
            case 0:
                var nameLength = payload[1];
                var sha = payload.Length >= nameLength + 34 ? payload.AsSpan(nameLength + 2, 32).ToArray() : null;
                return (true, sha);
            case 1:
                return (false, null);
            default:
                throw new MerlinSpeakerException($"searchFile({fileName}) a échoué : statut={payload[0]}");
        }
    }

    /// <summary>
    /// Envoie un fichier sur la carte SD. Requête : [lenNom][nom][taille u32][SHA-256].
    /// Si l'enceinte répond SUCCESS, le contenu est envoyé brut (hors trame), puis elle confirme par SHA256_VALID.
    /// Renvoie false si le fichier était déjà présent à l'identique.
    /// </summary>
    public async Task<bool> UploadFileAsync(string fileName, byte[] content, IProgress<long> progress = null, CancellationToken cancellationToken = default)
    {
        var name = Encoding.UTF8.GetBytes(fileName);
        var request = new byte[name.Length + 37];
        request[0] = (byte)name.Length;
        name.CopyTo(request, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(name.Length + 1, 4), (uint)content.Length);
        SHA256.HashData(content).CopyTo(request, name.Length + 5);

        await SendAsync(CmdUploadFile, request, cancellationToken);
        var payload = await ReadResponseAsync(CmdUploadFile, cancellationToken);
        if (payload[0] == UploadStatusSha256Valid)
        {
            return false;
        }
        EnsureSuccess(payload[0], $"uploadFile({fileName})", UploadStatusNames);

        for (var offset = 0; offset < content.Length; offset += UploadChunkSize)
        {
            var count = Math.Min(UploadChunkSize, content.Length - offset);
            await _stream.WriteAsync(content.AsMemory(offset, count), cancellationToken);
            await _stream.FlushAsync(cancellationToken);
            progress?.Report(offset + count);
        }

        payload = await ReadResponseAsync(CmdUploadFile, cancellationToken);
        if (payload[0] != UploadStatusSha256Valid)
        {
            throw new MerlinSpeakerException($"uploadFile({fileName}) a échoué : {StatusName(payload[0], UploadStatusNames)}");
        }
        return true;
    }

    /// <summary>
    /// Demande à l'enceinte de reconstruire playlist.bin à partir d'un fichier JSON préalablement envoyé.
    /// </summary>
    public async Task UpdatePlaylistAsync(string jsonFileName, CancellationToken cancellationToken = default)
    {
        await SendAsync(CmdUpdatePlaylist, Encoding.UTF8.GetBytes(jsonFileName), cancellationToken);
        var payload = await ReadResponseAsync(CmdUpdatePlaylist, cancellationToken, TimeSpan.FromMinutes(2));
        EnsureSuccess(payload[0], $"updatePlaylist({jsonFileName})", PlaylistStatusNames);
    }

    public async Task EndSynchronizationAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(CmdEndSynchronization, [], cancellationToken);
        await ReadResponseAsync(CmdEndSynchronization, cancellationToken);
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _stream = null;
        _tcpClient = null;
    }

    #region Trames
    private async Task SendAsync(byte cmd, byte[] payload, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new IOException("Enceinte non connectée");
        }
        if (payload.Length > 250)
        {
            throw new ArgumentException("Payload trop long pour une trame Merlin", nameof(payload));
        }

        var frame = new byte[payload.Length + 6];
        frame[0] = (byte)(payload.Length + 5);
        frame[1] = cmd;
        payload.CopyTo(frame, 2);
        var crc = ComputeCrc(frame.AsSpan(1, payload.Length + 1));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(payload.Length + 2, 4), crc);

        await _stream.WriteAsync(frame, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Lit les trames jusqu'à obtenir la réponse à <paramref name="expectedCmd"/> et renvoie son payload.
    /// Les trames non sollicitées (statistiques d'écoute, etc.) sont ignorées.
    /// </summary>
    private async Task<byte[]> ReadResponseAsync(byte expectedCmd, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? ResponseTimeout);
        try
        {
            while (true)
            {
                var (cmd, payload) = await ReadFrameAsync(timeoutSource.Token);
                if (cmd == expectedCmd)
                {
                    return payload;
                }
                if (cmd == CmdUnknown)
                {
                    throw new MerlinSpeakerException($"Commande 0x{expectedCmd:X2} inconnue de l'enceinte (statut={(payload.Length > 0 ? payload[0] : -1)})");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Pas de réponse de l'enceinte à la commande 0x{expectedCmd:X2}");
        }
    }

    private async Task<(byte Cmd, byte[] Payload)> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = new byte[1];
        while (true)
        {
            await ReadExactlyAsync(header, cancellationToken);
            var length = header[0];
            // Comme l'application : un octet de longueur <= 4 est ignoré (resynchronisation)
            if (length <= 4)
            {
                continue;
            }

            var body = new byte[length];
            await ReadExactlyAsync(body, cancellationToken);

            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(length - 4, 4));
            var actualCrc = ComputeCrc(body.AsSpan(0, length - 4));
            if (expectedCrc != actualCrc)
            {
                System.Diagnostics.Debug.WriteLine($"MerlinSpeaker bad CRC {actualCrc:X8} != {expectedCrc:X8}");
                continue;
            }

            return (body[0], body.AsSpan(1, length - 5).ToArray());
        }
    }

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            offset += await ReadWithTimeoutAsync(buffer.AsMemory(offset), cancellationToken);
        }
    }

    private async Task<int> ReadWithTimeoutAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new IOException("Enceinte non connectée");
        }

        var read = await _stream.ReadAsync(buffer, cancellationToken);
        if (read == 0)
        {
            throw new IOException("Connexion fermée par l'enceinte");
        }
        return read;
    }

    internal static uint ComputeCrc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= (uint)b << 24;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
            }
        }
        return crc;
    }
    #endregion

    private static void EnsureSuccess(byte status, string operation, string[] statusNames)
    {
        if (status != 0)
        {
            throw new MerlinSpeakerException($"{operation} a échoué : {StatusName(status, statusNames)}");
        }
    }

    private static string StatusName(byte status, string[] statusNames)
    {
        return statusNames is not null && status < statusNames.Length ? statusNames[status] : $"statut {status}";
    }
}

public sealed class MerlinSpeakerException(string message) : Exception(message);
