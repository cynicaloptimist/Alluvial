using System;

namespace Alluvial.Tests
{
    public class LogEntry
    {
        public Guid ActivityId { get; set; }

        public DateTimeOffset Timestamp { get; set; }

        public string Message { get; set; }
    }
}