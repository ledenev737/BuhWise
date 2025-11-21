using System;

namespace BuhWise.Models
{
    public class Operation
    {
        public long Id { get; set; }
        public DateTime Date { get; set; }
        public OperationType Type { get; set; }
        public string SourceCurrency { get; set; } = string.Empty;
        public double SourceAmount { get; set; }
        public string TargetCurrency { get; set; } = string.Empty;
        public double TargetAmount { get; set; }
        public double Rate { get; set; }
        public double? Commission { get; set; }
        public double UsdEquivalent { get; set; }
        public string? ExpenseCategory { get; set; }
        public string? ExpenseComment { get; set; }
    }
}
