namespace BuhWise.Models
{
    public class Currency
    {
        public long Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;

        public override string ToString() => Code;
    }
}
