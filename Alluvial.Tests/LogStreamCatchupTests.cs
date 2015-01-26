using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Alluvial.Tests.BankDomain;
using FluentAssertions;
using NUnit.Framework;

namespace Alluvial.Tests
{
    [TestFixture]
    public class LogStreamCatchupTests
    {
        private string filename;
        private Guid[] aggregateIds;
        private LogFileStream stream;

        [SetUp]
        public void SetUp()
        {
            aggregateIds = new[] {Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()};
            filename = Path.GetTempFileName();
            File.WriteAllLines(filename, new []
            {
                string.Format("1/16/2015 6:30:21.000PM: [{0}] POST /login", aggregateIds[0]),
                string.Format("1/16/2015 6:30:21.050PM: [{0}] User: bob@contoso.com", aggregateIds[0]),
                string.Format("1/16/2015 6:30:21.060PM: [{0}] POST /login", aggregateIds[1]),
                string.Format("1/16/2015 6:30:21.100PM: [{0}] GET /accounts", aggregateIds[2]),
                string.Format("1/16/2015 6:30:21.150PM: [{0}] User: jane@contoso.com", aggregateIds[1]),
                string.Format("1/16/2015 6:30:21.160PM: [{0}] StatusCode: 200", aggregateIds[0]),
            });
            // stream = new LogFileStream(new FileStream(filename, FileMode.Open), Guid.NewGuid().ToString());
        }

        [Test]
        public async Task Projections_can_be_built_from_whole_event_streams_on_demand()
        {
            var projectionStore = new InMemoryProjectionStore<RequestResponseProjection>();
            var file = new FileStream(filename, FileMode.Open);
            var logLineMatcher = new Regex(@"(?<Timestamp>\S+): \[(?<ActivityId>[a-fA-F0-9\-]+)\] (?<Message>.+)");

            await StreamCatchup.Create(
                Stream.Create(async query =>
                {
                    file.Seek(query.Cursor.As<long>(), SeekOrigin.Begin);
                    var lines = new List<string>();

                    using (var lineStream = new StreamReader(file))
                    {
                        while (lines.Count < (query.BatchCount ?? 100))
                        {
                            try
                            {
                                lines.Add(await lineStream.ReadLineAsync());
                            }
                            catch (IOException)
                            {
                                break;
                            }                 
                        }
                    }

                    return lines.Select(l => logLineMatcher.Match(l ?? "")).Where(m => m != null && m.Captures.Count > 0).Select(m => new LogEntry
                    {
                        ActivityId = Guid.Parse(m.Groups["ActivityId"].Value),
                        Timestamp = DateTimeOffset.Parse(m.Groups["Timestamp"].Value),
                        Message = m.Groups["Message"].Value
                    });
                })
                .Map(logEntries => logEntries.GroupBy(l => l.ActivityId)
                                             .Select(group => Stream.Create(group.Key.ToString(), 
                                                                            _ => group)))
              )
            .Subscribe(RequestResponseProjector(), projectionStore)
            .RunSingleBatch();

            projectionStore.Count().Should().Be(3);
            projectionStore.Single(rr => rr.User == "bob@contoso.com").StatusCode.Should().Be(200);
            projectionStore.Single(rr => rr.User == "jane@contoso.com").LogMessages.Should().BeEmpty();
        }

        private static IStreamAggregator<RequestResponseProjection, LogEntry> RequestResponseProjector()
        {
            return Aggregator.Create<RequestResponseProjection, LogEntry>(
                (projection, events) =>
                {
                    foreach (var logEvent in events)
                    {
                        if (logEvent.Message.StartsWith("User:"))
                        {
                            projection.User = logEvent.Message.Substring("user: ".Length);
                        }
                        else if (logEvent.Message.StartsWith("StatusCode: "))
                        {
                            projection.StatusCode = int.Parse(logEvent.Message.Substring("StatusCode: ".Length));
                        }
                    }
                })
                .Pipeline(async (projection, e, next) => await next(projection ?? new RequestResponseProjection(), e));
        }
    }

    public class RequestResponseProjection : IMapProjection
    {
        public string User { get; set; }

        public int StatusCode { get; set; }

        private readonly List<string> messages = new List<string>();
        public List<string> LogMessages
        {
            get
            {
                return messages;
            }
        }

        public string AggregateId { get; set; }
    }
}