namespace WindFlow.App;

/// <summary>Serializes invalidation with publication. Checking an epoch outside this lock would still
/// allow an old worker to publish between the check and a geometry/wind change.</summary>
internal sealed class PublicationEpoch
{
    readonly object gate = new();
    int epoch;
    public int Current { get { lock (gate) return epoch; } }
    public int Advance() { lock (gate) return ++epoch; }
    public bool Publish(int expected, Action action)
    {
        lock (gate)
        {
            if (expected != epoch) return false;
            action();
            return true;
        }
    }
}
