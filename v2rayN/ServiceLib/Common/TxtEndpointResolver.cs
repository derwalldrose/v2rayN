using System.Globalization;
using System.IO;

namespace ServiceLib.Common;

public readonly record struct TxtEndpoint(string Address, int Port);

public static class TxtEndpointResolver
{
    private static readonly string[] Resolvers = ["223.5.5.5", "119.29.29.29"];
    private static readonly ConcurrentDictionary<string, CachedEntry> MemoryCache = new();
    private static readonly Lock CacheLock = new();
    private static readonly IdnMapping Idn = new();

    private static readonly AsyncLocal<Func<string, (IReadOnlyList<string> Records, int Ttl)?>?> QueryOverrideLocal = new();

    public static bool AllowLiveQueries { get; set; } = true;

    public static Func<string, (IReadOnlyList<string> Records, int Ttl)?>? QueryOverride
    {
        get => QueryOverrideLocal.Value;
        set => QueryOverrideLocal.Value = value;
    }

    public static bool PrefersTxt(string? host)
    {
        var value = NormalizeHost(host);
        return value.Length > 0 && !IsIpLiteral(value);
    }

    public static TxtEndpoint? Parse(string? value)
    {
        if (value.IsNullOrEmpty())
        {
            return null;
        }

        var text = value.Trim();
        while (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1].Trim();
        }

        try
        {
            if (text.StartsWith('['))
            {
                var close = text.IndexOf(']');
                if (close <= 1 || close + 1 >= text.Length || text[close + 1] != ':')
                {
                    return null;
                }

                var address = text[1..close];
                if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    return null;
                }

                if (!int.TryParse(text[(close + 2)..], out var v6Port) || v6Port is < 1 or > 65535)
                {
                    return null;
                }

                return new TxtEndpoint(address, v6Port);
            }

            if (text.Count(c => c == ':') != 1)
            {
                return null;
            }

            var parts = text.Split(':', 2);
            if (!IPAddress.TryParse(parts[0], out var v4) || v4.AddressFamily != AddressFamily.InterNetwork)
            {
                return null;
            }

            if (!int.TryParse(parts[1], out var port) || port is < 1 or > 65535)
            {
                return null;
            }

            return new TxtEndpoint(parts[0], port);
        }
        catch
        {
            return null;
        }
    }

    public static (string Address, int Port) ResolveDial(string? host, int fallbackPort)
    {
        var resolved = Resolve(host);
        return resolved is null ? (host ?? string.Empty, fallbackPort) : (resolved.Value.Address, resolved.Value.Port);
    }

    public static TxtEndpoint? Resolve(string? host)
    {
        var key = NormalizeHost(host);
        if (!PrefersTxt(key))
        {
            return null;
        }

        if (QueryOverride is null)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (MemoryCache.TryGetValue(key, out var cached) && cached.ExpiresAtMs > now)
            {
                var fromMemory = Parse(cached.Endpoint);
                if (fromMemory is not null)
                {
                    return fromMemory;
                }
            }

            var fromFile = ReadFileCache(key, now);
            if (fromFile is not null)
            {
                MemoryCache[key] = new CachedEntry(Format(fromFile.Value.Endpoint), fromFile.Value.ExpiresAtMs);
                return fromFile.Value.Endpoint;
            }
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        (IReadOnlyList<string> Records, int Ttl)? answer;
        try
        {
            if (QueryOverride is not null)
            {
                answer = QueryOverride(key);
            }
            else if (AllowLiveQueries)
            {
                answer = QueryUdpTxt(key);
            }
            else
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TxtEndpointResolver", ex);
            return null;
        }

        if (answer is null)
        {
            return null;
        }

        var endpoints = answer.Value.Records
            .Select(Parse)
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .Distinct()
            .ToList();
        if (endpoints.Count == 0)
        {
            return null;
        }

        var chosen = endpoints[Random.Shared.Next(endpoints.Count)];
        if (answer.Value.Ttl > 0 && QueryOverride is null)
        {
            var expires = nowMs + (answer.Value.Ttl * 1000L);
            MemoryCache[key] = new CachedEntry(Format(chosen), expires);
            WriteFileCache(key, chosen, expires);
        }

        return chosen;
    }

    private static (IReadOnlyList<string> Records, int Ttl)? QueryUdpTxt(string host)
    {
        foreach (var resolver in Resolvers)
        {
            try
            {
                var parsed = QueryOne(host, resolver);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch
            {
                // try next resolver only on transport failure
            }
        }

        return null;
    }

    private static (IReadOnlyList<string> Records, int Ttl)? QueryOne(string host, string resolver)
    {
        var txid = (ushort)Random.Shared.Next(0, 65536);
        var query = BuildQuery(host, txid);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveTimeout = 1500;
        socket.SendTimeout = 1500;
        socket.SendTo(query, new IPEndPoint(IPAddress.Parse(resolver), 53));
        var buffer = new byte[4096];
        var remote = new IPEndPoint(IPAddress.Any, 0) as EndPoint;
        var length = socket.ReceiveFrom(buffer, ref remote);
        if (length < 12)
        {
            throw new InvalidDataException("short DNS response");
        }

        var id = (buffer[0] << 8) | buffer[1];
        if (id != txid)
        {
            throw new InvalidDataException("DNS transaction mismatch");
        }

        return ParseTxtResponse(buffer.AsSpan(0, length));
    }

    private static byte[] BuildQuery(string host, ushort txid)
    {
        var ascii = Idn.GetAscii(host.Trim().TrimEnd('.'));
        var labels = ascii.Split('.', StringSplitOptions.RemoveEmptyEntries);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write((byte)(txid >> 8));
        writer.Write((byte)(txid & 0xFF));
        writer.Write((byte)0x01);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);
        writer.Write(new byte[6]);
        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new InvalidDataException("invalid DNS name");
            }

            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }

        writer.Write((byte)0);
        writer.Write((byte)0x00);
        writer.Write((byte)16);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);
        return ms.ToArray();
    }

    private static (IReadOnlyList<string> Records, int Ttl) ParseTxtResponse(ReadOnlySpan<byte> packet)
    {
        var flags = (packet[2] << 8) | packet[3];
        var qdcount = (packet[4] << 8) | packet[5];
        var ancount = (packet[6] << 8) | packet[7];
        if ((flags & 0x000F) != 0)
        {
            return ([], 0);
        }

        var offset = 12;
        for (var i = 0; i < qdcount; i++)
        {
            offset = SkipName(packet, offset) + 4;
        }

        var records = new List<string>();
        var ttls = new List<int>();
        for (var i = 0; i < ancount; i++)
        {
            offset = SkipName(packet, offset);
            if (offset + 10 > packet.Length)
            {
                throw new InvalidDataException("truncated DNS answer");
            }

            var rtype = (packet[offset] << 8) | packet[offset + 1];
            var ttl = (packet[offset + 4] << 24) | (packet[offset + 5] << 16) | (packet[offset + 6] << 8) | packet[offset + 7];
            var rdlength = (packet[offset + 8] << 8) | packet[offset + 9];
            offset += 10;
            var end = offset + rdlength;
            if (end > packet.Length)
            {
                throw new InvalidDataException("truncated DNS rdata");
            }

            if (rtype == 16)
            {
                var parts = new List<byte>();
                var pos = offset;
                while (pos < end)
                {
                    var size = packet[pos];
                    pos += 1;
                    if (pos + size > end)
                    {
                        throw new InvalidDataException("truncated TXT chunk");
                    }

                    for (var n = 0; n < size; n++)
                    {
                        parts.Add(packet[pos + n]);
                    }

                    pos += size;
                }

                records.Add(Encoding.UTF8.GetString(parts.ToArray()));
                ttls.Add(ttl);
            }

            offset = end;
        }

        return (records, ttls.Count == 0 ? 0 : ttls.Min());
    }

    private static int SkipName(ReadOnlySpan<byte> packet, int offset)
    {
        while (true)
        {
            if (offset >= packet.Length)
            {
                throw new InvalidDataException("truncated DNS name");
            }

            var size = packet[offset];
            if ((size & 0xC0) == 0xC0)
            {
                if (offset + 1 >= packet.Length)
                {
                    throw new InvalidDataException("truncated DNS pointer");
                }

                return offset + 2;
            }

            offset += 1;
            if (size == 0)
            {
                return offset;
            }

            offset += size;
            if (offset > packet.Length)
            {
                throw new InvalidDataException("truncated DNS label");
            }
        }
    }

    private static string NormalizeHost(string? host)
    {
        var value = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (value.StartsWith('[') && value.EndsWith(']') && value.Length > 2)
        {
            value = value[1..^1];
        }

        return value;
    }

    private static bool IsIpLiteral(string host)
    {
        return IPAddress.TryParse(host, out _);
    }

    private static string Format(TxtEndpoint endpoint)
    {
        return endpoint.Address.Contains(':') ? $"[{endpoint.Address}]:{endpoint.Port}" : $"{endpoint.Address}:{endpoint.Port}";
    }

    private static string? CachePath()
    {
        try
        {
            return Utils.GetTempPath("txt-endpoints.json");
        }
        catch
        {
            return null;
        }
    }

    private static FileCacheEntry? ReadFileCache(string host, long now)
    {
        var path = CachePath();
        if (path.IsNullOrEmpty() || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = JsonUtils.ParseJson(File.ReadAllText(path)) as JsonObject;
            if (json?[host] is not JsonObject item)
            {
                return null;
            }

            var expires = item["expiresAtMs"]?.GetValue<long>() ?? 0;
            var endpoint = Parse(item["endpoint"]?.GetValue<string>());
            if (endpoint is null || expires <= now)
            {
                return null;
            }

            return new FileCacheEntry(endpoint.Value, expires);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteFileCache(string host, TxtEndpoint endpoint, long expiresAtMs)
    {
        var path = CachePath();
        if (path.IsNullOrEmpty())
        {
            return;
        }

        lock (CacheLock)
        {
            JsonObject root;
            try
            {
                root = File.Exists(path)
                    ? JsonUtils.ParseJson(File.ReadAllText(path)) as JsonObject ?? []
                    : [];
            }
            catch
            {
                root = [];
            }

            root[host] = new JsonObject
            {
                ["endpoint"] = Format(endpoint),
                ["expiresAtMs"] = expiresAtMs,
            };
            File.WriteAllText(path, root.ToJsonString());
        }
    }

    private readonly record struct CachedEntry(string Endpoint, long ExpiresAtMs);

    private readonly record struct FileCacheEntry(TxtEndpoint Endpoint, long ExpiresAtMs);
}
