namespace Dashboard.Tests;

public class HistoryBufferTests
{
    [Fact]
    public void ANewBufferIsEmpty()
    {
        var buffer = new HistoryBuffer<int>(3);

        Assert.Empty(buffer);
        Assert.Equal(3, buffer.Capacity);
    }

    [Fact]
    public void ItemsAreKeptOldestFirst()
    {
        var buffer = new HistoryBuffer<int>(3) { 1, 2 };

        Assert.Equal([1, 2], buffer);
        Assert.Equal(1, buffer[0]);
        Assert.Equal(2, buffer[1]);
    }

    [Fact]
    public void AFullBufferDropsTheOldestItem()
    {
        var buffer = new HistoryBuffer<int>(3) { 1, 2, 3, 4, 5 };

        Assert.Equal([3, 4, 5], buffer);
        Assert.Equal(3, buffer.Count);
        Assert.Equal(5, buffer[2]);
    }

    [Fact]
    public void ItWrapsAroundManyTimes()
    {
        var buffer = new HistoryBuffer<int>(30);
        for (var item = 1; item <= 100; item++)
        {
            buffer.Add(item);
        }

        Assert.Equal(Enumerable.Range(71, 30), buffer);
    }

    [Fact]
    public void AnIndexOutsideTheItemsKeptThrows()
    {
        var buffer = new HistoryBuffer<int>(3) { 1 };

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[-1]);
    }

    [Fact]
    public void ACapacityBelowOneIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryBuffer<int>(0));
}
