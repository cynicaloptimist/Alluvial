using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Alluvial.Tests
{
    public class LogFileStream : IStream<LogEntry>, IDisposable
    {
        private readonly string streamId;
        private readonly FileStream file;

        public LogFileStream(FileStream file, string streamId)
        {
            if (file == null)
            {
                throw new ArgumentNullException("file");
            }
            if (streamId == null)
            {
                throw new ArgumentNullException("streamId");
            }
            this.file = file;
            this.streamId = streamId;

            LogLineMatcher = new Regex(@"(?<Timestamp>\S+): \[(?<ActivityId>[a-fA-F0-9\-]+)\] (?<Message>.+)");
        }

        public string Id
        {
            get
            {
                return streamId;
            }
        }

        public Regex LogLineMatcher { get; set; }

        public async Task<IStreamBatch<LogEntry>> Fetch(IStreamQuery query)
        {
            file.Seek((long)query.Cursor.Position, SeekOrigin.Begin);
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

            var logEvents = StreamQueryBatch.Create(lines.Select(l => LogLineMatcher.Match(l)).Where(m => m != null).Select(m => new LogEntry
            {
                ActivityId = Guid.Parse(m.Groups["ActivityId"].Value),
                Timestamp = DateTimeOffset.Parse(m.Groups["Timestamp"].Value),
                Message = m.Groups["Message"].Value
            }), query.Cursor);

            query.Cursor.AdvanceTo(file.Position);

            return logEvents;
        }

        public void Dispose()
        {
            file.Dispose();
        }
    }
}