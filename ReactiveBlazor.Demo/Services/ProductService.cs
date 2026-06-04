using System.Collections.Generic;
using System.Linq;
using ReactiveBlazor.Demo.Models;

namespace ReactiveBlazor.Demo.Services;

public class ProductService
{
    private static readonly List<Product> MockProducts = new()
    {
        new Product
        {
            Id = 1,
            Name = "Mechanical Keyboard",
            Description = "75% layout, hot-swappable tactile switches, and premium double-shot PBT keycaps.",
            Price = 129.99m,
            ImageUrl = "https://images.unsplash.com/photo-1618384887929-16ec33fab9ef?w=400&auto=format&fit=crop&q=60"
        },
        new Product
        {
            Id = 2,
            Name = "Ergonomic Wireless Mouse",
            Description = "Precision tracking, adjustable DPI, and comfortable thumb rest for long hours.",
            Price = 79.99m,
            ImageUrl = "https://images.unsplash.com/photo-1615663245857-ac93bb7c39e7?w=400&auto=format&fit=crop&q=60"
        },
        new Product
        {
            Id = 3,
            Name = "Noise-Cancelling Headphones",
            Description = "Active noise cancellation, 30-hour battery life, and ultra-comfortable ear cushions.",
            Price = 249.99m,
            ImageUrl = "https://images.unsplash.com/photo-1505740420928-5e560c06d30e?w=400&auto=format&fit=crop&q=60"
        },
        new Product
        {
            Id = 4,
            Name = "Ultra-Wide Creator Monitor",
            Description = "34-inch curved display, 99% sRGB color accuracy, and USB-C power delivery.",
            Price = 449.99m,
            ImageUrl = "https://images.unsplash.com/photo-1527443224154-c4a3942d3acf?w=400&auto=format&fit=crop&q=60"
        },
        new Product
        {
            Id = 5,
            Name = "Smart RGB Desk Lamp",
            Description = "Voice control, adjustable color temperature, and built-in wireless phone charger.",
            Price = 49.99m,
            ImageUrl = "https://images.unsplash.com/photo-1507473885765-e6ed057f782c?w=400&auto=format&fit=crop&q=60"
        },
        new Product
        {
            Id = 6,
            Name = "Developer Insulated Tumbler",
            Description = "Double-wall vacuum insulated, keeps coffee hot for 8 hours. Matte black finish.",
            Price = 24.99m,
            ImageUrl = "https://images.unsplash.com/photo-1514432324607-a09d9b4aefdd?w=400&auto=format&fit=crop&q=60"
        }
    };

    public List<Product> GetProducts() => MockProducts;

    public Product? GetProductById(int id) => MockProducts.FirstOrDefault(p => p.Id == id);
}
