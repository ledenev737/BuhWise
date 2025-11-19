using System;

namespace BuhWise.Models
{
    public class OperationChange
    {
        public long Id { get; set; }
        public long? OperationId { get; set; }
        public string Action { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Details { get; set; } = string.Empty;
        public string? Reason { get; set; }
    }
}
