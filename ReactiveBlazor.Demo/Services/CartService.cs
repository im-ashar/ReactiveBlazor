using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using ReactiveBlazor.Demo.Models;

namespace ReactiveBlazor.Demo.Services;

public class CartService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ProductService _productService;
    
    // In-memory store for user carts. Key is Cart ID Guid.
    private static readonly ConcurrentDictionary<Guid, List<CartItem>> Carts = new();
    
    private const string CookieName = "ReactiveCartId";

    public CartService(IHttpContextAccessor httpContextAccessor, ProductService productService)
    {
        _httpContextAccessor = httpContextAccessor;
        _productService = productService;
    }

    private Guid GetOrCreateCartId()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Guid.Empty;

        // Try reading from request cookies
        if (context.Request.Cookies.TryGetValue(CookieName, out var value) && Guid.TryParse(value, out var cartId))
        {
            return cartId;
        }

        // If not found, create a new one
        var newId = Guid.NewGuid();
        
        // Only write cookie if response headers haven't started sending yet
        if (!context.Response.HasStarted)
        {
            context.Response.Cookies.Append(CookieName, newId.ToString(), new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Expires = DateTimeOffset.UtcNow.AddDays(14)
            });
        }

        return newId;
    }

    public List<CartItem> GetCartItems()
    {
        var cartId = GetOrCreateCartId();
        if (cartId == Guid.Empty) return [];

        return Carts.GetOrAdd(cartId, _ => []);
    }

    public void AddToCart(int productId, int quantity = 1)
    {
        var cartId = GetOrCreateCartId();
        if (cartId == Guid.Empty) return;

        var items = Carts.GetOrAdd(cartId, _ => []);
        lock (items)
        {
            var existing = items.FirstOrDefault(i => i.ProductId == productId);
            if (existing is not null)
            {
                existing.Quantity += quantity;
            }
            else
            {
                var product = _productService.GetProductById(productId);
                if (product is not null)
                {
                    items.Add(new CartItem
                    {
                        ProductId = product.Id,
                        ProductName = product.Name,
                        Price = product.Price,
                        ImageUrl = product.ImageUrl,
                        Quantity = quantity
                    });
                }
            }
        }
    }

    public void RemoveFromCart(int productId)
    {
        var cartId = GetOrCreateCartId();
        if (cartId == Guid.Empty) return;

        var items = Carts.GetOrAdd(cartId, _ => []);
        lock (items)
        {
            items.RemoveAll(i => i.ProductId == productId);
        }
    }

    public void UpdateQuantity(int productId, int quantity)
    {
        var cartId = GetOrCreateCartId();
        if (cartId == Guid.Empty) return;

        if (quantity <= 0)
        {
            RemoveFromCart(productId);
            return;
        }

        var items = Carts.GetOrAdd(cartId, _ => []);
        lock (items)
        {
            var existing = items.FirstOrDefault(i => i.ProductId == productId);
            if (existing is not null)
            {
                existing.Quantity = quantity;
            }
        }
    }

    public int GetCartCount()
    {
        var cartId = GetOrCreateCartId();
        if (cartId == Guid.Empty) return 0;

        var items = Carts.GetOrAdd(cartId, _ => []);
        lock (items)
        {
            return items.Sum(i => i.Quantity);
        }
    }

    public decimal GetSubtotal()
    {
        var cartId = GetOrCreateCartId();
        if (cartId == Guid.Empty) return 0m;

        var items = Carts.GetOrAdd(cartId, _ => []);
        lock (items)
        {
            return items.Sum(i => i.TotalPrice);
        }
    }
}
