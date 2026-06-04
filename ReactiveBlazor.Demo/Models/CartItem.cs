namespace ReactiveBlazor.Demo.Models;

public class CartItem
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public decimal Price { get; set; }
    public int Quantity { get; set; }
    public string ImageUrl { get; set; } = "";

    public decimal TotalPrice => Price * Quantity;
}
