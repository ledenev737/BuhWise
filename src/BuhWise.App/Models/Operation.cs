using System;

namespace BuhWise.Models
{
    public class Operation
    {
        public long Id { get; set; }
        public DateTime Date { get; set; }
        public OperationType Type { get; set; }
        public Currency SourceCurrency { get; set; }
        public double SourceAmount { get; set; }
        public Currency TargetCurrency { get; set; }
        public double TargetAmount { get; set; }
        public double Rate { get; set; }
        public double? Commission { get; set; }
        public double UsdEquivalent { get; set; }
    }
}
