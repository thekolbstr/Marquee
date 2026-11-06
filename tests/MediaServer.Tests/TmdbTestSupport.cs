using System.Net;
using System.Text;
using System.Text.Json;
using MediaServer.Api.Data;
using Microsoft.Extensions.Configuration;

namespace MediaServer.Tests;

/// <summary>Scripted HTTP handler: records every request and answers from a routing function.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public int Count(string pathContains) => Requests.Count(r => r.RequestUri!.PathAndQuery.Contains(pathContains));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        return Task.FromResult(route(request));
    }
}

public static class TmdbJson
{
    public static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Status(HttpStatusCode c) => new(c);

    /// <summary>(id, title, "YYYY-MM-DD", popularity). Emits movie and tv field names so one helper serves both.</summary>
    public static HttpResponseMessage Search(params (int Id, string Title, string Date, double Pop)[] hits) =>
        Ok(JsonSerializer.Serialize(new
        {
            results = hits.Select(h => new
            {
                id = h.Id, title = h.Title, original_title = h.Title, name = h.Title, original_name = h.Title,
                release_date = h.Date, first_air_date = h.Date, popularity = h.Pop, poster_path = $"/poster{h.Id}.jpg", overview = "o",
            }),
        }));

    public static HttpResponseMessage Details(int id, string title, string date) =>
        Ok(JsonSerializer.Serialize(new
        {
            id, title, name = title, original_title = title, original_name = title, release_date = date, first_air_date = date,
            overview = "Overview", poster_path = $"/poster{id}.jpg", backdrop_path = $"/backdrop{id}.jpg", vote_average = 8.2,
            genres = new[] { new { name = "Action" }, new { name = "Sci-Fi" } },
        }));
}

public sealed class TestDb : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("tmdb").FullName;
    public Database Db { get; }
    public IConfiguration Config { get; }

    public TestDb(Dictionary<string, string?>? extra = null)
    {
        var d = new Dictionary<string, string?> { ["Database:Path"] = Path.Combine(Dir, "t.db"), ["Library:Root"] = Path.Combine(Dir, "lib"), ["Images:CacheDir"] = Path.Combine(Dir, "img") };
        if (extra is not null) foreach (var (k, v) in extra) d[k] = v;
        Config = new ConfigurationBuilder().AddInMemoryCollection(d).Build();
        Db = new Database(Config);
        Db.Migrate();
    }

    public long Insert(string path, string kind, string title, int? year = null)
    {
        using var c = Db.Open();
        Database.Execute(c, null, """
            INSERT INTO media_files(path,size,mtime_ticks,first_seen,last_scanned,parser_version,kind,title,year)
            VALUES(@p,1,1,'x','x',1,@k,@t,@y)
            """, ("@p", path), ("@k", kind), ("@t", title), ("@y", year));
        return Convert.ToInt64(Database.Scalar(c, "SELECT last_insert_rowid()"));
    }

    public string? Scalar(string sql) { using var c = Db.Open(); return Convert.ToString(Database.Scalar(c, sql)); }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}
