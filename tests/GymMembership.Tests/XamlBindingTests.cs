using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GymMembership.Core.Models;
using Xunit;

/// <summary>
/// A binding to a property that does not exist fails silently at run time (the label stays blank, the button does nothing),
/// and this project's build does not warn about it. This reads every page and checks each binding path against the real type.
/// </summary>
public class XamlBindingTests
{
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2009/xaml";
    static readonly Assembly Core = typeof(Payment).Assembly;

    static string ViewsFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "gym-membership.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("solution folder not found"), "src", "GymMembership.App", "Views");
    }

    public static IEnumerable<object[]> Pages() => Directory.GetFiles(ViewsFolder(), "*.xaml").Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Pages))]
    public void Every_binding_on_the_page_points_at_a_real_property(string file)
    {
        var doc = XDocument.Load(Path.Combine(ViewsFolder(), file));
        var problems = new List<string>();
        Walk(doc.Root!, null, problems, file);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    static void Walk(XElement el, Type? scope, List<string> problems, string file)
    {
        if (el.Attribute(X + "DataType") is { } dt) scope = ResolveType(el, dt.Value) ?? scope;

        // <Binding Path="..."/> inside a MultiBinding
        if (el.Name.LocalName == "Binding" && el.Attribute("Path") is { } p && scope is not null) Check(scope, p.Value, problems, file, "<Binding>");

        // ItemDisplayBinding reads from each picker item, not from the page, so it is checked by eye
        foreach (var a in el.Attributes().Where(a => a.Value.StartsWith("{Binding") && a.Name.LocalName != "ItemDisplayBinding"))
        {
            var (path, source) = Parse(a.Value);
            var type = source is not null ? ResolveType(el, source) : scope;
            if (type is not null && path is not null) Check(type, path, problems, file, a.Value);
        }
        foreach (var child in el.Elements()) Walk(child, scope, problems, file);
    }

    // "{Binding Payment.CreatedAt, Converter=...}" or "{Binding Source={RelativeSource AncestorType={x:Type vm:Foo}}, Path=BarCommand}"
    static (string? Path, string? SourceType) Parse(string markup)
    {
        var inner = markup[1..^1].Trim()["Binding".Length..].Trim();
        var parts = new List<string>(); int depth = 0, start = 0; var quoted = false;
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\'') quoted = !quoted;   // format strings such as 'h:mm tt' hold commas and colons
            else if (quoted) continue;
            else if (inner[i] == '{') depth++;
            else if (inner[i] == '}') depth--;
            else if (inner[i] == ',' && depth == 0) { parts.Add(inner[start..i].Trim()); start = i + 1; }
        }
        parts.Add(inner[start..].Trim());

        string? path = null, source = null;
        foreach (var part in parts.Where(x => x.Length > 0))
        {
            if (part.StartsWith("Source=")) { var m = Regex.Match(part, @"AncestorType=\{x:Type ([^}]+)\}"); if (m.Success) source = m.Groups[1].Value; else return (null, null); }
            else if (part.StartsWith("Path=")) path = part["Path=".Length..];
            else if (!part.Contains('=')) path = part;
        }
        return (path is null or "." ? null : path, source);
    }

    static Type? ResolveType(XElement scopeEl, string typeRef)
    {
        var colon = typeRef.IndexOf(':');
        if (colon < 0) return null;
        var uri = scopeEl.GetNamespaceOfPrefix(typeRef[..colon])?.NamespaceName;
        var m = uri is null ? null : Regex.Match(uri, @"clr-namespace:([^;]+);assembly=GymMembership\.Core");
        return m is { Success: true } ? Core.GetType($"{m.Groups[1].Value}.{typeRef[(colon + 1)..]}") : null;
    }

    static void Check(Type type, string path, List<string> problems, string file, string where)
    {
        var current = type;
        foreach (var segment in path.Split('.'))
        {
            var name = segment.Split('[')[0];
            var prop = current.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null) { problems.Add($"{file}: '{path}' (in {where}): {current.Name} has no property '{name}'"); return; }
            current = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        }
    }
}
