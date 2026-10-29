using System.Numerics;

namespace WindFlow.Validation;

/// <summary>Apply a measured quantity after averaging vector components, including for nonlinear
/// quantities such as speed. Uncertainty resamples whole blocks, retaining component covariance.</summary>
public static class BlockVelocity
{
    public static double Mean(IReadOnlyList<Vector3> blocks, Func<Vector3,double> quantity)
    {
        if(blocks.Count==0) return double.NaN;
        Vector3 sum=default;foreach(var v in blocks) sum+=v;
        return quantity(sum/blocks.Count);
    }

    public static double StandardError(IReadOnlyList<Vector3> blocks,Func<Vector3,double> quantity)
    {
        if(blocks.Count<2) return double.NaN;
        const int replicates=512;
        uint state=0x9E3779B9;
        double mean=0,m2=0;
        for(int r=1;r<=replicates;r++)
        {
            Vector3 sum=default;
            for(int k=0;k<blocks.Count;k++)
            {
                state^=state<<13;state^=state>>17;state^=state<<5;
                sum+=blocks[(int)(state%(uint)blocks.Count)];
            }
            double value=quantity(sum/blocks.Count),delta=value-mean;
            mean+=delta/r;m2+=delta*(value-mean);
        }
        return Math.Sqrt(m2/(replicates-1));
    }
}
