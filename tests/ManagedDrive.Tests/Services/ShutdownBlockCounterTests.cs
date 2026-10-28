using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

public sealed class ShutdownBlockCounterTests
{
    /// <summary>
    /// Creates a counter that records how often the reason was registered and cleared.
    /// </summary>
    private static ShutdownBlockCounter Create(bool registers, int[] counts) => new(
        () =>
        {
            counts[0]++;
            return registers;
        },
        () => counts[1]++);

    [Fact]
    public void Acquire_FirstHolder_RegistersOnce()
    {
        var counts = new int[2];
        var counter = Create(registers: true, counts);

        using var first = counter.Acquire();
        using var second = counter.Acquire();

        Assert.Equal(1, counts[0]);
        Assert.Equal(0, counts[1]);
    }

    [Fact]
    public void Dispose_EarlierHolderWhileAnotherIsAlive_KeepsTheReason()
    {
        var counts = new int[2];
        var counter = Create(registers: true, counts);
        var first = counter.Acquire();
        using var second = counter.Acquire();

        first.Dispose();

        Assert.Equal(0, counts[1]);
    }

    [Fact]
    public void Dispose_LastHolder_ClearsTheReasonOnce()
    {
        var counts = new int[2];
        var counter = Create(registers: true, counts);
        var first = counter.Acquire();
        var second = counter.Acquire();

        first.Dispose();
        second.Dispose();
        second.Dispose();

        Assert.Equal(1, counts[1]);
    }

    [Fact]
    public void Dispose_RegistrationFailed_DoesNotClear()
    {
        var counts = new int[2];
        var counter = Create(registers: false, counts);

        counter.Acquire().Dispose();

        Assert.Equal(0, counts[1]);
    }

    [Fact]
    public void Acquire_AfterEveryHolderLeft_RegistersAgain()
    {
        var counts = new int[2];
        var counter = Create(registers: true, counts);
        counter.Acquire().Dispose();

        counter.Acquire().Dispose();

        Assert.Equal(2, counts[0]);
        Assert.Equal(2, counts[1]);
    }

    [Fact]
    public void Acquire_EarlierRegistrationFailed_RetriesForALaterHolderAndClearsAtTheEnd()
    {
        var attempts = 0;
        var cleared = 0;
        var counter = new ShutdownBlockCounter(() => ++attempts > 1, () => cleared++);
        var first = counter.Acquire();
        var second = counter.Acquire();

        first.Dispose();
        Assert.Equal(0, cleared);
        second.Dispose();

        Assert.Equal(2, attempts);
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void Acquire_RegisterThrows_LeavesNoHolderBehind()
    {
        var attempts = 0;
        var cleared = 0;
        var counter = new ShutdownBlockCounter(
            () => ++attempts > 1 ? true : throw new InvalidOperationException("no window"),
            () => cleared++);

        Assert.Throws<InvalidOperationException>(counter.Acquire);
        counter.Acquire().Dispose();

        Assert.Equal(2, attempts);
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void Dispose_ClearThrows_PropagatesAndTheNextAcquireRegistersAgain()
    {
        var registered = 0;
        var throwOnClear = true;
        var counter = new ShutdownBlockCounter(
            () =>
            {
                registered++;
                return true;
            },
            () =>
            {
                if (throwOnClear)
                {
                    throw new InvalidOperationException("window gone");
                }
            });
        var holder = counter.Acquire();

        Assert.Throws<InvalidOperationException>(holder.Dispose);
        throwOnClear = false;
        counter.Acquire().Dispose();

        Assert.Equal(2, registered);
    }
}
