namespace AccentIt.Tests;

public class ScaffoldTests
{
    [Fact]
    public void WpfApplicationAssemblyIsReferenced()
    {
        var assemblyName = typeof(global::AccentIt.MainWindow).Assembly.GetName().Name;

        Assert.Equal("AccentIt", assemblyName);
    }
}
