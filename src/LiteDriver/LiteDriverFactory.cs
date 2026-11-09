namespace LiteDriver;

/// <summary>内部构建入口的公开薄封装，供 Hosting 集成层调用。</summary>
public static class LiteDriverFactory
{
    /// <summary>用指定观察者构建驱动。</summary>
    public static LiteDriver BuildWithObserver(LiteDriverBuilder builder, Observability.IDriverObserver observer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.BuildWithObserver(observer);
    }
}
