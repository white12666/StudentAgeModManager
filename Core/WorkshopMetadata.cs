using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace StudentAgeModManager.Core
{
    public sealed class WorkshopMetadata
    {
        public string WorkshopId { get; set; }
        public uint ConsumerAppId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
    }

    public sealed class WorkshopMetadataBatchResult
    {
        public Dictionary<string, WorkshopMetadata> Items { get; } =
            new Dictionary<string, WorkshopMetadata>(StringComparer.Ordinal);

        public Dictionary<string, string> Errors { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public interface IWorkshopMetadataProvider
    {
        Task<WorkshopMetadataBatchResult> GetDetailsAsync(IList<string> workshopIds,
            CancellationToken ct = default(CancellationToken));
    }

    /// <summary>Minimal injectable transport used only for Steam's fixed metadata API.</summary>
    public interface IWorkshopMetadataTransport
    {
        Task<byte[]> PostFormAsync(string endpoint, NameValueCollection values,
            TimeSpan timeout, int maxResponseBytes,
            CancellationToken ct = default(CancellationToken));
    }

    /// <summary>HTTPS form transport with an end-to-end timeout and a streaming size cap.</summary>
    public sealed class SteamWorkshopMetadataTransport : IWorkshopMetadataTransport
    {
        public async Task<byte[]> PostFormAsync(string endpoint, NameValueCollection values,
            TimeSpan timeout, int maxResponseBytes,
            CancellationToken ct = default(CancellationToken))
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            if (maxResponseBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));

            Uri endpointUri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out endpointUri) ||
                !string.Equals(endpointUri.Scheme, Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(endpoint, SteamWorkshopMetadataProvider.Endpoint,
                    StringComparison.Ordinal))
                throw new ArgumentException("Steam 元数据接口必须是固定的官方 HTTPS 地址。",
                    nameof(endpoint));

            byte[] body = EncodeForm(values);
            var request = (HttpWebRequest)WebRequest.Create(endpointUri);
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded; charset=utf-8";
            request.ContentLength = body.Length;
            request.UserAgent = "StudentAgeModManager/1.0";
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            int timeoutMilliseconds = (int)Math.Min(int.MaxValue,
                Math.Ceiling(timeout.TotalMilliseconds));
            request.Timeout = timeoutMilliseconds;
            request.ReadWriteTimeout = timeoutMilliseconds;

            using (ct.Register(request.Abort))
            {
                Task<byte[]> operation = ExecuteAsync(request, body, maxResponseBytes, ct);
                Task completed = await Task.WhenAny(operation, Task.Delay(timeout, ct))
                    .ConfigureAwait(false);
                if (completed != operation)
                {
                    request.Abort();
                    try { await operation.ConfigureAwait(false); } catch { }
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException("Steam Workshop 元数据请求超时。");
                }

                try
                {
                    return await operation.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    ct.ThrowIfCancellationRequested();
                    throw;
                }
            }
        }

        private static async Task<byte[]> ExecuteAsync(HttpWebRequest request, byte[] body,
            int maxResponseBytes, CancellationToken ct)
        {
            using (Stream requestStream = await request.GetRequestStreamAsync()
                .ConfigureAwait(false))
                await requestStream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);

            using (var response = (HttpWebResponse)await request.GetResponseAsync()
                .ConfigureAwait(false))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new IOException("Steam Workshop 元数据接口返回 HTTP " +
                        ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + "。");
                if (response.ContentLength > maxResponseBytes)
                    throw new InvalidDataException("Steam Workshop 元数据响应超过 4 MiB 限制。");

                using (Stream responseStream = response.GetResponseStream())
                using (var output = new MemoryStream())
                {
                    if (responseStream == null)
                        throw new InvalidDataException("Steam Workshop 元数据响应为空。");
                    var buffer = new byte[8192];
                    while (true)
                    {
                        int read = await responseStream.ReadAsync(buffer, 0, buffer.Length, ct)
                            .ConfigureAwait(false);
                        if (read == 0) break;
                        if (output.Length + read > maxResponseBytes)
                            throw new InvalidDataException(
                                "Steam Workshop 元数据响应超过 4 MiB 限制。");
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
            }
        }

        private static byte[] EncodeForm(NameValueCollection values)
        {
            var body = new StringBuilder();
            foreach (string key in values.AllKeys)
            {
                if (key == null) throw new ArgumentException("表单参数名不能为 null。",
                    nameof(values));
                string[] entries = values.GetValues(key) ?? new[] { string.Empty };
                foreach (string value in entries)
                {
                    if (body.Length > 0) body.Append('&');
                    body.Append(WebUtility.UrlEncode(key));
                    body.Append('=');
                    body.Append(WebUtility.UrlEncode(value ?? string.Empty));
                }
            }
            return Encoding.UTF8.GetBytes(body.ToString());
        }
    }

    /// <summary>Calls Steam's public Workshop details API and accepts display metadata only.</summary>
    public sealed class SteamWorkshopMetadataProvider : IWorkshopMetadataProvider
    {
        public const string Endpoint =
            "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

        private const int MaxBatchSize = 50;
        private const int MaxResponseBytes = 4 * 1024 * 1024;
        private const int RequestTimeoutSeconds = 10;
        private const int MaxAttempts = 2;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = MaxResponseBytes,
            RecursionLimit = 64,
        };

        private readonly IWorkshopMetadataTransport _transport;
        private readonly int _retryDelayMilliseconds;

        static SteamWorkshopMetadataProvider()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public SteamWorkshopMetadataProvider()
            : this(new SteamWorkshopMetadataTransport(), 250)
        {
        }

        public SteamWorkshopMetadataProvider(IWorkshopMetadataTransport transport,
            int retryDelayMilliseconds = 250)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            if (retryDelayMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(retryDelayMilliseconds));
            _retryDelayMilliseconds = retryDelayMilliseconds;
        }

        public async Task<WorkshopMetadataBatchResult> GetDetailsAsync(
            IList<string> workshopIds, CancellationToken ct = default(CancellationToken))
        {
            if (workshopIds == null) throw new ArgumentNullException(nameof(workshopIds));

            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string workshopId in workshopIds)
            {
                string normalized;
                string error;
                if (!WorkshopItem.TryNormalizeReference(workshopId, out normalized, out error) ||
                    !string.Equals(workshopId, normalized, StringComparison.Ordinal))
                    throw new ArgumentException(
                        "Steam 元数据请求只能使用规范 Workshop ID: " + workshopId,
                        nameof(workshopIds));
                if (seen.Add(workshopId)) unique.Add(workshopId);
            }

            var result = new WorkshopMetadataBatchResult();
            for (int offset = 0; offset < unique.Count; offset += MaxBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                int count = Math.Min(MaxBatchSize, unique.Count - offset);
                var batch = unique.GetRange(offset, count);
                string response = await RequestBatchWithRetryAsync(batch, ct);
                WorkshopMetadataBatchResult parsed = ParseResponse(response, batch);
                foreach (KeyValuePair<string, WorkshopMetadata> pair in parsed.Items)
                {
                    if (result.Items.ContainsKey(pair.Key) || result.Errors.ContainsKey(pair.Key))
                        throw new InvalidDataException(
                            "Steam 元数据响应重复返回 Workshop ID " + pair.Key + "。");
                    result.Items.Add(pair.Key, pair.Value);
                }
                foreach (KeyValuePair<string, string> pair in parsed.Errors)
                {
                    if (result.Items.ContainsKey(pair.Key) || result.Errors.ContainsKey(pair.Key))
                        throw new InvalidDataException(
                            "Steam 元数据响应重复返回 Workshop ID " + pair.Key + "。");
                    result.Errors.Add(pair.Key, pair.Value);
                }
            }
            return result;
        }

        private async Task<string> RequestBatchWithRetryAsync(IList<string> ids,
            CancellationToken ct)
        {
            Exception last = null;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await RequestBatchAsync(ids, ct);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException) &&
                                           !(ex is InvalidDataException) &&
                                           !(ex is ArgumentException))
                {
                    if (ct.IsCancellationRequested)
                        throw new OperationCanceledException(ct);
                    last = ex;
                    if (attempt < MaxAttempts && _retryDelayMilliseconds > 0)
                        await Task.Delay(_retryDelayMilliseconds * attempt, ct);
                }
            }
            throw new IOException("Steam Workshop 元数据请求失败。", last);
        }

        private async Task<string> RequestBatchAsync(IList<string> ids,
            CancellationToken ct)
        {
            var values = new NameValueCollection
            {
                ["itemcount"] = ids.Count.ToString(CultureInfo.InvariantCulture),
            };
            for (int i = 0; i < ids.Count; i++)
                values["publishedfileids[" + i.ToString(CultureInfo.InvariantCulture) + "]"] =
                    ids[i];

            byte[] bytes = await _transport.PostFormAsync(Endpoint, values,
                TimeSpan.FromSeconds(RequestTimeoutSeconds), MaxResponseBytes, ct);
            if (bytes == null || bytes.Length == 0)
                throw new InvalidDataException("Steam Workshop 元数据响应为空。");
            if (bytes.Length > MaxResponseBytes)
                throw new InvalidDataException("Steam Workshop 元数据响应超过 4 MiB 限制。");
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException(
                    "Steam Workshop 元数据响应不是有效 UTF-8。", ex);
            }
        }

        public static WorkshopMetadataBatchResult ParseResponse(string text,
            IList<string> requestedIds)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (requestedIds == null) throw new ArgumentNullException(nameof(requestedIds));

            var requested = new HashSet<string>(StringComparer.Ordinal);
            foreach (string requestedId in requestedIds)
            {
                string normalized;
                string error;
                if (!WorkshopItem.TryNormalizeReference(requestedId, out normalized, out error) ||
                    !string.Equals(requestedId, normalized, StringComparison.Ordinal))
                    throw new ArgumentException(
                        "Steam 元数据响应解析只能使用规范 Workshop ID: " + requestedId,
                        nameof(requestedIds));
                if (!requested.Add(requestedId))
                    throw new ArgumentException("请求包含重复 Workshop ID " + requestedId + "。",
                        nameof(requestedIds));
            }

            object raw;
            try
            {
                raw = Json.DeserializeObject(text);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Steam Workshop 元数据 JSON 无法解析: " +
                    ex.Message, ex);
            }

            var root = raw as Dictionary<string, object>;
            var response = GetDictionary(root, "response", "Steam 元数据响应");
            int topResult = ReadInt32(GetRequired(response, "result", "Steam 元数据响应"),
                "response.result");
            if (topResult != 1)
                throw new InvalidDataException("Steam 元数据响应 result 不是成功状态。");

            object rawDetails;
            if (!response.TryGetValue("publishedfiledetails", out rawDetails))
                throw new InvalidDataException("Steam 元数据响应缺少 publishedfiledetails。");
            var details = rawDetails as object[];
            if (details == null)
                throw new InvalidDataException("Steam 元数据 publishedfiledetails 不是数组。");

            var result = new WorkshopMetadataBatchResult();
            foreach (object rawDetail in details)
            {
                var detail = rawDetail as Dictionary<string, object>;
                if (detail == null)
                    throw new InvalidDataException("Steam 元数据项目不是对象。");

                string workshopId = ReadWorkshopId(detail, "publishedfileid");
                if (!requested.Contains(workshopId))
                    throw new InvalidDataException(
                        "Steam 元数据响应包含未请求的 Workshop ID " + workshopId + "。");
                if (result.Items.ContainsKey(workshopId) || result.Errors.ContainsKey(workshopId))
                    throw new InvalidDataException(
                        "Steam 元数据响应重复返回 Workshop ID " + workshopId + "。");

                int itemResult = ReadInt32(GetRequired(detail, "result",
                    "Workshop " + workshopId), "Workshop " + workshopId + " result");
                if (itemResult != 1)
                {
                    result.Errors.Add(workshopId,
                        "Steam 返回 result=" + itemResult.ToString(CultureInfo.InvariantCulture) + "。");
                    continue;
                }

                uint consumerAppId = ReadUInt32(GetRequired(detail, "consumer_app_id",
                    "Workshop " + workshopId), "Workshop " + workshopId + " consumer_app_id");
                string title = ReadOptionalString(detail, "title",
                    "Workshop " + workshopId + " title");
                string description = ReadOptionalString(detail, "description",
                    "Workshop " + workshopId + " description");
                result.Items.Add(workshopId, new WorkshopMetadata
                {
                    WorkshopId = workshopId,
                    ConsumerAppId = consumerAppId,
                    Title = WorkshopMetadataText.CleanTitle(title),
                    Description = WorkshopMetadataText.CleanDescription(description),
                });
            }

            foreach (string requestedId in requested)
                if (!result.Items.ContainsKey(requestedId) &&
                    !result.Errors.ContainsKey(requestedId))
                    result.Errors.Add(requestedId, "Steam 响应缺少该项目。");
            return result;
        }

        private static Dictionary<string, object> GetDictionary(
            Dictionary<string, object> owner, string propertyName, string location)
        {
            if (owner == null)
                throw new InvalidDataException(location + "不是对象。");
            object value;
            if (!owner.TryGetValue(propertyName, out value))
                throw new InvalidDataException(location + "缺少 " + propertyName + "。");
            var dictionary = value as Dictionary<string, object>;
            if (dictionary == null)
                throw new InvalidDataException(location + "的 " + propertyName + " 不是对象。");
            return dictionary;
        }

        private static object GetRequired(Dictionary<string, object> values, string propertyName,
            string location)
        {
            object value;
            if (!values.TryGetValue(propertyName, out value) || value == null)
                throw new InvalidDataException(location + " 缺少 " + propertyName + "。");
            return value;
        }

        private static string ReadWorkshopId(Dictionary<string, object> values,
            string propertyName)
        {
            object raw = GetRequired(values, propertyName, "Steam 元数据项目");
            var value = raw as string;
            if (value == null)
                throw new InvalidDataException("Steam 元数据返回的 Workshop ID 不是字符串。");
            string normalized;
            string error;
            if (!WorkshopItem.TryNormalizeReference(value, out normalized, out error) ||
                !string.Equals(value, normalized, StringComparison.Ordinal))
                throw new InvalidDataException("Steam 元数据返回了非规范 Workshop ID。");
            return normalized;
        }

        private static string ReadOptionalString(Dictionary<string, object> values,
            string propertyName, string location)
        {
            object value;
            if (!values.TryGetValue(propertyName, out value) || value == null) return null;
            var text = value as string;
            if (text == null)
                throw new InvalidDataException(location + " 不是字符串。");
            return text;
        }

        private static int ReadInt32(object value, string location)
        {
            long parsed;
            if (!TryReadSignedInteger(value, out parsed) ||
                parsed < int.MinValue || parsed > int.MaxValue)
                throw new InvalidDataException(location + " 不是有效 JSON 整数。");
            return (int)parsed;
        }

        private static uint ReadUInt32(object value, string location)
        {
            ulong parsed;
            if (!TryReadUnsignedInteger(value, out parsed) || parsed > uint.MaxValue)
                throw new InvalidDataException(location + " 不是有效 JSON 无符号整数。");
            return (uint)parsed;
        }

        private static bool TryReadSignedInteger(object value, out long parsed)
        {
            parsed = 0;
            if (value == null) return false;
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                    parsed = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.Byte:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                    parsed = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.UInt64:
                    ulong unsigned = (ulong)value;
                    if (unsigned > long.MaxValue) return false;
                    parsed = (long)unsigned;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryReadUnsignedInteger(object value, out ulong parsed)
        {
            parsed = 0;
            if (value == null) return false;
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Byte:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                    parsed = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                    long signed = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    if (signed < 0) return false;
                    parsed = (ulong)signed;
                    return true;
                default:
                    return false;
            }
        }
    }



    public static class WorkshopMetadataText
    {
        public const int MaxTitleLength = 128;
        public const int MaxDescriptionLength = 240;
        private const int MaxRawTextLength = 64 * 1024;
        private static readonly Regex BbCode = new Regex(
            @"\[(?:/?[a-z*][^\]\r\n]{0,128})\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string CleanTitle(string value)
        {
            return Clean(value, MaxTitleLength);
        }

        public static string CleanDescription(string value)
        {
            return Clean(value, MaxDescriptionLength);
        }

        private static string Clean(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.Length > MaxRawTextLength)
                value = value.Substring(0, MaxRawTextLength);
            value = WebUtility.HtmlDecode(value);
            value = BbCode.Replace(value, string.Empty);

            var output = new StringBuilder(Math.Min(value.Length, maxLength));
            bool pendingSpace = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                        continue;
                    UnicodeCategory pairCategory =
                        CharUnicodeInfo.GetUnicodeCategory(value, i);
                    if (pairCategory == UnicodeCategory.Control ||
                        pairCategory == UnicodeCategory.Format)
                    {
                        pendingSpace = output.Length > 0;
                        i++;
                        continue;
                    }
                    if (pendingSpace)
                    {
                        output.Append(' ');
                        pendingSpace = false;
                    }
                    output.Append(c);
                    output.Append(value[++i]);
                    continue;
                }
                if (char.IsLowSurrogate(c)) continue;

                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (char.IsControl(c) || char.IsWhiteSpace(c) ||
                    category == UnicodeCategory.Format)
                {
                    pendingSpace = output.Length > 0;
                    continue;
                }
                if (pendingSpace)
                {
                    output.Append(' ');
                    pendingSpace = false;
                }
                output.Append(c);
            }

            string cleaned = output.ToString().Trim();
            if (cleaned.Length <= maxLength) return cleaned;
            int take = maxLength - 1;
            if (take > 0 && take < cleaned.Length &&
                char.IsHighSurrogate(cleaned[take - 1]) && char.IsLowSurrogate(cleaned[take]))
                take--;
            return cleaned.Substring(0, take).TrimEnd() + "…";
        }
    }

    public sealed class WorkshopMetadataCacheDocument
    {
        public int schemaVersion { get; set; }
        public Dictionary<string, WorkshopMetadataCacheEntry> items { get; set; }
    }

    public sealed class WorkshopMetadataCacheEntry
    {
        public uint consumerAppId { get; set; }
        public string title { get; set; }
        public string description { get; set; }
        public string fetchedAtUtc { get; set; }
    }

    /// <summary>Enriches optional display fields and verifies Workshop ownership for CI.</summary>
    public sealed class WorkshopMetadataService
    {
        public const uint StudentAgeAppId = 1991040;
        public const string DefaultDescription = "Steam 创意工坊项目";
        private const int CacheSchemaVersion = 1;
        private const int MaxCacheBytes = 1024 * 1024;
        private static readonly TimeSpan CacheFreshness = TimeSpan.FromHours(24);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private readonly IWorkshopMetadataProvider _provider;
        private readonly string _cachePath;
        private readonly Func<DateTime> _utcNow;

        public WorkshopMetadataService(IWorkshopMetadataProvider provider, string cachePath = null,
            Func<DateTime> utcNow = null)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _cachePath = cachePath;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public async Task EnrichMissingAsync(ModIndex index,
            CancellationToken ct = default(CancellationToken))
        {
            if (index == null) throw new ArgumentNullException(nameof(index));
            if (index.mods == null) throw new InvalidDataException("索引缺少 mods 数组。");

            DateTime now = EnsureUtc(_utcNow());
            Dictionary<string, CachedMetadata> cache = LoadCache(now);
            var currentIds = new HashSet<string>(StringComparer.Ordinal);
            var targets = new List<EnrichmentTarget>();

            foreach (ModEntry entry in index.mods)
            {
                if (entry == null || !WorkshopItem.IsDeclared(entry)) continue;
                string workshopId;
                if (!WorkshopItem.TryGetId(entry, out workshopId)) continue;
                currentIds.Add(workshopId);

                bool needsName = string.IsNullOrWhiteSpace(entry.name);
                bool needsDescription = string.IsNullOrWhiteSpace(entry.description);
                if (!needsName && !needsDescription) continue;

                CachedMetadata cached;
                cache.TryGetValue(workshopId, out cached);
                targets.Add(new EnrichmentTarget
                {
                    Entry = entry,
                    WorkshopId = workshopId,
                    Cached = cached,
                    NeedsLookup = cached == null || !IsFresh(cached.FetchedAtUtc, now),
                });
            }

            var lookupIds = new List<string>();
            var lookupSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (EnrichmentTarget target in targets)
                if (target.NeedsLookup && lookupSet.Add(target.WorkshopId))
                    lookupIds.Add(target.WorkshopId);

            bool cacheChanged = false;
            var live = new Dictionary<string, WorkshopMetadata>(StringComparer.Ordinal);
            if (lookupIds.Count > 0)
            {
                try
                {
                    WorkshopMetadataBatchResult response =
                        await _provider.GetDetailsAsync(lookupIds, ct);
                    if (response == null)
                        throw new InvalidDataException("Steam Workshop 元数据响应为空。");
                    foreach (string workshopId in lookupIds)
                    {
                        WorkshopMetadata metadata;
                        if (!response.Items.TryGetValue(workshopId, out metadata)) continue;
                        metadata = NormalizeMetadata(metadata, workshopId);
                        if (metadata == null || metadata.ConsumerAppId != StudentAgeAppId ||
                            string.IsNullOrEmpty(metadata.Title))
                            continue;
                        live[workshopId] = metadata;
                        cache[workshopId] = new CachedMetadata
                        {
                            Metadata = metadata,
                            FetchedAtUtc = now,
                        };
                        cacheChanged = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Display metadata is optional. Keep a stale cache or safe fallback.
                }
            }

            foreach (EnrichmentTarget target in targets)
            {
                WorkshopMetadata metadata;
                if (live.TryGetValue(target.WorkshopId, out metadata))
                    ApplyMissing(target.Entry, metadata);
                if (target.Cached != null)
                    ApplyMissing(target.Entry, target.Cached.Metadata);
                if (string.IsNullOrWhiteSpace(target.Entry.name))
                    target.Entry.name = target.Entry.id;
                if (string.IsNullOrWhiteSpace(target.Entry.description))
                    target.Entry.description = DefaultDescription;
            }

            if (cacheChanged || HasEntriesOutside(cache, currentIds))
                SaveCache(cache, currentIds);
        }

        public async Task<int> VerifyIndexAsync(ModIndex index,
            CancellationToken ct = default(CancellationToken))
        {
            if (index == null) throw new ArgumentNullException(nameof(index));
            if (index.mods == null) throw new InvalidDataException("索引缺少 mods 数组。");

            var ids = new List<string>();
            var locations = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < index.mods.Count; i++)
            {
                ModEntry entry = index.mods[i];
                if (entry == null || !WorkshopItem.IsDeclared(entry)) continue;
                string workshopId;
                if (!WorkshopItem.TryGetId(entry, out workshopId))
                    throw new InvalidDataException("mods[" + i + "] 的 workshopId 尚未规范化。");
                ids.Add(workshopId);
                locations.Add(workshopId, "mods[" + i + "]（" + entry.id + "）");
            }
            if (ids.Count == 0) return 0;

            WorkshopMetadataBatchResult response;
            try
            {
                response = await _provider.GetDetailsAsync(ids, ct);
                if (response == null)
                    throw new InvalidDataException("Steam Workshop 元数据响应为空。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Steam Workshop 在线验证请求失败: " +
                    ex.Message, ex);
            }

            var errors = new List<string>();
            foreach (string workshopId in ids)
            {
                string location = locations[workshopId];
                string itemError;
                if (response.Errors.TryGetValue(workshopId, out itemError))
                {
                    errors.Add(location + "：" + itemError);
                    continue;
                }

                WorkshopMetadata metadata;
                if (!response.Items.TryGetValue(workshopId, out metadata))
                {
                    errors.Add(location + "：Steam 响应缺少该项目。");
                    continue;
                }
                metadata = NormalizeMetadata(metadata, workshopId);
                if (metadata == null)
                {
                    errors.Add(location + "：Steam 返回的 Workshop ID 不匹配。");
                    continue;
                }
                if (metadata.ConsumerAppId != StudentAgeAppId)
                {
                    errors.Add(location + "：属于 AppID " +
                        metadata.ConsumerAppId.ToString(CultureInfo.InvariantCulture) +
                        "，不是 StudentAge AppID 1991040。");
                    continue;
                }
                if (string.IsNullOrEmpty(metadata.Title))
                    errors.Add(location + "：Steam 工坊标题为空。");
            }

            if (errors.Count > 0)
                throw new InvalidDataException("Steam Workshop 在线验证失败:" +
                    Environment.NewLine + "- " + string.Join(Environment.NewLine + "- ", errors));
            return ids.Count;
        }

        private static WorkshopMetadata NormalizeMetadata(WorkshopMetadata metadata,
            string expectedId)
        {
            if (metadata == null ||
                !string.Equals(metadata.WorkshopId, expectedId, StringComparison.Ordinal))
                return null;
            return new WorkshopMetadata
            {
                WorkshopId = expectedId,
                ConsumerAppId = metadata.ConsumerAppId,
                Title = WorkshopMetadataText.CleanTitle(metadata.Title),
                Description = WorkshopMetadataText.CleanDescription(metadata.Description),
            };
        }

        private static void ApplyMissing(ModEntry entry, WorkshopMetadata metadata)
        {
            if (metadata == null) return;
            if (string.IsNullOrWhiteSpace(entry.name) &&
                !string.IsNullOrWhiteSpace(metadata.Title))
                entry.name = metadata.Title;
            if (string.IsNullOrWhiteSpace(entry.description) &&
                !string.IsNullOrWhiteSpace(metadata.Description))
                entry.description = metadata.Description;
        }

        private Dictionary<string, CachedMetadata> LoadCache(DateTime now)
        {
            var cache = new Dictionary<string, CachedMetadata>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(_cachePath)) return cache;
            try
            {
                if (!File.Exists(_cachePath)) return cache;
                FileAttributes attributes = File.GetAttributes(_cachePath);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) != 0)
                    return cache;
                var info = new FileInfo(_cachePath);
                if (info.Length <= 0 || info.Length > MaxCacheBytes) return cache;

                object rawDocument = Json.DeserializeObject(File.ReadAllText(_cachePath));
                var document = rawDocument as Dictionary<string, object>;
                object rawSchemaVersion;
                object rawItems;
                uint schemaVersion;
                if (document == null ||
                    !document.TryGetValue("schemaVersion", out rawSchemaVersion) ||
                    !TryReadCacheUInt32(rawSchemaVersion, out schemaVersion) ||
                    schemaVersion != CacheSchemaVersion ||
                    !document.TryGetValue("items", out rawItems))
                    return cache;
                var items = rawItems as Dictionary<string, object>;
                if (items == null) return cache;

                foreach (KeyValuePair<string, object> pair in items)
                {
                    string normalized;
                    string error;
                    var value = pair.Value as Dictionary<string, object>;
                    object rawAppId;
                    object rawTitle;
                    object rawDescription;
                    object rawFetchedAt;
                    uint appId;
                    if (value == null ||
                        !value.TryGetValue("consumerAppId", out rawAppId) ||
                        !TryReadCacheUInt32(rawAppId, out appId) ||
                        !value.TryGetValue("title", out rawTitle) || !(rawTitle is string) ||
                        !value.TryGetValue("description", out rawDescription) ||
                        (rawDescription != null && !(rawDescription is string)) ||
                        !value.TryGetValue("fetchedAtUtc", out rawFetchedAt) ||
                        !(rawFetchedAt is string))
                        continue;

                    DateTime fetchedAt;
                    if (!WorkshopItem.TryNormalizeReference(pair.Key, out normalized, out error) ||
                        !string.Equals(pair.Key, normalized, StringComparison.Ordinal) ||
                        appId != StudentAgeAppId ||
                        !DateTime.TryParseExact((string)rawFetchedAt, "o",
                            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                            out fetchedAt))
                        continue;
                    fetchedAt = EnsureUtc(fetchedAt);
                    if (fetchedAt > now.AddMinutes(5)) continue;
                    string title = WorkshopMetadataText.CleanTitle((string)rawTitle);
                    if (title.Length == 0) continue;
                    cache.Add(pair.Key, new CachedMetadata
                    {
                        FetchedAtUtc = fetchedAt,
                        Metadata = new WorkshopMetadata
                        {
                            WorkshopId = pair.Key,
                            ConsumerAppId = appId,
                            Title = title,
                            Description = WorkshopMetadataText.CleanDescription(
                                rawDescription as string),
                        },
                    });
                }
            }
            catch
            {
                // A cache is never authoritative. Treat corruption or I/O failure as a miss.
            }
            return cache;
        }

        private static bool TryReadCacheUInt32(object value, out uint parsed)
        {
            parsed = 0;
            if (value == null) return false;
            ulong unsigned;
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Byte:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                    unsigned = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
                    break;
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                    long signed = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    if (signed < 0) return false;
                    unsigned = (ulong)signed;
                    break;
                default:
                    return false;
            }
            if (unsigned > uint.MaxValue) return false;
            parsed = (uint)unsigned;
            return true;
        }

        private void SaveCache(Dictionary<string, CachedMetadata> cache,
            HashSet<string> currentIds)
        {
            if (string.IsNullOrEmpty(_cachePath)) return;
            string temp = null;
            try
            {
                if (File.Exists(_cachePath) &&
                    (File.GetAttributes(_cachePath) & FileAttributes.ReparsePoint) != 0)
                    return;
                string directory = Path.GetDirectoryName(_cachePath);
                if (string.IsNullOrEmpty(directory)) return;
                Directory.CreateDirectory(directory);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;

                var document = new WorkshopMetadataCacheDocument
                {
                    schemaVersion = CacheSchemaVersion,
                    items = new Dictionary<string, WorkshopMetadataCacheEntry>(StringComparer.Ordinal),
                };
                foreach (string workshopId in currentIds)
                {
                    CachedMetadata cached;
                    if (!cache.TryGetValue(workshopId, out cached) || cached == null ||
                        cached.Metadata == null)
                        continue;
                    document.items.Add(workshopId, new WorkshopMetadataCacheEntry
                    {
                        consumerAppId = cached.Metadata.ConsumerAppId,
                        title = cached.Metadata.Title,
                        description = cached.Metadata.Description,
                        fetchedAtUtc = EnsureUtc(cached.FetchedAtUtc).ToString("o",
                            CultureInfo.InvariantCulture),
                    });
                }

                byte[] bytes = new UTF8Encoding(false).GetBytes(Json.Serialize(document));
                if (bytes.Length > MaxCacheBytes) return;
                temp = Path.Combine(directory, "." + Path.GetFileName(_cachePath) + "." +
                    Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(_cachePath))
                    File.Replace(temp, _cachePath, null, true);
                else
                    File.Move(temp, _cachePath);
                temp = null;
            }
            catch
            {
                // Cache persistence failure must not hide an otherwise valid index.
            }
            finally
            {
                if (temp != null)
                    try { File.Delete(temp); } catch { }
            }
        }

        private static bool HasEntriesOutside(Dictionary<string, CachedMetadata> cache,
            HashSet<string> currentIds)
        {
            foreach (string workshopId in cache.Keys)
                if (!currentIds.Contains(workshopId)) return true;
            return false;
        }

        private static bool IsFresh(DateTime fetchedAtUtc, DateTime nowUtc)
        {
            TimeSpan age = nowUtc - fetchedAtUtc;
            return age >= TimeSpan.Zero && age <= CacheFreshness;
        }

        private static DateTime EnsureUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            if (value.Kind == DateTimeKind.Unspecified)
                return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            return value.ToUniversalTime();
        }

        private sealed class EnrichmentTarget
        {
            public ModEntry Entry { get; set; }
            public string WorkshopId { get; set; }
            public CachedMetadata Cached { get; set; }
            public bool NeedsLookup { get; set; }
        }

        private sealed class CachedMetadata
        {
            public WorkshopMetadata Metadata { get; set; }
            public DateTime FetchedAtUtc { get; set; }
        }
    }
}
