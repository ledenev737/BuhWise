using System;

namespace BuhWise.Models
{
    public class OperationDraft
    {
        public DateTime Date { get; set; }
        public OperationType Type { get; set; }
        public Currency SourceCurrency { get; set; }
        public Currency TargetCurrency { get; set; }
        public double SourceAmount { get; set; }
        public double Rate { get; set; }
        public double? Commission { get; set; }
    }
}
