namespace OpenVector.Core.Profiles;

/// <summary>Converts the canonical model into the D14 sampled target (see <see cref="Profile.SampleForProjection"/>).</summary>
public static class ProjectionSampler
{
    public const double DensityKappa = 26.0;
    public const double WeightFloor = 0.05;

    public static ProjectionSamples Sample(GainModel model, GainBounds bounds, IReadOnlyList<DisplayBin> bins, int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        var theta = new double[n];
        var gain = new double[n];
        var dens = new double[n];
        for (int j = 0; j < n; j++)
        {
            theta[j] = Angles.TwoPi * j / n;
            gain[j] = model.Gain(theta[j], bounds);
        }
        foreach (var b in bins)
        {
            if (b.N <= 0) continue;
            for (int j = 0; j < n; j++)
                dens[j] += b.N * Math.Exp(DensityKappa * (Math.Cos(theta[j] - b.Theta) - 1.0));
        }
        double mean = dens.Sum() / n;
        var weight = new double[n];
        for (int j = 0; j < n; j++)
            weight[j] = mean > 0 ? (dens[j] / mean + WeightFloor) / (1.0 + WeightFloor) : 1.0;
        return new ProjectionSamples(theta, gain, weight);
    }
}
