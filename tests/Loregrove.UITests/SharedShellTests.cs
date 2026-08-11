using Microsoft.AspNetCore.Components;

namespace Loregrove.UITests;

public sealed class SharedShellTests
{
    private static readonly string[] ExpectedRoutes =
        ["/", "/ask", "/knowledge", "/library", "/review", "/search", "/settings"];

    [Fact]
    public void SharedUiDefinesEveryPrimaryRouteExactlyOnce()
    {
        var routes = typeof(UI.App).Assembly
            .GetTypes()
            .SelectMany(type => type.GetCustomAttributes(typeof(RouteAttribute), inherit: false))
            .Cast<RouteAttribute>()
            .Select(attribute => attribute.Template)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedRoutes, routes);
    }
}
