using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BDVM.Domain;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace BDVM.Adapters;

// Only primitive observations cross the worker boundary. This client never reads Unity
// and never promotes a failed connection into authority or submits an economic snapshot.
public sealed class DedicatedWorldClient : IDisposable
{
    private readonly HttpClient http;
    private readonly string checkpoint;
    private readonly string worker = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private readonly JsonSerializerSettings json = new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver(), MaxDepth = 8 };
    private WorldLeaseGrant? lease;
    private long nextSequence;
    private int sending;

    public DedicatedWorldClient(ExternalAuthorityConfiguration config)
    {
        var key = File.ReadAllText(config.WorkerKeyFile).Trim();
        if (key.Length != 64 || !System.Linq.Enumerable.All(key, Uri.IsHexDigit)) throw new InvalidDataException("World worker credential is invalid.");
        checkpoint = config.CheckpointId;
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new Uri(config.ServerUrl), Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task ObserveAsync(bool worldLoaded, long tick)
    {
        if (Interlocked.Exchange(ref sending, 1) != 0) throw new InvalidOperationException("Only one world observation may be in flight.");
        try
        {
            if (lease == null)
            {
                lease = JsonConvert.DeserializeObject<WorldLeaseGrant>(await Send("/api/world/connect", new WorldLeaseRequest { CheckpointId = checkpoint, WorkerId = worker }).ConfigureAwait(false), json)
                    ?? throw new InvalidDataException("Missing world lease.");
                if (!Guid.TryParseExact(lease.LeaseId, "N", out _) || !Guid.TryParseExact(lease.ServerEpoch, "N", out _) || lease.NextSequence < 1)
                    throw new InvalidDataException("Invalid world lease.");
                nextSequence = lease.NextSequence;
            }
            await Send("/api/world/heartbeat", new WorldHeartbeat { CheckpointId = checkpoint, WorkerId = worker,
                ServerEpoch = lease.ServerEpoch, LeaseId = lease.LeaseId, Sequence = nextSequence,
                WorldLoaded = worldLoaded, ObservedTick = tick }).ConfigureAwait(false);
            nextSequence = checked(nextSequence + 1);
        }
        catch { lease = null; throw; }
        finally { Volatile.Write(ref sending, 0); }
    }

    private async Task<string> Send(string path, object payload)
    {
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        using (var request = new HttpRequestMessage(HttpMethod.Post, path))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            request.Content = new StringContent(JsonConvert.SerializeObject(payload, json), Encoding.UTF8, "application/json");
            using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var result = new MemoryStream())
                {
                    var buffer = new byte[1024];
                    int count;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) > 0)
                    {
                        if (result.Length + count > 4096) throw new InvalidDataException("Oversized world response.");
                        result.Write(buffer, 0, count);
                    }
                    return Encoding.UTF8.GetString(result.ToArray());
                }
            }
        }
    }

    public void Dispose() { lifetime.Cancel(); http.Dispose(); lifetime.Dispose(); }
}
