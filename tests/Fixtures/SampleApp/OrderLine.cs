namespace SampleApp;

public class OrderLine
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public Order? Order { get; set; }

    public string Product { get; set; } = string.Empty;
}
