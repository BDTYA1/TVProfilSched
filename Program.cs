using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using AngleSharp;
using AngleSharp.Dom;

namespace TVProfilSched;

internal class ReqData
{
    public readonly string datum;
    public readonly string kanal;
    public readonly string bCodeName;
    public readonly int bCode;

    public ReqData(string date, string channel)
    {
        datum = date;
        kanal = channel;

        int b = 2;
        int c = 4;
        string a = datum + kanal + c;
        string ua = kanal + datum;

        if (string.IsNullOrEmpty(ua))
            ua = "none";

        c += ua.Sum(ch => ch);
        for (int i = a.Length - 1; i > 0; i--)
            b += (a[i] + c * 2) * i;

        bCode = b;
        bCodeName = "b" + (int)b.ToString()[^1];
    }
}

internal static class Program
{
    private const int MaxConnections = 8;

    private static readonly HttpClient Client = new Func<HttpClient>(() =>
    {
        HttpClient client = new(new HttpClientHandler { MaxConnectionsPerServer = MaxConnections, UseCookies = false });
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
    })();

    private static readonly SemaphoreSlim Semaphore = new(MaxConnections);

    private static IEnumerable<DateTime> DateRange(DateTime from, DateTime to)
    {
        for (DateTime day = from.Date; day.Date <= to.Date; day = day.AddDays(1))
            yield return day;
    }

    private static async Task<List<string>> RunScrape(List<DateTime> dateRange, string channel, string searchTerm)
    {
        List<string> result = [];
        List<DateTime> processed = [];
        DateTime[] remaining = dateRange.Except(processed).ToArray();

        while (remaining.Length > 0)
        {
            CancellationTokenSource ctsIpBlock = new();
            CancellationTokenSource ctsRateLimit = new();

            await Task.WhenAll(remaining.Select(async dt =>
            {
                await Semaphore.WaitAsync();
                try
                {
                    if (ctsIpBlock.Token.IsCancellationRequested || ctsRateLimit.Token.IsCancellationRequested)
                        return;

                    ReqData data = new(dt.ToString("yyyy-MM-dd"), channel);
                    Console.WriteLine($"Parsing {data.datum}");

                    HttpResponseMessage message = await Client.GetAsync(
                        $"https://tvprofil.com/gb/tvschedule/program/?callback=tvprogramen{data.bCodeName}&datum={data.datum}&kanal={data.kanal}&{data.bCodeName}={data.bCode}");
                    if (message.StatusCode == HttpStatusCode.Forbidden)
                    {
                        Console.WriteLine($"Problem occurred getting {data.datum}: IP blocked. Try again later");
                        await ctsIpBlock.CancelAsync();
                        return;
                    }

                    string html = await message.Content.ReadAsStringAsync();
                    html = html.Replace($"tvprogramen{data.bCodeName}(", "")[..^1];

                    if (!TryNodeParse(html, out JsonNode node) || node is null)
                    {
                        Console.WriteLine($"Problem occurred getting {data.datum}: Malformed data");
                        return;
                    }

                    if (!message.IsSuccessStatusCode)
                    {
                        if (node["code"]?.GetValue<int>() == 1226) // rate limit
                            await ctsRateLimit.CancelAsync();
                        else if (node["message"] is not null)
                            Console.WriteLine($"Problem occurred getting {data.datum}: {node["message"]}");
                        else
                            Console.WriteLine($"Problem occurred getting {data.datum}: {message.StatusCode} - {message.ReasonPhrase}");
                        return;
                    }

                    string program = node["data"]?["program"]?.ToString();
                    if (string.IsNullOrWhiteSpace(program))
                        return;

                    IDocument document = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(program));
                    foreach (IElement row in document.QuerySelectorAll(".row"))
                    {
                        IElement col = row.QuerySelector(".col:not(.time)");
                        IElement showElm = row.GetElementsByTagName("a").FirstOrDefault();

                        if (!string.IsNullOrWhiteSpace(searchTerm))
                        {
                            string titles = col?.TextContent + showElm?.TextContent + (showElm?.GetAttribute("title") ?? "");
                            if (!titles.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }

                        DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeSeconds(
                            long.Parse(row.GetAttribute("data-ts") ?? string.Empty));
                        result.Add($"{timestamp:s}: {col?.TextContent}");
                    }

                    processed.Add(dt);
                }
                finally
                {
                    Semaphore.Release();
                }
            }));

            if (ctsRateLimit.Token.IsCancellationRequested)
            {
                Console.WriteLine("Rate limited! Sleeping for 20 seconds...");
                await Task.Delay(20000);
                remaining = dateRange.Except(processed).ToArray();
            }
            else
            {
                break;
            }
        }

        return result;
    }

    private static bool TryNodeParse(string html, out JsonNode node)
    {
        try
        {
            node = JsonNode.Parse(html);
            return true;
        }
        catch (Exception)
        {
            node = null;
            return false;
        }
    }

    public static async Task Main(string[] args)
    {
        if (args.Length > 0)
            Client.DefaultRequestHeaders.Add("Cookie", "tvp_login=" + args[0]);
        else if (File.Exists("tvp_login"))
            Client.DefaultRequestHeaders.Add("Cookie", "tvp_login=" + File.ReadAllText("tvp_login").Trim());

        Console.Write("Type a starting date (yyyy-MM-dd): ");
        string startDate = Console.ReadLine();

        if (!DateTime.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start))
        {
            Console.WriteLine("Date is invalid or in an invalid format (not yyyy-MM-dd).");
            return;
        }

        Console.Write("Type an ending date (optional): ");
        string endDate = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(endDate))
            endDate = DateTime.Now.ToString("yyyy-MM-dd");

        if (!DateTime.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime end))
        {
            Console.WriteLine("Date is invalid or in an invalid format (not yyyy-MM-dd).");
            return;
        }

        Console.Write("Type a channel name: ");
        string channel = Console.ReadLine();

        Console.Write("Type a search term (optional): ");
        string searchTerm = Console.ReadLine();

        File.Delete("out.txt");

        List<string> entries = await RunScrape(DateRange(start, end).ToList(), channel, searchTerm);
        entries.Sort();

        await File.WriteAllLinesAsync("out.txt", entries);
    }
}
