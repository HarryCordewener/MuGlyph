using Microsoft.Extensions.Logging;
using SharpMUTerm.Crawler;
using SharpMUTerm.Crawler.Output;
using SharpMUTerm.Crawler.Probing;
using SharpMUTerm.Crawler.Scheduling;
using SharpMUTerm.Crawler.Storage;

var parsed = CommandLine.Parse(args);

if (parsed.WantsHelp)
{
    Console.WriteLine(CommandLine.Usage);
    return 0;
}

if (parsed.Error is { } error)
{
    Console.Error.WriteLine(error);
    return 2;
}

var options = parsed.Options!;
foreach (var note in parsed.Notes)
{
    Console.Error.WriteLine(note);
}

using var loggerFactory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(args.Contains("--quiet") ? LogLevel.Warning : LogLevel.Information)

    // TelnetNegotiationCore narrates its whole plugin set at Information on every connection — thirty
    // lines per host, which on a crawl of any size is the only thing you can see. It shares the logger
    // it is given, so the category is the only seam: everything telnet says goes to "telnet" and is
    // quiet unless it is a warning, while the crawl's own account of what it found stays visible.
    .AddFilter("telnet", LogLevel.Warning)
    .AddSimpleConsole(console =>
    {
        console.SingleLine = true;
        console.TimestampFormat = "HH:mm:ss ";
    }));

var logger = loggerFactory.CreateLogger("crawl");

Directory.CreateDirectory(options.OutputDirectory);
var store = new CrawlStore(Path.Combine(options.OutputDirectory, "state.json"));

var resumed = store.Load(out var storeProblem);
if (storeProblem is not null)
{
    logger.LogWarning("Starting without previous state: {Problem}.", storeProblem);
}

var frontier = new CrawlFrontier(options, resumed);
var now = DateTimeOffset.UtcNow;
foreach (var seed in options.Seeds)
{
    frontier.AddSeed(seed, now);
}

if (frontier.Records.Count == 0)
{
    Console.Error.WriteLine(
        "Nothing to crawl: no seeds were given and no previous run's state was found. Try --help.");
    return 2;
}

var due = frontier.Records.Count(record => record.IsDue(now));
logger.LogInformation(
    "{Known} host(s) known, {Due} due now. Resuming from {Store}.",
    frontier.Records.Count, due, store.Path);
logger.LogInformation(
    "Limits: {Hosts} hosts, {Duration}, depth {Depth}; {Concurrency} at once, {Interval}s apart; "
    + "revisit after {Revisit}h.",
    options.MaxHosts, options.MaxDuration, options.MaxDepth, options.MaxConcurrency,
    options.GlobalInterval.TotalSeconds, options.RevisitInterval.TotalHours);
logger.LogInformation("Identifying as {Identity}.", options.TerminalTypes[0]);

if (due == 0)
{
    logger.LogInformation(
        "Every known host has been visited within its revisit interval. Nothing to do — this is the "
        + "politeness working, not a failure.");
}

using var interrupt = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.Error.WriteLine("Interrupted; finishing the connections already open.");
    interrupt.Cancel();
};

using var observations = new ObservationLog(Path.Combine(options.OutputDirectory, "observations.jsonl"));

var crawler = new MsspCrawler(
    options,
    new TelnetMsspProbe(options, logger: loggerFactory.CreateLogger("telnet")),
    frontier,
    TimeProvider.System,
    logger,
    observations,
    store.Save);

var summary = await crawler.RunAsync(interrupt.Token);

var reportPath = Path.Combine(options.OutputDirectory, "report.md");
CrawlReport.Write(reportPath, summary, options);

logger.LogInformation(
    "Done: {Contacted} contacted, {WithMssp} answered with MSSP, {Known} host(s) known. Report: {Report}",
    summary.Contacted, summary.WithMssp, summary.Hosts.Count, reportPath);

// 0 when the run ended on its own terms, 1 when a cap or an interrupt cut it short — so a scheduled
// run can tell "finished" from "ran out of budget" without parsing the report.
return summary.StopReason == CrawlStopReason.Exhausted ? 0 : 1;
