namespace ManagedDrive.Tests;

public sealed class MemoryHeadroomBudgetTests
{
    private const ulong Reserve = MemoryHeadroomBudget.ReserveBytes;

    [Fact]
    public void WouldExceed_ChargesFromEveryCaller_DrawDownTheSameHeadroom()
    {
        // Two disks sharing one budget: what the first is granted must no longer be available to
        // the second, even while the cached reading is still fresh.
        var queries = 0;
        var budget = new MemoryHeadroomBudget(() =>
        {
            queries++;
            return Reserve + 1024;
        }, refreshMs: int.MaxValue);

        Assert.False(budget.WouldExceed(768)); // "disk A"
        Assert.False(budget.WouldExceed(256)); // "disk B": exactly the 256 bytes A left
        Assert.Equal(1, queries);

        // Headroom is now spent, so the next byte from either disk must go back to the provider.
        budget.WouldExceed(1);
        Assert.Equal(2, queries);
    }

    [Fact]
    public void WouldExceed_ZeroBytes_NeverQueries()
    {
        var queries = 0;
        var budget = new MemoryHeadroomBudget(() =>
        {
            queries++;
            return 0;
        });

        Assert.False(budget.WouldExceed(0));
        Assert.Equal(0, queries);
    }

    [Fact]
    public void WouldExceed_ProviderAtOrBelowReserve_DeniesAnyGrowth()
    {
        var budget = new MemoryHeadroomBudget(() => Reserve);

        Assert.True(budget.WouldExceed(1));
    }
}
