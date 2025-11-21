using System;

namespace BuhWise.Models
{
    public class OperationDraft
    {
        public DateTime Date { get; set; }
        public OperationType Type { get; set; }
        public string SourceCurrency { get; set; } = string.Empty;
        public string TargetCurrency { get; set; } = string.Empty;
        public double SourceAmount { get; set; }
        public double Rate { get; set; }
        public double? Commission { get; set; }
        public string? ExpenseCategory { get; set; }
        public string? ExpenseComment { get; set; }
    }
}
