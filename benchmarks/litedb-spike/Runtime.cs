namespace LiteDbSpike;

internal static class Runtime
{
    /// <summary>Native AOT has no assembly files; PublishAot also clears IsDynamicCodeSupported under JIT, so not that.</summary>
    public static string Mode => typeof(object).Assembly.Location.Length is 0 ? "Native AOT" : "JIT";
}
