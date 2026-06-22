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

    // ---- Large generated catalog (for the "large list" demo) ----
    // Deterministically generated once and cached. Stands in for a database table with thousands
    // of rows. The point of the demo: this list NEVER goes into reactive state — the component
    // re-queries it server-side on every render from a few small state values (search/sort/page).

    private const int LargeCatalogSize = 5000;

    private static readonly string[] Adjectives =
        ["Mechanical", "Wireless", "Ergonomic", "Ultra-Wide", "Smart", "Portable", "Premium",
         "Compact", "Gaming", "Studio", "Industrial", "Minimalist", "Rugged", "Modular"];

    private static readonly string[] Nouns =
        ["Keyboard", "Mouse", "Headphones", "Monitor", "Desk Lamp", "Webcam", "Microphone",
         "Dock", "Hub", "Stand", "Cable", "Charger", "Speaker", "Tablet", "Stylus", "Trackpad"];

    private static readonly Lazy<List<Product>> LargeCatalog = new(() =>
    {
        var list = new List<Product>(LargeCatalogSize);
        for (var i = 0; i < LargeCatalogSize; i++)
        {
            var adj = Adjectives[i % Adjectives.Length];
            var noun = Nouns[(i / Adjectives.Length) % Nouns.Length];
            list.Add(new Product
            {
                Id = i + 1,
                Name = $"{adj} {noun} #{i + 1}",
                Description = $"A {adj.ToLowerInvariant()} {noun.ToLowerInvariant()} built for everyday productivity.",
                // Deterministic pseudo-price in the 9.99–509.99 range (no Random — keeps it stable).
                Price = 9.99m + (i * 37 % 500),
                ImageUrl = ""
            });
        }
        return list;
    });

    /// <summary>
    /// Queries the large catalog server-side: filter by search term, sort, and return just the
    /// requested page. Mirrors what a real <c>IQueryable</c>/EF Core query against a DB would do.
    /// </summary>
    public (List<Product> Items, int TotalCount) QueryCatalog(
        string? search, string sortBy, bool descending, int page, int pageSize)
    {
        IEnumerable<Product> query = LargeCatalog.Value;

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim().ToLowerInvariant();
            query = query.Where(p => p.Name.ToLowerInvariant().Contains(q));
        }

        query = sortBy switch
        {
            "Price" => descending ? query.OrderByDescending(p => p.Price) : query.OrderBy(p => p.Price),
            "Id" => descending ? query.OrderByDescending(p => p.Id) : query.OrderBy(p => p.Id),
            _ => descending ? query.OrderByDescending(p => p.Name) : query.OrderBy(p => p.Name),
        };

        var total = query.Count();
        var items = query.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return (items, total);
    }
}
