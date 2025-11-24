using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

public static class Extensions {
    public static IEnumerable<T> SelectRecursive<T>(this T? source, Func<T, T?> selector) {
        if (source == null)
            return Enumerable.Empty<T>();
        return new[] { source }.Concat(selector(source).SelectRecursive(selector));
    }
}