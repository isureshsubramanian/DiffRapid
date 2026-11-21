namespace DiffRapid.Core.Tests;

// Tests that measure elapsed time run after the parallel ones, with nothing else competing for the thread pool.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollection
{
    public const string Name = "Serial";
}
