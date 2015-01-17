using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alluvial.Tests.BankDomain;
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
            stream = new LogFileStream(new FileStream(filename, FileMode.Open), Guid.NewGuid().ToString());
        }

        [Test]
        public async Task Projections_can_be_built_from_whole_event_streams_on_demand()
        {
            var projectionStore = new InMemoryProjectionStore<RequestResponseProjection>();

            await StreamCatchup.Create(stream)
                         .Subscribe(RequestResponseProjector(), projectionStore)
                         .RunSingleBatch();
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

    internal class RequestResponseProjection : IMapProjection
    {
        public string User { get; set; }

        public int StatusCode { get; set; }

        public List<string> LogMessages { get; set; }

        public string AggregateId { get; set; }
    }
}