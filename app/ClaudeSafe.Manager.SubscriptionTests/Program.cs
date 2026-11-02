using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeSafe.Manager;

int passed = 0;
async Task Check(string name, Func<Task> check)
{
    try { await check(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { Console.Error.WriteLine("FAIL " + name + " (" + e.GetType().Name + ")"); Environment.ExitCode = 1; }
}
void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
async Task<string> Refuses(Func<Task> action)
{
    try { await action(); }
    catch (SafeOperationException e) { return e.Message; }
    throw new Exception("Expected a safe refusal.");
}
void RefusesRead(Action action)
{
    try { action(); }
    catch (SafeOperationException) { return; }
    throw new Exception("Expected a safe refusal.");
}
long Revision(ManagerService manager) => (long)typeof(ManagerService).GetField("configurationRevision", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
List<JsonObject> Active(Fixture fixture) => SubscriptionSourceReader.LoadActiveNodes(fixture.Directory);
async Task<string> Add(Fixture fixture, string name = "Remote", string suffix = "a")
{
    await fixture.Manager.AddSubscriptionAsync(name, Fixtures.Url(suffix));
    return (await fixture.Manager.SubscriptionsAsync()).Items.Single(item => item.Name == name).Id;
}
void NoSecrets(string value)
{
    Assert(!value.Contains(Fixtures.Secret, StringComparison.Ordinal));
    Assert(!value.Contains(Fixtures.PrivatePath, StringComparison.Ordinal));
    Assert(!value.Contains(Fixtures.Credential, StringComparison.Ordinal));
}

await Check("legacy source migrates once and remains active without new approvals", async () =>
{
    using var fixture = new Fixture(Fixtures.Source());
    var state = await fixture.Manager.SubscriptionsAsync();
    Assert(state.Items.Count == 1 && state.Items[0].Kind == "file" && state.Items[0].Active);
    Assert(state.ActiveId == state.Items[0].Id && Active(fixture).Single()["name"]!.GetValue<string>() == "Fixture");
    Assert((await fixture.Manager.SnapshotAsync()).Nodes.Single().Approved);
    var stored = fixture.ReadSettings(); Assert(stored.Pins.Count == 1);
    fixture.Manager.Dispose();
    File.WriteAllText(fixture.LegacyPath, "invalid legacy file left after migration");
    using var reopened = new ManagerService(fixture.Directory, fixture.Network.CreateHandler);
    Assert((await reopened.SubscriptionsAsync()).ActiveId == state.ActiveId);
    Assert(SubscriptionSourceReader.LoadActiveNodes(fixture.Directory).Count == 1);
});

await Check("read-only source reader performs no migration or settings writes", () =>
{
    using var fixture = new Fixture(Fixtures.Source(), initialize: false);
    var before = System.IO.Directory.GetFiles(fixture.Directory).Order(StringComparer.Ordinal).ToArray();
    var read = Active(fixture); read[0]["server"] = "changed.invalid";
    var after = System.IO.Directory.GetFiles(fixture.Directory).Order(StringComparer.Ordinal).ToArray();
    Assert(before.SequenceEqual(after) && !File.Exists(fixture.CatalogPath));
    Assert(Active(fixture).Single()["server"]!.GetValue<string>() == "example.invalid");
    return Task.CompletedTask;
});

await Check("fresh install stays without an active source until explicit use", async () =>
{
    using var fixture = new Fixture();
    var initial = await fixture.Manager.SubscriptionsAsync(); Assert(initial.ActiveId == "" && initial.Items.Count == 0);
    var revision = Revision(fixture.Manager);
    var id = await Add(fixture);
    Assert((await fixture.Manager.SubscriptionsAsync()).ActiveId == "" && Active(fixture).Count == 0);
    Assert((await fixture.Manager.SnapshotAsync()).Nodes.Count == 0 && Revision(fixture.Manager) == revision);
    await fixture.Manager.UseSubscriptionAsync(id);
    var used = await fixture.Manager.SnapshotAsync();
    Assert(used.Nodes.Count == 1 && !used.Running && used.Selected == "REJECT");
});

await Check("HTTPS add is inactive and snapshot redacts URL path query and leaf credentials", async () =>
{
    using var fixture = new Fixture(Fixtures.Source());
    var before = await fixture.Manager.SubscriptionsAsync(); var revision = Revision(fixture.Manager);
    await Add(fixture);
    var state = await fixture.Manager.SubscriptionsAsync();
    Assert(state.ActiveId == before.ActiveId && state.Items.Count == 2 && Revision(fixture.Manager) == revision);
    Assert(state.Items.Single(item => item.Kind == "https").MaskedUrl == "https://subscriptions.example.invalid/…");
    NoSecrets(JsonSerializer.Serialize(state, ManagerJson.Options));
    var privateCatalog = File.ReadAllText(fixture.CatalogPath);
    Assert(privateCatalog.Contains(Fixtures.Secret) && privateCatalog.Contains(Fixtures.Credential));
});

await Check("download request has Clash content negotiation and no account headers", async () =>
{
    using var fixture = new Fixture();
    fixture.Network.Reply = (request, _) =>
    {
        Assert(request.Method == HttpMethod.Get && request.RequestUri!.Scheme == "https");
        Assert(request.Headers.UserAgent.ToString().Contains("Clash.Meta", StringComparison.Ordinal));
        Assert(request.Headers.Authorization is null && !request.Headers.Contains("Cookie"));
        Assert(request.Headers.AcceptEncoding.ToString() == "identity");
        return Task.FromResult(Fixtures.Response(Fixtures.Source()));
    };
    await Add(fixture); Assert(fixture.Network.Requests == 1);
});

await Check("use switches only cached source and preserves exact approval pins", async () =>
{
    using var fixture = new Fixture(Fixtures.Source());
    var pins = JsonSerializer.Serialize(fixture.ReadSettings().Pins);
    fixture.Network.Reply = (_, _) => Task.FromResult(Fixtures.Response(Fixtures.Source(Fixtures.Leaf("New", "new.invalid"))));
    var id = await Add(fixture); var requests = fixture.Network.Requests;
    await fixture.Manager.UseSubscriptionAsync(id);
    var snapshot = await fixture.Manager.SnapshotAsync();
    Assert(snapshot.Nodes.Count == 1 && snapshot.Nodes[0].Name == "New" && !snapshot.Nodes[0].Approved);
    Assert(!snapshot.Running && snapshot.Selected == "REJECT" && fixture.Network.Requests == requests);
    Assert(JsonSerializer.Serialize(fixture.ReadSettings().Pins) == pins);
});

await Check("inactive refresh does not change active source or configuration revision", async () =>
{
    using var fixture = new Fixture(Fixtures.Source());
    var id = await Add(fixture); var revision = Revision(fixture.Manager); var activeId = (await fixture.Manager.SubscriptionsAsync()).ActiveId;
    fixture.Network.Reply = (_, _) => Task.FromResult(Fixtures.Response(Fixtures.Source(Fixtures.Leaf("Remote 2"))));
    await fixture.Manager.UpdateSubscriptionAsync(id);
    Assert(Revision(fixture.Manager) == revision && (await fixture.Manager.SubscriptionsAsync()).ActiveId == activeId);
    Assert(Active(fixture).Single()["name"]!.GetValue<string>() == "Fixture");
});

await Check("unchanged active refresh ignores leaf and JSON property order", async () =>
{
    using var fixture = new Fixture();
    var a = Fixtures.Leaf("A"); var b = Fixtures.Leaf("B");
    fixture.Network.Reply = (_, _) => Task.FromResult(Fixtures.Response(Fixtures.Source(a, b)));
    var id = await Add(fixture); await fixture.Manager.UseSubscriptionAsync(id); var revision = Revision(fixture.Manager);
    var reversed = new JsonObject(); foreach (var pair in a.Reverse()) reversed[pair.Key] = pair.Value?.DeepClone();
    fixture.Network.Reply = (_, _) => Task.FromResult(Fixtures.Response(Fixtures.Source(b, reversed)));
    await fixture.Manager.UpdateSubscriptionAsync(id);
    Assert(Revision(fixture.Manager) == revision && (await fixture.Manager.SubscriptionsAsync()).Items.Single().LastError == "");
});

await Check("changed active leaf invalidates tests and identity drift is unapproved", async () =>
{
    using var fixture = new Fixture(Fixtures.Source());
    var id = await Add(fixture); await fixture.Manager.UseSubscriptionAsync(id); var revision = Revision(fixture.Manager);
    fixture.Network.Reply = (_, _) => Task.FromResult(Fixtures.Response(Fixtures.Source(Fixtures.Leaf(server: "drift.invalid"))));
    await fixture.Manager.UpdateSubscriptionAsync(id);
    var state = await fixture.Manager.SnapshotAsync();
    Assert(Revision(fixture.Manager) > revision && !state.Running && state.Selected == "REJECT");
    Assert(!state.Nodes.Single().Approved && !state.Nodes.Single().Eligible && state.Nodes.Single().CanApprove);
    Assert(fixture.ReadSettings().Pins.Single().Server == "example.invalid");
});

await Check("credential changes in active source also invalidate the running configuration", async () =>
{
    using var fixture = new Fixture();
    var id = await Add(fixture); await fixture.Manager.UseSubscriptionAsync(id); var revision = Revision(fixture.Manager);
    var changed = Fixtures.Leaf(); changed["uuid"] = "fixture-new-credential";
    fixture.Network.Reply = (_, _) => Task.FromResult(Fixtures.Response(Fixtures.Source(changed)));
    await fixture.Manager.UpdateSubscriptionAsync(id);
    Assert(Revision(fixture.Manager) > revision && Active(fixture).Single()["uuid"]!.GetValue<string>() == "fixture-new-credential");
});

await Check("failed HTTP refresh keeps last good cache timestamp active source and pins", async () =>
{
    using var fixture = new Fixture();
    var id = await Add(fixture); await fixture.Manager.UseSubscriptionAsync(id);
    var old = (await fixture.Manager.SubscriptionsAsync()).Items.Single(); var revision = Revision(fixture.Manager);
    var leaves = Fixtures.Source(Active(fixture).ToArray()); var pins = JsonSerializer.Serialize(fixture.ReadSettings().Pins);
    fixture.Network.Reply = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
    { Content = new StringContent(Fixtures.Secret + Fixtures.PrivatePath) });
    NoSecrets(await Refuses(() => fixture.Manager.UpdateSubscriptionAsync(id)));
    var state = await fixture.Manager.SubscriptionsAsync();
    Assert(state.ActiveId == id && state.Items.Single().LastUpdatedUtc == old.LastUpdatedUtc && state.Items.Single().LastError.Length > 0);
    Assert(Fixtures.Source(Active(fixture).ToArray()) == leaves && Revision(fixture.Manager) == revision);
    Assert(JsonSerializer.Serialize(fixture.ReadSettings().Pins) == pins); NoSecrets(JsonSerializer.Serialize(state));
});

await Check("redirect is not followed and preserves cache", async () =>
{
    using var fixture = new Fixture(); var id = await Add(fixture); var requests = fixture.Network.Requests;
    fixture.Network.Reply = (_, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(Fixtures.Url("redirect")); return Task.FromResult(response);
    };
    await Refuses(() => fixture.Manager.UpdateSubscriptionAsync(id));
    Assert(fixture.Network.Requests == requests + 1 && (await fixture.Manager.SubscriptionsAsync()).Items.Single().NodeCount == 1);
});

await Check("network exception text cannot disclose saved tokens", async () =>
{
    using var fixture = new Fixture(); var id = await Add(fixture);
    fixture.Network.Reply = (_, _) => throw new HttpRequestException(Fixtures.Url("private") + Fixtures.Credential);
    NoSecrets(await Refuses(() => fixture.Manager.UpdateSubscriptionAsync(id)));
    NoSecrets(JsonSerializer.Serialize(await fixture.Manager.SubscriptionsAsync()));
});

await Check("invalid UTF-8 or malformed replacement never overwrites good nodes", async () =>
{
    using var fixture = new Fixture(); var id = await Add(fixture); await fixture.Manager.UseSubscriptionAsync(id);
    var original = Fixtures.Source(Active(fixture).ToArray()); var revision = Revision(fixture.Manager);
    foreach (var bytes in new[] { new byte[] { 0xff, 0xfe, 0xff }, Encoding.UTF8.GetBytes("<html>not nodes</html>"), Encoding.UTF8.GetBytes("proxies: &nodes []\nother: *nodes") })
    {
        fixture.Network.Reply = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        await Refuses(() => fixture.Manager.UpdateSubscriptionAsync(id));
        Assert(Fixtures.Source(Active(fixture).ToArray()) == original && Revision(fixture.Manager) == revision);
    }
});

await Check("declared and streaming response byte limits are enforced", async () =>
{
    using var fixture = new Fixture(); var id = await Add(fixture);
    fixture.Network.Reply = (_, _) =>
    {
        var content = new ByteArrayContent([]); content.Headers.ContentLength = CoreConfig.MaximumSourceBytes + 1L;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    };
    await Refuses(() => fixture.Manager.UpdateSubscriptionAsync(id));
    fixture.Network.Reply = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StreamContent(new GeneratedStream(CoreConfig.MaximumSourceBytes + 1L)) });
    await Refuses(() => fixture.Manager.UpdateSubscriptionAsync(id));
    Assert((await fixture.Manager.SubscriptionsAsync()).Items.Single().NodeCount == 1);
});

await Check("duplicate and unsafe node sources are rejected before adding a subscription", async () =>
{
    using var fixture = new Fixture();
    foreach (var source in new[] { Fixtures.Source(Fixtures.Leaf(), Fixtures.Leaf()), "proxies:\n- name: DIRECT\n  type: http\n  server: example.invalid\n  port: 443" })
    {
        fixture.Network.Reply = (_, _) => Task.FromResult(Fixtures.Response(source));
        await Refuses(() => fixture.Manager.AddSubscriptionAsync("Invalid", Fixtures.Url("invalid")));
        Assert((await fixture.Manager.SubscriptionsAsync()).Items.Count == 0);
    }
});

await Check("invalid URL schemes credentials fragment whitespace and bounds never make a request", async () =>
{
    using var fixture = new Fixture();
    var credentials = new UriBuilder("https://example.invalid/") { UserName = "fixture", Password = "fixture" }.Uri.AbsoluteUri;
    foreach (var invalid in new[] { "http://example.invalid/", "file:///fixture", "https://example.invalid/#fragment", "https://example.invalid/ bad", "https://example.invalid/\n", credentials, "https://example.invalid/" + new string('x', 8192) })
        await Refuses(() => fixture.Manager.AddSubscriptionAsync("Invalid", invalid));
    Assert(fixture.Network.Requests == 0 && (await fixture.Manager.SubscriptionsAsync()).Items.Count == 0);
});

await Check("blank duplicate and overlong names do not download or replace existing entries", async () =>
{
    using var fixture = new Fixture(); var id = await Add(fixture); var requests = fixture.Network.Requests;
    foreach (var name in new[] { "", "  ", "Bad\nName", new string('x', 81), "remote" })
        await Refuses(() => fixture.Manager.AddSubscriptionAsync(name, Fixtures.Url("different")));
    Assert(fixture.Network.Requests == requests && (await fixture.Manager.SubscriptionsAsync()).Items.Single().Id == id);
});

await Check("rename and URL replacement preserve active cache without configuration revision", async () =>
{
    using var fixture = new Fixture(); var id = await Add(fixture); await fixture.Manager.UseSubscriptionAsync(id);
    var revision = Revision(fixture.Manager); var requests = fixture.Network.Requests;
    await fixture.Manager.RenameSubscriptionAsync(id, "Renamed");
    await fixture.Manager.SetSubscriptionUrlAsync(id, Fixtures.Url("changed"));
    var state = await fixture.Manager.SubscriptionsAsync();
    Assert(state.Items.Single().Name == "Renamed" && state.ActiveId == id && Active(fixture).Count == 1);
    Assert(Revision(fixture.Manager) == revision && fixture.Network.Requests == requests);
    fixture.Manager.Dispose();
    using var reopened = new ManagerService(fixture.Directory, fixture.Network.CreateHandler);
    Assert((await reopened.SubscriptionsAsync()).Items.Single().Name == "Renamed");
});

await Check("stale update cannot overwrite a newly replaced URL or cache", async () =>
{
    using var fixture = new Fixture(); var id = await Add(fixture); await fixture.Manager.UseSubscriptionAsync(id);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Network.Reply = (_, cancellation) => { entered.TrySetResult(); return response.Task.WaitAsync(cancellation); };
    var update = fixture.Manager.UpdateSubscriptionAsync(id); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert((await fixture.Manager.SubscriptionsAsync()).Busy);
    await fixture.Manager.SetSubscriptionUrlAsync(id, Fixtures.Url("replacement"));
    var expected = File.ReadAllText(fixture.CatalogPath);
    response.SetResult(Fixtures.Response(Fixtures.Source(Fixtures.Leaf("Stale"))));
    await Refuses(() => update);
    Assert(File.ReadAllText(fixture.CatalogPath) == expected && Active(fixture).Single()["name"]!.GetValue<string>() == "Fixture");
});

await Check("pending update cannot resurrect a removed subscription", async () =>
{
    using var fixture = new Fixture(); var id = await Add(fixture); await fixture.Manager.UseSubscriptionAsync(id);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Network.Reply = (_, cancellation) => { entered.TrySetResult(); return response.Task.WaitAsync(cancellation); };
    var update = fixture.Manager.UpdateSubscriptionAsync(id); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await fixture.Manager.RemoveSubscriptionAsync(id);
    response.SetResult(Fixtures.Response(Fixtures.Source(Fixtures.Leaf("Stale")))); await Refuses(() => update);
    Assert((await fixture.Manager.SubscriptionsAsync()).Items.Count == 0 && Active(fixture).Count == 0);
});

await Check("rename during a pending update retains both new name and downloaded nodes", async () =>
{
    using var fixture = new Fixture(); var id = await Add(fixture);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Network.Reply = (_, cancellation) => { entered.TrySetResult(); return response.Task.WaitAsync(cancellation); };
    var update = fixture.Manager.UpdateSubscriptionAsync(id); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await fixture.Manager.RenameSubscriptionAsync(id, "Latest name");
    response.SetResult(Fixtures.Response(Fixtures.Source(Fixtures.Leaf("Fresh")))); await update;
    var item = (await fixture.Manager.SubscriptionsAsync()).Items.Single();
    Assert(item.Name == "Latest name" && item.NodeCount == 1 && item.LastError == "");
});

await Check("dispose cancels an in-flight download without saving a late result", async () =>
{
    using var fixture = new Fixture(); var id = await Add(fixture);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Network.Reply = async (_, token) =>
    {
        entered.TrySetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        return Fixtures.Response(Fixtures.Source());
    };
    var update = fixture.Manager.UpdateSubscriptionAsync(id); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var expected = File.ReadAllText(fixture.CatalogPath);
    fixture.Manager.Dispose();
    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Refuses(() => update).WaitAsync(TimeSpan.FromSeconds(5));
    Assert(File.ReadAllText(fixture.CatalogPath) == expected);
});

await Check("batch update keeps successful entries despite another subscription failing", async () =>
{
    using var fixture = new Fixture(Fixtures.Source()); var a = await Add(fixture, "A", "a"); var b = await Add(fixture, "B", "b");
    var activeId = (await fixture.Manager.SubscriptionsAsync()).ActiveId; var revision = Revision(fixture.Manager);
    fixture.Network.Reply = (request, _) => Task.FromResult(request.RequestUri!.Query.EndsWith("a", StringComparison.Ordinal)
        ? Fixtures.Response(Fixtures.Source(Fixtures.Leaf("Fresh A"), Fixtures.Leaf("Fresh A2")))
        : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    await Refuses(() => fixture.Manager.UpdateAllSubscriptionsAsync());
    var state = await fixture.Manager.SubscriptionsAsync();
    Assert(state.Items.Single(item => item.Id == a).NodeCount == 2 && state.Items.Single(item => item.Id == a).LastError == "");
    Assert(state.Items.Single(item => item.Id == b).NodeCount == 1 && state.Items.Single(item => item.Id == b).LastError.Length > 0);
    Assert(state.ActiveId == activeId && Revision(fixture.Manager) == revision && !state.Busy);
});

await Check("removing inactive source preserves active nodes while removing active clears them", async () =>
{
    using var fixture = new Fixture(Fixtures.Source()); var id = await Add(fixture);
    var activeId = (await fixture.Manager.SubscriptionsAsync()).ActiveId; var revision = Revision(fixture.Manager);
    await fixture.Manager.RemoveSubscriptionAsync(id);
    Assert(Revision(fixture.Manager) == revision && Active(fixture).Count == 1);
    await fixture.Manager.RemoveSubscriptionAsync(activeId);
    Assert(Revision(fixture.Manager) > revision && Active(fixture).Count == 0 && fixture.ReadSettings().Pins.Count == 1);
    Assert((await fixture.Manager.SubscriptionsAsync()).ActiveId == "" && (await fixture.Manager.SnapshotAsync()).Selected == "REJECT");
});

await Check("file import adds and activates a reusable snapshot without losing previous profile", async () =>
{
    using var fixture = new Fixture(Fixtures.Source());
    var file = Path.Combine(fixture.Directory, "fixture-import.yaml");
    File.WriteAllText(file, "proxies:\n- name: Local New\n  type: vless\n  server: local-new.invalid\n  port: 443\n  uuid: fixture\n");
    await fixture.Manager.ImportFileAsync(file);
    var state = await fixture.Manager.SubscriptionsAsync(); var local = state.Items.Single(item => item.Active);
    Assert(state.Items.Count == 2 && local.Kind == "file" && local.Name == "fixture-import");
    Assert(Active(fixture).Single()["name"]!.GetValue<string>() == "Local New" && fixture.ReadSettings().Pins.Count == 1);
    await Refuses(() => fixture.Manager.UpdateSubscriptionAsync(local.Id));
    await Refuses(() => fixture.Manager.SetSubscriptionUrlAsync(local.Id, Fixtures.Url("replace-file")));
});

await Check("catalog corruption is refused without falling back to otherwise valid legacy source", () =>
{
    using var fixture = new Fixture(Fixtures.Source()); fixture.Manager.Dispose();
    const string broken = "{ invalid catalog"; File.WriteAllText(fixture.CatalogPath, broken);
    RefusesRead(() => SubscriptionSourceReader.LoadActiveNodes(fixture.Directory));
    RefusesRead(() => { using var reopened = new ManagerService(fixture.Directory); });
    Assert(File.ReadAllText(fixture.CatalogPath) == broken && File.ReadAllText(fixture.LegacyPath) == Fixtures.Source());
    return Task.CompletedTask;
});

await Check("catalog path occupied by a directory refuses reader and manager without legacy fallback", () =>
{
    using var fixture = new Fixture(Fixtures.Source()); fixture.Manager.Dispose();
    File.Delete(fixture.CatalogPath); System.IO.Directory.CreateDirectory(fixture.CatalogPath);
    RefusesRead(() => SubscriptionSourceReader.LoadActiveNodes(fixture.Directory));
    RefusesRead(() => { using var reopened = new ManagerService(fixture.Directory); });
    Assert(System.IO.Directory.Exists(fixture.CatalogPath) && File.ReadAllText(fixture.LegacyPath) == Fixtures.Source());
    return Task.CompletedTask;
});

await Check("unreadable shared catalog refuses reader and manager without overwriting or fallback", () =>
{
    using var fixture = new Fixture(Fixtures.Source()); fixture.Manager.Dispose();
    var expected = File.ReadAllText(fixture.CatalogPath);
    using (var locked = new FileStream(fixture.CatalogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        RefusesRead(() => SubscriptionSourceReader.LoadActiveNodes(fixture.Directory));
        RefusesRead(() => { using var reopened = new ManagerService(fixture.Directory); });
        Assert(locked.Length == Encoding.UTF8.GetByteCount(expected));
    }
    Assert(File.ReadAllText(fixture.CatalogPath) == expected);
    return Task.CompletedTask;
});

await Check("invalid catalog schema identities duplicates and orphan active profile are refused", () =>
{
    using var fixture = new Fixture(Fixtures.Source()); fixture.Manager.Dispose();
    var original = File.ReadAllText(fixture.CatalogPath);
    var invalids = new List<string>();
    void Alter(Action<JsonObject> change) { var json = JsonNode.Parse(original)!.AsObject(); change(json); invalids.Add(json.ToJsonString()); }
    Alter(json => json["version"] = 2);
    Alter(json => json["activeId"] = Guid.NewGuid().ToString("N"));
    Alter(json => json.Remove("version"));
    Alter(json => json["items"]![0]!.AsObject().Remove("id"));
    Alter(json => json["items"]![0]!["nodes"] = null);
    Alter(json => json["items"]![0]!["lastErrorCode"] = Fixtures.Secret);
    Alter(json => json["items"]!.AsArray().Add(json["items"]![0]!.DeepClone()));
    Alter(json => { var extra = json["items"]![0]!.DeepClone(); extra["id"] = Guid.NewGuid().ToString("N"); json["items"]!.AsArray().Add(extra); });
    Alter(json => json["items"]![0]!["kind"] = "remote-script");
    Alter(json => json["items"]![0]!["nodes"]!.AsArray().Add(json["items"]![0]!["nodes"]![0]!.DeepClone()));
    invalids.Add(original.Replace("\"version\": 1", "\"version\": 1, \"Version\": 1", StringComparison.Ordinal));
    foreach (var invalid in invalids)
    {
        File.WriteAllText(fixture.CatalogPath, invalid);
        RefusesRead(() => SubscriptionSourceReader.LoadActiveNodes(fixture.Directory));
        Assert(File.ReadAllText(fixture.CatalogPath) == invalid);
    }
    return Task.CompletedTask;
});

await Check("oversized catalog is refused before parsing or overwriting", () =>
{
    using var fixture = new Fixture(Fixtures.Source()); fixture.Manager.Dispose();
    using (var stream = File.Create(fixture.CatalogPath)) stream.SetLength(64L * 1024 * 1024 + 1);
    RefusesRead(() => SubscriptionSourceReader.LoadActiveNodes(fixture.Directory));
    Assert(new FileInfo(fixture.CatalogPath).Length == 64L * 1024 * 1024 + 1);
    return Task.CompletedTask;
});

await Check("catalog save failure preserves the old active source and cache", async () =>
{
    using var fixture = new Fixture(Fixtures.Source());
    fixture.Network.Reply = (_, _) => Task.FromResult(Fixtures.Response(Fixtures.Source(Fixtures.Leaf("Alternative"))));
    var id = await Add(fixture); var expected = File.ReadAllText(fixture.CatalogPath);
    File.SetAttributes(fixture.CatalogPath, FileAttributes.ReadOnly);
    try { await Refuses(() => fixture.Manager.UseSubscriptionAsync(id)); }
    finally { File.SetAttributes(fixture.CatalogPath, FileAttributes.Normal); }
    Assert(File.ReadAllText(fixture.CatalogPath) == expected && Active(fixture).Single()["name"]!.GetValue<string>() == "Fixture");
    Assert((await fixture.Manager.SnapshotAsync()).Nodes.Single().Name == "Fixture");
});

await Check("subscription capacity is enforced before any 33rd download", async () =>
{
    using var fixture = new Fixture();
    for (int i = 1; i <= 32; i++) await Add(fixture, "Subscription " + i, i.ToString());
    var requests = fixture.Network.Requests;
    await Refuses(() => fixture.Manager.AddSubscriptionAsync("Overflow", Fixtures.Url("overflow")));
    Assert((await fixture.Manager.SubscriptionsAsync()).Items.Count == 32 && fixture.Network.Requests == requests);
});

Console.WriteLine($"{passed} subscription checks passed; no real network or proxy core was used.");

static class Fixtures
{
    public const string Secret = "fixture-redaction-marker";
    public const string PrivatePath = "private-subscription-path";
    public const string Credential = "fixture-leaf-credential-marker";
    public static string Url(string suffix) => new UriBuilder("https://subscriptions.example.invalid/")
    { Path = PrivatePath, Query = "token=" + Secret + "-" + suffix }.Uri.AbsoluteUri;
    public static JsonObject Leaf(string name = "Fixture", string server = "example.invalid") => new()
    { ["name"] = name, ["type"] = "vless", ["server"] = server, ["port"] = 443, ["uuid"] = Credential, ["tls"] = true };
    public static string Source(params JsonObject[] nodes) => new JsonObject
    { ["proxies"] = new JsonArray((nodes.Length == 0 ? new[] { Leaf() } : nodes).Select(node => node.DeepClone()).ToArray()) }.ToJsonString(ManagerJson.Options);
    public static HttpResponseMessage Response(string source) => new(HttpStatusCode.OK) { Content = new StringContent(source, Encoding.UTF8, "application/yaml") };
}

sealed class Fixture : IDisposable
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClaudeSafe.SubscriptionTests"));
    private ManagerService? manager;
    public string Directory { get; } = Path.Combine(Root, Guid.NewGuid().ToString("N"));
    public string CatalogPath => Path.Combine(Directory, "subscriptions.json");
    public string LegacyPath => Path.Combine(Directory, "source.json");
    public FakeNetwork Network { get; } = new();
    public ManagerService Manager => manager ?? throw new InvalidOperationException();
    public Fixture(string? source = null, bool initialize = true)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var settings = new ManagerSettings { Pins = [new NodePin("Fixture", "vless", "example.invalid", 443)], Benchmark = new BenchmarkPreferences { ScheduleEnabled = false } };
        File.WriteAllText(Path.Combine(Directory, "settings.json"), JsonSerializer.Serialize(settings, ManagerJson.Options));
        if (source is not null) File.WriteAllText(LegacyPath, source);
        if (initialize) manager = new ManagerService(Directory, Network.CreateHandler);
    }
    public ManagerSettings ReadSettings() => JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(Path.Combine(Directory, "settings.json")), ManagerJson.Options)!;
    public void Dispose()
    {
        manager?.Dispose();
        var target = Path.GetFullPath(Directory);
        if (!target.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(target).Length != 32)
            throw new InvalidOperationException("Fixture cleanup target escaped its assigned directory.");
        if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, true);
    }
}

sealed class FakeNetwork
{
    private int requests;
    public int Requests => Volatile.Read(ref requests);
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } = (_, _) => Task.FromResult(Fixtures.Response(Fixtures.Source()));
    public HttpMessageHandler CreateHandler() => new Handler(async (request, cancellation) =>
    { Interlocked.Increment(ref requests); return await Reply(request, cancellation).ConfigureAwait(false); });
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request, cancellationToken);
    }
}

sealed class GeneratedStream(long length) : Stream
{
    private long position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    { int read = (int)Math.Min(count, length - position); Array.Fill(buffer, (byte)'x', offset, read); position += read; return read; }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); int read = (int)Math.Min(buffer.Length, length - position); buffer.Span[..read].Fill((byte)'x'); position += read; return ValueTask.FromResult(read); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
